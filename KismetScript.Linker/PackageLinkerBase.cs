using KismetScript.Compiler.Compiler;
using KismetScript.Compiler.Compiler.Context;
using KismetScript.Compiler.Compiler.Intermediate;
using KismetScript.Utilities;
using System.Text.RegularExpressions;
using UAssetAPI;
using UAssetAPI.CustomVersions;
using UAssetAPI.ExportTypes;
using UAssetAPI.FieldTypes;
using UAssetAPI.Kismet.Bytecode.Expressions;
using UAssetAPI.Kismet.Bytecode;
using UAssetAPI.UnrealTypes;

namespace KismetScript.Linker;

public abstract partial class PackageLinker<T> where T : UnrealPackage
{
    protected T Package { get; }

    protected bool SerializeLoadedProperties
        => Package.GetCustomVersion<FCoreObjectVersion>() >= FCoreObjectVersion.FProperties;

    private readonly record struct PreservedFieldPath(string[] PathSegments, FPackageIndex ResolvedOwner);

    protected enum PropertyPointerKind
    {
        Default,
        ArrayInner
    }

    // Dictionary to preserve original field path metadata from existing bytecode
    // Maps property name -> queue of original path segments/resolved owner (supports multiple same-named properties)
    private Dictionary<(PropertyPointerKind Kind, string PropertyName), Queue<PreservedFieldPath>> _originalPropertyFieldPaths = new();

    // Set of variable names whose property pointers had empty Path/owner in the original bytecode
    // (e.g. certain K2Node temporaries used as EX_Let Value pointers)
    private HashSet<string> _k2NodeVariablesWithEmptyPath = new();

    // Dictionary to preserve original function call info from existing bytecode.
    // Keyed by function name -> queue of preserved function call data (supports multiple calls to same function).
    private record PreservedFunctionCall(Type ExprType, FPackageIndex? StackNode, FName? VirtualFunctionName, int? UbergraphOffset);
    private Dictionary<string, Queue<PreservedFunctionCall>> _originalFunctionCalls = new();

    // Pending expression replacements (old -> new) to apply after the Flatten() fixup loop
    private Dictionary<KismetExpression, KismetExpression> _pendingExprReplacements = new();

    // Queue to preserve original expression types for EX_Context subtypes
    // (EX_ClassContext, EX_Context_FailSilent) that the compiler may downgrade to EX_Context.
    private Queue<Type> _preservedContextTypes = new();

    // Queue to preserve original EX_StructMemberContext expressions that the compiler
    // may incorrectly compile as EX_Context. These need full expression replacement.
    private Queue<KismetExpression> _preservedStructMemberContexts = new();

    // Set of expressions whose property pointer fixups should be skipped.
    // When EX_Context is replaced with EX_StructMemberContext, the ContextExpression subtree
    // is discarded, so its property pointers should not consume from the preserved path queue.
    private HashSet<KismetExpression> _skipPropertyFixupExpressions = new();

    // Queues to preserve original bytecode offset values (PushingAddress, CodeOffset, etc.)
    // These are restored after expression type replacements to maintain correct byte offsets.
    private Queue<uint> _preservedPushingAddresses = new();
    private Queue<uint> _preservedJumpOffsets = new();
    private Queue<uint> _preservedJumpIfNotOffsets = new();
    private Queue<uint> _preservedSkipOffsets = new();
    private Queue<uint> _preservedSwitchEndGotoOffsets = new();
    private Queue<(uint NextOffset, int CaseCount)> _preservedSwitchCaseOffsets = new();
    private Queue<uint> _preservedContextOffsets = new();

    protected PackageLinker()
    {
        Package = CreateDefaultAsset();
    }

    public PackageLinker(T package)
    {
        Package = package;
    }

    protected FName AddName(string name)
    {
        var idSuffixMatch = NameIdSuffix().Match(name);
        if (idSuffixMatch?.Success ?? false)
        {
            var nameWithoutSuffix = name[..^idSuffixMatch.Length];
            var baseFString = new FString(nameWithoutSuffix);
            var containsBase = Package.ContainsNameReference(baseFString);
            if (containsBase && int.TryParse(idSuffixMatch.Groups[1].Value, out var id))
            {
                Console.WriteLine($"[AddName] '{name}' -> reusing base '{nameWithoutSuffix}' with Number={id + 1}");
                return new FName(Package, nameWithoutSuffix, id + 1);
            }
            // Debug: Log when we don't find the base name
            Console.WriteLine($"[AddName] '{name}' -> base '{nameWithoutSuffix}' not found (contains: {containsBase})");
        }

        // Debug: check if name already exists before adding
        var fstring = new FString(name);
        if (!Package.ContainsNameReference(fstring))
        {
            // Name will be added to NameMap automatically when FName is created
        }
        return new FName(Package, name);
    }

    protected static Dictionary<string, string> _typeToPropertySerializedType = new()
    {
        { "byte", "ByteProperty" },
        { "bool", "BoolProperty" },
        { "int", "IntProperty" },
        { "int64", "Int64Property" },
        { "int16", "Int16Property" },
        { "uint32", "UInt32Property" },
        { "uint64", "UInt64Property" },
        { "string", "StrProperty" },
        { "float", "FloatProperty" },
        { "double", "DoubleProperty" },
        { "Interface", "InterfaceProperty" },
        { "Struct", "StructProperty" },
        { "Array", "ArrayProperty" },
        { "Enum", "IntProperty" },
        { "Object", "ObjectProperty" },
        { "Delegate", "DelegateProperty" },
        { "MulticastDelegate", "MulticastDelegateProperty" },
        { "MulticastInlineDelegate", "MulticastInlineDelegateProperty" },
        { "MulticastSparseDelegate", "MulticastSparseDelegateProperty" },
        { "Class", "ClassProperty" },
        { "SoftObject", "SoftObjectProperty" },
        { "SoftClass", "SoftClassProperty" },
        { "Name", "NameProperty" },
        { "Text", "TextProperty" },
        { "Map", "MapProperty" },
        { "Set", "SetProperty" }
    };

    protected static string GetPropertySerializedType(VariableSymbol symbol)
    {
        var type = symbol.Declaration?.Type.Text ?? "";
        if (_typeToPropertySerializedType.Values.Contains(type))
            return type;
        if (!_typeToPropertySerializedType.TryGetValue(type, out var serializedType))
            throw new NotImplementedException($"Creating new property of type {type} is not implemented");
        return serializedType;
    }

    protected static EPropertyFlags GetPropertyFlags(VariableSymbol symbol)
    {
        EPropertyFlags flags = 0;
        if (symbol.IsParameter)
        {
            flags = EPropertyFlags.CPF_BlueprintVisible | EPropertyFlags.CPF_BlueprintReadOnly | EPropertyFlags.CPF_Parm;
            if (symbol.IsOutParameter)
            {
                flags |= EPropertyFlags.CPF_OutParm;
            }
            if (symbol.IsReturnParameter)
            {
                flags |= EPropertyFlags.CPF_OutParm;
                flags |= EPropertyFlags.CPF_ReturnParm;
            }
        }
        return flags;
    }

    protected UProperty CreateUProperty(VariableSymbol symbol)
    {
        var serializedType = GetPropertySerializedType(symbol);
        var propertyFlags = GetPropertyFlags(symbol);

        return serializedType switch
        {
            "ByteProperty" => new UByteProperty()
            {
                Enum = new FPackageIndex(0),
                ArrayDim = EArrayDim.TArray,
                ElementSize = 0,
                PropertyFlags = propertyFlags,
                RepNotifyFunc = AddName("None"),
                BlueprintReplicationCondition = UAssetAPI.FieldTypes.ELifetimeCondition.COND_None,
                RawValue = null,
                Next = null,
            },
            "BoolProperty" => new UBoolProperty()
            {
                NativeBool = true,
                ArrayDim = EArrayDim.TArray,
                ElementSize = 1,
                PropertyFlags = propertyFlags,
                RepNotifyFunc = AddName("None"),
                BlueprintReplicationCondition = UAssetAPI.FieldTypes.ELifetimeCondition.COND_None,
                RawValue = null,
                Next = null,
            },
            "IntProperty" => new UIntProperty()
            {
                ArrayDim = EArrayDim.TArray,
                ElementSize = 0,
                PropertyFlags = propertyFlags,
                RepNotifyFunc = AddName("None"),
                BlueprintReplicationCondition = UAssetAPI.FieldTypes.ELifetimeCondition.COND_None,
                RawValue = null,
                Next = null,
            },
            "StrProperty" => new UStrProperty()
            {
                ArrayDim = EArrayDim.TArray,
                ElementSize = 0,
                PropertyFlags = propertyFlags,
                RepNotifyFunc = AddName("None"),
                BlueprintReplicationCondition = UAssetAPI.FieldTypes.ELifetimeCondition.COND_None,
                RawValue = null,
                Next = null,
            },
            "FloatProperty" => new UFloatProperty()
            {
                ArrayDim = EArrayDim.TArray,
                ElementSize = 0,
                PropertyFlags = propertyFlags,
                RepNotifyFunc = AddName("None"),
                BlueprintReplicationCondition = UAssetAPI.FieldTypes.ELifetimeCondition.COND_None,
                RawValue = null,
                Next = null,
            },
            "DoubleProperty" => new UDoubleProperty()
            {
                ArrayDim = EArrayDim.TArray,
                ElementSize = 0,
                PropertyFlags = propertyFlags,
                RepNotifyFunc = AddName("None"),
                BlueprintReplicationCondition = UAssetAPI.FieldTypes.ELifetimeCondition.COND_None,
                RawValue = null,
                Next = null,
            },
            "InterfaceProperty" => new UInterfaceProperty()
            {
                InterfaceClass = FindPackageIndexInAsset(symbol.InnerSymbol!),
                ArrayDim = EArrayDim.TArray,
                ElementSize = 0,
                PropertyFlags = propertyFlags,
                RepNotifyFunc = AddName("None"),
                BlueprintReplicationCondition = UAssetAPI.FieldTypes.ELifetimeCondition.COND_None,
                RawValue = null,
                Next = null
            },
            "StructProperty" => new UStructProperty()
            {
                Struct = FindPackageIndexInAsset(symbol.InnerSymbol!),
                ArrayDim = EArrayDim.TArray,
                ElementSize = 0,
                PropertyFlags = propertyFlags,
                RepNotifyFunc = AddName("None"),
                BlueprintReplicationCondition = UAssetAPI.FieldTypes.ELifetimeCondition.COND_None,
                RawValue = null,
                Next = null
            },
            "ArrayProperty" => new UArrayProperty()
            {
                Inner = FindPackageIndexInAsset(symbol.InnerSymbol!),
                ArrayDim = EArrayDim.TArray,
                ElementSize = 0,
                PropertyFlags = propertyFlags,
                RepNotifyFunc = AddName("None"),
                BlueprintReplicationCondition = UAssetAPI.FieldTypes.ELifetimeCondition.COND_None,
                RawValue = null,
                Next = null,
            },
            "ObjectProperty" => new UObjectProperty()
            {
                PropertyClass = FindPackageIndexInAsset(symbol.InnerSymbol!),
                ArrayDim = EArrayDim.TArray,
                ElementSize = 0,
                PropertyFlags = propertyFlags,
                RepNotifyFunc = AddName("None"),
                BlueprintReplicationCondition = UAssetAPI.FieldTypes.ELifetimeCondition.COND_None,
                RawValue = null,
                Next = null,
            },
            "DelegateProperty" => new UDelegateProperty()
            {
                SignatureFunction = FindPackageIndexInAsset(symbol.InnerSymbol!),
                ArrayDim = EArrayDim.TArray,
                ElementSize = 0,
                PropertyFlags = propertyFlags,
                RepNotifyFunc = AddName("None"),
                BlueprintReplicationCondition = UAssetAPI.FieldTypes.ELifetimeCondition.COND_None,
                RawValue = null,
                Next = null,
            },
            "ClassProperty" => new UClassProperty()
            {
                MetaClass = FindPackageIndexInAsset(symbol.InnerSymbol!),
                //PropertyClass = FindPack
                ArrayDim = EArrayDim.TArray,
                ElementSize = 0,
                PropertyFlags = propertyFlags,
                RepNotifyFunc = AddName("None"),
                BlueprintReplicationCondition = UAssetAPI.FieldTypes.ELifetimeCondition.COND_None,
                RawValue = null,
                Next = null,
            },
            _ => throw new NotImplementedException(serializedType),
        };
    }

