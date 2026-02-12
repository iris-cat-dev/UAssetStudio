using KismetScript.Compiler.Compiler;
using KismetScript.Compiler.Compiler.Context;
using KismetScript.Utilities;
using KismetScript.Utilities.Metadata;
using System.Text.RegularExpressions;
using UAssetAPI;
using UAssetAPI.CustomVersions;
using UAssetAPI.ExportTypes;
using UAssetAPI.FieldTypes;
using UAssetAPI.Kismet.Bytecode;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;

namespace KismetScript.Linker;

public partial class UAssetLinker : PackageLinker<UAsset>
{
    private KmsMetadata? _metadata;
    private static KmsMetadata? _pendingMetadata;
    /// <summary>
    /// Tracks consumed export references for same-named exports.
    /// When multiple exports share the same name (e.g., OverclockShematicItem_0),
    /// this counter ensures each reference picks the next unconsumed instance.
    /// </summary>
    private readonly Dictionary<string, int> _exportReferenceCounter = new();

    public UAssetLinker()
    {
    }

    public UAssetLinker(UAsset asset) : base(asset)
    {
    }

    /// <summary>
    /// Creates a new UAssetLinker from metadata for standalone compilation.
    /// </summary>
    public UAssetLinker(KmsMetadata metadata) : base()
    {
        _metadata = metadata;
    }

    /// <summary>
    /// Creates a UAssetLinker from metadata (factory method).
    /// Object exports will be built from the CompiledScriptContext during linking.
    /// </summary>
    public static UAssetLinker FromMetadata(KmsMetadata metadata)
    {
        _pendingMetadata = metadata;
        var linker = new UAssetLinker(metadata);
        _pendingMetadata = null;
        return linker;
    }

    /// <summary>
    /// Creates a UAsset from metadata.
    /// Only infrastructure exports (class, CDO, functions) are created here.
    /// Object exports will be added during LinkCompiledScript from KMS data.
    /// Imports are NOT built here — they're derived from KMS import statements at link time.
    /// </summary>
    private UAsset CreateAssetFromMetadata(KmsMetadata metadata)
    {
        var asset = new UAsset()
        {
            LegacyFileVersion = metadata.Package.LegacyFileVersion,
            UsesEventDrivenLoader = metadata.Package.UsesEventDrivenLoader,
            Imports = new(),
            DependsMap = new(),
            SoftPackageReferenceList = null,
            AssetRegistryData = new byte[] { 0, 0, 0, 0 },
            ValorantGarbageData = null,
            Generations = new(),
            PackageGuid = metadata.Package.Guid != null ? Guid.Parse(metadata.Package.Guid) : Guid.NewGuid(),
            RecordedEngineVersion = new()
            {
                Major = 0,
                Minor = 0,
                Patch = 0,
                Changelist = 0,
                Branch = null
            },
            RecordedCompatibleWithEngineVersion = new()
            {
                Major = 0,
                Minor = 0,
                Patch = 0,
                Changelist = 0,
                Branch = null
            },
            ChunkIDs = Array.Empty<int>(),
            PackageSource = metadata.Package.PackageSource ?? 4048401688,
            FolderName = new("None"),
            IsUnversioned = metadata.Package.IsUnversioned,
            FileVersionLicenseeUE = 0,
            ObjectVersion = ParseObjectVersion(metadata.EngineVersion.ObjectVersion),
            ObjectVersionUE5 = ParseObjectVersionUE5(metadata.EngineVersion.ObjectVersionUE5),
            CustomVersionContainer = BuildCustomVersions(metadata.EngineVersion.CustomVersions),
            Exports = new(),
            WorldTileInfo = null,
            PackageFlags = ParsePackageFlags(metadata.Package.Flags),
            BulkData = !string.IsNullOrEmpty(metadata.Package.BulkData)
                ? Convert.FromBase64String(metadata.Package.BulkData)
                : Array.Empty<byte>(),
            UseSeparateBulkDataFiles = metadata.Package.UsesEventDrivenLoader,
        };

        // Use reflection to set internal fields
        var bindingFlags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var additionalPackagesField = typeof(UAsset).GetField("AdditionalPackagesToCook", bindingFlags);
        additionalPackagesField?.SetValue(asset, new List<FString>());
        var worldTileInfoField = typeof(UAsset).GetField("doWeHaveWorldTileInfo", bindingFlags);
        worldTileInfoField?.SetValue(asset, false);

        // Initialize empty NameMap — names will be added dynamically as FNames are created
        asset.ClearNameIndexList();

        // Imports are NOT built here — they will be derived from KMS import statements
        // in BuildImportsFromKms() during LinkCompiledScript.

        // Build ONLY infrastructure exports (class, CDO, functions)
        BuildExports(asset, metadata.InfrastructureExports);

        // DependsMap and Generations will be finalized in Build()
        // after object exports are added from KMS
        for (int i = 0; i < asset.Exports.Count; i++)
        {
            asset.DependsMap.Add(Array.Empty<int>());
        }

        return asset;
    }

    private static ObjectVersion ParseObjectVersion(string version)
    {
        if (Enum.TryParse<ObjectVersion>(version, out var result))
            return result;
        return ObjectVersion.VER_UE4_FIX_WIDE_STRING_CRC;
    }

    private static ObjectVersionUE5 ParseObjectVersionUE5(string version)
    {
        if (Enum.TryParse<ObjectVersionUE5>(version, out var result))
            return result;
        return ObjectVersionUE5.UNKNOWN;
    }

    private static EPackageFlags ParsePackageFlags(List<string> flags)
    {
        EPackageFlags result = EPackageFlags.PKG_None;
        foreach (var flag in flags)
        {
            if (Enum.TryParse<EPackageFlags>(flag, out var f))
                result |= f;
        }
        return result;
    }

    private static List<CustomVersion> BuildCustomVersions(Dictionary<string, int> customVersions)
    {
        var result = new List<CustomVersion>();
        var knownVersions = new Dictionary<string, Guid>
        {
            { "FCoreObjectVersion", Guid.Parse("{375EC13C-06E4-48FB-B500-84F0262A717E}") },
            { "FEditorObjectVersion", Guid.Parse("{E4B068ED-F494-42E9-A231-DA0B2E46BB41}") },
            { "FFrameworkObjectVersion", Guid.Parse("{CFFC743F-43B0-4480-9391-14DF171D2073}") },
            { "FSequencerObjectVersion", Guid.Parse("{7B5AE74C-D270-4C10-A958-57980B212A5A}") },
            { "FAnimPhysObjectVersion", Guid.Parse("{29E575DD-E0A3-4627-9D10-D276232CDCEA}") },
            { "FFortniteMainBranchObjectVersion", Guid.Parse("{601D1886-AC64-4F84-AA16-D3DE0DEAC7D6}") },
            { "FReleaseObjectVersion", Guid.Parse("{9C54D522-A826-4FBE-9421-074661B482D0}") },
        };

        foreach (var cv in customVersions)
        {
            if (knownVersions.TryGetValue(cv.Key, out var key))
            {
                result.Add(new CustomVersion
                {
                    Key = key,
                    FriendlyName = cv.Key,
                    Version = cv.Value,
                    IsSerialized = false
                });
            }
        }

        return result;
    }

    private static void BuildExports(UAsset asset, List<ExportMetadata> exports)
    {
        foreach (var exportMeta in exports.OrderBy(x => x.Index))  // Order by index
        {
            Export export = CreateExportFromMetadata(asset, exportMeta);
            asset.Exports.Add(export);
        }
    }

