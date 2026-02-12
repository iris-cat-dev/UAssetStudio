using System.Diagnostics;
using System.Text.RegularExpressions;
using KismetScript.Linker.Decompiler;
using KismetScript.Linker.Decompiler.Analysis;
using KismetScript.Linker.Decompiler.Context;
using KismetScript.Linker.Decompiler.Context.Nodes;
using KismetScript.Linker.Decompiler.Context.Properties;
using KismetScript.Linker.Decompiler.Passes;
using KismetScript.Parser;
using KismetScript.Utilities;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.Kismet.Bytecode;
using UAssetAPI.Kismet.Bytecode.Expressions;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;

namespace KismetScript.Decompiler;

public partial class KismetDecompiler
{
    enum ContextType
    {
        Default,
        Interface
    }

    class Context
    {
        public string Expression { get; set; } = string.Empty;
        public ContextType Type { get; set; }
    }

    private UnrealPackage _asset = default!;
    private FunctionExport _function = default!;
    
    private bool _useFullPropertyNames = false;
    private bool _useFullFunctionNames = false;
    private bool _insideExecutionFlow = false;
    private FunctionState _functionState = default!;
    
    private readonly IndentedWriter _writer;
    private Context? _context;
    private ClassExport _class = default!;
    private PackageAnalysisResult _analysisResult = default!;

    private static EClassFlags[] classModifierFlags = new[] { EClassFlags.CLASS_Abstract };

    public KismetDecompiler(TextWriter writer)
    {
        _writer = new IndentedWriter(writer);
    }

    public void Decompile(UnrealPackage asset)
    {
        _asset = asset;
        _class = _asset.GetClassExport();

        var analyser = new PackageAnalyser();
        _analysisResult = analyser.Analyse(asset);

        if (_class != null)
        {
            //_writer.WriteLine($"// LegacyFileVersion={_asset.LegacyFileVersion}");
            //_writer.WriteLine($"// UsesEventDrivenLoader={_asset.UsesEventDrivenLoader}");

            //WriteImportsOld();
            WriteBuiltins();
            WriteImports();
            WriteClass();
        }
        else
        {
            // Asset without ClassExport - could be a data asset or other asset type
            WriteImports();

            // Write data exports (NormalExports that are not CDOs or sub-objects)
            WriteDataExports();

            // Group functions by their outer class
            var functionsByClass = asset.Exports
                .Where(x => x is FunctionExport)
                .Cast<FunctionExport>()
                .GroupBy(x => x.OuterIndex.IsNull() ? null : x.OuterIndex.ToExport(asset))
                .ToList();

            foreach (var group in functionsByClass)
            {
                var classExport = group.Key;
                if (classExport is ClassExport cls)
                {
                    // Write class definition
                    var classBaseClass = _asset.GetName(cls.SuperStruct);
                    var className = cls.ObjectName.ToString();

                    _writer.WriteLine($"class {className} : {classBaseClass} {{");
                    _writer.Push();

                    foreach (var func in group)
                    {
                        try
                        {
                            DecompileFunction(func);
                        }
                        catch (Exception ex)
                        {
                            _writer.WriteLine($"// Error decompiling function {FormatIdentifier(func.ObjectName.ToString())}: {ex.Message}");
                            _writer.WriteLine();
                        }
                    }

                    _writer.Pop();
                    _writer.WriteLine("}\n");
                }
                else
                {
                    // Functions without a clear class, decompile them directly
                    foreach (var func in group)
                    {
                        DecompileFunction(func);
                    }
                }
            }
        }
    }

    private void WriteBuiltins()
    {
    }

    /// <summary>
    /// Write data exports for assets without ClassExport (data assets).
    /// These are NormalExports that represent standalone data objects.
    /// </summary>
    private void WriteDataExports()
    {
        // Find all NormalExports that are:
        // 1. Top-level (OuterIndex is null or points to package)
        // 2. Not CDOs (don't start with Default__)
        // 3. Not FunctionExports
        var dataExports = _asset.Exports
            .OfType<NormalExport>()
            .Where(x => x is not FunctionExport &&
                        x is not ClassExport &&
                        !x.ObjectName.ToString().StartsWith("Default__") &&
                        (x.OuterIndex.IsNull() || !x.OuterIndex.IsExport()))
            .ToList();

        foreach (var export in dataExports)
        {
            WriteDataExport(export);
        }

        // Also write any sub-objects of these data exports
        foreach (var export in dataExports)
        {
            WriteDataExportSubObjects(export);
        }
    }

    /// <summary>
    /// Write a single data export as an object declaration.
    /// </summary>
    private void WriteDataExport(NormalExport export)
    {
        var className = GetExportClassName(export);
        var objectName = FormatIdentifier(export.ObjectName.ToString());

        _writer.WriteLine($"object {objectName} : {FormatIdentifier(className)} {{");
        _writer.Push();

        if (export.Data != null)
        {
            foreach (var propData in export.Data)
            {
                var propType = InferPropertyType(propData);
                var propName = FormatIdentifier(propData.Name.ToString());
                var propValue = GetDecompiledPropertyValue(propData);
                _writer.WriteLine($"{propType} {propName} = {propValue};");
            }
        }

        _writer.Pop();
        _writer.WriteLine("}");
        _writer.WriteLine();
    }

    /// <summary>
    /// Write sub-objects of a data export.
    /// </summary>
    private void WriteDataExportSubObjects(NormalExport parentExport)
    {
        var subObjects = _asset.Exports
            .OfType<NormalExport>()
            .Where(x => x is not FunctionExport &&
                        !x.OuterIndex.IsNull() &&
                        x.OuterIndex.IsExport() &&
                        x.OuterIndex.ToExport(_asset) == parentExport &&
                        !x.ObjectName.ToString().StartsWith("Default__"))
            .ToList();

        foreach (var subObj in subObjects)
        {
            WriteDataExport(subObj);
            // Recursively write sub-objects of sub-objects
            WriteDataExportSubObjects(subObj);
        }
    }

    #region CDO and Property Value Support