    protected FProperty CreateFProperty(VariableSymbol symbol)
    {
        var serializedType = GetPropertySerializedType(symbol);
        var propertyFlags = GetPropertyFlags(symbol);
        return serializedType switch
        {
            "ByteProperty" => new FByteProperty()
            {
                // FField values
                SerializedType = AddName(serializedType),
                Name = AddName(symbol.Name),
                Flags = EObjectFlags.RF_Public,

                // FProperty values
                ArrayDim = EArrayDim.TArray,
                ElementSize = 1,
                PropertyFlags = propertyFlags,
                RepIndex = 0,
                RepNotifyFunc = AddName("None"),
                BlueprintReplicationCondition = UAssetAPI.FieldTypes.ELifetimeCondition.COND_None,
                RawValue = null,

                Enum = new FPackageIndex(0),
            },
            "BoolProperty" => new FBoolProperty()
            {
                // FField values
                SerializedType = AddName(serializedType),
                Name = AddName(symbol.Name),
                Flags = EObjectFlags.RF_Public,

                // FProperty values
                ArrayDim = EArrayDim.TArray,
                ElementSize = 1,
                PropertyFlags = propertyFlags,
                RepIndex = 0,
                RepNotifyFunc = AddName("None"),
                BlueprintReplicationCondition = UAssetAPI.FieldTypes.ELifetimeCondition.COND_None,
                RawValue = null,

                FieldSize = 1,
                ByteOffset = 0,
                ByteMask = 0x1,
                FieldMask = 0xFF,
                NativeBool = true,
                Value = true, // TODO: is this correct?
            },
            "IntProperty" => new FGenericProperty()
            {
                // FField values
                SerializedType = AddName(serializedType),
                Name = AddName(symbol.Name),
                Flags = EObjectFlags.RF_Public,

                // FProperty values
                ArrayDim = EArrayDim.TArray,
                ElementSize = 4,
                PropertyFlags = propertyFlags,
                RepIndex = 0,
                RepNotifyFunc = AddName("None"),
                BlueprintReplicationCondition = UAssetAPI.FieldTypes.ELifetimeCondition.COND_None,
                RawValue = null,
            },
            "StrProperty" => new FGenericProperty()
            {
                // FField values
                SerializedType = AddName(serializedType),
                Name = AddName(symbol.Name),
                Flags = EObjectFlags.RF_Public,

                // FProperty values
                ArrayDim = EArrayDim.TArray,
                ElementSize = 8,
                PropertyFlags = propertyFlags,
                RepIndex = 0,
                RepNotifyFunc = AddName("None"),
                BlueprintReplicationCondition = UAssetAPI.FieldTypes.ELifetimeCondition.COND_None,
                RawValue = null,
            },
            "FloatProperty" => new FGenericProperty()
            {
                // FField values
                SerializedType = AddName(serializedType),
                Name = AddName(symbol.Name),
                Flags = EObjectFlags.RF_Public,

                // FProperty values
                ArrayDim = EArrayDim.TArray,
                ElementSize = 4,
                PropertyFlags = propertyFlags,
                RepIndex = 0,
                RepNotifyFunc = AddName("None"),
                BlueprintReplicationCondition = UAssetAPI.FieldTypes.ELifetimeCondition.COND_None,
                RawValue = null,
            },
            "DoubleProperty" => new FGenericProperty()
            {
                // FField values
                SerializedType = AddName(serializedType),
                Name = AddName(symbol.Name),
                Flags = EObjectFlags.RF_Public,

                // FProperty values
                ArrayDim = EArrayDim.TArray,
                ElementSize = 8,
                PropertyFlags = propertyFlags,
                RepIndex = 0,
                RepNotifyFunc = AddName("None"),
                BlueprintReplicationCondition = UAssetAPI.FieldTypes.ELifetimeCondition.COND_None,
                RawValue = null,
            },
            "InterfaceProperty" => new FInterfaceProperty()
            {
                // FField values
                SerializedType = AddName(serializedType),
                Name = AddName(symbol.Name),
                Flags = EObjectFlags.RF_Public,

                // FProperty values
                ArrayDim = EArrayDim.TArray,
                ElementSize = 8,
                PropertyFlags = propertyFlags,
                RepIndex = 0,
                RepNotifyFunc = AddName("None"),
                BlueprintReplicationCondition = UAssetAPI.FieldTypes.ELifetimeCondition.COND_None,
                RawValue = null,

                InterfaceClass = FindPackageIndexInAsset(symbol.InnerSymbol!),
            },
            "StructProperty" => new FStructProperty()
            {
                // FField values
                SerializedType = AddName(serializedType),
                Name = AddName(symbol.Name),
                Flags = EObjectFlags.RF_Public,

                // FProperty values
                ArrayDim = EArrayDim.TArray,
                ElementSize = 0, // TODO: depends on the actual size of the struct
                PropertyFlags = propertyFlags,
                RepIndex = 0,
                RepNotifyFunc = AddName("None"),
                BlueprintReplicationCondition = UAssetAPI.FieldTypes.ELifetimeCondition.COND_None,
                RawValue = null,

                Struct = FindPackageIndexInAsset(symbol.InnerSymbol!),
            },
            "ArrayProperty" => new FArrayProperty()
            {
                // FField values
                SerializedType = AddName(serializedType),
                Name = AddName(symbol.Name),
                Flags = EObjectFlags.RF_Public,

                // FProperty values
                ArrayDim = EArrayDim.TArray,
                ElementSize = 16,
                PropertyFlags = propertyFlags,
                RepIndex = 0,
                RepNotifyFunc = AddName("None"),
                BlueprintReplicationCondition = UAssetAPI.FieldTypes.ELifetimeCondition.COND_None,
                RawValue = null,

                // Create inner property based on the inner symbol type
                Inner = CreateFPropertyForArrayInner(symbol),
            },
            "DelegateProperty" => new FDelegateProperty()
            {
                // FField values
                SerializedType = AddName(serializedType),
                Name = AddName(symbol.Name),
                Flags = EObjectFlags.RF_Public,

                // FProperty values
                ArrayDim = EArrayDim.TArray,
                ElementSize = 20,
                PropertyFlags = propertyFlags,
                RepIndex = 0,
                RepNotifyFunc = AddName("None"),
                BlueprintReplicationCondition = UAssetAPI.FieldTypes.ELifetimeCondition.COND_None,
                RawValue = null,

                // TODO verify if this works
                SignatureFunction = FindPackageIndexInAsset(symbol.InnerSymbol!),
            },
            "ObjectProperty" => new FObjectProperty()
            {
                // FField values
                SerializedType = AddName(serializedType),
                Name = AddName(symbol.Name),
                Flags = EObjectFlags.RF_Public,

                // FProperty values
                ArrayDim = EArrayDim.TArray,
                ElementSize = 8,
                PropertyFlags = propertyFlags,
                RepIndex = 0,
                RepNotifyFunc = AddName("None"),
                BlueprintReplicationCondition = UAssetAPI.FieldTypes.ELifetimeCondition.COND_None,
                RawValue = null,

                PropertyClass = FindPackageIndexInAsset(symbol.InnerSymbol!),
            },
            "MulticastInlineDelegateProperty" or "MulticastDelegateProperty" or "MulticastSparseDelegateProperty" => new FMulticastInlineDelegateProperty()
            {
                // FField values
                SerializedType = AddName(serializedType),
                Name = AddName(symbol.Name),
                Flags = EObjectFlags.RF_Public,

                // FProperty values
                ArrayDim = EArrayDim.TArray,
                ElementSize = 16,
                PropertyFlags = propertyFlags,
                RepIndex = 0,
                RepNotifyFunc = AddName("None"),
                BlueprintReplicationCondition = UAssetAPI.FieldTypes.ELifetimeCondition.COND_None,
                RawValue = null,

                SignatureFunction = symbol.InnerSymbol != null ? FindPackageIndexInAsset(symbol.InnerSymbol) : new FPackageIndex(0),
            },
            "SoftClassProperty" => new FSoftClassProperty()
            {
                SerializedType = AddName(serializedType),
                Name = AddName(symbol.Name),
                Flags = EObjectFlags.RF_Public,
                ArrayDim = EArrayDim.TArray,
                ElementSize = 24,
                PropertyFlags = propertyFlags,
                RepIndex = 0,
                RepNotifyFunc = AddName("None"),
                BlueprintReplicationCondition = UAssetAPI.FieldTypes.ELifetimeCondition.COND_None,
                RawValue = null,
                // FSoftClassProperty inherits from FObjectProperty and needs both PropertyClass and MetaClass
                PropertyClass = symbol.InnerSymbol != null ? FindPackageIndexInAsset(symbol.InnerSymbol) : new FPackageIndex(0),
                MetaClass = symbol.InnerSymbol != null ? FindPackageIndexInAsset(symbol.InnerSymbol) : new FPackageIndex(0),
            },
            "SoftObjectProperty" => new FSoftObjectProperty()
            {
                SerializedType = AddName(serializedType),
                Name = AddName(symbol.Name),
                Flags = EObjectFlags.RF_Public,
                ArrayDim = EArrayDim.TArray,
                ElementSize = 24,
                PropertyFlags = propertyFlags,
                RepIndex = 0,
                RepNotifyFunc = AddName("None"),
                BlueprintReplicationCondition = UAssetAPI.FieldTypes.ELifetimeCondition.COND_None,
                RawValue = null,
                PropertyClass = symbol.InnerSymbol != null ? FindPackageIndexInAsset(symbol.InnerSymbol) : new FPackageIndex(0),
            },
            "NameProperty" => new FGenericProperty()
            {
                SerializedType = AddName(serializedType),
                Name = AddName(symbol.Name),
                Flags = EObjectFlags.RF_Public,
                ArrayDim = EArrayDim.TArray,
                ElementSize = 8,
                PropertyFlags = propertyFlags,
                RepIndex = 0,
                RepNotifyFunc = AddName("None"),
                BlueprintReplicationCondition = UAssetAPI.FieldTypes.ELifetimeCondition.COND_None,
                RawValue = null,
            },
            "TextProperty" => new FGenericProperty()
            {
                SerializedType = AddName(serializedType),
                Name = AddName(symbol.Name),
                Flags = EObjectFlags.RF_Public,
                ArrayDim = EArrayDim.TArray,
                ElementSize = 24,
                PropertyFlags = propertyFlags,
                RepIndex = 0,
                RepNotifyFunc = AddName("None"),
                BlueprintReplicationCondition = UAssetAPI.FieldTypes.ELifetimeCondition.COND_None,
                RawValue = null,
            },
            "MapProperty" => new FMapProperty()
            {
                SerializedType = AddName(serializedType),
                Name = AddName(symbol.Name),
                Flags = EObjectFlags.RF_Public,
                ArrayDim = EArrayDim.TArray,
                ElementSize = 80,
                PropertyFlags = propertyFlags,
                RepIndex = 0,
                RepNotifyFunc = AddName("None"),
                BlueprintReplicationCondition = UAssetAPI.FieldTypes.ELifetimeCondition.COND_None,
                RawValue = null,
                KeyProp = null!, // Will be set separately if needed
                ValueProp = null!, // Will be set separately if needed
            },
            "SetProperty" => new FSetProperty()
            {
                SerializedType = AddName(serializedType),
                Name = AddName(symbol.Name),
                Flags = EObjectFlags.RF_Public,
                ArrayDim = EArrayDim.TArray,
                ElementSize = 80,
                PropertyFlags = propertyFlags,
                RepIndex = 0,
                RepNotifyFunc = AddName("None"),
                BlueprintReplicationCondition = UAssetAPI.FieldTypes.ELifetimeCondition.COND_None,
                RawValue = null,
                ElementProp = null!, // Will be set separately if needed
            },
            "Int64Property" => new FGenericProperty()
            {
                SerializedType = AddName(serializedType),
                Name = AddName(symbol.Name),
                Flags = EObjectFlags.RF_Public,
                ArrayDim = EArrayDim.TArray,
                ElementSize = 8,
                PropertyFlags = propertyFlags,
                RepIndex = 0,
                RepNotifyFunc = AddName("None"),
                BlueprintReplicationCondition = UAssetAPI.FieldTypes.ELifetimeCondition.COND_None,
                RawValue = null,
            },
            "Int16Property" => new FGenericProperty()
            {
                SerializedType = AddName(serializedType),
                Name = AddName(symbol.Name),
                Flags = EObjectFlags.RF_Public,
                ArrayDim = EArrayDim.TArray,
                ElementSize = 2,
                PropertyFlags = propertyFlags,
                RepIndex = 0,
                RepNotifyFunc = AddName("None"),
                BlueprintReplicationCondition = UAssetAPI.FieldTypes.ELifetimeCondition.COND_None,
                RawValue = null,
            },
            "UInt32Property" => new FGenericProperty()
            {
                SerializedType = AddName(serializedType),
                Name = AddName(symbol.Name),
                Flags = EObjectFlags.RF_Public,
                ArrayDim = EArrayDim.TArray,
                ElementSize = 4,
                PropertyFlags = propertyFlags,
                RepIndex = 0,
                RepNotifyFunc = AddName("None"),
                BlueprintReplicationCondition = UAssetAPI.FieldTypes.ELifetimeCondition.COND_None,
                RawValue = null,
            },
            "UInt64Property" => new FGenericProperty()
            {
                SerializedType = AddName(serializedType),
                Name = AddName(symbol.Name),
                Flags = EObjectFlags.RF_Public,
                ArrayDim = EArrayDim.TArray,
                ElementSize = 8,
                PropertyFlags = propertyFlags,
                RepIndex = 0,
                RepNotifyFunc = AddName("None"),
                BlueprintReplicationCondition = UAssetAPI.FieldTypes.ELifetimeCondition.COND_None,
                RawValue = null,
            },
            "ClassProperty" => new FClassProperty()
            {
                SerializedType = AddName(serializedType),
                Name = AddName(symbol.Name),
                Flags = EObjectFlags.RF_Public,
                ArrayDim = EArrayDim.TArray,
                ElementSize = 8,
                PropertyFlags = propertyFlags,
                RepIndex = 0,
                RepNotifyFunc = AddName("None"),
                BlueprintReplicationCondition = UAssetAPI.FieldTypes.ELifetimeCondition.COND_None,
                RawValue = null,
                MetaClass = symbol.InnerSymbol != null ? FindPackageIndexInAsset(symbol.InnerSymbol) : new FPackageIndex(0),
                PropertyClass = new FPackageIndex(0), // TODO: set properly
            },
            _ => throw new NotImplementedException(serializedType),
        };
    }