    private static Export CreateExportFromMetadata(UAsset asset, ExportMetadata exportMeta)
    {
        Export export;

        // Determine export type based on className and flags
        if (exportMeta.ClassName.EndsWith("GeneratedClass") ||
            exportMeta.ClassName == "Class" ||
            exportMeta.ClassFlags != null)
        {
            var classExport = new ClassExport(asset, Array.Empty<byte>())
            {
                ClassFlags = ParseClassFlags(exportMeta.ClassFlags),
                LoadedProperties = RestoreLoadedProperties(asset, exportMeta.LoadedProperties),
                Data = new List<PropertyData>(),
                FuncMap = new(),
                Children = Array.Empty<FPackageIndex>(),
                Interfaces = Array.Empty<SerializedInterfaceReference>(),
                ClassConfigName = GetExistingFName(asset, "Engine"),
                ClassWithin = new FPackageIndex(0),
                ClassGeneratedBy = new FPackageIndex(0),
                ClassDefaultObject = new FPackageIndex(0),
                bCooked = true,
                Field = new UField { Next = new FPackageIndex(0) },
                SuperStruct = new FPackageIndex(exportMeta.SuperIndex ?? 0),
                // StructExport fields - required for serialization
                ScriptBytecode = Array.Empty<KismetExpression>(),
            };
            export = classExport;
        }
        else if (exportMeta.ClassName == "Function" || exportMeta.FunctionFlags != null)
        {
            var funcExport = new FunctionExport(asset, Array.Empty<byte>())
            {
                FunctionFlags = ParseFunctionFlags(exportMeta.FunctionFlags),
                ScriptBytecode = Array.Empty<KismetExpression>(),
                LoadedProperties = RestoreLoadedProperties(asset, exportMeta.LoadedProperties),
                Data = new List<PropertyData>(),
                Children = Array.Empty<FPackageIndex>(),
                Field = new UField { Next = new FPackageIndex(0) },
                SuperStruct = new FPackageIndex(exportMeta.SuperIndex ?? 0),
            };
            export = funcExport;
        }
        else
        {
            export = new NormalExport(asset, Array.Empty<byte>())
            {
                Data = new List<PropertyData>()
            };
        }

        export.ObjectName = GetExistingFName(asset, exportMeta.ObjectName);
        export.OuterIndex = new FPackageIndex(exportMeta.OuterIndex);
        export.SuperIndex = new FPackageIndex(exportMeta.SuperIndex ?? 0);
        export.TemplateIndex = new FPackageIndex(exportMeta.TemplateIndex ?? 0);
        export.ObjectFlags = ParseObjectFlags(exportMeta.ObjectFlags);

        // Restore export flags
        export.bNotAlwaysLoadedForEditorGame = exportMeta.BNotAlwaysLoadedForEditorGame ?? false;
        export.bIsAsset = exportMeta.BIsAsset ?? false;

        // Set ClassIndex based on className
        export.ClassIndex = FindOrCreateClassImport(asset, exportMeta.ClassName);

        // Restore Extras data
        if (!string.IsNullOrEmpty(exportMeta.Extras))
        {
            export.Extras = Convert.FromBase64String(exportMeta.Extras);
        }

        // Restore dependencies
        if (exportMeta.Dependencies != null)
        {
            if (exportMeta.Dependencies.SerializationBeforeSerialization != null)
            {
                export.SerializationBeforeSerializationDependencies = exportMeta.Dependencies.SerializationBeforeSerialization
                    .Select(i => new FPackageIndex(i)).ToList();
            }
            if (exportMeta.Dependencies.CreateBeforeSerialization != null)
            {
                export.CreateBeforeSerializationDependencies = exportMeta.Dependencies.CreateBeforeSerialization
                    .Select(i => new FPackageIndex(i)).ToList();
            }
            if (exportMeta.Dependencies.SerializationBeforeCreate != null)
            {
                export.SerializationBeforeCreateDependencies = exportMeta.Dependencies.SerializationBeforeCreate
                    .Select(i => new FPackageIndex(i)).ToList();
            }
            if (exportMeta.Dependencies.CreateBeforeCreate != null)
            {
                export.CreateBeforeCreateDependencies = exportMeta.Dependencies.CreateBeforeCreate
                    .Select(i => new FPackageIndex(i)).ToList();
            }
        }

        return export;
    }

    private static FPackageIndex FindOrCreateClassImport(UAsset asset, string className)
    {
        // First, look for existing import
        for (int i = 0; i < asset.Imports.Count; i++)
        {
            if (asset.Imports[i].ObjectName.ToString() == className)
            {
                return FPackageIndex.FromImport(i);
            }
        }

        // Also check exports (for self-referencing classes)
        for (int i = 0; i < asset.Exports.Count; i++)
        {
            if (asset.Exports[i].ObjectName.ToString() == className)
            {
                return FPackageIndex.FromExport(i);
            }
        }

        // Return null index if not found (will be resolved later during linking)
        return new FPackageIndex(0);
    }

    private static EObjectFlags ParseObjectFlags(List<string> flags)
    {
        EObjectFlags result = EObjectFlags.RF_NoFlags;
        foreach (var flag in flags)
        {
            if (Enum.TryParse<EObjectFlags>(flag, out var f))
                result |= f;
        }
        return result;
    }

    private static EFunctionFlags ParseFunctionFlags(List<string>? flags)
    {
        if (flags == null) return EFunctionFlags.FUNC_None;

        EFunctionFlags result = EFunctionFlags.FUNC_None;
        foreach (var flag in flags)
        {
            if (Enum.TryParse<EFunctionFlags>(flag, out var f))
                result |= f;
        }
        return result;
    }

    private static EClassFlags ParseClassFlags(List<string>? flags)
    {
        if (flags == null) return EClassFlags.CLASS_None;

        EClassFlags result = EClassFlags.CLASS_None;
        foreach (var flag in flags)
        {
            if (Enum.TryParse<EClassFlags>(flag, out var f))
                result |= f;
        }
        return result;
    }

    private static FProperty[] RestoreLoadedProperties(UAsset asset, List<FPropertyMetadata>? properties)
    {
        if (properties == null || properties.Count == 0)
            return Array.Empty<FProperty>();

        return properties.Select(p => RestoreFProperty(asset, p)).ToArray();
    }

    private static FProperty RestoreFProperty(UAsset asset, FPropertyMetadata meta)
    {
        FProperty prop = meta.SerializedType switch
        {
            "ClassProperty" => new FClassProperty
            {
                PropertyClass = new FPackageIndex(meta.PropertyClass ?? 0),
                MetaClass = new FPackageIndex(meta.MetaClass ?? 0)
            },
            "SoftClassProperty" => new FSoftClassProperty
            {
                PropertyClass = new FPackageIndex(meta.PropertyClass ?? 0),
                MetaClass = new FPackageIndex(meta.MetaClass ?? 0)
            },
            "ObjectProperty" => new FObjectProperty
            {
                PropertyClass = new FPackageIndex(meta.PropertyClass ?? 0)
            },
            "WeakObjectProperty" => new FWeakObjectProperty
            {
                PropertyClass = new FPackageIndex(meta.PropertyClass ?? 0)
            },
            "SoftObjectProperty" => new FSoftObjectProperty
            {
                PropertyClass = new FPackageIndex(meta.PropertyClass ?? 0)
            },
            "DelegateProperty" => new FDelegateProperty
            {
                SignatureFunction = new FPackageIndex(meta.SignatureFunction ?? 0)
            },
            "MulticastDelegateProperty" => new FMulticastDelegateProperty
            {
                SignatureFunction = new FPackageIndex(meta.SignatureFunction ?? 0)
            },
            "MulticastInlineDelegateProperty" => new FMulticastInlineDelegateProperty
            {
                SignatureFunction = new FPackageIndex(meta.SignatureFunction ?? 0)
            },
            "InterfaceProperty" => new FInterfaceProperty
            {
                InterfaceClass = new FPackageIndex(meta.InterfaceClass ?? 0)
            },
            "StructProperty" => new FStructProperty
            {
                Struct = new FPackageIndex(meta.Struct ?? 0)
            },
            "EnumProperty" => new FEnumProperty
            {
                Enum = new FPackageIndex(meta.Enum ?? 0),
                UnderlyingProp = meta.UnderlyingProp != null ? RestoreFProperty(asset, meta.UnderlyingProp) : null!
            },
            "ByteProperty" => new FByteProperty
            {
                Enum = new FPackageIndex(meta.Enum ?? 0)
            },
            "BoolProperty" => new FBoolProperty
            {
                FieldSize = (byte)(meta.FieldSize ?? 0),
                ByteOffset = (byte)(meta.ByteOffset ?? 0),
                ByteMask = (byte)(meta.ByteMask ?? 0),
                FieldMask = (byte)(meta.FieldMask ?? 0),
                NativeBool = meta.NativeBool ?? false,
                Value = meta.BoolValue ?? false
            },
            "ArrayProperty" => new FArrayProperty
            {
                Inner = meta.Inner != null ? RestoreFProperty(asset, meta.Inner) : null!
            },
            "SetProperty" => new FSetProperty
            {
                ElementProp = meta.ElementProp != null ? RestoreFProperty(asset, meta.ElementProp) : null!
            },
            "MapProperty" => new FMapProperty
            {
                KeyProp = meta.KeyProp != null ? RestoreFProperty(asset, meta.KeyProp) : null!,
                ValueProp = meta.ValueProp != null ? RestoreFProperty(asset, meta.ValueProp) : null!
            },
            _ => new FGenericProperty()
        };

        // Set base FField properties - use GetExistingFName to avoid adding to NameMap
        prop.SerializedType = GetExistingFName(asset, meta.SerializedType);
        prop.Name = GetExistingFName(asset, meta.Name);
        prop.Flags = ParseObjectFlags(meta.Flags ?? new List<string>());

        // Set base FProperty properties
        prop.ArrayDim = Enum.TryParse<EArrayDim>(meta.ArrayDim, out var arrDim) ? arrDim : EArrayDim.TArray;
        prop.ElementSize = meta.ElementSize;
        prop.PropertyFlags = ParsePropertyFlags(meta.PropertyFlags);
        prop.RepIndex = (ushort)meta.RepIndex;
        prop.RepNotifyFunc = GetExistingFName(asset, meta.RepNotifyFunc ?? "None");
        prop.BlueprintReplicationCondition = Enum.TryParse<ELifetimeCondition>(meta.BlueprintReplicationCondition, out var replCond)
            ? replCond : ELifetimeCondition.COND_None;

        return prop;
    }