    /// <summary>
    /// Find the Class Default Object (CDO) export for a class.
    /// CDO names follow the pattern "Default__ClassName".
    /// </summary>
    private NormalExport? FindCDOForClass(ClassExport classExport)
    {
        var cdoName = $"Default__{classExport.ObjectName}";
        return _asset.Exports
            .OfType<NormalExport>()
            .FirstOrDefault(x => x.ObjectName.ToString() == cdoName);
    }

    /// <summary>
    /// Find sub-object exports that are children of the CDO.
    /// These are objects with OuterIndex pointing to the CDO.
    /// </summary>
    private IEnumerable<NormalExport> FindSubObjectExports(NormalExport cdoExport)
    {
        return _asset.Exports
            .OfType<NormalExport>()
            .Where(x => !x.OuterIndex.IsNull() &&
                        x.OuterIndex.ToExport(_asset) == cdoExport &&
                        !x.ObjectName.ToString().StartsWith("Default__"));
    }

    /// <summary>
    /// Write object declarations for sub-objects of the CDO.
    /// Syntax: object Name : ClassName { Type PropName = Value; ... }
    /// </summary>
    private void WriteSubObjectDeclarations(NormalExport cdoExport)
    {
        var subObjects = FindSubObjectExports(cdoExport).ToList();
        if (!subObjects.Any()) return;

        foreach (var subObj in subObjects)
        {
            var className = GetExportClassName(subObj);
            _writer.WriteLine($"object {FormatIdentifier(subObj.ObjectName.ToString())} : {FormatIdentifier(className)} {{");
            _writer.Push();

            foreach (var propData in subObj.Data)
            {
                var propType = InferPropertyType(propData);
                var propName = FormatIdentifier(propData.Name.ToString());
                var propValue = GetDecompiledPropertyValue(propData);
                _writer.WriteLine($"{propType} {propName} = {propValue};");
            }

            _writer.Pop();
            _writer.WriteLine("}");
            _writer.WriteLine();
        }
    }

    /// <summary>
    /// Get the class name for an export.
    /// </summary>
    private string GetExportClassName(Export export)
    {
        if (export.ClassIndex.IsImport())
            return _asset.Imports[-export.ClassIndex.Index - 1].ObjectName.ToString();
        if (export.ClassIndex.IsExport())
            return _asset.Exports[export.ClassIndex.Index - 1].ObjectName.ToString();
        return "Object";
    }

    /// <summary>
    /// Infer the KMS type from a PropertyData instance.
    /// </summary>
    private string InferPropertyType(PropertyData propData)
    {
        return propData switch
        {
            FloatPropertyData => "float",
            DoublePropertyData => "double",
            IntPropertyData => "int",
            Int64PropertyData => "int64",
            Int16PropertyData => "int16",
            Int8PropertyData => "int8",
            UInt16PropertyData => "uint16",
            UInt32PropertyData => "uint32",
            UInt64PropertyData => "uint64",
            BoolPropertyData => "bool",
            BytePropertyData => "byte",
            StrPropertyData => "string",
            NamePropertyData => "Name",
            TextPropertyData => "Text",
            ObjectPropertyData objProp => GetObjectPropertyType(objProp),
            ArrayPropertyData arrayProp => GetArrayPropertyType(arrayProp),
            MapPropertyData mapProp => GetMapPropertyType(mapProp),
            StructPropertyData structProp => $"Struct<{structProp.StructType}>",
            EnumPropertyData enumProp => $"Enum<{enumProp.EnumType}>",
            SoftObjectPropertyData => "SoftObject",
            GuidPropertyData => "Guid",
            _ => propData.PropertyType.ToString().Replace("Property", "")
        };
    }

    /// <summary>
    /// Get the type string for a MapPropertyData.
    /// </summary>
    private string GetMapPropertyType(MapPropertyData mapProp)
    {
        // TODO: The grammar doesn't support Map<KeyType, ValueType> syntax with comma
        // For now, return a generic Object type until grammar supports multi-parameter generics
        return "Object";
    }

    /// <summary>
    /// Get the type string for an ObjectPropertyData.
    /// </summary>
    private string GetObjectPropertyType(ObjectPropertyData objProp)
    {
        if (objProp.Value.IsNull())
            return "Object";

        string className;
        if (objProp.Value.IsImport())
        {
            var import = _asset.Imports[-objProp.Value.Index - 1];
            className = import.ClassName.ToString();
        }
        else if (objProp.Value.IsExport())
        {
            var export = _asset.Exports[objProp.Value.Index - 1];
            className = GetExportClassName(export);
        }
        else
        {
            className = "Object";
        }

        return $"Object<{FormatIdentifier(className)}>";
    }

    /// <summary>
    /// Get the type string for an ArrayPropertyData.
    /// </summary>
    private string GetArrayPropertyType(ArrayPropertyData arrayProp)
    {
        var innerType = arrayProp.ArrayType?.ToString() ?? "Object";

        // Convert inner type to decompiled form
        var decompiledInnerType = innerType switch
        {
            "FloatProperty" => "float",
            "IntProperty" => "int",
            "BoolProperty" => "bool",
            "StrProperty" => "string",
            "ByteProperty" => "byte",
            "ObjectProperty" => "Object",
            "StructProperty" => arrayProp.DummyStruct != null
                ? $"Struct<{arrayProp.DummyStruct.StructType}>"
                : "Struct",
            _ => innerType.Replace("Property", "")
        };

        // For ObjectProperty arrays, try to get the actual object type
        if (innerType == "ObjectProperty" && arrayProp.Value?.Length > 0)
        {
            var firstElement = arrayProp.Value[0] as ObjectPropertyData;
            if (firstElement != null && !firstElement.Value.IsNull())
            {
                decompiledInnerType = GetObjectPropertyType(firstElement);
            }
        }

        return $"Array<{decompiledInnerType}>";
    }