    /// <summary>
    /// Creates an FProperty for the inner type of an array property.
    /// This handles cases where InnerSymbol is a ClassSymbol (for Object types).
    /// </summary>
    protected FProperty CreateFPropertyForArrayInner(VariableSymbol symbol)
    {
        var innerSymbol = symbol.InnerSymbol;

        // If the inner symbol is a VariableSymbol, use the standard method
        if (innerSymbol is VariableSymbol variableSymbol)
        {
            return CreateFProperty(variableSymbol);
        }

        // If the inner symbol is a ClassSymbol, create an ObjectProperty for it
        if (innerSymbol is ClassSymbol classSymbol)
        {
            return new FObjectProperty()
            {
                SerializedType = AddName("ObjectProperty"),
                Name = AddName(symbol.Name),
                Flags = EObjectFlags.RF_Public,
                ArrayDim = EArrayDim.TArray,
                ElementSize = 8,
                PropertyFlags = EPropertyFlags.CPF_None,
                RepIndex = 0,
                RepNotifyFunc = AddName("None"),
                BlueprintReplicationCondition = UAssetAPI.FieldTypes.ELifetimeCondition.COND_None,
                RawValue = null,
                PropertyClass = FindPackageIndexInAsset(classSymbol),
            };
        }

        // If inner symbol is null, try to create a generic property based on symbol type name
        if (innerSymbol == null)
        {
            // Default to a generic object property with null class
            return new FGenericProperty()
            {
                SerializedType = AddName("ObjectProperty"),
                Name = AddName(symbol.Name),
                Flags = EObjectFlags.RF_Public,
                ArrayDim = EArrayDim.TArray,
                ElementSize = 8,
                PropertyFlags = EPropertyFlags.CPF_None,
                RepIndex = 0,
                RepNotifyFunc = AddName("None"),
                BlueprintReplicationCondition = UAssetAPI.FieldTypes.ELifetimeCondition.COND_None,
                RawValue = null,
            };
        }

        throw new NotImplementedException($"Cannot create array inner property for symbol type: {innerSymbol?.GetType().Name ?? "null"}");
    }

    protected (FProperty Property, FPackageIndex ExportIndex, PropertyExport? Export) CreatePropertyAsFProperty(VariableSymbol symbol)
    {
        var property = CreateFProperty(symbol);
        // TODO: create _GEN_VARIABLE if necessary
        var exportIndex = new FPackageIndex(0);
        PropertyExport? export = null;
        return (property, exportIndex, export);
    }

    protected (FPackageIndex Index, PropertyExport Export) CreatePropertyAsPropertyExport(VariableSymbol symbol)
    {
        var serializedType = GetPropertySerializedType(symbol);
        var property = CreateUProperty(symbol);

        var serializationBeforeSerializationDependencies = new List<FPackageIndex>();
        var createBeforeSerializationDependencies = new List<FPackageIndex>();
        var createBeforeCreateDependencies = new List<FPackageIndex>();

        var className = symbol.DeclaringClass?.Name;
        var classExport = className != null ? Package.FindClassExportByName(className) : null;
        var functionName = symbol?.DeclaringProcedure?.Name;
        var functionExport = functionName != null ? Package.FindFunctionExportByName(functionName) : null;
        var coreUObjectImport = Package.FindImportIndexByObjectName("/Script/CoreUObject") ?? throw new NotImplementedException();
        var propertyClassImportIndex = EnsureObjectImported(coreUObjectImport, serializedType, "Class");
        var propertyTemplateImportIndex = EnsureObjectImported(coreUObjectImport, $"Default__{serializedType}", serializedType);

        var functionOwnerIndex = functionExport != null ? FPackageIndex.FromExport(Package.Exports.IndexOf(functionExport)) : new FPackageIndex(0);
        var classOwnerIndex = classExport != null ? FPackageIndex.FromExport(Package.Exports.IndexOf(classExport)) : new FPackageIndex(0);
        var propertyOwnerIndex =
            functionExport != null ?
                functionOwnerIndex :
            classExport != null ?
                classOwnerIndex :
                new FPackageIndex(0);

        if (!propertyOwnerIndex.IsNull())
        {
            createBeforeCreateDependencies.Insert(0, propertyOwnerIndex);
        }

        var propertyExport = new PropertyExport()
        {
            Asset = Package,
            Property = property,
            Data = new(),
            ObjectName = AddName(symbol?.Name ?? string.Empty),
            ObjectFlags = EObjectFlags.RF_Public,
            SerialSize = 0, // Filled by serializer
            SerialOffset = 0, // Filled by serializer
            bForcedExport = false,
            bNotForClient = false,
            bNotForServer = false,
            PackageGuid = Guid.Empty,
            IsInheritedInstance = false,
            PackageFlags = EPackageFlags.PKG_None,
            bNotAlwaysLoadedForEditorGame = false,
            bIsAsset = false,
            GeneratePublicHash = false,
            SerializationBeforeSerializationDependencies = new(serializationBeforeSerializationDependencies),
            CreateBeforeSerializationDependencies = new(createBeforeSerializationDependencies),
            SerializationBeforeCreateDependencies = new(),
            CreateBeforeCreateDependencies = new(createBeforeCreateDependencies),
            Extras = new byte[0],
            OuterIndex = propertyOwnerIndex,
            ClassIndex = propertyClassImportIndex,
            SuperIndex = new FPackageIndex(0),
            TemplateIndex = propertyTemplateImportIndex,
        };

        Package.Exports.Add(propertyExport);
        var packageIndex = FPackageIndex.FromExport(Package.Exports.Count - 1);
        return (packageIndex, propertyExport);
    }

    protected FPackageIndex EnsurePackageIndexForSymbolCreated(Symbol? symbol)
    {
        if (symbol == null)
            return new FPackageIndex(0);

        if (TryFindPackageIndexInAsset(symbol, out var packageIndex))
            return packageIndex ?? new FPackageIndex(0);

        return CreatePackageIndexForSymbol(symbol);
    }