    private static EPropertyFlags ParsePropertyFlags(List<string>? flags)
    {
        if (flags == null) return EPropertyFlags.CPF_None;

        EPropertyFlags result = EPropertyFlags.CPF_None;
        foreach (var flag in flags)
        {
            if (Enum.TryParse<EPropertyFlags>(flag, out var f))
                result |= f;
        }
        return result;
    }

    /// <summary>
    /// Finds a field path from metadata for the given function and property name.
    /// </summary>
    protected FieldPathMetadata? FindFieldPathInMetadata(string functionName, string propertyName)
    {
        if (_metadata?.FieldPaths == null)
            return null;

        if (_metadata.FieldPaths.TryGetValue(functionName, out var functionPaths))
        {
            if (functionPaths.TryGetValue(propertyName, out var fieldPath))
            {
                return fieldPath;
            }
        }

        return null;
    }

    protected override UAsset CreateDefaultAsset()
    {
        // If we have pending metadata, create asset from it
        if (_pendingMetadata != null)
        {
            return CreateAssetFromMetadata(_pendingMetadata);
        }

        var asset = new UAsset()
        {
            LegacyFileVersion = -7,
            UsesEventDrivenLoader = true,
            Imports = new(),
            DependsMap = new(),
            SoftPackageReferenceList = new(),
            AssetRegistryData = new byte[] { 0, 0, 0, 0 },
            ValorantGarbageData = null,
            Generations = new(),
            PackageGuid = Guid.NewGuid(),
            RecordedEngineVersion = new()
            {
                Major = 0,
                Minor = 0,
                Patch = 0,
                Changelist = 0,
                Branch = null
            },
            RecordedCompatibleWithEngineVersion = new()
            {
                Major = 0,
                Minor = 0,
                Patch = 0,
                Changelist = 0,
                Branch = null
            },
            ChunkIDs = Array.Empty<int>(),
            PackageSource = 4048401688,
            FolderName = new("None"),
            IsUnversioned = true,
            FileVersionLicenseeUE = 0,
            ObjectVersion = ObjectVersion.VER_UE4_FIX_WIDE_STRING_CRC,
            ObjectVersionUE5 = ObjectVersionUE5.UNKNOWN,
            CustomVersionContainer = new()
            {
                new(){ Key = Guid.Parse("{375EC13C-06E4-48FB-B500-84F0262A717E}"), FriendlyName = "FCoreObjectVersion", Version = 3, IsSerialized = false },
                new(){ Key = Guid.Parse("{E4B068ED-F494-42E9-A231-DA0B2E46BB41}"), FriendlyName = "FEditorObjectVersion", Version = 34, IsSerialized = false },
                new(){ Key = Guid.Parse("{CFFC743F-43B0-4480-9391-14DF171D2073}"), FriendlyName = "FFrameworkObjectVersion", Version = 35, IsSerialized = false },
                new(){ Key = Guid.Parse("{7B5AE74C-D270-4C10-A958-57980B212A5A}"), FriendlyName = "FSequencerObjectVersion", Version = 11, IsSerialized = false },
                new(){ Key = Guid.Parse("{29E575DD-E0A3-4627-9D10-D276232CDCEA}"), FriendlyName = "FAnimPhysObjectVersion", Version = 17, IsSerialized = false },
                new(){ Key = Guid.Parse("{601D1886-AC64-4F84-AA16-D3DE0DEAC7D6}"), FriendlyName = "FFortniteMainBranchObjectVersion", Version = 27, IsSerialized = false },
                new(){ Key = Guid.Parse("{9C54D522-A826-4FBE-9421-074661B482D0}"), FriendlyName = "FReleaseObjectVersion", Version = 23, IsSerialized = false },
            },
            Exports = new(),
            WorldTileInfo = null,
            PackageFlags = EPackageFlags.PKG_FilterEditorOnly,
        };
        asset.ClearNameIndexList();
        return asset;
    }

    protected override FPackageIndex EnsurePackageImported(string objectName, bool bImportOptional = false)
    {
        if (objectName == null)
        return new FPackageIndex(0);

        var import = Package.FindImportByObjectName(objectName);
        if (import == null)
        {
            import = new Import()
            {
                ObjectName = new(Package, objectName),
            OuterIndex = new FPackageIndex(0),
                ClassPackage = new(Package, objectName),
                ClassName = new(Package, "Package"),
                bImportOptional = bImportOptional
            };
            Package.Imports.Add(import);
        }

        return FPackageIndex.FromImport(Package.Imports.IndexOf(import));
    }

    protected override FPackageIndex EnsureObjectImported(FPackageIndex parent, string objectName, string className, bool bImportOptional = false)
    {
        var import = Package.FindImportByObjectName(objectName);
        if (import == null)
        {
            var parentImport = parent.ToImport(Package);
            import = new Import()
            {
                ObjectName = new(Package, objectName),
                OuterIndex = parent,
                ClassPackage = parentImport.ObjectName,
                ClassName = new(Package, className),
                bImportOptional = bImportOptional
            };
            Package.Imports.Add(import);
        }

        return FPackageIndex.FromImport(Package.Imports.IndexOf(import));
    }