    /// <summary>
    /// Convert a PropertyData value to its KMS representation.
    /// </summary>
    private string GetDecompiledPropertyValue(PropertyData propData)
    {
        return propData switch
        {
            FloatPropertyData floatProp => FormatFloat(floatProp.Value),
            DoublePropertyData doubleProp => $"{doubleProp.Value}d",
            IntPropertyData intProp => intProp.Value.ToString(),
            Int64PropertyData int64Prop => $"{int64Prop.Value}L",
            Int16PropertyData int16Prop => int16Prop.Value.ToString(),
            Int8PropertyData int8Prop => int8Prop.Value.ToString(),
            UInt16PropertyData uint16Prop => uint16Prop.Value.ToString(),
            UInt32PropertyData uint32Prop => $"{uint32Prop.Value}u",
            UInt64PropertyData uint64Prop => $"{uint64Prop.Value}uL",
            BoolPropertyData boolProp => boolProp.Value ? "true" : "false",
            BytePropertyData byteProp => byteProp.Value.ToString(),
            StrPropertyData strProp => FormatString(strProp.Value?.ToString() ?? ""),
            NamePropertyData nameProp => FormatString(nameProp.Value?.ToString() ?? ""),
            TextPropertyData textProp => FormatString(textProp.Value?.ToString() ?? ""),
            ObjectPropertyData objProp => GetObjectReference(objProp.Value),
            ArrayPropertyData arrayProp => GetArrayLiteral(arrayProp),
            MapPropertyData mapProp => GetMapLiteral(mapProp),
            StructPropertyData structProp => GetStructLiteral(structProp),
            EnumPropertyData enumProp => FormatIdentifier(enumProp.Value?.ToString() ?? "None"),
            SoftObjectPropertyData softProp => FormatString(softProp.Value.AssetPath.AssetName?.ToString() ?? ""),
            GuidPropertyData guidProp => FormatGuid(guidProp.Value),
            _ => $"__unsupported_{propData.PropertyType}__"
        };
    }

    /// <summary>
    /// Format a float value with proper suffix using round-trip format for exact precision.
    /// Preserves negative zero (-0.0f).
    /// </summary>
    private string FormatFloat(float value)
    {
        // Detect negative zero: value == 0 but has the sign bit set
        if (value == 0.0f && float.IsNegativeInfinity(1.0f / value))
        {
            return "-0.0f";
        }
        // Use "R" (round-trip) format to preserve exact binary representation
        var str = value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        // Ensure there's a decimal point or 'E' so it's recognized as float
        if (!str.Contains('.') && !str.Contains('E') && !str.Contains('e'))
        {
            str += ".0";
        }
        return str + "f";
    }