    protected FPackageIndex CreatePackageIndexForSymbol(Symbol symbol)
    {
        // FIXME: remove this hack
        if (symbol.Name.StartsWith("AnonymousClass_"))
            return new FPackageIndex(0);

        if (symbol is VariableSymbol variableSymbol)
        {
            return CreatePropertyAsPropertyExport(variableSymbol).Index;
        }
        else if (symbol is ProcedureSymbol procedureSymbol)
        {
            if (procedureSymbol.IsExternal)
            {
                return CreateProcedureImport(procedureSymbol);
            }
            else
            {
                // Internal procedure - try to find by local name in exports
                var candidates = GetPackageIndexByLocalName(procedureSymbol.Name).ToList();
                if (candidates.Count == 1)
                {
                    return candidates[0].PackageIndex;
                }
                else if (candidates.Count > 1)
                {
                    // Multiple matches - try to find by class.function pattern
                    var className = procedureSymbol.DeclaringClass?.Name;
                    if (className != null)
                    {
                        var fullName = $"{className}.{procedureSymbol.Name}";
                        var fullCandidates = GetPackageIndexByFullName(fullName).ToList();
                        if (fullCandidates.Count == 1)
                        {
                            return fullCandidates[0].PackageIndex;
                        }
                    }
                }
                // Return null index if still not found - let the linker handle it later
                return new FPackageIndex(0);
            }
        }
        else if (symbol is UnknownSymbol)
        {
            // For UnknownSymbol, we cannot resolve the actual package index
            // This will be handled specially in FixPropertyPointer to preserve the name
            return new FPackageIndex(0);
        }
        else if (symbol is ClassSymbol classSymbol)
        {
            // For ClassSymbol (e.g., from K2Node dynamic casts), try to find the class
            var candidates = GetPackageIndexByLocalName(classSymbol.Name).ToList();
            if (candidates.Count >= 1)
            {
                return candidates[0].PackageIndex;
            }
            // Class not found - return null index
            return new FPackageIndex(0);
        }
        else
        {
            Console.WriteLine($"NotImplementedException CreatePackageIndexForSymbol: {symbol.Name}");
            throw new NotImplementedException();
        }
    }

    protected void FixPropertyPointer(ref KismetPropertyPointer pointer, PropertyPointerKind kind = PropertyPointerKind.Default)
    {
        if (pointer is IntermediatePropertyPointer iProperty)
        {
            if (SerializeLoadedProperties)
            {
                var propertyName = iProperty.Symbol.Name;

                // Reuse preserved path metadata when available to match the original asset exactly.
                // Uses a queue to support multiple occurrences of the same property name with different owners.
                if (_originalPropertyFieldPaths.TryGetValue((kind, propertyName), out var preservedQueue) && preservedQueue.Count > 0)
                {
                    var preservedFieldPath = preservedQueue.Dequeue();
                    Console.WriteLine($"[FixPropertyPointer] '{propertyName}' using preserved path");
                    pointer = new KismetPropertyPointer()
                    {
                        Old = new FPackageIndex(0),
                        New = RehydrateFieldPath(preservedFieldPath),
                    };
                    return;
                }
                Console.WriteLine($"[FixPropertyPointer] '{propertyName}' NOT using preserved path, will call AddName");

                FPackageIndex resolvedOwner;
                if (iProperty.Symbol is UnknownSymbol)
                {
                    resolvedOwner = TryFindPropertyOwner(propertyName);

                    if (resolvedOwner.IsNull() && iProperty.OriginalResolvedOwner != null)
                    {
                        resolvedOwner = iProperty.OriginalResolvedOwner;
                    }

                    if (resolvedOwner.IsNull())
                    {
                        resolvedOwner = new FPackageIndex(-1);
                    }
                }
                else
                {
                    resolvedOwner = EnsurePackageIndexForSymbolCreated(iProperty.Symbol.DeclaringSymbol);
                }

                pointer = new KismetPropertyPointer()
                {
                    Old = new FPackageIndex(0),
                    New = new()
                    {
                        Path = new[] { AddName(propertyName) },
                        ResolvedOwner = resolvedOwner,
                    }
                };
            }
            else
            {
                var packageIndex = EnsurePackageIndexForSymbolCreated(iProperty.Symbol);
                pointer = new KismetPropertyPointer()
                {
                    Old = packageIndex,
                    New = new FFieldPath()
                };
            }
        }
    }

    private FFieldPath RehydrateFieldPath(PreservedFieldPath preservedFieldPath)
    {
        return new FFieldPath()
        {
            Path = preservedFieldPath.PathSegments.Select(AddName).ToArray(),
            ResolvedOwner = preservedFieldPath.ResolvedOwner,
        };
    }

    private void FixLetValuePropertyPointer(ref KismetPropertyPointer pointer)
    {
        if (pointer is IntermediatePropertyPointer iProperty &&
            SerializeLoadedProperties &&
            _k2NodeVariablesWithEmptyPath.Contains(iProperty.Symbol.Name))
        {
            pointer = new KismetPropertyPointer()
            {
                Old = new FPackageIndex(0),
                New = new()
                {
                    Path = Array.Empty<FName>(),
                    ResolvedOwner = new FPackageIndex(0),
                }
            };
            return;
        }

        FixPropertyPointer(ref pointer);
    }

    /// <summary>
    /// Try to find the package index of the owner class/struct that contains a property with the given name.
    /// This is used when the property symbol cannot be fully resolved during compilation.
    /// </summary>
    protected virtual FPackageIndex TryFindPropertyOwner(string propertyName)
    {
        // First, search through exports for structs/classes with LoadedProperties
        foreach (var export in Package.Exports)
        {
            if (export is StructExport structExport)
            {
                // Check if this struct has a property with the given name
                foreach (var fprop in structExport.LoadedProperties ?? Array.Empty<FProperty>())
                {
                    if (fprop.Name.ToString() == propertyName)
                    {
                        // Found the property in this struct/class, return its export index
                        return FPackageIndex.FromExport(Package.Exports.IndexOf(export));
                    }
                }

                // Also check UProperty children for older UE versions
                foreach (var child in structExport.Children ?? Array.Empty<FPackageIndex>())
                {
                    if (child.IsExport())
                    {
                        var childExport = child.ToExport(Package);
                        if (childExport is PropertyExport propExport &&
                            propExport.ObjectName.ToString() == propertyName)
                        {
                            return FPackageIndex.FromExport(Package.Exports.IndexOf(export));
                        }
                    }
                }
            }
        }

        // Next, search through imports for classes/structs that might have this property as a child
        for (int i = 0; i < Package.Imports.Count; i++)
        {
            var import = Package.Imports[i];
            var importIndex = FPackageIndex.FromImport(i);

            // Check if this is a class/struct type import
            if (import.ClassName.ToString() == "Class" || import.ClassName.ToString() == "WidgetBlueprintGeneratedClass" ||
                import.ClassName.ToString() == "BlueprintGeneratedClass" || import.ClassName.ToString() == "ScriptStruct")
            {
                // Try to find a property with this name as a child import
                for (int j = 0; j < Package.Imports.Count; j++)
                {
                    var candidateImport = Package.Imports[j];
                    if (candidateImport.OuterIndex == importIndex &&
                        candidateImport.ObjectName.ToString() == propertyName)
                    {
                        // Found a property import with this name that belongs to this class
                        return importIndex;
                    }
                }
            }
        }

        // Not found
        return new FPackageIndex(0);
    }

    protected void FixPackageIndex(ref FPackageIndex packageIndex)
    {
        if (packageIndex is IntermediatePackageIndex iPackageIndex)
        {
            packageIndex = EnsurePackageIndexForSymbolCreated(iPackageIndex.Symbol);
        }
    }

    protected FPackageIndex FixPackageIndex(FPackageIndex packageIndex)
    {
        FixPackageIndex(ref packageIndex);
        return packageIndex ?? new FPackageIndex(0);
    }

    protected void FixName(ref FName name)
    {
        if (name is IntermediateName iName)
        {
            name = AddName(iName.TextValue);
        }
    }

    protected FName FixName(FName name)
    {
        FixName(ref name);
        return name;
    }

    protected string? GetFullName(object? obj)
    {
        if (obj is Import import)
        {
            if (import.OuterIndex.Index != 0)
            {
                var parent = GetFullName(import.OuterIndex) ?? string.Empty;
                return parent + "." + import.ObjectName.ToString();
            }
            else
            {
                return import.ObjectName.ToString();
            }
        }
        else if (obj is Export export)
        {
            if (export.OuterIndex.Index != 0)
            {
                var parent = GetFullName(export.OuterIndex) ?? string.Empty;
                return parent + "." + export.ObjectName.ToString();
            }
            else
            {
                return export.ObjectName.ToString();
            }
        }
        else if (obj is FField field)
            return field.Name.ToString();
        else if (obj is FName fname)
            return fname.ToString();
        else if (obj is FPackageIndex packageIndex)
        {
            if (packageIndex.IsImport())
                return GetFullName(packageIndex.ToImport(Package));
            else if (packageIndex.IsExport())
                return GetFullName(packageIndex.ToExport(Package));
            else if (packageIndex.IsNull())
                return null;
            else
                throw new NotImplementedException();
        }
        else
        {
            return null;
        }
    }

    protected FPackageIndex FindPackageIndexInAsset(Symbol? symbol)
    {
        if (symbol == null)
            return new FPackageIndex(0);

        if (!TryFindPackageIndexInAsset(symbol, out var packageIndex))
        {
            var pkgName = symbol.DeclaringPackage?.Name;
            if (string.IsNullOrWhiteSpace(pkgName))
            {
                packageIndex = new FPackageIndex(0);
            }
            else
            {
                packageIndex = EnsurePackageImported(pkgName);
            }
            var objName = symbol.Name;
            if (!string.IsNullOrWhiteSpace(objName))
            {
                packageIndex = EnsureObjectImported(packageIndex, objName, "Class"); // TODO classname
            }
        }

        return packageIndex ?? new FPackageIndex(0);
    }

    protected bool TryFindPackageIndexInAsset(Symbol? symbol, out FPackageIndex? index)
    {
        index = null;

        if (symbol == null)
            return false;

        var packageName = symbol.DeclaringPackage?.Name;
        var className = symbol.DeclaringClass?.Name;
        var functionName = symbol.DeclaringProcedure?.Name;
        var name = symbol.Name;

        // TODO fix
        if (name == "<null>")
        {
            index = new FPackageIndex(0);
            return true;
        }

        var packageClassFunctionLocalName = string.Join(".", new[] { packageName, className, functionName, name }.Where(x => !string.IsNullOrWhiteSpace(x)));
        var classFunctionLocalName = string.Join(".", new[] { className, functionName, name }.Where(x => !string.IsNullOrWhiteSpace(x)));
        var classLocalName = string.Join(".", new[] { className, name }.Where(x => !string.IsNullOrWhiteSpace(x)));
        var localName = name;

        var packageClassFunctionLocalCandidates = GetPackageIndexByFullName(packageClassFunctionLocalName).ToList();
        if (packageClassFunctionLocalCandidates.Count == 1)
        {
            index = packageClassFunctionLocalCandidates[0].PackageIndex;
            return true;
        }

        var classFunctionLocalCandidates = GetPackageIndexByFullName(classFunctionLocalName).ToList();
        if (classFunctionLocalCandidates.Count == 1)
        {
            index = classFunctionLocalCandidates[0].PackageIndex;
            return true;
        }

        var classLocalCandidates = GetPackageIndexByFullName(classLocalName).ToList();
        if (classLocalCandidates.Count == 1)
        {
            index = classLocalCandidates[0].PackageIndex;
            return true;
        }

        var localCandidates = GetPackageIndexByLocalName(localName).ToList();
        if (localCandidates.Count == 1)
        {
            index = localCandidates[0].PackageIndex;
            return true;
        }

        return false;
    }