    /// <summary>
    /// Builds the import table from KMS import declarations and ClassPackages metadata.
    /// Each KMS import statement like:
    ///   from "/Script/FSD" import { class OverclockBank; OverclockBank Default__OverclockBank; }
    /// generates:
    ///   1. Package import: /Script/FSD (ClassName=Package, ClassPackage=/Script/CoreUObject)
    ///   2. Object import: OverclockBank (ClassName=Class, ClassPackage=/Script/CoreUObject, OuterIndex→/Script/FSD)
    ///   3. Object import: Default__OverclockBank (ClassName=OverclockBank, ClassPackage=/Script/FSD, OuterIndex→/Script/FSD)
    /// </summary>
    private void BuildImportsFromKms(CompiledScriptContext scriptContext)
    {
        if (_metadata == null) return;

        // Determine which classes need actual Class import entries.
        // Only classes that are referenced by FPackageIndex in exports need Class imports.
        // Classes used only as ClassName strings in Import entries (e.g., CarvedResourceData
        // used as ClassName for imported objects) do NOT need separate Class import entries.
        var referencedClassNames = new HashSet<string>();

        // Classes referenced by KMS object declarations (exported objects reference their class by FPackageIndex)
        foreach (var obj in scriptContext.Objects)
        {
            if (!string.IsNullOrEmpty(obj.ClassName))
                referencedClassNames.Add(obj.ClassName);
        }

        // Classes defined in KMS class declarations
        foreach (var cls in scriptContext.Classes)
        {
            if (!string.IsNullOrEmpty(cls.Symbol?.Name))
                referencedClassNames.Add(cls.Symbol.Name);
        }

        // Classes referenced by infrastructure exports (ClassExport, CDO, functions)
        foreach (var exp in _metadata.InfrastructureExports)
        {
            if (!string.IsNullOrEmpty(exp.ClassName))
                referencedClassNames.Add(exp.ClassName);
        }

        foreach (var importCtx in scriptContext.Imports)
        {
            var packagePath = importCtx.Symbol.Name; // e.g., "/Script/FSD" or "/Game/WeaponsNTools/..."

            // Create Package import
            var packageImport = new Import
            {
                ObjectName = new FName(Package, packagePath),
                ClassName = new FName(Package, "Package"),
                ClassPackage = new FName(Package, "/Script/CoreUObject"),
                OuterIndex = new FPackageIndex(0),
                bImportOptional = false
            };
            Package.Imports.Add(packageImport);
            var packageIndex = FPackageIndex.FromImport(Package.Imports.Count - 1);

            // Create object imports for each declaration in this package
            foreach (var decl in importCtx.Declarations)
            {
                var objectName = decl.Symbol.Name;
                string className;
                string classPackage;

                if (decl.Symbol is ClassSymbol)
                {
                    // Only create Class import if this class is actually referenced by
                    // exports or Default__ objects (needs FPackageIndex-based reference).
                    // Classes only used as ClassName strings don't need import entries.
                    if (!referencedClassNames.Contains(objectName))
                        continue;

                    // class OverclockBank; → ClassName="Class", ClassPackage="/Script/CoreUObject"
                    className = "Class";
                    classPackage = "/Script/CoreUObject";
                }
                else if (decl.Symbol is VariableSymbol varSymbol)
                {
                    // OverclockBank Default__OverclockBank; → ClassName="OverclockBank", ClassPackage from metadata
                    // Get the class name from the variable's type, not its declaring class
                    className = varSymbol.InnerSymbol?.Name
                        ?? varSymbol.Declaration?.Type?.Text
                        ?? "Object";

                    // Look up the class package from ClassPackages metadata
                    if (_metadata.ClassPackages.TryGetValue(className, out var pkg))
                    {
                        classPackage = pkg;
                    }
                    else
                    {
                        // Fallback: use the package path itself as classPackage
                        classPackage = packagePath;
                    }
                }
                else if (decl.Symbol is ProcedureSymbol procSymbol)
                {
                    // Imported function → ClassName="Function", ClassPackage="/Script/CoreUObject"
                    className = "Function";
                    classPackage = "/Script/CoreUObject";
                }
                else if (decl.Symbol is EnumSymbol)
                {
                    // Imported enum → ClassName="Enum", ClassPackage="/Script/CoreUObject"
                    className = "Enum";
                    classPackage = "/Script/CoreUObject";
                }
                else
                {
                    // Generic fallback
                    className = decl.Symbol.DeclaringClass?.Name ?? "Object";

                    if (_metadata.ClassPackages.TryGetValue(className, out var pkg))
                    {
                        classPackage = pkg;
                    }
                    else
                    {
                        classPackage = packagePath;
                    }
                }

                var objectImport = new Import
                {
                    ObjectName = new FName(Package, objectName),
                    ClassName = new FName(Package, className),
                    ClassPackage = new FName(Package, classPackage),
                    OuterIndex = packageIndex,
                    bImportOptional = false
                };
                Package.Imports.Add(objectImport);
            }
        }
    }

    public override UAssetLinker LinkCompiledScript(CompiledScriptContext scriptContext)
    {
        // Metadata mode: build imports from KMS import declarations before any linking
        if (_metadata != null)
        {
            BuildImportsFromKms(scriptContext);
        }

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
                // Skip variables that only have initializers (CDO property values)
                // These are inherited properties - we don't need to create new property definitions
                // The values will be linked in LinkClassCDO
                if (variableContext.Initializer != null &&
                    !HasExistingPropertyDefinition(classExport, variableContext.Symbol.Name))
                {
                    continue;
                }

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

            // Link CDO (Class Default Object) with property values
            LinkClassCDO(classExport, classContext, scriptContext.Objects);
        }

        // Metadata mode: create object exports from KMS declarations before linking
        if (_metadata != null)
        {
            CreateObjectExportsFromKms(scriptContext.Objects);
        }

        // Link top-level objects (non-CDO objects)
        // Track already-linked exports to handle same-named objects (e.g., multiple OverclockShematicItem_0)
        var linkedExports = new HashSet<NormalExport>();
        foreach (var objectContext in scriptContext.Objects)
        {
            LinkTopLevelObject(objectContext, linkedExports);
        }