    /// <summary>
    /// Format a double value using round-trip format for exact precision.
    /// Preserves negative zero (-0.0).
    /// </summary>
    private static string FormatDouble(double value)
    {
        // Detect negative zero: value == 0 but has the sign bit set
        if (value == 0.0 && double.IsNegativeInfinity(1.0 / value))
        {
            return "-0.0";
        }
        return value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Get the object reference as a symbol name.
    /// </summary>
    private string GetObjectReference(FPackageIndex index)
    {
        if (index.IsNull())
            return "null";

        if (index.IsImport())
        {
            var import = _asset.Imports[-index.Index - 1];
            return FormatIdentifier(import.ObjectName.ToString());
        }

        if (index.IsExport())
        {
            var export = _asset.Exports[index.Index - 1];
            return FormatIdentifier(export.ObjectName.ToString());
        }

        return "null";
    }

    /// <summary>
    /// Convert an ArrayPropertyData to its array literal representation.
    /// </summary>
    private string GetArrayLiteral(ArrayPropertyData arrayProp)
    {
        if (arrayProp.Value == null || arrayProp.Value.Length == 0)
            return "[]";

        var elements = arrayProp.Value.Select(GetDecompiledPropertyValue);
        return $"[{string.Join(", ", elements)}]";
    }

    /// <summary>
    /// Convert a StructPropertyData to its literal representation.
    /// </summary>
    private string GetStructLiteral(StructPropertyData structProp)
    {
        if (structProp.Value == null || structProp.Value.Count == 0)
            return "{}";

        var fields = structProp.Value.Select(p =>
        {
            var propValue = GetDecompiledPropertyValue(p);
            // Handle unsupported properties that would create invalid syntax
            if (propValue.StartsWith("/*") && propValue.EndsWith("*/"))
            {
                // Skip the unsupported property entirely or provide a valid default
                return null;
            }
            return $"{FormatIdentifier(p.Name.ToString())}: {propValue}";
        }).Where(f => f != null);

        return $"{{ {string.Join(", ", fields)} }}";
    }

    /// <summary>
    /// Convert a MapPropertyData to its literal representation.
    /// </summary>
    private string GetMapLiteral(MapPropertyData mapProp)
    {
        if (mapProp.Value == null || mapProp.Value.Count == 0)
            return "{}";

        var entries = new List<string>();
        foreach (var kvp in mapProp.Value)
        {
            var key = GetDecompiledPropertyValue(kvp.Key);
            var value = GetDecompiledPropertyValue(kvp.Value);
            entries.Add($"{key}: {value}");
        }
        return $"{{ {string.Join(", ", entries)} }}";
    }

    /// <summary>
    /// Format a Guid value as a string literal.
    /// </summary>
    private string FormatGuid(Guid guid)
    {
        return $"\"{guid:B}\"";  // Format as {xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx}
    }

    #endregion

    private void WriteClass()
    {
        var classBaseClass = _asset.GetName(_class.SuperStruct);
        var classChildExports = _class.Children
            .Select(x => x.ToExport(_asset));

        var classProperties = classChildExports
            .Where(x => x is PropertyExport)
            .Cast<PropertyExport>()
            .Select(x => (IPropertyData)new PropertyExportData(x))
            .OrderBy(x => _asset.Exports.IndexOf((Export)x.Source))
            .Union(_class.LoadedProperties.Select(x => new FPropertyData(_asset, x)))
            .ToList();

        // Debug: Find all functions in the asset that belong to this class
        var allFunctionsInAsset = _asset.Exports
            .Where(x => x is FunctionExport)
            .Cast<FunctionExport>()
            .ToList();

        var functionsWithThisOuter = allFunctionsInAsset
            .Where(x => !x.OuterIndex.IsNull() && x.OuterIndex.ToExport(_asset) == _class)
            .ToList();

        var classFunctions = classChildExports
            .Where(x => x is FunctionExport)
            .Cast<FunctionExport>()
            .Union(functionsWithThisOuter)
            .OrderBy(x =>
            {
                var idx = _class.FuncMap?.IndexOf(x.ObjectName) ?? -1;
                return idx >= 0 ? idx : _asset.Exports.IndexOf(x);
            });

        // Find CDO (Class Default Object) for this class
        var cdoExport = FindCDOForClass(_class);

        // Build property value map from CDO
        var cdoPropertyValues = new Dictionary<string, PropertyData>();
        if (cdoExport != null)
        {
            foreach (var propData in cdoExport.Data)
            {
                cdoPropertyValues[propData.Name.ToString()] = propData;
            }
        }

        // Write sub-object declarations before class
        if (cdoExport != null)
        {
            WriteSubObjectDeclarations(cdoExport);
        }

        var classModifiers = GetClassModifiers(_class);
        var classAttributes = GetClassAttributes(_class);

        var modifierText = string.Join(" ", classModifiers).Trim();
        var attributeText = string.Join(", ", classAttributes).Trim();
        var nameText = $"class {_class.ObjectName} : {classBaseClass}";

        if (!string.IsNullOrWhiteSpace(attributeText))
            attributeText = $"[{attributeText}]";

        _writer.WriteLine($"{string.Join(" ", new[] { attributeText, modifierText }.Where(x => !string.IsNullOrWhiteSpace(x)))}");
        _writer.WriteLine($"{nameText} {{");
        _writer.Push();

        // Write CDO properties with values as initializers (no type declaration needed for CDO properties)
        if (cdoExport != null && cdoExport.Data.Any())
        {
            foreach (var propData in cdoExport.Data)
            {
                var propType = InferPropertyType(propData);
                var propName = FormatIdentifier(propData.Name.ToString());
                var propValue = GetDecompiledPropertyValue(propData);
                _writer.WriteLine($"{propType} {propName} = {propValue};");
            }
        }

        // Write class-declared properties (without initializers, from PropertyExport/LoadedProperties)
        foreach (var prop in classProperties)
        {
            // Skip if this property is already written from CDO
            if (!cdoPropertyValues.ContainsKey(prop.Name))
            {
                _writer.WriteLine($"{GetDecompiledPropertyText(prop)};");
            }
        }

        foreach (var fun in classFunctions)
        {
            try
            {
                DecompileFunction(fun);
            }
            catch (Exception ex)
            {
                _writer.WriteLine($"// Error decompiling function {FormatIdentifier(fun.ObjectName.ToString())}: {ex.Message}");
                _writer.WriteLine();
            }
        }

        _writer.Pop();
        _writer.WriteLine($"}}");
    }

    private void WriteImports()
    {
        // Collect all root package symbols and group their children by package path
        var importSymbols = _analysisResult.RootSymbols
            .Where(x => x.Import != null)
            .OrderBy(x => x.ImportIndex!.Index)
            .ToList();

        // Build ordered list of package groups, preserving first-seen order
        var packageGroups = new List<(string PackagePath, List<Symbol> Children)>();
        foreach (var symbol in importSymbols)
        {
            if (symbol.Parent != null)
            {
                Trace.WriteLine($"Unexpected non-root import symbol: {symbol}");
                continue;
            }
            if (symbol.Class?.Name != "Package")
            {
                Trace.WriteLine($"Invalid class for package {symbol}");
                continue;
            }

            var pkgPath = symbol.Name;
            var existing = packageGroups.FirstOrDefault(x => x.PackagePath == pkgPath);
            if (existing.Children != null)
                existing.Children.AddRange(symbol.Children);
            else
                packageGroups.Add((pkgPath, new List<Symbol>(symbol.Children)));
        }

        // Emit each package group using from...import syntax
        bool isFirst = true;
        foreach (var (packagePath, children) in packageGroups)
        {
            if (!isFirst)
                _writer.WriteLine();
            isFirst = false;

            if (children.Count == 1)
            {
                // Inline syntax: from "path" import <declaration>;
                _writer.Write($"from {FormatString(packagePath)} import ");
                WriteImportDeclaration(children[0], isInsideClassDecl: false, isInline: true);
            }
            else
            {
                // Block syntax: from "path" import { ... }
                _writer.WriteLine($"from {FormatString(packagePath)} import {{");
                _writer.Push();
                foreach (var child in children)
                    WriteImportDeclaration(child, isInsideClassDecl: false, isInline: false);
                _writer.Pop();
                _writer.WriteLine("}");
            }
        }

        _writer.WriteLine();
    }

    /// <summary>
    /// Writes a single import declaration (class, variable, or function) inside a from...import block.
    /// Does not emit the [Import(...)] attribute. Top-level declarations omit the "public" modifier.
    /// </summary>
    private void WriteImportDeclaration(Symbol symbol, bool isInsideClassDecl, bool isInline)
    {
        var importQueue = new Queue<Symbol>();

        if (symbol.Children.Count > 0)
        {
            if (symbol.Class!.Name == "ArrayProperty")
            {
                if (symbol.Children.Count() != 1)
                    throw new NotImplementedException();

                _writer.Write($"Array<{GetDecompiledTypeName(symbol.Children.First()!.Class!.Name)}> {FormatIdentifier(symbol.Name)}");
                if (isInline)
                    _writer.WriteLine(";");
            }
            else if (isInsideClassDecl)
            {
                _writer.WriteLine($"public {FormatIdentifier(symbol.Class?.Name ?? string.Empty)} {FormatIdentifier(symbol.Name!)};");
                if (!importQueue.Any(x => x.Name == symbol.Name))
                    importQueue.Enqueue(symbol);
            }
            else
            {
                // Class with children (members/functions)
                var modifiers = new List<string>();
                if (symbol.ClassMetadata.IsStaticClass)
                    modifiers.Add("static");
                var modifierText = string.Join(" ", modifiers);
                if (!string.IsNullOrWhiteSpace(modifierText))
                    modifierText += " ";

                if (symbol.Super != null)
                    _writer.WriteLine($"{modifierText}class {FormatIdentifier(symbol.Name)} : {FormatIdentifier(symbol.Super.Name)} {{");
                else
                    _writer.WriteLine($"{modifierText}class {FormatIdentifier(symbol.Name)} {{");
                _writer.Push();
                foreach (var child in symbol.Children)
                    WriteImportDeclaration(child, isInsideClassDecl: true, isInline: false);
                _writer.Pop();
                _writer.WriteLine("}");

                while (importQueue.Count > 0)
                {
                    var queuedSymbol = importQueue.Dequeue();
                    //WriteImport(queuedSymbol);
                }
            }
        }
        else if (symbol.Class?.Name == "Function")
        {
            if (symbol.Name == "Default__Function")
            {
                _writer.WriteLine($"{FormatIdentifier(symbol.Class.Name)} {FormatIdentifier(symbol.Name)};");
            }
            else
            {
                var functionModifiers = new List<string>();
                if (!isInsideClassDecl)
                {
                    // Top-level functions in import blocks don't need "public"
                }
                else
                {
                    functionModifiers.Add("public");
                }
                var functionAttributes = new List<string>() { "UnknownSignature" };

                var functionModifier = symbol.FunctionMetadata.CallingConvention switch
                {
                    CallingConvention.FinalFunction => "sealed",
                    CallingConvention.LocalFinalFunction => "sealed",

                    CallingConvention.VirtualFunction => "virtual",
                    CallingConvention.LocalVirtualFunction => "virtual",

                    CallingConvention.CallMath => "static sealed",
                    _ => "",
                };
                functionModifiers.Add(functionModifier);
                var functionAttribute = symbol.FunctionMetadata.CallingConvention switch
                {
                    CallingConvention.FinalFunction => "FinalFunction",
                    CallingConvention.LocalFinalFunction => "LocalFinalFunction",

                    CallingConvention.VirtualFunction => "VirtualFunction",
                    CallingConvention.LocalVirtualFunction => "LocalVirtualFunction",

                    CallingConvention.CallMath => "MathFunction",
                    _ => ""
                };
                if (!string.IsNullOrWhiteSpace(functionAttribute))
                    functionAttributes.Add(functionAttribute);

                var functionAttributeText = string.Join(", ", functionAttributes);
                if (!string.IsNullOrWhiteSpace(functionAttributeText))
                    functionAttributeText = $"[{functionAttributeText}] ";

                var functionModifierText = string.Join(" ", functionModifiers.Where(x => !string.IsNullOrWhiteSpace(x)));
                if (!string.IsNullOrWhiteSpace(functionModifierText))
                    functionModifierText = $"{functionModifierText} ";

                var functionParameterText =
                    string.Join(", ", symbol.FunctionMetadata.Parameters.Select(x => $"{GetDecompiledTypeName(x.Class!.Name)} {x.Name}"));

                var functionReturnTypeText =
                    symbol.FunctionMetadata.ReturnType == null ? "void" : GetDecompiledTypeName(symbol.FunctionMetadata.ReturnType!.Name);

                _writer.WriteLine($"{functionAttributeText}{functionModifierText}{functionReturnTypeText} {FormatIdentifier(symbol.Name)}({functionParameterText});");
            }
        }
        else
        {
            // Leaf node: variable or empty class
            if (isInsideClassDecl)
            {
                var cls = (symbol.Class?.Name == "Class" || symbol.Class?.Name == "BlueprintGeneratedClass" || symbol.Class == null) ? "object" : symbol.Class.Name;
                _writer.WriteLine($"public {FormatIdentifier(cls)} {FormatIdentifier(symbol.Name)};");
            }
            else
            {
                if (symbol.Class != null && (symbol.Class.Name != "Class" && symbol.Class.Name != "BlueprintGeneratedClass"))
                {
                    // Typed variable: ClassName VarName;
                    _writer.WriteLine($"{FormatIdentifier(symbol.Class.Name)} {FormatIdentifier(symbol.Name)};");
                }
                else
                {
                    // Empty class declaration
                    if (symbol.Super != null)
                        _writer.WriteLine($"class {FormatIdentifier(symbol.Name)} : {FormatIdentifier(symbol.Super.Name)};");
                    else
                        _writer.WriteLine($"class {FormatIdentifier(symbol.Name)};");
                }
            }
        }
    }

    public string DecompileFunction(FunctionExport function)
    {
        _asset ??= (UAsset)function.Asset;
        _function = function;
        _functionState = new FunctionState();
        var root = ExecutePass<CreateBasicNodesPass>(null);
        root = ExecutePass<ResolveJumpTargetsPass>(root);
        root = ExecutePass<ResolveReferencesPass>(root);
        root = ExecutePass<CreateBasicBlocksPass>(root);
        root = ExecutePass<RemoveGotoReturnsPass>(root);
        root = ExecutePass<CreateIfBlocksPass>(root);
        root = ExecutePass<CreateWhileBlocksPass>(root);

        WriteFunction(function, root);

        _writer.Flush();

        return string.Empty;
    }

    private void WriteFunction(FunctionExport function, Node root)
    {
        var functionFlags =
            typeof(EFunctionFlags)
                .GetEnumValues()
                .Cast<EFunctionFlags>()
                .Where(x => (function.FunctionFlags & x) != 0 && x != EFunctionFlags.FUNC_AllFlags);

        var functionModifierFlags = new[] {
            EFunctionFlags.FUNC_Final, EFunctionFlags.FUNC_Static, EFunctionFlags.FUNC_Public, EFunctionFlags.FUNC_Private, EFunctionFlags.FUNC_Protected};

        var functionModifiers = functionFlags.Where(x => functionModifierFlags.Contains(x))
            .Select(x => x.ToString().Replace("FUNC_", "").ToLower())
            .Select(x => x == "final" ? "sealed" : x)
            .ToList();
        if (!function.SuperIndex.IsNull())
            functionModifiers.Add("override");

        var functionAttributes = functionFlags
                .Except(functionModifierFlags)
                .Select(x => x.ToString().Replace("FUNC_", ""))
                .ToList();

        //var functionProperties = function.Children != null ?
        //    function.Children.Select(x => x.ToExport(_asset)).Cast<PropertyExport>() :
        //    _asset.Exports.Where(x => !x.OuterIndex.IsNull() && x.OuterIndex.ToExport(_asset) == function)
        //    .Cast<PropertyExport>();

        var functionChildExports =
            _asset.Exports.Where(x => !x.OuterIndex.IsNull() && x.OuterIndex.ToExport(_asset) == function);

        var functionProperties =
            functionChildExports
            .Where(x => x is PropertyExport)
            .Select(x => (IPropertyData)new PropertyExportData((PropertyExport)x))
            .Union(function.LoadedProperties.Select(x => new FPropertyData(_asset, x)))
            // Deduplicate by property name to avoid repeated local declarations
            .GroupBy(x => x.Name)
            .Select(g => g.First())
            .ToList();

        var functionParams = functionProperties
            .Where(x => (x.PropertyFlags & EPropertyFlags.CPF_Parm) != 0);

        var functionLocals = functionProperties
            .Except(functionParams)
            .GroupBy(x => x.Name)
            .Select(g => g.First())
            .ToList();


        var functionParameterText =
            string.Join(", ", functionParams.Select(GetDecompiledPropertyText));

        var isUbergraphFunction = function.IsUbergraphFunction();

        var functionAttributeText = string.Join(", ", functionAttributes);

        if (!string.IsNullOrWhiteSpace(functionAttributeText))
        {
            _writer.WriteLine($"[{functionAttributeText}]");
        }
        var functionName = function.ObjectName.ToString();
        var functionDeclaration = $"void {FormatIdentifier(functionName)}({functionParameterText}) {{";
        var functionModifierText = string.Join(" ", functionModifiers);
        if (!string.IsNullOrWhiteSpace(functionModifierText))
        {
            functionDeclaration = functionModifierText + " " + functionDeclaration;
        }

        _writer.WriteLine(functionDeclaration);
        _writer.Push();

        if (functionLocals.Any())
        {
            _writer.WriteLine($"// Locals");
            foreach (var functionLocal in functionLocals)
                _writer.WriteLine($"{GetDecompiledPropertyText(functionLocal)};");
            _writer.WriteLine();
        }

        var nextBlockIndex = 1;
        void WriteBlock(Node root)
        {
            foreach (Node block in root.Children)
            {
                if (block is IfBlockNode ifBlock)
                {
                    var isBlockStart = block.ReferencedBy.Count > 0 || (isUbergraphFunction && IsUbergraphEntrypoint(block.CodeStartOffset));
                    string? callingFunctionName = default;
                    if (isBlockStart)
                        callingFunctionName = GetUbergraphEntryFunction(block.CodeStartOffset)?.ObjectName?.ToString();

                    if (isBlockStart)
                        _writer.WriteLine($"{FormatCodeOffset((uint)block.CodeStartOffset, functionName, callingFunctionName)}:");

                    var cond = ifBlock.Condition != null ? FormatExpression(ifBlock.Condition, null) : "true /* null condition */";
                    _writer.WriteLine($"if ({cond}) {{");
                    _writer.Push();
                    WriteBlock(ifBlock);
                    _writer.Pop();
                    _writer.WriteLine($"}}");
                    _writer.WriteLine();
                }
                else if (block is JumpBlockNode whileBlock)
                {
                    var isBlockStart = block.ReferencedBy.Count > 0 || (isUbergraphFunction && IsUbergraphEntrypoint(block.CodeStartOffset));
                    string? callingFunctionName = default;
                    if (isBlockStart)
                        callingFunctionName = GetUbergraphEntryFunction(block.CodeStartOffset)?.ObjectName?.ToString();

                    if (isBlockStart)
                        _writer.WriteLine($"{FormatCodeOffset((uint)block.CodeStartOffset, functionName, callingFunctionName)}:");

                    _writer.WriteLine($"while (true) {{");
                    _writer.Push();
                    var savedInsideExecutionFlow = _insideExecutionFlow;
                    _insideExecutionFlow = true;
                    WriteBlock(whileBlock);
                    _insideExecutionFlow = savedInsideExecutionFlow;
                    _writer.Pop();
                    _writer.WriteLine($"}}");
                    _writer.WriteLine();
                }
                else
                {
                    _writer.WriteLine($"// Block {nextBlockIndex++}");
                    foreach (var node in block.Children)
                    {
                        if (node is ReturnNode returnNode)
                        {
                            if (returnNode.Source is EX_Jump jump)
                            {
                                WriteExpression(node, isUbergraphFunction, "return");
                            }
                            else if (returnNode.Source is EX_JumpIfNot jumpIfNot)
                            {
                                WriteExpression(node, isUbergraphFunction, $"if (!{FormatExpression(jumpIfNot.BooleanExpression, null)}) return");
                            }
                            else
                            {
                                WriteExpression(node, isUbergraphFunction);
                            }
                        }
                        else if (node is JumpNode jumpNode)
                        {
                            if (!jumpNode.Parent!.Children.Any(x => x.Source is EX_PushExecutionFlow) &&
                                !jumpNode.Parent!.Parent!.Children.SelectMany(x => x.Children).Any(x => x.Source is EX_PushExecutionFlow))
                            {
                                if (jumpNode.Source is EX_PopExecutionFlow)
                                {
                                    WriteExpression(node, isUbergraphFunction, "break");
                                }
                                else if (jumpNode.Source is EX_PopExecutionFlowIfNot popExecutionFlowIfNot)
                                {
                                    WriteExpression(node, isUbergraphFunction, $"if (!{FormatExpression(popExecutionFlowIfNot.BooleanExpression, null)}) break");
                                }
                                else
                                {
                                    WriteExpression(node, isUbergraphFunction);
                                }
                            }
                            else
                            {
                                WriteExpression(node, isUbergraphFunction);
                            }
                        }
                        else
                        {
                            WriteExpression(node, isUbergraphFunction);
                        }
  
                    }
                }
                _writer.WriteLine();
            }
        }

        WriteBlock(root);

        _writer.Pop();
        _writer.WriteLine("}\n");
    }

    private void WriteExpression(Node node, bool isUbergraphFunction, string expr)
    {
        string line = "";
        var isBlockStart = node.ReferencedBy.Count > 0 || (isUbergraphFunction && IsUbergraphEntrypoint(node.CodeStartOffset));
        string? callingFunctionName = default;
        if (isBlockStart)
            callingFunctionName = GetUbergraphEntryFunction(node.CodeStartOffset)?.ObjectName?.ToString();

        if (string.IsNullOrWhiteSpace(expr))
        {
            if (isBlockStart)
            {
                line = $"{FormatCodeOffset((uint)node.CodeStartOffset, _function.ObjectName.ToString(), callingFunctionName)}:";
            }
        }
        else
        {
            if (isBlockStart)
            {
                line = $"{FormatCodeOffset((uint)node.CodeStartOffset, _function.ObjectName.ToString(), callingFunctionName)}: {expr}";
            }
            else
            {
                line = $"{expr}";
            }

            if (line.Contains(" //"))
            {
                var parts = line.Split(" //");
                if (!string.IsNullOrWhiteSpace(parts[0]))
                    line = string.Join("; //", parts);
            }
            else if (line.Contains(" /*"))
            {
                var parts = line.Split(" /*");
                if (!string.IsNullOrWhiteSpace(parts[0]))
                    line = string.Join("; /*", parts);
            }
            else
            {
                line += ";";
            }
        }

        // Write the line to output if it's not empty
        if (!string.IsNullOrWhiteSpace(line))
        {
            _writer.WriteLine(line);
        }
    }

    private void WriteExpression(Node node, bool isUbergraphFunction)
    {
        try
        {
            var formatted = node.Source != null ? FormatExpression(node.Source, null) : string.Empty;
            WriteExpression(node, isUbergraphFunction, formatted);
        }
        catch (Exception)
        {
            // Skip malformed expression to preserve compilability of the output
            WriteExpression(node, isUbergraphFunction, string.Empty);
        }
    }

    public IEnumerable<EClassFlags> GetClassFlags(ClassExport classExport)
    {
        var classFlags = typeof(EClassFlags)
            .GetEnumValues()
            .Cast<EClassFlags>()
            .Where(x => (classExport.ClassFlags & x) != 0);
        return classFlags;
    }

    public List<string> GetClassModifiers(ClassExport classExport)
    {
        var classModifiers =
            GetClassFlags(classExport)
            .Where(x => classModifierFlags.Contains(x))
            .Select(GetModifierForClassFlag)
            .ToList();

        return classModifiers;
    }

    public List<string> GetClassAttributes(ClassExport classExport)
    {
        var classAttributes =
                GetClassFlags(classExport)
                .Except(classModifierFlags)
                .Select(x => x.ToString().Replace("CLASS_", "").Trim())
                .ToList();
        return classAttributes;
    }

    private Node ExecutePass<T>(Node? root) where T : IDecompilerPass, new()
    {
        var pass = new T();
        return pass.Execute(new DecompilerContext()
        {
            Asset = _asset,
            Class = _class,
            Function = _function
        }, root);
    }

    private string GetDecompiledTypeName(string classType)
    {
        switch (classType)
        {
            case "Package":
                return "package";
            case "FloatProperty":
                return "float";
            case "IntProperty":
                return "int";
            case "StrProperty":
                return "string";
            case "BoolProperty":
                return "bool";
            case "ByteProperty":
                return "byte";
            case "Class":
                return "class";
            default:
                if (classType != "Property" &&
                    classType.EndsWith("Property"))
                    return FormatIdentifier(classType.Substring(0, classType.IndexOf("Property")));

                return FormatIdentifier(classType);
        }
    }


    private string GetDecompiledTypeName(Import import)
    {
        var classType = import.ClassName.ToString();
        return GetDecompiledTypeName(classType);
    }

    private string GetDecompiledType(IPropertyData prop)
    {
        var classType = prop.TypeName;
        switch (classType)
        {
            case "IntProperty":
                return "int";
            case "StrProperty":
                return "string";
            case "FloatProperty":
                return "float";
            case "InterfaceProperty":
                {
                    var interfaceName = prop.InterfaceClassName;
                    return $"Interface<{interfaceName}>";
                }
            case "StructProperty":
                {
                    var structName = prop.StructName;
                    return $"Struct<{structName}>";
                }
            case "BoolProperty":
                return "bool";
            case "ByteProperty":
                return "byte";
            case "ArrayProperty":
                {
                    if (prop.ArrayInnerProperty != null)
                    {
                        return $"Array<{GetDecompiledType(prop.ArrayInnerProperty)}>";
                    }

                    // TODO
                    return $"Array";
                }
            case "ObjectProperty":
                {
                    return $"Object<{prop.PropertyClassName}>";
                }
            default:
                if (classType != "Property" &&
                    classType.EndsWith("Property"))
                    return classType.Substring(0, classType.IndexOf("Property"));

                return classType;
        }
    }

    private string GetModifierForPropertyFlag(EPropertyFlags flag)
    {
        switch (flag)
        {
            case EPropertyFlags.CPF_ConstParm:
                return "const";
            case EPropertyFlags.CPF_Parm:
                return "";
            case EPropertyFlags.CPF_OutParm:
                return "out";
            case EPropertyFlags.CPF_ReferenceParm:
                return "ref";
            default:
                throw new ArgumentOutOfRangeException(nameof(flag));
        }
    }

    private string GetModifierForClassFlag(EClassFlags flag)
    {
        switch (flag)
        {
            case EClassFlags.CLASS_Abstract:
                return "abstract";
            default:
                throw new ArgumentOutOfRangeException(nameof(flag));
        }
    }

    private string FormatIdentifier(string name, bool allowKeywords = false)
    {
        if (!IdentifierRegex().IsMatch(name) ||
            (!allowKeywords && KismetScriptParser.IsKeyword(name)))
            return $"`{name}`";

        return name;
    }

    private string FormatString(string value)
    {
        if (value.Contains("\\"))
            value = value.Replace("\\", "\\\\");
        if (value.Contains("\""))
            value = value.Replace("\"", "\\\"");
        return $"\"{value}\"";
    }

    private string GetDecompiledPropertyText(IPropertyData prop)
    {
        var flags =
            typeof(EPropertyFlags)
                .GetEnumValues()
                .Cast<EPropertyFlags>()
                .Where(x => (prop.PropertyFlags & x) != 0 && x != EPropertyFlags.CPF_Parm);

        var modifierFlags = new[] {
            EPropertyFlags.CPF_ConstParm, EPropertyFlags.CPF_OutParm, EPropertyFlags.CPF_ReferenceParm};

        var modifiers = flags.Where(x => modifierFlags.Contains(x))
            .Select(GetModifierForPropertyFlag)
            .ToList();

        var attributes = flags
                .Except(modifierFlags)
                .Select(x => x.ToString().Replace("CPF_", "").Replace("Param", "").Replace("Parm", "").Trim())
                .ToList();


        //if (!prop.Property.PropertyFlags.HasFlag(EPropertyFlags.CPF_Parm))
        //{
        //    if (modifiers.Contains("ref"))
        //    {
        //        modifiers.Remove("ref");
        //        attributes.Add("Ref");
        //    }
        //}

        var modifierText = string.Join(" ", modifiers).Trim();
        var attributeText = string.Join(", ", attributes).Trim();
        var nameText = $"{GetDecompiledType(prop)} {FormatIdentifier(prop.Name)}";

        if (!string.IsNullOrWhiteSpace(attributeText))
            attributeText = $"[{attributeText}]";

        var result = string.Join(" ", new[] { attributeText, modifierText, nameText }.Where(x => !string.IsNullOrWhiteSpace(x))).Trim();
        return result;
    }

    private FunctionExport? GetUbergraphEntryFunction(int codeOffset)
    {
        var finalFunctionEntryPoints = _asset.Exports
            .Where(x => x is FunctionExport)
            .Cast<FunctionExport>()
            .Where(x =>
                x.ScriptBytecode
                .Where(x => x is EX_FinalFunction)
                .Cast<EX_FinalFunction>()
                .Where(x => x.StackNode.IsExport() && _asset.GetFunctionExport(x.StackNode).IsUbergraphFunction())
                .Where(x => x.Parameters.Any() && (x.Parameters[0] as EX_IntConst)?.Value == codeOffset)
                .Any()
            );

        var virtualFunctionEntryPoints = _asset.Exports
            .Where(x => x is FunctionExport)
            .Cast<FunctionExport>()
            .Where(x =>
                x.ScriptBytecode
                .Where(x => x is EX_VirtualFunction)
                .Cast<EX_VirtualFunction>()
                .Where(x => x.VirtualFunctionName.ToString().StartsWith("ExecuteUbergraph_"))
                .Where(x => x.Parameters.Any() && (x.Parameters[0] as EX_IntConst)?.Value == codeOffset)
                .Any()
            );

        return finalFunctionEntryPoints.Union(virtualFunctionEntryPoints).SingleOrDefault();
    }

    private bool IsUbergraphEntrypoint(int codeOffset)
    {
        var finalFunctionEntryPoints = _asset.Exports
            .Where(x => x is FunctionExport)
            .Cast<FunctionExport>()
            .SelectMany(x => x.ScriptBytecode)
            .Where(x => x.Token == EExprToken.EX_LocalFinalFunction || x.Token == EExprToken.EX_FinalFunction)
            .Cast<EX_FinalFunction>()
            .Where(x => x.StackNode.IsExport() && _asset.GetFunctionExport(x.StackNode).IsUbergraphFunction())
            .Select(x => x.Parameters.FirstOrDefault() as EX_IntConst)
            .Where(x => x != null)
            .Select(x => x!.Value);

        var virtualFunctionEntryPoints = _asset.Exports
            .Where(x => x is FunctionExport)
            .Cast<FunctionExport>()
            .SelectMany(x => x.ScriptBytecode)
            .Where(x => x.Token == EExprToken.EX_VirtualFunction || x.Token == EExprToken.EX_LocalVirtualFunction)
            .Cast<EX_VirtualFunction>()
            .Where(x => x.VirtualFunctionName.ToString().StartsWith("ExecuteUbergraph_"))
            .Select(x => x.Parameters.FirstOrDefault() as EX_IntConst)
            .Where(x => x != null)
            .Select(x => x!.Value);

        return finalFunctionEntryPoints.Union(virtualFunctionEntryPoints).Contains(codeOffset);
    }

    private string EscapeFullName(string name)
    {
        return name;
    }

    private string GetFunctionClassName(FPackageIndex index)
    {
        var classIndex = _asset.GetOuterIndex(index);
        return _asset.GetName(classIndex);
    }

    private string GetFunctionName(FPackageIndex index)
    {
        return _useFullFunctionNames ?
            EscapeFullName(_asset.GetFullName(index)) :
            _asset.GetName(index);
    }

    private string FormatCodeOffset(uint codeOffset, string? functionName = null, string? callingFunctionName = null)
    {
        var resolvedFunctionName = functionName ?? _function.ObjectName.ToString();
        var currentFunctionName = _function.ObjectName.ToString();
        
        if (callingFunctionName != null &&
            callingFunctionName != resolvedFunctionName)
        {
            // Cross-function entry point: use calling function name as meaningful label
            // e.g., "SetPetActive_788" instead of "ExecuteUbergraph_BP_PetComponent_788_SetPetActive"
            return FormatIdentifier($"{callingFunctionName}_{codeOffset}");
        }
        else if (resolvedFunctionName == currentFunctionName)
        {
            // Internal label within current function: use short format
            // e.g., "L_788" instead of "ExecuteUbergraph_BP_PetComponent_788"
            return FormatIdentifier($"L_{codeOffset}");
        }
        else
        {
            // Cross-function reference: keep full function name for clarity
            return FormatIdentifier($"{resolvedFunctionName}_{codeOffset}");
        }
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z_\\d]*$")]
    private static partial Regex IdentifierRegex();
}