    protected KismetExpression[] GetFixedBytecode(IEnumerable<KismetExpression> expressions)
    {
        var bytecode = expressions.ToArray();
        int intermediateCount = 0;
        int totalCount = 0;
        foreach (var baseExpr in bytecode.Flatten())
        {
            // Skip property pointer fixup for expressions that will be discarded
            // (e.g., the ContextExpression subtree when replacing EX_Context → EX_StructMemberContext)
            if (_skipPropertyFixupExpressions.Contains(baseExpr))
                continue;

            switch (baseExpr)
            {
                case EX_BindDelegate expr:
                    FixName(ref expr.FunctionName);
                    break;

                case EX_InstanceDelegate expr:
                    FixName(ref expr.FunctionName);
                    break;

                case EX_InstrumentationEvent expr:
                    FixName(ref expr.EventName);
                    break;

                case EX_NameConst expr:
                    expr.Value = FixName(expr.Value);
                    break;

                case EX_VirtualFunction expr:
                    {
                        // Check if the original was a FinalFunction (type mismatch from compiler)
                        // Get function name safely - IntermediateName doesn't support ToString()
                        string? vfName = null;
                        if (expr.VirtualFunctionName is IntermediateName iVfName)
                            vfName = iVfName.TextValue;
                        else if (expr.VirtualFunctionName != null)
                        {
                            try { vfName = expr.VirtualFunctionName.ToString(); } catch { }
                        }
                        if (vfName != null && _originalFunctionCalls.TryGetValue(vfName, out var vfQueue) && vfQueue.Count > 0)
                        {
                            var preserved = vfQueue.Peek();
                            if (preserved.StackNode != null &&
                                (preserved.ExprType == typeof(EX_FinalFunction) ||
                                 preserved.ExprType.IsSubclassOf(typeof(EX_FinalFunction))))
                            {
                                // Mark for post-processing type conversion (can't change type in-place)
                                vfQueue.Dequeue();
                                var replacement = CreateFinalFunctionReplacement(preserved.ExprType, preserved.StackNode, expr.Parameters)
                                    ?? new EX_FinalFunction()
                                    {
                                        StackNode = preserved.StackNode,
                                        Parameters = expr.Parameters
                                    };
                                RestoreUbergraphOffset(preserved, ((EX_FinalFunction)replacement).Parameters);
                                _pendingExprReplacements[expr] = replacement;
                                break;
                            }
                            else if (preserved.VirtualFunctionName != null)
                            {
                                vfQueue.Dequeue();
                                // Check for VirtualFunction/LocalVirtualFunction type mismatch
                                if (preserved.ExprType != expr.GetType())
                                {
                                    KismetExpression? vfReplacement = null;
                                    if (preserved.ExprType == typeof(EX_VirtualFunction))
                                    {
                                        vfReplacement = new EX_VirtualFunction()
                                        {
                                            VirtualFunctionName = preserved.VirtualFunctionName,
                                            Parameters = expr.Parameters
                                        };
                                    }
                                    else if (preserved.ExprType == typeof(EX_LocalVirtualFunction))
                                    {
                                        vfReplacement = new EX_LocalVirtualFunction()
                                        {
                                            VirtualFunctionName = preserved.VirtualFunctionName,
                                            Parameters = expr.Parameters
                                        };
                                    }
                                    if (vfReplacement != null)
                                    {
                                        _pendingExprReplacements[expr] = vfReplacement;
                                        break;
                                    }
                                }
                                // Same type, just use preserved name
                                expr.VirtualFunctionName = preserved.VirtualFunctionName;
                                break;
                            }
                        }
                        if (expr.VirtualFunctionName != null)
                            FixName(ref expr.VirtualFunctionName);
                        break;
                    }

                case EX_CrossInterfaceCast expr:
                    FixPackageIndex(ref expr.ClassPtr);
                    break;

                case EX_DynamicCast expr:
                    FixPackageIndex(ref expr.ClassPtr);
                    break;

                case EX_FinalFunction expr:
                    // Try to reuse preserved original StackNode and exact expression type
                    // to maintain correct import/export references and call opcode semantics.
                    {
                        var funcName = GetFinalFunctionName(expr.StackNode);
                        if (funcName != null && _originalFunctionCalls.TryGetValue(funcName, out var stackQueue) && stackQueue.Count > 0)
                        {
                            var preserved = stackQueue.Peek();
                            if (preserved.StackNode != null)
                            {
                                stackQueue.Dequeue();
                                expr.StackNode = preserved.StackNode;
                                RestoreUbergraphOffset(preserved, expr.Parameters);

                                // Check for FinalFunction subtype mismatch (e.g., EX_LocalFinalFunction).
                                // This also covers calls whose StackNode has already resolved to a concrete
                                // FPackageIndex before this fixup runs.
                                if (preserved.ExprType != expr.GetType())
                                {
                                    var replacement = CreateFinalFunctionReplacement(preserved.ExprType, expr.StackNode, expr.Parameters);
                                    if (replacement != null)
                                    {
                                        _pendingExprReplacements[expr] = replacement;
                                    }
                                }
                                break;
                            }
                        }
                    }
                    FixPackageIndex(ref expr.StackNode);
                    break;

                case EX_InterfaceToObjCast expr:
                    FixPackageIndex(ref expr.ClassPtr);
                    break;

                case EX_MetaCast expr:
                    FixPackageIndex(ref expr.ClassPtr);
                    break;

                case EX_ObjectConst expr:
                    expr.Value = FixPackageIndex(expr.Value);
                    break;

                case EX_ObjToInterfaceCast expr:
                    FixPackageIndex(ref expr.ClassPtr);
                    break;

                case EX_SetArray expr:
                    if (Package.ObjectVersion < ObjectVersion.VER_UE4_CHANGE_SETARRAY_BYTECODE)
                        FixPackageIndex(ref expr.ArrayInnerProp);
                    break;

                case EX_StructConst expr:
                    FixPackageIndex(ref expr.Struct);
                    break;

                case EX_TextConst expr:
                    if (expr.Value?.StringTableAsset != null)
                    {
                        expr.Value.StringTableAsset = FixPackageIndex(expr.Value.StringTableAsset);
                    }
                    break;

                case EX_ArrayConst expr:
                    FixPropertyPointer(ref expr.InnerProperty, PropertyPointerKind.ArrayInner);
                    break;

                case EX_ClassSparseDataVariable expr:
                    FixPropertyPointer(ref expr.Variable);
                    break;

                case EX_Context expr:
                    FixPropertyPointer(ref expr.RValuePointer);
                    // Restore original context subtype (EX_ClassContext, EX_Context_FailSilent)
                    // or replace with EX_StructMemberContext if the compiler miscompiled it.
                    if (_preservedContextTypes.Count > 0)
                    {
                        var originalType = _preservedContextTypes.Dequeue();
                        if (originalType == typeof(EX_StructMemberContext))
                        {
                            // The compiler incorrectly emitted EX_Context instead of EX_StructMemberContext.
                            // Convert back: StructMemberExpression = RValuePointer, StructExpression = ObjectExpression.
                            var replacement = new EX_StructMemberContext()
                            {
                                StructMemberExpression = expr.RValuePointer,
                                StructExpression = expr.ObjectExpression
                            };
                            _pendingExprReplacements[expr] = replacement;

                            // Mark the ContextExpression subtree as skippable since it will be discarded.
                            // This prevents the extra property pointer references from consuming
                            // entries from the preserved path queue.
                            if (expr.ContextExpression != null)
                            {
                                foreach (var childExpr in expr.ContextExpression.Flatten())
                                {
                                    _skipPropertyFixupExpressions.Add(childExpr);
                                }
                            }
                        }
                        else if (expr.GetType() != originalType && originalType.IsSubclassOf(typeof(EX_Context)))
                        {
                            KismetExpression? replacement = null;
                            if (originalType == typeof(EX_ClassContext))
                            {
                                replacement = new EX_ClassContext()
                                {
                                    ObjectExpression = expr.ObjectExpression,
                                    Offset = expr.Offset,
                                    RValuePointer = expr.RValuePointer,
                                    ContextExpression = expr.ContextExpression
                                };
                            }
                            else if (originalType == typeof(EX_Context_FailSilent))
                            {
                                replacement = new EX_Context_FailSilent()
                                {
                                    ObjectExpression = expr.ObjectExpression,
                                    Offset = expr.Offset,
                                    RValuePointer = expr.RValuePointer,
                                    ContextExpression = expr.ContextExpression
                                };
                            }
                            if (replacement != null)
                                _pendingExprReplacements[expr] = replacement;
                        }
                    }
                    break;

                case EX_DefaultVariable expr:
                    FixPropertyPointer(ref expr.Variable);
                    break;

                case EX_InstanceVariable expr:
                    FixPropertyPointer(ref expr.Variable);
                    break;

                case EX_Let expr:
                    FixLetValuePropertyPointer(ref expr.Value);
                    break;

                case EX_LetValueOnPersistentFrame expr:
                    FixPropertyPointer(ref expr.DestinationProperty);
                    break;

                case EX_LocalOutVariable expr:
                    FixPropertyPointer(ref expr.Variable);
                    break;

                case EX_LocalVariable expr:
                    FixPropertyPointer(ref expr.Variable);
                    break;

                case EX_MapConst expr:
                    FixPropertyPointer(ref expr.KeyProperty);
                    FixPropertyPointer(ref expr.ValueProperty);
                    break;

                case EX_PropertyConst expr:
                    FixPropertyPointer(ref expr.Property);
                    break;

                case EX_SetConst expr:
                    FixPropertyPointer(ref expr.InnerProperty);
                    break;

                case EX_StructMemberContext expr:
                    FixPropertyPointer(ref expr.StructMemberExpression);
                    // Consume the preserved context type entry when the compiler
                    // correctly emitted EX_StructMemberContext (keeps queue in sync).
                    if (_preservedContextTypes.Count > 0 &&
                        _preservedContextTypes.Peek() == typeof(EX_StructMemberContext))
                    {
                        _preservedContextTypes.Dequeue();
                    }
                    break;

                default:
                    break;
            }
            totalCount++;
            if (baseExpr is EX_LocalVariable lv && lv.Variable is IntermediatePropertyPointer)
                intermediateCount++;
            else if (baseExpr is EX_InstanceVariable iv && iv.Variable is IntermediatePropertyPointer)
                intermediateCount++;
        }
        Console.WriteLine($"[GetFixedBytecode] total={totalCount}, intermediate={intermediateCount}");

        // Apply any pending expression type replacements (e.g., VirtualFunction -> FinalFunction)
        if (_pendingExprReplacements.Count > 0)
        {
            ApplyExpressionReplacements(bytecode);
            _pendingExprReplacements.Clear();
        }

        // Restore preserved bytecode offset values after all fixups.
        // This is needed because expression type replacements may change serialized sizes,
        // invalidating offsets that were calculated during compilation.
        foreach (var fixedExpr in bytecode.Flatten())
        {
            switch (fixedExpr)
            {
                case EX_PushExecutionFlow push when _preservedPushingAddresses.Count > 0:
                    push.PushingAddress = _preservedPushingAddresses.Dequeue();
                    break;
                case EX_Jump jump when _preservedJumpOffsets.Count > 0:
                    jump.CodeOffset = _preservedJumpOffsets.Dequeue();
                    break;
                case EX_JumpIfNot jumpIfNot when _preservedJumpIfNotOffsets.Count > 0:
                    jumpIfNot.CodeOffset = _preservedJumpIfNotOffsets.Dequeue();
                    break;
                case EX_Skip skip when _preservedSkipOffsets.Count > 0:
                    skip.CodeOffset = _preservedSkipOffsets.Dequeue();
                    break;
                case EX_SwitchValue sv when _preservedSwitchEndGotoOffsets.Count > 0:
                    sv.EndGotoOffset = _preservedSwitchEndGotoOffsets.Dequeue();
                    break;
                case EX_Context ctx when _preservedContextOffsets.Count > 0:
                    ctx.Offset = _preservedContextOffsets.Dequeue();
                    break;
            }
        }

        return bytecode;
    }