        return this;
    }

    /// <summary>
    /// Metadata mode: Creates NormalExport entries for each object declaration in KMS.
    /// The export ordering follows the KMS declaration order within the asset's export table.
    /// </summary>
    private void CreateObjectExportsFromKms(List<CompiledObjectContext> objects)
    {
        if (_metadata == null) return;

        // Group objects: first parent objects (e.g., OSB_M1000), then their children in declaration order
        // The KMS already has them in the correct order (parent first, child after)
        var firstExportIndex = Package.Exports.Count; // Infrastructure exports already placed

        foreach (var objCtx in objects)
        {
            _metadata.ObjectDefaults.TryGetValue(objCtx.ClassName, out var classDefaults);

            // Parse object flags from class defaults
            var objectFlags = EObjectFlags.RF_NoFlags;
            if (classDefaults?.ObjectFlags != null)
            {
                foreach (var flagStr in classDefaults.ObjectFlags)
                {
                    if (Enum.TryParse<EObjectFlags>(flagStr, out var flag))
                        objectFlags |= flag;
                }
            }

            var export = new NormalExport(Package, Array.Empty<byte>())
            {
                Data = new List<PropertyData>(),
                ObjectName = new FName(Package, objCtx.Name),
                OuterIndex = GetOuterIndexForObject(objCtx),
                SuperIndex = new FPackageIndex(0),
                TemplateIndex = GetTemplateIndexForObject(objCtx, classDefaults),
                ClassIndex = FindOrCreateClassImport(Package, objCtx.ClassName),
                ObjectFlags = objectFlags,
                bNotAlwaysLoadedForEditorGame = classDefaults?.BNotAlwaysLoadedForEditorGame ?? false,
                bIsAsset = classDefaults?.BIsAsset ?? false,
            };

            Package.Exports.Add(export);
            Package.DependsMap.Add(Array.Empty<int>());
        }
    }

    /// <summary>
    /// Determines the OuterIndex for an object based on its position in the KMS.
    /// Most top-level objects have the main OSB/package export as their outer.
    /// Sub-objects (like OverclockShematicItem_0) have their parent SCE export as outer.
    /// </summary>
    private FPackageIndex GetOuterIndexForObject(CompiledObjectContext objCtx)
    {
        // For metadata mode, find the first export (usually the OSB/main export) as the default outer
        // Sub-object relationships are preserved: if an export's outer should be another export,
        // this is determined by the object declaration order in KMS.
        // Simple heuristic: export[0] (first infrastructure export) is the default outer for top-level objects.
        if (Package.Exports.Count > 0)
        {
            return new FPackageIndex(1); // First export (1-based) as outer
        }
        return new FPackageIndex(0);
    }

    /// <summary>
    /// Gets the template index for an object from class defaults.
    /// </summary>
    private FPackageIndex GetTemplateIndexForObject(CompiledObjectContext objCtx, ObjectClassDefaults? classDefaults)
    {
        if (classDefaults?.TemplateClassName != null)
        {
            // Find or create import for the template (e.g., Default__Schematic)
            var templateImports = GetPackageIndexByLocalName(classDefaults.TemplateClassName);
            var templateImport = templateImports?.FirstOrDefault();
            if (templateImport != null)
            {
                return templateImport.Value.PackageIndex;
            }
        }
        return new FPackageIndex(0);
    }

    /// <summary>
    /// Links the CDO (Class Default Object) for a class, populating its Data with property values.
    /// </summary>
    private void LinkClassCDO(ClassExport classExport, CompiledClassContext classContext, List<CompiledObjectContext> objects)
    {
        var cdoName = $"Default__{classContext.Symbol.Name}";
        var cdoExport = Package.Exports
            .OfType<NormalExport>()
            .FirstOrDefault(x => x.ObjectName.ToString() == cdoName);

        if (cdoExport == null)
            return; // CDO doesn't exist, nothing to update

        // Initialize Data for new CDOs (or reuse existing list)
        cdoExport.Data ??= new List<PropertyData>();

        // First, link sub-objects referenced by this CDO
        foreach (var objectContext in objects)
        {
            LinkSubObject(objectContext, cdoExport);
        }

        // Then populate CDO Data with property values from compiled class variables
        foreach (var variableContext in classContext.Variables)
        {
            if (variableContext.Initializer != null)
            {
                var existingPropIndex = cdoExport.Data.FindIndex(p => p.Name.ToString() == variableContext.Symbol.Name);

                // Skip property creation when it already exists - prevents NameMap bloat
                if (existingPropIndex >= 0)
                    continue;

                // Construct full type hint including nested type parameters
                // (e.g., "Array<Struct<GameActivitySubTask>>")
                var typeDecl = variableContext.Symbol.Declaration?.Type;
                var typeHint = GetFullTypeHint(typeDecl);
                var propData = CreatePropertyDataFromValue(
                    variableContext.Symbol.Name,
                    typeHint,
                    variableContext.Initializer);

                if (propData != null)
                    cdoExport.Data.Add(propData);
            }
        }
    }

    /// <summary>
    /// Links a sub-object export, populating its Data with property values.
    /// </summary>
    private void LinkSubObject(CompiledObjectContext objectContext, NormalExport outerExport)
    {
        // Find existing sub-object export
        var subObjectExport = Package.Exports
            .OfType<NormalExport>()
            .FirstOrDefault(x => x.ObjectName.ToString() == objectContext.Name &&
                                 !x.OuterIndex.IsNull() &&
                                 x.OuterIndex.ToExport(Package) == outerExport);

        if (subObjectExport == null)
            return; // Sub-object doesn't exist, nothing to update

        // Initialize Data for new sub-objects (or reuse existing list)
        subObjectExport.Data ??= new List<PropertyData>();

        // Track if this export already has data to avoid cross-contamination
        bool hasExistingData = subObjectExport.Data.Count > 0;

        // Populate Data from compiled properties
        foreach (var prop in objectContext.Properties)
        {
            var existingPropIndex = subObjectExport.Data.FindIndex(p => p.Name.ToString() == prop.Name);

            // Skip property creation when it already exists - prevents NameMap bloat
            if (existingPropIndex >= 0)
                continue;

            if (!hasExistingData)
            {
                var propData = CreatePropertyDataFromValue(prop.Name, prop.Type, prop.Value);
                if (propData != null)
                    subObjectExport.Data.Add(propData);
            }
        }
    }

    /// <summary>
    /// Links a top-level object export, populating its Data with property values.
    /// Uses linkedExports set to skip already-populated exports when multiple exports share the same name
    /// (e.g., multiple "OverclockShematicItem_0" with different OuterIndex).
    /// </summary>
    private void LinkTopLevelObject(CompiledObjectContext objectContext, HashSet<NormalExport>? linkedExports = null)
    {
        // Find existing top-level object export, skipping already-linked ones
        var objectExport = Package.Exports
            .OfType<NormalExport>()
            .FirstOrDefault(x => x.ObjectName.ToString() == objectContext.Name &&
                                 (linkedExports == null || !linkedExports.Contains(x)));

        if (objectExport == null)
            return; // Object doesn't exist, nothing to update

        // Mark as linked
        linkedExports?.Add(objectExport);

        // Initialize Data (or reuse existing list)
        objectExport.Data ??= new List<PropertyData>();

        // Track if this export already has data to avoid cross-contamination
        // from same-named exports (e.g., multiple "ParticleLODLevel_1" in particle systems)
        bool hasExistingData = objectExport.Data.Count > 0;

        // Populate Data from compiled properties
        foreach (var prop in objectContext.Properties)
        {
            var existingPropIndex = objectExport.Data.FindIndex(p => p.Name.ToString() == prop.Name);

            // Skip property creation entirely when the property already exists in the export.
            // PreserveOriginalPropertyMetadata returns existingProp for same-type properties,
            // but CreatePropertyDataFromValue has the side effect of adding names to the NameMap.
            // By skipping creation, we prevent NameMap bloat from unused type/struct names.
            if (existingPropIndex >= 0)
                continue;

            if (!hasExistingData)
            {
                var propData = CreatePropertyDataFromValue(prop.Name, prop.Type, prop.Value);
                if (propData != null)
                    objectExport.Data.Add(propData);
            }
        }
    }

    /// <summary>
    /// Preserves metadata from the original property when replacing it with a new one.
    /// This handles cases where the KMS format doesn't capture all property metadata,
    /// such as ByteType (FName vs Byte), NamePropertyData vs StrPropertyData,
    /// TextPropertyData fields, Ancestry, and nested struct/array types.
    /// </summary>
    private PropertyData PreserveOriginalPropertyMetadata(PropertyData existingProp, PropertyData newProp)
    {
        // Preserve Ancestry and Name FName from the original property for correct serialization
        newProp.Ancestry = existingProp.Ancestry;
        newProp.Name = existingProp.Name; // Preserve original FName (with correct Number for numbered names)

        // If the property types are fundamentally different (e.g., StructPropertyData vs ArrayPropertyData),
        // the KMS round-trip has lost critical type information. Preserve the original property entirely.
        if (existingProp.GetType() != newProp.GetType())
        {
            // Special cases where type conversion is expected and handled below
            if (existingProp is NamePropertyData && newProp is StrPropertyData)
            {
                // Handled below - NamePropertyData/StrPropertyData conversion
            }
            else
            {
                return existingProp;
            }
        }

        // ObjectPropertyData: when the original references an export (sub-object in the same asset)
        // but the new one references an import, preserve the original reference since sub-object
        // exports should be referenced directly.
        if (existingProp is ObjectPropertyData existingObj && newProp is ObjectPropertyData newObj)
        {
            if (existingObj.Value.Index != newObj.Value.Index)
            {
                // Prefer the original reference - it has the correct export/import resolution
                return existingObj;
            }
        }

        // BytePropertyData: preserve EnumType and ByteType metadata from the original
        if (existingProp is BytePropertyData existingByte)
        {
            if (existingByte.ByteType.ToString() == "FName")
            {
                // KMS can't represent FName-type byte properties; preserve the original entirely
                return existingByte;
            }
            // For raw byte properties, preserve the EnumType (should be "None" for raw bytes)
            if (newProp is BytePropertyData newByte)
            {
                newByte.EnumType = existingByte.EnumType;
                newByte.ByteType = existingByte.ByteType;
            }
        }

        // NamePropertyData: KMS decompiles as "Name" type but linker creates StrPropertyData;
        // convert back to NamePropertyData to preserve the original type
        if (existingProp is NamePropertyData existingName && newProp is StrPropertyData newStr)
        {
            // Preserve null Value when the original had null (decompiler outputs "" for null)
            var newValue = newStr.Value?.ToString();
            if (string.IsNullOrEmpty(newValue) && existingName.Value == null)
            {
                // Original had null Value, keep it null
                return existingName;
            }
            existingName.Value = AddName(newValue ?? "None");
            return existingName;
        }

        // StrPropertyData: preserve null Value when the original had null
        // (decompiler outputs "" for null, compiler creates FString(""))
        if (existingProp is StrPropertyData existingStr && newProp is StrPropertyData newStrProp)
        {
            if (existingStr.Value == null && (newStrProp.Value == null || string.IsNullOrEmpty(newStrProp.Value.ToString())))
            {
                newStrProp.Value = null;
            }
        }

        // TextPropertyData: preserve metadata fields that the KMS format doesn't capture
        if (existingProp is TextPropertyData existingText && newProp is TextPropertyData newText)
        {
            newText.Flags = existingText.Flags;
            newText.HistoryType = existingText.HistoryType;
            newText.Namespace ??= existingText.Namespace;
            newText.CultureInvariantString ??= existingText.CultureInvariantString;
            newText.TableId ??= existingText.TableId;
        }

        // StructPropertyData: merge new field values into existing struct to preserve
        // field types (NamePropertyData, BytePropertyData FName, etc.) that KMS doesn't capture
        if (existingProp is StructPropertyData existingStruct && newProp is StructPropertyData newStruct)
        {
            if (existingStruct.Value != null && newStruct.Value != null)
            {
                // For each field in the new struct, find and update the matching existing field
                foreach (var newField in newStruct.Value)
                {
                    var existingFieldIndex = existingStruct.Value.FindIndex(p => p.Name.ToString() == newField.Name.ToString());
                    if (existingFieldIndex >= 0)
                    {
                        var existingField = existingStruct.Value[existingFieldIndex];
                        existingStruct.Value[existingFieldIndex] = PreserveOriginalPropertyMetadata(existingField, newField);
                    }
                    // Don't add new fields that weren't in the original struct.
                    // This prevents cross-contamination from same-named exports.
                }
                return existingStruct;
            }
        }

        // ArrayPropertyData: preserve existing array metadata and merge elements
        if (existingProp is ArrayPropertyData existingArray && newProp is ArrayPropertyData newArray)
        {
            // Preserve the original ArrayType if the new one seems wrong
            // (e.g., when type hint info was lost and defaulted to "ObjectProperty")
            if (existingArray.ArrayType?.ToString() != newArray.ArrayType?.ToString())
            {
                newArray.ArrayType = existingArray.ArrayType;
            }
            // Preserve DummyStruct for empty struct arrays
            if (existingArray.DummyStruct != null && newArray.DummyStruct == null)
            {
                newArray.DummyStruct = existingArray.DummyStruct;
            }
            // If the new array is empty but existing has data, preserve existing data.
            // This handles cases where nested struct array contents were lost during KMS round-trip.
            if ((newArray.Value == null || newArray.Value.Length == 0) &&
                existingArray.Value != null && existingArray.Value.Length > 0)
            {
                newArray.Value = existingArray.Value;
            }
            // If both arrays have elements of same length, try to merge element metadata
            else if (existingArray.Value != null && newArray.Value != null &&
                existingArray.Value.Length == newArray.Value.Length)
            {
                for (int i = 0; i < newArray.Value.Length; i++)
                {
                    newArray.Value[i] = PreserveOriginalPropertyMetadata(existingArray.Value[i], newArray.Value[i]);
                }
            }
        }

        // For all remaining simple property types where the types match,
        // prefer the original property to avoid format/precision loss in the round-trip.
        // The KMS format may not faithfully represent float precision, null values,
        // enum metadata, or other property-specific details.
        if (existingProp.GetType() == newProp.GetType())
        {
            return existingProp;
        }

        return newProp;
    }

    /// <summary>
    /// Recursively constructs a full type hint string from a TypeIdentifier chain.
    /// e.g., Array -> Struct -> GameActivitySubTask becomes "Array<Struct<GameActivitySubTask>>"
    /// </summary>
    private static string GetFullTypeHint(KismetScript.Syntax.Statements.Expressions.Identifiers.TypeIdentifier? typeDecl)
    {
        if (typeDecl == null) return "Object";
        var text = typeDecl.Text ?? "Object";
        if (typeDecl.TypeParameter != null)
        {
            text = $"{text}<{GetFullTypeHint(typeDecl.TypeParameter)}>";
        }
        return text;
    }

    /// <summary>
    /// Creates a PropertyData instance from a compiled property value.
    /// </summary>
    private PropertyData? CreatePropertyDataFromValue(string name, string typeHint, CompiledPropertyValue? value, FName? overrideName = null)
    {
        if (value == null)
            return null;

        var fname = overrideName ?? AddName(name);

        if (value.FloatValue.HasValue)
        {
            return new FloatPropertyData(fname) { Value = value.FloatValue.Value };
        }
        if (value.IntValue.HasValue)
        {
            // Distinguish between byte and int based on type hint from KMS
            if (typeHint == "byte")
            {
                return new BytePropertyData(fname) { Value = (byte)value.IntValue.Value };
            }
            return new IntPropertyData(fname) { Value = value.IntValue.Value };
        }
        if (value.BoolValue.HasValue)
        {
            return new BoolPropertyData(fname) { Value = value.BoolValue.Value };
        }
        if (value.StringValue != null)
        {
            // Check if this is a Text type based on typeHint
            if (typeHint == "Text")
            {
                return new TextPropertyData(fname) { Value = new FString(value.StringValue) };
            }
            // Check if this is a SoftObject path string
            if (typeHint == "SoftObject")
            {
                var assetPath = AddName(value.StringValue);
                var softPath = new FSoftObjectPath(null, assetPath, null);
                return new SoftObjectPropertyData(fname) { Value = softPath };
            }
            return new StrPropertyData(fname) { Value = new FString(value.StringValue) };
        }
        if (value.ObjectReference != null)
        {
            // Handle null object references (decompiled from FPackageIndex.IsNull())
            if (value.ObjectReference == "null")
            {
                return new ObjectPropertyData(fname) { Value = new FPackageIndex(0) };
            }

            // Handle enum types: Enum<EnumTypeName> with value "EnumTypeName::Value"
            if (typeHint.StartsWith("Enum<") && typeHint.EndsWith(">"))
            {
                var enumTypeName = typeHint.Substring(5, typeHint.Length - 6);
                return new EnumPropertyData(fname)
                {
                    EnumType = AddName(enumTypeName),
                    Value = AddName(value.ObjectReference)
                };
            }

            var results = GetPackageIndexByLocalName(value.ObjectReference)?.ToList();
            if (results == null || results.Count == 0)
            {
                // Object not found - try to find it in the compiled script context and create an import
                // This handles cases where user adds new object references not in the original asset
                var packageIndex = TryCreateImportForReference(value.ObjectReference, typeHint);
                if (packageIndex.Index == 0)
                {
                    Console.WriteLine($"Warning: Object reference '{value.ObjectReference}' not found and could not be imported");
                    return new ObjectPropertyData(fname) { Value = new FPackageIndex(0) };
                }
                return new ObjectPropertyData(fname) { Value = packageIndex };
            }

            // When multiple exports share the same name (e.g., multiple OverclockShematicItem_0),
            // use a round-robin counter to pick the next unconsumed instance
            if (results.Count > 1 && _metadata != null)
            {
                _exportReferenceCounter.TryGetValue(value.ObjectReference, out var counter);
                var index = counter % results.Count;
                _exportReferenceCounter[value.ObjectReference] = counter + 1;
                return new ObjectPropertyData(fname) { Value = results[index].PackageIndex };
            }

            var result = results.First();
            return new ObjectPropertyData(fname) { Value = result.PackageIndex };
        }
        // Handle map types: {key: value} syntax with typeHint "Object" (untyped map)
        // or explicit Map<K,V> type hint. StructValue dictionary represents key-value pairs.
        if (value.StructValue != null && !typeHint.StartsWith("Struct<"))
        {
            var mapProp = new MapPropertyData(fname);
            var mapData = new TMap<PropertyData, PropertyData>();

            foreach (var kvp in value.StructValue)
            {
                // Create key PropertyData (object reference)
                var keyProp = CreatePropertyDataFromValue(name, "Object", new CompiledPropertyValue { ObjectReference = kvp.Key },
                    FName.DefineDummy(Package, name, int.MinValue));

                // Create value PropertyData (could be object reference or literal)
                PropertyData? valProp;
                if (kvp.Value.IntValue.HasValue)
                {
                    valProp = new IntPropertyData(FName.DefineDummy(Package, name, int.MinValue)) { Value = kvp.Value.IntValue.Value };
                }
                else
                {
                    valProp = CreatePropertyDataFromValue(name, "Object", kvp.Value,
                        FName.DefineDummy(Package, name, int.MinValue));
                }

                if (keyProp != null && valProp != null)
                    mapData.Add(keyProp, valProp);
            }

            mapProp.Value = mapData;
            return mapProp;
        }

        // Handle struct types - must be checked before ArrayValue since {} compiles as empty array
        if (typeHint.StartsWith("Struct<") && typeHint.EndsWith(">") &&
            (value.ArrayValue != null || value.StructValue != null))
        {
            var structTypeName = typeHint.Substring(7, typeHint.Length - 8);
            var structProp = new StructPropertyData(fname)
            {
                StructType = AddName(structTypeName),
                Value = new List<PropertyData>(),
                SerializeNone = true,
            };

            // Handle Guid structs with custom serialization:
            // The KMS format stores Guid as { SaveGameID: "{guid-string}" }
            // but UAssetAPI expects a GuidPropertyData with 16-byte raw serialization
            if (structTypeName == "Guid" && value.StructValue != null)
            {
                // Extract the Guid string from the first (and only) field value
                var guidString = value.StructValue.Values.FirstOrDefault()?.StringValue;
                if (guidString != null)
                {
                    // Remove curly braces if present: {xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx}
                    var guidValue = Guid.Parse(guidString.Trim('{', '}'));
                    var guidProp = new GuidPropertyData(fname) { Value = guidValue };
                    structProp.Value.Add(guidProp);
                    return structProp;
                }
            }

            // If there are struct field values (from {key: value} syntax), populate them
            if (value.StructValue != null)
            {
                foreach (var kvp in value.StructValue)
                {
                    var fieldProp = CreatePropertyDataFromValue(kvp.Key, "Object", kvp.Value);
                    if (fieldProp != null)
                        structProp.Value.Add(fieldProp);
                }
            }

            return structProp;
        }

        if (value.ArrayValue != null)
        {
            var arrayProp = new ArrayPropertyData(fname);
            var elements = new List<PropertyData>();

            // Extract inner type from Array<...>
            string innerTypeHint = typeHint;
            if (typeHint.StartsWith("Array<") && typeHint.EndsWith(">"))
            {
                innerTypeHint = typeHint.Substring(6, typeHint.Length - 7);
            }

            // For struct array elements, use the parent array name (required for serialization).
            // For other element types, use DefineDummy to avoid polluting the name map.
            bool isStructArray = innerTypeHint.StartsWith("Struct<");

            for (int i = 0; i < value.ArrayValue.Count; i++)
            {
                var element = value.ArrayValue[i];
                FName elementName;
                if (isStructArray)
                {
                    // Struct array elements use the parent array's name (matches UAssetAPI serialization)
                    elementName = AddName(name);
                }
                else
                {
                    elementName = FName.DefineDummy(Package, i.ToString(), int.MinValue);
                }
                var elementProp = CreatePropertyDataFromValue(i.ToString(), innerTypeHint, element, elementName);
                if (elementProp != null)
                    elements.Add(elementProp);
            }

            arrayProp.Value = elements.ToArray();

            // Determine array type: prefer type hint over first element's PropertyType
            // This is critical because for Array<Struct<X>>, elements[0].PropertyType
            // may be "ObjectProperty" (from struct fields) instead of "StructProperty"
            if (typeHint.StartsWith("Array<") && typeHint.EndsWith(">"))
            {
                var innerType = typeHint.Substring(6, typeHint.Length - 7);
                arrayProp.ArrayType = AddName(InferArrayTypeFromTypeHint(innerType));
            }
            else if (elements.Any())
            {
                arrayProp.ArrayType = AddName(elements[0].PropertyType.ToString());
            }

            return arrayProp;
        }

        return null;
    }

    /// <summary>
    /// Infers the UE property type name from a KMS type hint.
    /// </summary>
    private static string InferArrayTypeFromTypeHint(string typeHint)
    {
        if (typeHint.StartsWith("Object<"))
            return "ObjectProperty";
        if (typeHint.StartsWith("Struct<"))
            return "StructProperty";
        if (typeHint == "SoftObject")
            return "SoftObjectProperty";

        return typeHint switch
        {
            "float" => "FloatProperty",
            "int" => "IntProperty",
            "bool" => "BoolProperty",
            "string" => "StrProperty",
            "byte" => "ByteProperty",
            "Text" => "TextProperty",
            _ => "ObjectProperty"
        };
    }

    protected override void LinkCompiledFunction(CompiledFunctionContext functionContext)
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

    protected override FPackageIndex CreateProcedureImport(ProcedureSymbol symbol)
    {
        var import = new Import()
        {
            ObjectName = new(Package, symbol.Name),
            OuterIndex = FindPackageIndexInAsset(symbol?.DeclaringClass),
            ClassPackage = new(Package, symbol?.DeclaringPackage?.Name),
            ClassName = new(Package, "Function"),
            bImportOptional = false
        };
        Package.Imports.Add(import);
        return FPackageIndex.FromImport(Package.Imports.IndexOf(import));
    }

    protected override IEnumerable<(object ImportOrExport, FPackageIndex PackageIndex)> GetPackageIndexByLocalName(string name)
    {
        if (Package is UAsset uasset)
        {
            foreach (var import in uasset.Imports)
            {
                if (import.ObjectName.ToString() == name)
                {
                    yield return (import, new FPackageIndex(-(uasset.Imports.IndexOf(import) + 1)));
                }
            }
        }
        else
        {
            throw new NotImplementedException("Zen import");
        }
        foreach (var export in Package.Exports)
        {
            if (export.ObjectName.ToString() == name)
            {
                yield return (export, new FPackageIndex(+(Package.Exports.IndexOf(export) + 1)));
            }
        }
    }

    protected override IEnumerable<(object ImportOrExport, FPackageIndex PackageIndex)> GetPackageIndexByFullName(string name)
    {
        if (Package is UAsset uasset)
        {
            foreach (var import in uasset.Imports)
            {
                var importFullName = GetFullName(import);
                if (importFullName == name)
                {
                    yield return (import, new FPackageIndex(-(uasset.Imports.IndexOf(import) + 1)));
                }
            }
        }
        else
        {
            throw new NotImplementedException("Zen import");
        }
        foreach (var export in Package.Exports)
        {
            var exportFullName = GetFullName(export);
            if (exportFullName == name)
            {
                yield return (export, new FPackageIndex(+(Package.Exports.IndexOf(export) + 1)));
            }
        }
    }

    public override UAsset Build()
    {
        // Ensure all property type names used in export data are in the name map
        // before serialization time (when AddNameReference is locked).
        // In metadata mode, NameMap is built dynamically, so we DO add type names.
        if (!Package.HasUnversionedProperties)
        {
            EnsurePropertyTypeNamesInNameMap();
        }

        // Metadata mode: finalize Generations after all exports are added
        if (_metadata != null)
        {
            Package.Generations.Clear();
            Package.Generations.Add(new FGenerationInfo(
                Package.Exports.Count,
                Package.GetNameMapIndexList().Count));
        }

        return Package;
    }

    /// <summary>
    /// Walks all exports and their property data recursively to ensure that all
    /// property type names, array types, struct types, and "None" sentinels
    /// are pre-registered in the name map before serialization.
    /// </summary>
    private void EnsurePropertyTypeNamesInNameMap()
    {
        // Always ensure "None" is in the name map (used as property list terminator)
        EnsureNameInMap("None");

        foreach (var export in Package.Exports)
        {
            if (export is NormalExport normalExport && normalExport.Data != null)
            {
                EnsurePropertyNamesRecursive(normalExport.Data);
            }
        }
    }

    /// <summary>
    /// Recursively ensures all property-related names are in the name map.
    /// </summary>
    /// <param name="properties">Properties to scan.</param>
    /// <param name="insideMapEntry">If true, skip registering PropertyType and Name for
    /// top-level entries because map entries are written with includeHeader=false.</param>
    private void EnsurePropertyNamesRecursive(IEnumerable<PropertyData> properties, bool insideMapEntry = false)
    {
        foreach (var prop in properties)
        {
            if (!insideMapEntry)
            {
                // Ensure the property type name (e.g., "StrProperty", "IntProperty") is in the name map
                if (prop.PropertyType?.Value != null)
                {
                    EnsureNameInMap(prop.PropertyType.Value);
                }

                // Ensure the property name is in the name map (skip dummy names used by array elements)
                if (prop.Name?.Value?.Value != null && prop.Name.Number != int.MinValue)
                {
                    EnsureNameInMap(prop.Name.Value.Value);
                }
            }

            // Handle nested properties
            if (prop is StructPropertyData structProp)
            {
                // Ensure StructType is in the name map (skip dummy FNames, and skip if inside map entry
                // since struct type is not written for map entries with includeHeader=false)
                if (!insideMapEntry && structProp.StructType?.Value?.Value != null && !structProp.StructType.IsDummy)
                {
                    EnsureNameInMap(structProp.StructType.Value.Value);
                }
                if (structProp.Value != null)
                {
                    // Propagate insideMapEntry: all data within map entries is serialized
                    // without property headers, so their type names don't need NameMap entries.
                    EnsurePropertyNamesRecursive(structProp.Value, insideMapEntry);
                }
            }
            else if (prop is ArrayPropertyData arrayProp)
            {
                // Ensure ArrayType is in the name map (only outside map entries)
                if (!insideMapEntry)
                {
                    if (arrayProp.ArrayType?.Value?.Value != null)
                    {
                        EnsureNameInMap(arrayProp.ArrayType.Value.Value);
                    }
                    if (arrayProp.ArrayType?.Value?.Value == "StructProperty")
                    {
                        EnsureNameInMap("StructProperty");
                    }
                }
                if (arrayProp.Value != null)
                {
                    // Propagate insideMapEntry through array recursion
                    EnsurePropertyNamesRecursive(arrayProp.Value, insideMapEntry);
                }
            }
            else if (prop is MapPropertyData mapProp)
            {
                if (mapProp.Value != null)
                {
                    // Use the map's header type FNames (KeyType/ValueType) rather than the
                    // first entry's PropertyType to avoid adding unwanted names to the NameMap
                    // (e.g., "NiagaraVariable" when the header uses "StructProperty").
                    if (!insideMapEntry)
                    {
                        if (mapProp.KeyType?.Value?.Value != null && !mapProp.KeyType.IsDummy)
                        {
                            EnsureNameInMap(mapProp.KeyType.Value.Value);
                        }
                        else if (mapProp.Value.Keys.Count > 0)
                        {
                            var firstKey = mapProp.Value.Keys.First();
                            if (firstKey.PropertyType?.Value != null)
                                EnsureNameInMap(firstKey.PropertyType.Value);
                        }
                        if (mapProp.ValueType?.Value?.Value != null && !mapProp.ValueType.IsDummy)
                        {
                            EnsureNameInMap(mapProp.ValueType.Value.Value);
                        }
                        else if (mapProp.Value.Count > 0)
                        {
                            var firstValue = mapProp.Value.Values.First();
                            if (firstValue.PropertyType?.Value != null)
                                EnsureNameInMap(firstValue.PropertyType.Value);
                        }
                    }
                    // Map entries are written without headers, so pass insideMapEntry=true
                    EnsurePropertyNamesRecursive(mapProp.Value.Keys, insideMapEntry: true);
                    EnsurePropertyNamesRecursive(mapProp.Value.Values, insideMapEntry: true);
                }
            }
            else if (prop is SetPropertyData setProp)
            {
                if (setProp.Value != null)
                {
                    EnsurePropertyNamesRecursive(setProp.Value, insideMapEntry);
                }
            }
            else if (prop is EnumPropertyData enumProp)
            {
                if (enumProp.EnumType?.Value?.Value != null)
                    EnsureNameInMap(enumProp.EnumType.Value.Value);
                if (enumProp.Value?.Value?.Value != null)
                    EnsureNameInMap(enumProp.Value.Value.Value);
            }
        }
    }

    /// <summary>
    /// Ensures a name string exists in the package's name map.
    /// </summary>
    private void EnsureNameInMap(string name)
    {
        var fstring = new FString(name);
        if (!Package.ContainsNameReference(fstring))
        {
            Package.AddNameReference(fstring);
        }
    }

    /// <summary>
    /// Creates an FName that looks up the existing name in NameMap without adding a new entry.
    /// Used in metadata mode where NameMap is pre-populated.
    /// Handles FName number suffixes (e.g., "SCE_M1000_Ammo__1182618035" -> base "SCE_M1000_Ammo_" + Number=1182618036).
    /// Falls back to standard FName creation if name is not found (for safety).
    /// </summary>
    private static FName GetExistingFName(UAsset asset, string name)
    {
        var fstring = new FString(name);
        if (asset.ContainsNameReference(fstring))
        {
            var index = asset.SearchNameReference(fstring);
            return new FName(asset, index, 0);
        }

        // Check for FName number suffix pattern (e.g., "_0", "_1182618035")
        // UE4 stores these as BaseName + Number, where NameMap only contains BaseName
        var idSuffixMatch = NameIdSuffix().Match(name);
        if (idSuffixMatch?.Success ?? false)
        {
            var nameWithoutSuffix = name[..^idSuffixMatch.Length];
            var baseFString = new FString(nameWithoutSuffix);
            if (asset.ContainsNameReference(baseFString) && int.TryParse(idSuffixMatch.Groups[1].Value, out var id))
            {
                var baseIndex = asset.SearchNameReference(baseFString);
                return new FName(asset, baseIndex, id + 1);
            }
        }

        // Fallback - name doesn't exist, use normal creation
        return new FName(asset, name);
    }

    /// <summary>
    /// Checks if a class export already has a property definition for the given name.
    /// This checks both LoadedProperties (UE5+) and Children PropertyExports (UE4).
    /// </summary>
    private bool HasExistingPropertyDefinition(ClassExport classExport, string propertyName)
    {
        // Check LoadedProperties (UE5 FProperties)
        if (classExport.LoadedProperties?.Any(x => x.Name.ToString() == propertyName) == true)
            return true;

        // Check Children exports (UE4 PropertyExports)
        if (classExport.Children != null)
        {
            foreach (var childIndex in classExport.Children)
            {
                if (childIndex.IsExport())
                {
                    var child = childIndex.ToExport(Package);
                    if (child?.ObjectName.ToString() == propertyName)
                        return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Tries to create an import for an object reference that doesn't exist in the package.
    /// Looks for the import declaration in the KMS file to get the full path.
    /// </summary>
    private FPackageIndex TryCreateImportForReference(string objectName, string typeHint)
    {
        // Extract class name from typeHint like "Object<ItemUpgradeCategory>"
        string className = "Package";
        if (typeHint.StartsWith("Object<") && typeHint.EndsWith(">"))
        {
            className = typeHint.Substring(7, typeHint.Length - 8);
        }

        // Try to find the import path by checking existing similar imports
        // This is a heuristic approach: look for similar named imports
        if (Package is UAsset uasset)
        {
            foreach (var import in uasset.Imports)
            {
                if (import.ObjectName.ToString().Contains(objectName.Split('_')[0]))
                {
                    // Found a similar import, use its package structure
                    var packagePath = GetImportPackagePath(import);
                    if (!string.IsNullOrEmpty(packagePath))
                    {
                        // Create new import with similar structure
                        return EnsureObjectImported(
                            EnsurePackageImported(packagePath),
                            objectName,
                            className
                        );
                    }
                }
            }
        }

        return new FPackageIndex(0);
    }

    /// <summary>
    /// Gets the package path for an import by traversing its outer chain.
    /// </summary>
    private string GetImportPackagePath(Import import)
    {
        if (Package is not UAsset uasset)
            return string.Empty;

        // Traverse to the root package
        var current = import;
        while (!current.OuterIndex.IsNull() && current.OuterIndex.IsImport())
        {
            current = current.OuterIndex.ToImport(uasset);
        }

        // The root should be a package
        return current.ObjectName.ToString();
    }

    [GeneratedRegex("_(\\d+)$")]
    private static partial Regex NameIdSuffix();
}