    /// <summary>
    /// Recursively walks the bytecode tree and replaces expressions marked in _pendingExprReplacements.
    /// </summary>
    private void ApplyExpressionReplacements(KismetExpression[] expressions)
    {
        for (int i = 0; i < expressions.Length; i++)
        {
            if (_pendingExprReplacements.TryGetValue(expressions[i], out var replacement))
            {
                expressions[i] = replacement;
            }
            ApplyExpressionReplacementsInChildren(expressions[i]);
        }
    }

    private void ApplyExpressionReplacementsInChildren(KismetExpression expr)
    {
        // Handle ALL expression types that contain sub-expressions.
        // This must mirror the visitor in KismetExpressionVisitor.cs to ensure
        // replacements can be applied at any level of the expression tree.
        switch (expr)
        {
            // Function calls with Parameters arrays
            // Note: EX_CallMath extends EX_FinalFunction, EX_LocalFinalFunction extends EX_FinalFunction,
            // EX_LocalVirtualFunction extends EX_VirtualFunction - all handled by base class cases.
            case EX_CallMulticastDelegate cmd:
                ReplaceIfNeeded(ref cmd.Delegate);
                ApplyExpressionReplacements(cmd.Parameters);
                break;
            case EX_FinalFunction ff:
                ApplyExpressionReplacements(ff.Parameters);
                break;
            case EX_VirtualFunction vf:
                ApplyExpressionReplacements(vf.Parameters);
                break;

            // Context expressions
            case EX_Context ctx:
                ReplaceIfNeeded(ref ctx.ObjectExpression);
                ReplaceIfNeeded(ref ctx.ContextExpression);
                break;
            // EX_ClassContext and EX_Context_FailSilent extend EX_Context, handled above
            case EX_StructMemberContext smc:
                ReplaceIfNeeded(ref smc.StructExpression);
                break;
            case EX_InterfaceContext ic:
                ReplaceIfNeeded(ref ic.InterfaceValue);
                break;

            // Let expressions (assignment) - both Variable and Expression sides
            case EX_Let let:
                ReplaceIfNeeded(ref let.Variable);
                ReplaceIfNeeded(ref let.Expression);
                break;
            case EX_LetObj letObj:
                ReplaceIfNeeded(ref letObj.VariableExpression);
                ReplaceIfNeeded(ref letObj.AssignmentExpression);
                break;
            case EX_LetBool letBool:
                ReplaceIfNeeded(ref letBool.VariableExpression);
                ReplaceIfNeeded(ref letBool.AssignmentExpression);
                break;
            case EX_LetWeakObjPtr letWeak:
                ReplaceIfNeeded(ref letWeak.VariableExpression);
                ReplaceIfNeeded(ref letWeak.AssignmentExpression);
                break;
            case EX_LetDelegate letDel:
                ReplaceIfNeeded(ref letDel.VariableExpression);
                ReplaceIfNeeded(ref letDel.AssignmentExpression);
                break;
            case EX_LetMulticastDelegate letMcDel:
                ReplaceIfNeeded(ref letMcDel.VariableExpression);
                ReplaceIfNeeded(ref letMcDel.AssignmentExpression);
                break;
            case EX_LetValueOnPersistentFrame letPf:
                ReplaceIfNeeded(ref letPf.AssignmentExpression);
                break;

            // Cast expressions
            case EX_PrimitiveCast pc:
                ReplaceIfNeeded(ref pc.Target);
                break;
            case EX_ObjToInterfaceCast otic:
                ReplaceIfNeeded(ref otic.Target);
                break;
            case EX_CrossInterfaceCast cic:
                ReplaceIfNeeded(ref cic.Target);
                break;
            case EX_InterfaceToObjCast itoc:
                ReplaceIfNeeded(ref itoc.Target);
                break;
            case EX_MetaCast mc:
                ReplaceIfNeeded(ref mc.TargetExpression);
                break;
            case EX_DynamicCast dc:
                ReplaceIfNeeded(ref dc.TargetExpression);
                break;

            // Control flow
            case EX_Return ret:
                ReplaceIfNeeded(ref ret.ReturnExpression);
                break;
            case EX_JumpIfNot jin:
                ReplaceIfNeeded(ref jin.BooleanExpression);
                break;
            case EX_Assert asrt:
                ReplaceIfNeeded(ref asrt.AssertExpression);
                break;
            case EX_PopExecutionFlowIfNot pefin:
                ReplaceIfNeeded(ref pefin.BooleanExpression);
                break;
            case EX_ComputedJump cj:
                ReplaceIfNeeded(ref cj.CodeOffsetExpression);
                break;
            case EX_SwitchValue sv:
                ReplaceIfNeeded(ref sv.IndexTerm);
                ReplaceIfNeeded(ref sv.DefaultTerm);
                if (sv.Cases != null)
                {
                    for (int ci = 0; ci < sv.Cases.Length; ci++)
                    {
                        var caseItem = sv.Cases[ci];
                        ReplaceIfNeeded(ref caseItem.CaseIndexValueTerm);
                        ReplaceIfNeeded(ref caseItem.CaseTerm);
                        sv.Cases[ci] = caseItem;
                    }
                }
                break;

            // Delegate operations
            case EX_AddMulticastDelegate amd:
                ReplaceIfNeeded(ref amd.Delegate);
                ReplaceIfNeeded(ref amd.DelegateToAdd);
                break;
            case EX_RemoveMulticastDelegate rmd:
                ReplaceIfNeeded(ref rmd.Delegate);
                ReplaceIfNeeded(ref rmd.DelegateToAdd);
                break;
            case EX_ClearMulticastDelegate cmd2:
                ReplaceIfNeeded(ref cmd2.DelegateToClear);
                break;
            case EX_BindDelegate bd:
                ReplaceIfNeeded(ref bd.Delegate);
                ReplaceIfNeeded(ref bd.ObjectTerm);
                break;

            // Collection expressions with element arrays
            case EX_SetArray sa:
                if (sa.AssigningProperty != null)
                    ReplaceIfNeeded(ref sa.AssigningProperty);
                ApplyExpressionReplacements(sa.Elements);
                break;
            case EX_SetSet ss:
                ReplaceIfNeeded(ref ss.SetProperty);
                ApplyExpressionReplacements(ss.Elements);
                break;
            case EX_SetMap sm:
                ReplaceIfNeeded(ref sm.MapProperty);
                ApplyExpressionReplacements(sm.Elements);
                break;
            case EX_ArrayConst ac:
                ApplyExpressionReplacements(ac.Elements);
                break;
            case EX_SetConst sc:
                ApplyExpressionReplacements(sc.Elements);
                break;
            case EX_MapConst mc2:
                ApplyExpressionReplacements(mc2.Elements);
                break;
            case EX_StructConst stc:
                if (stc.Value != null)
                    ApplyExpressionReplacements(stc.Value);
                break;

            // Array operations
            case EX_ArrayGetByRef agbr:
                ReplaceIfNeeded(ref agbr.ArrayVariable);
                ReplaceIfNeeded(ref agbr.ArrayIndex);
                break;

            // Misc expressions with sub-expressions
            // Note: Value is a property, not a field, so we need a temp variable for ref
            case EX_SoftObjectConst soc:
                {
                    var val = soc.Value;
                    ReplaceIfNeeded(ref val);
                    soc.Value = val;
                }
                break;
            case EX_FieldPathConst fpc:
                {
                    var val = fpc.Value;
                    ReplaceIfNeeded(ref val);
                    fpc.Value = val;
                }
                break;

            default:
                // Leaf expressions (constants, variables, jumps, etc.) have no sub-expressions
                break;
        }

        void ReplaceIfNeeded(ref KismetExpression subExpr)
        {
            if (subExpr != null && _pendingExprReplacements.TryGetValue(subExpr, out var repl))
            {
                subExpr = repl;
            }
            if (subExpr != null)
            {
                ApplyExpressionReplacementsInChildren(subExpr);
            }
        }
    }

    protected FunctionExport CreateFunctionExport(CompiledFunctionContext context)
    {
        var coreUObjectImport = EnsurePackageImported("/Script/CoreUObject");
        var functionClassImport = EnsureObjectImported(coreUObjectImport, "Function", "Class");
        var functionDefaultObjectImport = EnsureObjectImported(coreUObjectImport, "Default__Function", "Function");
        var className2 = context.Symbol?.DeclaringClass?.Name;
        var classExport = className2 != null ? Package.FindClassExportByName(className2) : null;

        var ownerIndex = classExport != null ?
            FPackageIndex.FromExport(Package.Exports.IndexOf(classExport)) :
            new FPackageIndex(0);

        var createBeforeCreateDependencies = new List<FPackageIndex>();
        if (!ownerIndex.IsNull())
            createBeforeCreateDependencies.Add(ownerIndex);

        // TODO: override
        var baseFunctionClassIndex = new FPackageIndex(0);
        var baseFunctionIndex = new FPackageIndex(0);
        var ubergraphFunction = Package.GetUbergraphFunction();
        var ubergraphFunctionIndex = ubergraphFunction != null ?
            FPackageIndex.FromExport(Package.Exports.IndexOf(ubergraphFunction)) :
            new FPackageIndex(0);

        var export = new FunctionExport()
        {
            FunctionFlags = context.Symbol?.Flags ?? default,
            SuperStruct = baseFunctionIndex,
            Children = Array.Empty<FPackageIndex>(),
            LoadedProperties = Array.Empty<FProperty>(),
            ScriptBytecode = null,
            ScriptBytecodeSize = 0,
            ScriptBytecodeRaw = null,
            Field = new() { Next = null },
            Data = new(),
            ObjectName = new(Package, context.Symbol?.Name ?? string.Empty),
            ObjectFlags = EObjectFlags.RF_Public,
            SerialSize = 0xDEADBEEF,
            SerialOffset = 0xDEADBEEF,
            bForcedExport = false,
            bNotForClient = false,
            bNotForServer = false,
            PackageGuid = Guid.Empty,
            IsInheritedInstance = false,
            PackageFlags = EPackageFlags.PKG_None,
            bNotAlwaysLoadedForEditorGame = false,
            bIsAsset = false,
            GeneratePublicHash = false,
            SerializationBeforeSerializationDependencies = new(),
            CreateBeforeSerializationDependencies = new() { /*ubergraphFunctionIndex*/ },
            SerializationBeforeCreateDependencies = new(),
            CreateBeforeCreateDependencies = createBeforeCreateDependencies,
            Extras = new byte[] { 0, 0, 0, 0, 0, 0, 0, 0 },
            OuterIndex = ownerIndex,
            ClassIndex = functionClassImport,
            SuperIndex = baseFunctionIndex,
            TemplateIndex = functionDefaultObjectImport,
        };
        Package.Exports.Add(export);

        if (Package.GetCustomVersion<FCoreObjectVersion>() >= FCoreObjectVersion.FProperties)
        {
            var properties = context.Variables
                .Select(x => CreatePropertyAsFProperty(x.Symbol))
                .ToList();
            var propertyData = properties
                .Select(x => x.Property)
                .ToArray();
            var exportProperties = properties
                .Where(x => !x.ExportIndex.IsNull())
                .Select(x => x.ExportIndex)
                .ToArray();

            export.LoadedProperties = (export.LoadedProperties ?? Array.Empty<FProperty>())
                .Concat(propertyData)
                .ToArray();
            export.Children = (export.Children ?? Array.Empty<FPackageIndex>())
                .Concat(exportProperties)
                .ToArray();
            export.SerializationBeforeSerializationDependencies.AddRange(exportProperties);
        }
        else
        {
            var children = context.Variables
                .Select(x => CreatePackageIndexForSymbol(x.Symbol))
                .ToArray();
            export.Children = (export.Children ?? Array.Empty<FPackageIndex>())
                .Concat(children)
                .ToArray();
            export.SerializationBeforeSerializationDependencies.AddRange(children);
        }

        if (!baseFunctionClassIndex.IsNull())
        {
            export.CreateBeforeSerializationDependencies.Insert(0, baseFunctionClassIndex);
        }
        if (!baseFunctionIndex.IsNull())
        {
            export.SerializationBeforeSerializationDependencies.Insert(0, baseFunctionIndex);
            export.CreateBeforeCreateDependencies.Add(baseFunctionIndex);
        }

        var exportIndex = FPackageIndex.FromExport(Package.Exports.IndexOf(export));
        if (classExport != null)
        {
            classExport.FuncMap[export.ObjectName] = exportIndex;
            classExport.Children = (classExport.Children ?? Array.Empty<FPackageIndex>())
                .Concat(new[] { exportIndex })
                .ToArray();
            classExport.CreateBeforeSerializationDependencies.Add(exportIndex);
        }

        return export;
    }

    protected ClassExport CreateClassExport(CompiledClassContext classContext)
    {
        var scriptEnginePackageIndex = EnsurePackageImported("/Script/Engine");
        var blueprintGeneratedClassObjectIndex = EnsureObjectImported(scriptEnginePackageIndex, "BlueprintGeneratedClass", "Class");
        var blueprintGeneratedClassDefaultObjectIndex = EnsureObjectImported(blueprintGeneratedClassObjectIndex, "Default__BlueprintGeneratedClass", "BlueprintGeneratedClass");

        var scriptCoreUObjectPackageIndex = EnsurePackageImported("/Script/CoreUObject");
        var objectObjectIndex = EnsureObjectImported(scriptCoreUObjectPackageIndex, "Object", "Class");
        var objectDefaultObjectIndex = EnsureObjectImported(objectObjectIndex, "Default__Object", "Object");

        var classDefaultObjectIndex = new FPackageIndex(0);
        var baseClassObjectIndex = objectObjectIndex;
        var baseClassDefaultObjectIndex = objectDefaultObjectIndex;

        var serializationBeforeSerializationDependencies = new List<FPackageIndex>();
        if (!baseClassObjectIndex.IsNull())
        {
            serializationBeforeSerializationDependencies.Add(baseClassObjectIndex);
            serializationBeforeSerializationDependencies.Add(baseClassDefaultObjectIndex);
        }

        var createBeforeCreateDependencies = new List<FPackageIndex>();
        if (!baseClassObjectIndex.IsNull())
            createBeforeCreateDependencies.Add(baseClassObjectIndex);

        var classExport = new ClassExport()
        {
            FuncMap = new(),
            ClassFlags = EClassFlags.CLASS_Parsed | EClassFlags.CLASS_ReplicationDataIsSetUp | EClassFlags.CLASS_CompiledFromBlueprint | EClassFlags.CLASS_HasInstancedReference,
            ClassWithin = objectObjectIndex, // -11
            ClassConfigName = AddName("Engine"),
            Interfaces = Array.Empty<SerializedInterfaceReference>(),
            ClassGeneratedBy = new FPackageIndex(0),
            bDeprecatedForceScriptOrder = false,
            bCooked = true,
            ClassDefaultObject = classDefaultObjectIndex,
            SuperStruct = baseClassObjectIndex,
            Children = Array.Empty<FPackageIndex>(),
            LoadedProperties = Array.Empty<FProperty>(),
            ScriptBytecode = Array.Empty<KismetExpression>(),
            ScriptBytecodeSize = 0,
            ScriptBytecodeRaw = null,
            Field = new() { Next = null },
            /*
             * TODO
             *       "Data": [
                    {
                      "$type": "UAssetAPI.PropertyTypes.Objects.ObjectPropertyData, UAssetAPI",
                      "Name": "SimpleConstructionScript",
                      "DuplicationIndex": 0,
                      "Value": 382
                    },
                    {
                      "$type": "UAssetAPI.PropertyTypes.Objects.ObjectPropertyData, UAssetAPI",
                      "Name": "InheritableComponentHandler",
                      "DuplicationIndex": 0,
                      "Value": 184
                    },
                    {
                      "$type": "UAssetAPI.PropertyTypes.Objects.ObjectPropertyData, UAssetAPI",
                      "Name": "UberGraphFramePointerProperty",
                      "DuplicationIndex": 0,
                      "Value": 1
                    },
                    {
                      "$type": "UAssetAPI.PropertyTypes.Objects.ObjectPropertyData, UAssetAPI",
                      "Name": "UberGraphFunction",
                      "DuplicationIndex": 0,
                      "Value": 6
                    }
                  ],
             */
            Data = new(),
            ObjectName = AddName(classContext.Symbol.Name),
            ObjectFlags = EObjectFlags.RF_Public | EObjectFlags.RF_Transactional,
            SerialSize = 0xDEADBEEF,
            SerialOffset = 0xDEADBEEF,
            bForcedExport = false,
            bNotForClient = false,
            bNotForServer = false,
            PackageGuid = Guid.Empty,
            IsInheritedInstance = false,
            PackageFlags = EPackageFlags.PKG_None,
            bNotAlwaysLoadedForEditorGame = false,
            bIsAsset = false,
            GeneratePublicHash = false,
            /* 
             * TODO
             * - Base class
             * - Base class default object
             * - Class properties
             */
            SerializationBeforeSerializationDependencies = serializationBeforeSerializationDependencies,
            /*
             * TODO
             * - Function exports
             */
            CreateBeforeSerializationDependencies = new(),
            SerializationBeforeCreateDependencies = new() { blueprintGeneratedClassObjectIndex, blueprintGeneratedClassDefaultObjectIndex },
            CreateBeforeCreateDependencies = createBeforeCreateDependencies,
            Extras = Array.Empty<byte>(),
            OuterIndex = new FPackageIndex(0),
            ClassIndex = blueprintGeneratedClassObjectIndex, // -13
            SuperIndex = new FPackageIndex(0), // -2
            TemplateIndex = blueprintGeneratedClassDefaultObjectIndex,
        };
        Package.Exports.Add(classExport);
        return classExport;
    }

    protected TExport? FindChildExport<TExport>(StructExport? parent, string name) where TExport : Export
    {
        IEnumerable<Export> selection;
        if (parent != null && parent.Children != null && parent.Children.Length > 0)
        {
            // Use Children array when populated
            selection = parent.Children
                .Where(x => x.IsExport())
                .Select(x => x.ToExport(Package));
        }
        else if (parent != null)
        {
            // Fall back to searching by OuterIndex when Children is empty
            // This handles metadata-based asset creation where Children isn't populated
            var parentIndex = FPackageIndex.FromExport(Package.Exports.IndexOf(parent));
            selection = Package.Exports
                .Where(x => x.OuterIndex.Index == parentIndex.Index);
        }
        else
        {
            selection = Package.Exports ?? Enumerable.Empty<Export>();
        }
        return selection
            .Where(x => x is TExport && x.ObjectName.ToString() == name)
            .Cast<TExport>()
            .SingleOrDefault();
    }

    protected virtual void LinkCompiledFunction(CompiledFunctionContext functionContext)
    {
        var classExport = functionContext.Symbol.DeclaringClass != null ?
            FindChildExport<ClassExport>(null, functionContext.Symbol!.DeclaringClass!.Name) :
            null;

        var functionExport = FindChildExport<FunctionExport>(classExport, functionContext.Symbol.Name);

        // Preserve original property owners from existing bytecode before modifying
        if (functionExport != null && functionExport.ScriptBytecode != null)
        {
            PreserveOriginalPropertyOwners(functionExport.ScriptBytecode);
        }

        if (functionExport == null)
            functionExport = CreateFunctionExport(functionContext);

        foreach (var variableContext in functionContext.Variables)
        {
            if (SerializeLoadedProperties)
            {
                if (!functionExport.LoadedProperties.Any(x => x.Name.ToString() == variableContext.Symbol.Name))
                    functionExport.LoadedProperties = (functionExport.LoadedProperties ?? Array.Empty<FProperty>())
                        .Concat(new[] { CreateFProperty(variableContext.Symbol) })
                        .ToArray();
            }
            else
            {
                var export = FindChildExport<PropertyExport>(functionExport, variableContext.Symbol.Name);
                (var index, var propExport) = CreatePropertyAsPropertyExport(variableContext.Symbol);
                functionExport!.Children = (functionExport.Children ?? Array.Empty<FPackageIndex>())
                    .Concat(new[] { index })
                    .ToArray();
            }
        }

        functionExport!.ScriptBytecode = GetFixedBytecode(functionContext.Bytecode);

        // Clear the preserved owners after linking this function
        ClearPreservedPropertyOwners();
    }

    /// <summary>
    /// Clears the preserved property owners dictionary and K2Node tracking
    /// </summary>
    protected void ClearPreservedPropertyOwners()
    {
        _originalPropertyFieldPaths.Clear();
        _k2NodeVariablesWithEmptyPath.Clear();
        _originalFunctionCalls.Clear();
        _pendingExprReplacements.Clear();
        _preservedContextTypes.Clear();
        _preservedStructMemberContexts.Clear();
        _skipPropertyFixupExpressions.Clear();
        _preservedContextOffsets.Clear();
        _preservedPushingAddresses.Clear();
        _preservedJumpOffsets.Clear();
        _preservedJumpIfNotOffsets.Clear();
        _preservedSkipOffsets.Clear();
        _preservedSwitchEndGotoOffsets.Clear();
        _preservedSwitchCaseOffsets.Clear();
    }

    /// <summary>
    /// Extracts and preserves ResolvedOwner values from original bytecode before it's replaced.
    /// This allows us to maintain correct property ownership when recompiling.
    /// </summary>
    protected void PreserveOriginalPropertyOwners(KismetExpression[] originalBytecode)
    {
        foreach (var expr in originalBytecode.Flatten())
        {
            KismetPropertyPointer? pointer = expr switch
            {
                EX_ArrayConst arrayConst => arrayConst.InnerProperty,
                EX_ClassSparseDataVariable classSparseDataVariable => classSparseDataVariable.Variable,
                EX_Context ctx => ctx.RValuePointer,
                EX_DefaultVariable defaultVariable => defaultVariable.Variable,
                EX_InstanceVariable instanceVariable => instanceVariable.Variable,
                EX_Let let => let.Value,
                EX_LetValueOnPersistentFrame letValueOnPersistentFrame => letValueOnPersistentFrame.DestinationProperty,
                EX_LocalOutVariable localOutVariable => localOutVariable.Variable,
                EX_LocalVariable localVariable => localVariable.Variable,
                EX_PropertyConst propertyConst => propertyConst.Property,
                EX_SetConst setConst => setConst.InnerProperty,
                EX_StructMemberContext structMemberContext => structMemberContext.StructMemberExpression,
                EX_MapConst mapConst => mapConst.KeyProperty, // Handle both key and value separately
                _ => null
            };
            var pointerKind = expr switch
            {
                EX_ArrayConst => PropertyPointerKind.ArrayInner,
                _ => PropertyPointerKind.Default
            };

            // Preserve properties with non-empty Path
            if (pointer?.New != null && pointer.New.Path.Length > 0)
            {
                var propertyName = pointer.New.Path[0].ToString();
                TryPreserveFieldPath(propertyName, pointer.New, pointerKind);
            }
            // Track variables whose property pointers had empty Path/owner in the original bytecode.
            // These typically appear as EX_Let.Value where the matching Variable property
            // pointer (EX_LocalVariable / EX_InstanceVariable) carries the actual name.
            else if (pointer?.New != null && pointer.New.Path.Length == 0 && pointer.New.ResolvedOwner.Index == 0)
            {
                switch (expr)
                {
                    case EX_Let letExpr:
                        if (letExpr.Variable is EX_LocalVariable localVar &&
                            localVar.Variable?.New != null &&
                            localVar.Variable.New.Path.Length > 0)
                        {
                            var name = localVar.Variable.New.Path[0].ToString();
                            _k2NodeVariablesWithEmptyPath.Add(name);
                        }
                        else if (letExpr.Variable is EX_InstanceVariable instVar &&
                                 instVar.Variable?.New != null &&
                                 instVar.Variable.New.Path.Length > 0)
                        {
                            var name = instVar.Variable.New.Path[0].ToString();
                            _k2NodeVariablesWithEmptyPath.Add(name);
                        }
                        break;
                }
            }

            // Handle MapConst value property separately
            if (expr is EX_MapConst mapConstValue && mapConstValue.ValueProperty?.New != null && mapConstValue.ValueProperty.New.Path.Length > 0)
            {
                var propertyName = mapConstValue.ValueProperty.New.Path[0].ToString();
                TryPreserveFieldPath(propertyName, mapConstValue.ValueProperty.New, PropertyPointerKind.Default);
            }

            // Preserve function call expression types and StackNode/VirtualFunctionName from original bytecode.
            // This handles EX_FinalFunction (and subclasses), EX_VirtualFunction (and subclasses).
            if (expr is EX_FinalFunction finalFunc && finalFunc.StackNode != null && !finalFunc.StackNode.IsNull())
            {
                string? funcName = null;
                if (finalFunc.StackNode.IsImport())
                    funcName = finalFunc.StackNode.ToImport(Package)?.ObjectName?.ToString();
                else if (finalFunc.StackNode.IsExport())
                    funcName = finalFunc.StackNode.ToExport(Package)?.ObjectName?.ToString();

                // Detect ubergraph entry point calls and preserve the offset
                int? ubergraphOffset = null;
                if (finalFunc.StackNode.IsExport())
                {
                    var targetExport = finalFunc.StackNode.ToExport(Package);
                    if (targetExport is FunctionExport funcExport && funcExport.IsUbergraphFunction() &&
                        finalFunc.Parameters.Length == 1 && finalFunc.Parameters[0] is EX_IntConst intConst)
                    {
                        ubergraphOffset = intConst.Value;
                    }
                }

                if (funcName != null)
                    EnqueueFunctionCall(funcName, new PreservedFunctionCall(expr.GetType(), finalFunc.StackNode, null, ubergraphOffset));
            }
            else if (expr is EX_VirtualFunction virtualFunc)
            {
                var funcName = virtualFunc.VirtualFunctionName?.ToString();
                if (funcName != null)
                    EnqueueFunctionCall(funcName, new PreservedFunctionCall(expr.GetType(), null, virtualFunc.VirtualFunctionName, null));
            }

            // Preserve original expression type for EX_Context subtypes
            // The compiler may downgrade EX_ClassContext/EX_Context_FailSilent to EX_Context
            if (expr is EX_Context)
            {
                _preservedContextTypes.Enqueue(expr.GetType());
            }

            // Preserve original EX_StructMemberContext expressions
            // The compiler may incorrectly compile these as EX_Context.
            // We also enqueue the type into _preservedContextTypes so that
            // the fixup loop can detect and replace miscompiled EX_Context nodes.
            if (expr is EX_StructMemberContext)
            {
                _preservedContextTypes.Enqueue(typeof(EX_StructMemberContext));
                _preservedStructMemberContexts.Enqueue(expr);
            }

            // Preserve bytecode offset values for restoration after expression type replacements
            switch (expr)
            {
                case EX_PushExecutionFlow push:
                    _preservedPushingAddresses.Enqueue(push.PushingAddress);
                    break;
                case EX_Jump jump:
                    _preservedJumpOffsets.Enqueue(jump.CodeOffset);
                    break;
                case EX_JumpIfNot jumpIfNot:
                    _preservedJumpIfNotOffsets.Enqueue(jumpIfNot.CodeOffset);
                    break;
                case EX_Skip skip:
                    _preservedSkipOffsets.Enqueue(skip.CodeOffset);
                    break;
                case EX_SwitchValue sv:
                    _preservedSwitchEndGotoOffsets.Enqueue(sv.EndGotoOffset);
                    break;
                case EX_Context ctx:
                    _preservedContextOffsets.Enqueue(ctx.Offset);
                    break;
            }
        }
    }

    private void EnqueueFunctionCall(string funcName, PreservedFunctionCall call)
    {
        if (!_originalFunctionCalls.TryGetValue(funcName, out var queue))
        {
            queue = new Queue<PreservedFunctionCall>();
            _originalFunctionCalls[funcName] = queue;
        }
        queue.Enqueue(call);
    }

    private string? GetFinalFunctionName(FPackageIndex? stackNode)
    {
        if (stackNode == null || stackNode.IsNull())
            return null;

        if (stackNode is IntermediatePackageIndex intermediate)
            return intermediate.Symbol.Name;

        if (stackNode.IsImport())
            return stackNode.ToImport(Package)?.ObjectName?.ToString();

        if (stackNode.IsExport())
            return stackNode.ToExport(Package)?.ObjectName?.ToString();

        return null;
    }

    private static EX_FinalFunction? CreateFinalFunctionReplacement(Type preservedType, FPackageIndex stackNode, KismetExpression[] parameters)
    {
        if (preservedType == typeof(EX_LocalFinalFunction))
        {
            return new EX_LocalFinalFunction()
            {
                StackNode = stackNode,
                Parameters = parameters
            };
        }

        if (preservedType == typeof(EX_FinalFunction))
        {
            return new EX_FinalFunction()
            {
                StackNode = stackNode,
                Parameters = parameters
            };
        }

        return null;
    }

    private static void RestoreUbergraphOffset(PreservedFunctionCall preserved, KismetExpression[] parameters)
    {
        if (preserved.UbergraphOffset.HasValue &&
            parameters.Length == 1 &&
            parameters[0] is EX_IntConst intParam)
        {
            intParam.Value = preserved.UbergraphOffset.Value;
        }
    }

    private void TryPreserveFieldPath(string propertyName, FFieldPath fieldPath, PropertyPointerKind kind)
    {
        var key = (kind, propertyName);
        var pathSegments = fieldPath.Path.Select(name => name.ToString()).ToArray();
        var preservedPath = new PreservedFieldPath(pathSegments, fieldPath.ResolvedOwner);

        if (!_originalPropertyFieldPaths.TryGetValue(key, out var queue))
        {
            queue = new Queue<PreservedFieldPath>();
            _originalPropertyFieldPaths[key] = queue;
        }
        queue.Enqueue(preservedPath);
    }

    protected abstract FPackageIndex EnsurePackageImported(string objectName, bool bImportOptional = false);

    protected abstract FPackageIndex EnsureObjectImported(FPackageIndex parent, string objectName, string className, bool bImportOptional = false);

    protected abstract FPackageIndex CreateProcedureImport(ProcedureSymbol symbol);

    protected abstract IEnumerable<(object ImportOrExport, FPackageIndex PackageIndex)> GetPackageIndexByLocalName(string name);

    protected abstract IEnumerable<(object ImportOrExport, FPackageIndex PackageIndex)> GetPackageIndexByFullName(string name);

    protected abstract T CreateDefaultAsset();

    public virtual PackageLinker<T> LinkCompiledScript(CompiledScriptContext scriptContext)
    {
        foreach (var functionContext in scriptContext.Functions)
        {
            LinkCompiledFunction(functionContext);
        }

        foreach (var classContext in scriptContext.Classes)
        {
            var classExport = FindChildExport<ClassExport>(null, classContext.Symbol.Name);
            if (classExport == null)
                classExport = CreateClassExport(classContext);

            foreach (var variableContext in classContext.Variables)
            {
                if (SerializeLoadedProperties)
                {
                    if (!classExport.LoadedProperties.Any(x => x.Name.ToString() == variableContext.Symbol.Name))
                        classExport.LoadedProperties = (classExport.LoadedProperties ?? Array.Empty<FProperty>())
                            .Concat(new[] { CreateFProperty(variableContext.Symbol) })
                            .ToArray();
                }
                else
                {
                    var export = FindChildExport<PropertyExport>(classExport, variableContext.Symbol.Name);
                    (var index, var propExport) = CreatePropertyAsPropertyExport(variableContext.Symbol);
                    classExport!.Children = (classExport.Children ?? Array.Empty<FPackageIndex>())
                        .Concat(new[] { index })
                        .ToArray();
                }
            }

            foreach (var functionContext in classContext.Functions)
            {
                LinkCompiledFunction(functionContext);
            }
        }

        return this;
    }

    public virtual T Build()
    {
        return Package;
    }

    [GeneratedRegex("_(\\d+)$")]
    private static partial Regex NameIdSuffix();
}
