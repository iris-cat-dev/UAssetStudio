using KismetScript.Utilities;
using KismetScript.Utilities.Metadata;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.FieldTypes;
using UAssetAPI.Kismet.Bytecode;
using UAssetAPI.Kismet.Bytecode.Expressions;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;

namespace KismetScript.Decompiler;

/// <summary>
/// Extracts metadata from a UAsset for independent compilation.
/// </summary>
public class MetadataExtractor
{
    private UnrealPackage _asset = default!;

    /// <summary>
    /// Extract metadata from an asset.
    /// Imports are NOT stored — they're derived from KMS import statements + ClassPackages at link time.
    /// Infrastructure exports (class, CDO, functions) are stored; object exports come from KMS.
    /// NameMap is built dynamically at link time.
    /// </summary>
    public KmsMetadata Extract(UnrealPackage asset)
    {
        _asset = asset;

        return new KmsMetadata
        {
            Version = 2,
            Generated = DateTime.UtcNow,
            SourceAsset = Path.GetFileName(asset.FilePath ?? ""),
            Package = ExtractPackageInfo(),
            EngineVersion = ExtractEngineVersion(),
            InfrastructureExports = ExtractInfrastructureExports(),
            ClassPackages = ExtractClassPackages(),
            ObjectDefaults = ExtractObjectDefaults(),
            FieldPaths = ExtractFieldPaths(),
            CdoData = ExtractCdoData(),
        };
    }

    /// <summary>
    /// Extracts only infrastructure exports: class, CDO, and function exports.
    /// Object exports (NormalExport that are not CDO) are excluded — they come from KMS.
    /// </summary>
    private List<ExportMetadata> ExtractInfrastructureExports()
    {
        var exports = new List<ExportMetadata>();

        for (int i = 0; i < _asset.Exports.Count; i++)
        {
            var export = _asset.Exports[i];

            // Include: ClassExport, FunctionExport, CDO exports
            // Exclude: Regular NormalExport (object declarations from KMS)
            bool isInfrastructure = export is ClassExport
                                  || export is FunctionExport
                                  || export.ObjectFlags.HasFlag(EObjectFlags.RF_ClassDefaultObject);

            if (!isInfrastructure) continue;

            var exportMeta = new ExportMetadata
            {
                Index = i + 1,
                ObjectName = export.ObjectName.ToString(),
                ClassName = GetExportClassName(export),
                OuterIndex = export.OuterIndex.Index,
                SuperIndex = export.SuperIndex.IsNull() ? null : export.SuperIndex.Index,
                TemplateIndex = export.TemplateIndex.IsNull() ? null : export.TemplateIndex.Index,
                ObjectFlags = GetObjectFlags(export.ObjectFlags),
                IsCDO = export.ObjectFlags.HasFlag(EObjectFlags.RF_ClassDefaultObject) ? true : null,
                BNotAlwaysLoadedForEditorGame = export.bNotAlwaysLoadedForEditorGame ? true : null,
                BIsAsset = export.bIsAsset ? true : null
            };

            if (export is FunctionExport funcExport)
            {
                exportMeta.FunctionFlags = GetFunctionFlags(funcExport.FunctionFlags);
                exportMeta.LoadedProperties = ExtractLoadedProperties(funcExport.LoadedProperties);
            }

            if (export is ClassExport classExport)
            {
                exportMeta.ClassFlags = GetClassFlags(classExport.ClassFlags);
                exportMeta.LoadedProperties = ExtractLoadedProperties(classExport.LoadedProperties);
            }

            if (export is StructExport structExport && export is not FunctionExport && export is not ClassExport)
            {
                exportMeta.LoadedProperties = ExtractLoadedProperties(structExport.LoadedProperties);
            }

            exportMeta.Dependencies = ExtractExportDependencies(export);

            if (export.Extras != null && export.Extras.Length > 0)
            {
                exportMeta.Extras = Convert.ToBase64String(export.Extras);
            }

            exports.Add(exportMeta);
        }

        return exports;
    }

    /// <summary>
    /// Builds a mapping of class names to their /Script/ package paths.
    /// Scanned from the import table's ClassPackage fields.
    /// </summary>
    private Dictionary<string, string> ExtractClassPackages()
    {
        var classPackages = new Dictionary<string, string>();

        foreach (var import in _asset.Imports)
        {
            var className = import.ClassName.ToString();
            var classPackage = import.ClassPackage.ToString();
            if (!string.IsNullOrEmpty(className) && !string.IsNullOrEmpty(classPackage))
            {
                classPackages.TryAdd(className, classPackage);
            }
        }

        return classPackages;
    }

    /// <summary>
    /// Extracts per-class defaults for non-function, non-class object exports.
    /// Groups exports by their class and extracts common flags and property serialization hints.
    /// </summary>
    private Dictionary<string, ObjectClassDefaults> ExtractObjectDefaults()
    {
        var defaults = new Dictionary<string, ObjectClassDefaults>();

        foreach (var export in _asset.Exports)
        {
            // Skip function, class, and CDO exports
            if (export is FunctionExport || export is ClassExport)
                continue;
            if (export.ObjectFlags.HasFlag(EObjectFlags.RF_ClassDefaultObject))
                continue;

            var className = GetExportClassName(export);
            if (defaults.ContainsKey(className))
                continue; // Already extracted defaults for this class

            var classDefaults = new ObjectClassDefaults
            {
                ObjectFlags = GetObjectFlags(export.ObjectFlags),
                BNotAlwaysLoadedForEditorGame = export.bNotAlwaysLoadedForEditorGame ? true : null,
                BIsAsset = export.bIsAsset ? true : null,
            };

            // Extract template class name from TemplateIndex
            if (!export.TemplateIndex.IsNull() && export.TemplateIndex.IsImport())
            {
                var templateImport = _asset.Imports[-export.TemplateIndex.Index - 1];
                classDefaults.TemplateClassName = templateImport.ObjectName.ToString();
            }

            // Extract property serialization metadata from the export's data
            if (export is NormalExport normalExport && normalExport.Data != null)
            {
                classDefaults.PropertyMeta = ExtractPropertySerializationMeta(normalExport.Data);
            }

            defaults[className] = classDefaults;
        }

        return defaults;
    }

    /// <summary>
    /// Extracts property serialization hints from export data.
    /// Only stores metadata for properties that need special serialization
    /// (MapProperty, StructProperty with custom types, etc.)
    /// </summary>
    private Dictionary<string, PropertySerializationMeta>? ExtractPropertySerializationMeta(List<PropertyData> data)
    {
        var meta = new Dictionary<string, PropertySerializationMeta>();

        foreach (var prop in data)
        {
            var propName = prop.Name?.ToString();
            if (string.IsNullOrEmpty(propName)) continue;

            if (prop is MapPropertyData mapProp)
            {
                var m = new PropertySerializationMeta { Type = "MapProperty" };
                if (mapProp.KeyType?.Value?.Value != null)
                    m.KeyType = mapProp.KeyType.Value.Value;
                else if (mapProp.Value.Count > 0)
                    m.KeyType = mapProp.Value.Keys.First().PropertyType.Value;
                if (mapProp.ValueType?.Value?.Value != null)
                    m.ValueType = mapProp.ValueType.Value.Value;
                else if (mapProp.Value.Count > 0)
                    m.ValueType = mapProp.Value.Values.First().PropertyType.Value;
                meta[propName] = m;
            }
            else if (prop is StructPropertyData structProp)
            {
                var structType = structProp.StructType?.Value?.Value;
                if (structType != null && structType != "Generic")
                {
                    meta[propName] = new PropertySerializationMeta
                    {
                        Type = "StructProperty",
                        StructType = structType
                    };
                }
            }
        }

        return meta.Count > 0 ? meta : null;
    }

    private PackageMetadata ExtractPackageInfo()
    {
        return new PackageMetadata
        {
            Name = GetPackageName(),
            Guid = _asset.PackageGuid.ToString(),
            Flags = GetPackageFlags(),
            LegacyFileVersion = _asset.LegacyFileVersion,
            UsesEventDrivenLoader = _asset.UsesEventDrivenLoader,
            IsUnversioned = _asset.IsUnversioned,
            PackageSource = _asset.PackageSource,
            BulkData = _asset.BulkData != null && _asset.BulkData.Length > 0
                ? Convert.ToBase64String(_asset.BulkData)
                : null
        };
    }

    private string GetPackageName()
    {
        // Try to get the package name from the asset
        // Usually it's the first export's outer chain root or can be derived from FilePath
        if (_asset.FilePath != null)
        {
            var fileName = Path.GetFileNameWithoutExtension(_asset.FilePath);
            return $"/Game/{fileName}";
        }
        return "/Game/Unknown";
    }

    private List<string> GetPackageFlags()
    {
        var flags = new List<string>();
        var packageFlags = _asset.PackageFlags;

        foreach (EPackageFlags flag in Enum.GetValues(typeof(EPackageFlags)))
        {
            if (flag != EPackageFlags.PKG_None && (packageFlags & flag) != 0)
            {
                flags.Add(flag.ToString());
            }
        }

        return flags;
    }

    private EngineVersionMetadata ExtractEngineVersion()
    {
        var metadata = new EngineVersionMetadata
        {
            ObjectVersion = _asset.ObjectVersion.ToString(),
            ObjectVersionUE5 = _asset.ObjectVersionUE5.ToString(),
            CustomVersions = new Dictionary<string, int>()
        };

        if (_asset.CustomVersionContainer != null)
        {
            foreach (var cv in _asset.CustomVersionContainer)
            {
                if (cv.FriendlyName != null)
                {
                    metadata.CustomVersions[cv.FriendlyName] = cv.Version;
                }
            }
        }

        return metadata;
    }

    private string GetExportClassName(Export export)
    {
        if (export.ClassIndex.IsImport())
            return _asset.Imports[-export.ClassIndex.Index - 1].ObjectName.ToString();
        if (export.ClassIndex.IsExport())
            return _asset.Exports[export.ClassIndex.Index - 1].ObjectName.ToString();
        return "Object";
    }

    private List<string> GetObjectFlags(EObjectFlags flags)
    {
        var result = new List<string>();

        foreach (EObjectFlags flag in Enum.GetValues(typeof(EObjectFlags)))
        {
            if (flag != EObjectFlags.RF_NoFlags && (flags & flag) != 0)
            {
                result.Add(flag.ToString());
            }
        }

        return result;
    }

    private List<string> GetFunctionFlags(EFunctionFlags flags)
    {
        var result = new List<string>();

        foreach (EFunctionFlags flag in Enum.GetValues(typeof(EFunctionFlags)))
        {
            // Skip FUNC_None and FUNC_AllFlags (composite flags)
            if (flag == EFunctionFlags.FUNC_None || flag == EFunctionFlags.FUNC_AllFlags)
                continue;

            if (((uint)flags & (uint)flag) != 0)
            {
                result.Add(flag.ToString());
            }
        }

        return result.Count > 0 ? result : null!;
    }

    private List<string>? GetClassFlags(EClassFlags flags)
    {
        var result = new List<string>();

        foreach (EClassFlags flag in Enum.GetValues(typeof(EClassFlags)))
        {
            if (flag != EClassFlags.CLASS_None && (flags & flag) != 0)
            {
                result.Add(flag.ToString());
            }
        }

        return result.Count > 0 ? result : null;
    }

    private ExportDependencies? ExtractExportDependencies(Export export)
    {
        // Extract dependencies directly from the export object
        var hasDeps = export.SerializationBeforeSerializationDependencies.Count > 0 ||
                      export.CreateBeforeSerializationDependencies.Count > 0 ||
                      export.SerializationBeforeCreateDependencies.Count > 0 ||
                      export.CreateBeforeCreateDependencies.Count > 0;

        if (!hasDeps)
            return null;

        return new ExportDependencies
        {
            SerializationBeforeSerialization = export.SerializationBeforeSerializationDependencies.Count > 0
                ? export.SerializationBeforeSerializationDependencies.Select(x => x.Index).ToList()
                : null,
            CreateBeforeSerialization = export.CreateBeforeSerializationDependencies.Count > 0
                ? export.CreateBeforeSerializationDependencies.Select(x => x.Index).ToList()
                : null,
            SerializationBeforeCreate = export.SerializationBeforeCreateDependencies.Count > 0
                ? export.SerializationBeforeCreateDependencies.Select(x => x.Index).ToList()
                : null,
            CreateBeforeCreate = export.CreateBeforeCreateDependencies.Count > 0
                ? export.CreateBeforeCreateDependencies.Select(x => x.Index).ToList()
                : null
        };
    }

    private List<FPropertyMetadata>? ExtractLoadedProperties(FProperty[]? properties)
    {
        if (properties == null || properties.Length == 0)
            return null;

        return properties.Select(ExtractFProperty).ToList();
    }

    private FPropertyMetadata ExtractFProperty(FProperty prop)
    {
        var meta = new FPropertyMetadata
        {
            SerializedType = prop.SerializedType?.ToString() ?? prop.GetType().Name.Replace("F", "").Replace("Property", "Property"),
            Name = prop.Name?.ToString() ?? "",
            Flags = GetObjectFlagsAsStrings(prop.Flags),
            ArrayDim = prop.ArrayDim.ToString(),
            ElementSize = prop.ElementSize,
            PropertyFlags = GetPropertyFlagsAsStrings(prop.PropertyFlags),
            RepIndex = prop.RepIndex,
            RepNotifyFunc = prop.RepNotifyFunc?.ToString(),
            BlueprintReplicationCondition = prop.BlueprintReplicationCondition.ToString()
        };

        // Type-specific properties
        switch (prop)
        {
            case FClassProperty classProperty:
                meta.PropertyClass = classProperty.PropertyClass.Index;
                meta.MetaClass = classProperty.MetaClass.Index;
                break;
            case FSoftClassProperty softClassProperty:
                meta.PropertyClass = softClassProperty.PropertyClass.Index;
                meta.MetaClass = softClassProperty.MetaClass.Index;
                break;
            case FObjectProperty objectProperty:
                meta.PropertyClass = objectProperty.PropertyClass.Index;
                break;
            case FDelegateProperty delegateProperty:
                meta.SignatureFunction = delegateProperty.SignatureFunction.Index;
                break;
            case FInterfaceProperty interfaceProperty:
                meta.InterfaceClass = interfaceProperty.InterfaceClass.Index;
                break;
            case FStructProperty structProperty:
                meta.Struct = structProperty.Struct.Index;
                break;
            case FEnumProperty enumProperty:
                meta.Enum = enumProperty.Enum.Index;
                if (enumProperty.UnderlyingProp != null)
                    meta.UnderlyingProp = ExtractFProperty(enumProperty.UnderlyingProp);
                break;
            case FByteProperty byteProperty:
                meta.Enum = byteProperty.Enum.Index;
                break;
            case FBoolProperty boolProperty:
                meta.FieldSize = boolProperty.FieldSize;
                meta.ByteOffset = boolProperty.ByteOffset;
                meta.ByteMask = boolProperty.ByteMask;
                meta.FieldMask = boolProperty.FieldMask;
                meta.NativeBool = boolProperty.NativeBool;
                meta.BoolValue = boolProperty.Value;
                break;
            case FArrayProperty arrayProperty:
                if (arrayProperty.Inner != null)
                    meta.Inner = ExtractFProperty(arrayProperty.Inner);
                break;
            case FSetProperty setProperty:
                if (setProperty.ElementProp != null)
                    meta.ElementProp = ExtractFProperty(setProperty.ElementProp);
                break;
            case FMapProperty mapProperty:
                if (mapProperty.KeyProp != null)
                    meta.KeyProp = ExtractFProperty(mapProperty.KeyProp);
                if (mapProperty.ValueProp != null)
                    meta.ValueProp = ExtractFProperty(mapProperty.ValueProp);
                break;
        }

        return meta;
    }

    private List<string>? GetObjectFlagsAsStrings(EObjectFlags flags)
    {
        if (flags == EObjectFlags.RF_NoFlags)
            return null;

        var result = new List<string>();
        foreach (EObjectFlags flag in Enum.GetValues(typeof(EObjectFlags)))
        {
            if (flag != EObjectFlags.RF_NoFlags && (flags & flag) != 0)
            {
                result.Add(flag.ToString());
            }
        }
        return result.Count > 0 ? result : null;
    }

    private List<string>? GetPropertyFlagsAsStrings(EPropertyFlags flags)
    {
        if (flags == EPropertyFlags.CPF_None)
            return null;

        var result = new List<string>();
        foreach (EPropertyFlags flag in Enum.GetValues(typeof(EPropertyFlags)))
        {
            if (flag != EPropertyFlags.CPF_None && ((ulong)flags & (ulong)flag) != 0)
            {
                result.Add(flag.ToString());
            }
        }
        return result.Count > 0 ? result : null;
    }

    private Dictionary<string, Dictionary<string, FieldPathMetadata>> ExtractFieldPaths()
    {
        var result = new Dictionary<string, Dictionary<string, FieldPathMetadata>>();

        foreach (var export in _asset.Exports)
        {
            if (export is not FunctionExport funcExport)
                continue;

            var funcName = funcExport.ObjectName.ToString();
            var fieldPaths = new Dictionary<string, FieldPathMetadata>();

            var bytecode = funcExport.ScriptBytecode;
            if (bytecode == null)
                continue;

            // Traverse all expressions and extract property pointers
            foreach (var expr in bytecode.Flatten())
            {
                ExtractPropertyPointersFromExpression(expr, fieldPaths);
            }

            if (fieldPaths.Count > 0)
            {
                result[funcName] = fieldPaths;
            }
        }

        return result;
    }

    private void ExtractPropertyPointersFromExpression(KismetExpression expr, Dictionary<string, FieldPathMetadata> fieldPaths)
    {
        KismetPropertyPointer? pointer = expr switch
        {
            EX_ArrayConst arrayConst => arrayConst.InnerProperty,
            EX_Context ctx => ctx.RValuePointer,
            EX_DefaultVariable defaultVariable => defaultVariable.Variable,
            EX_InstanceVariable instanceVariable => instanceVariable.Variable,
            EX_Let let => let.Value,
            EX_LetValueOnPersistentFrame letPersistent => letPersistent.DestinationProperty,
            EX_LocalVariable localVariable => localVariable.Variable,
            EX_LocalOutVariable localOutVariable => localOutVariable.Variable,
            EX_PropertyConst propertyConst => propertyConst.Property,
            EX_StructMemberContext structMember => structMember.StructMemberExpression,
            EX_MapConst mapConst => mapConst.KeyProperty, // Also has ValueProperty
            EX_SetConst setConst => setConst.InnerProperty,
            EX_ClassSparseDataVariable sparseData => sparseData.Variable,
            _ => null
        };

        if (pointer?.New != null && pointer.New.Path.Length > 0)
        {
            var propertyName = pointer.New.Path[0].ToString();
            if (!fieldPaths.ContainsKey(propertyName))
            {
                fieldPaths[propertyName] = new FieldPathMetadata
                {
                    Path = pointer.New.Path.Select(p => p.ToString()).ToList(),
                    ResolvedOwner = pointer.New.ResolvedOwner.Index,
                    EmptyPath = null
                };
            }
        }
        else if (pointer?.New != null && pointer.New.Path.Length == 0)
        {
            // Handle empty path case for K2Node variables
            // We need to find the associated variable name from the context
        }

        // Handle MapConst's ValueProperty separately
        if (expr is EX_MapConst mapConst2 && mapConst2.ValueProperty?.New != null && mapConst2.ValueProperty.New.Path.Length > 0)
        {
            var propertyName = mapConst2.ValueProperty.New.Path[0].ToString();
            if (!fieldPaths.ContainsKey(propertyName))
            {
                fieldPaths[propertyName] = new FieldPathMetadata
                {
                    Path = mapConst2.ValueProperty.New.Path.Select(p => p.ToString()).ToList(),
                    ResolvedOwner = mapConst2.ValueProperty.New.ResolvedOwner.Index,
                    EmptyPath = null
                };
            }
        }
    }

    private Dictionary<string, CdoMetadata> ExtractCdoData()
    {
        var result = new Dictionary<string, CdoMetadata>();

        // Find all CDO exports
        var cdoExports = _asset.Exports
            .OfType<NormalExport>()
            .Where(x => x.ObjectFlags.HasFlag(EObjectFlags.RF_ClassDefaultObject))
            .ToList();

        foreach (var cdo in cdoExports)
        {
            var cdoName = cdo.ObjectName.ToString();
            var cdoMeta = new CdoMetadata
            {
                SubObjects = ExtractSubObjects(cdo),
                PreservedProperties = ExtractPreservedProperties(cdo)
            };

            result[cdoName] = cdoMeta;
        }

        return result;
    }

    private List<SubObjectMetadata>? ExtractSubObjects(NormalExport cdoExport)
    {
        var cdoIndex = _asset.Exports.IndexOf(cdoExport);
        var subObjects = _asset.Exports
            .OfType<NormalExport>()
            .Where(x => !x.OuterIndex.IsNull() &&
                        x.OuterIndex.Index == cdoIndex + 1 &&  // Export indices are 1-based
                        !x.ObjectName.ToString().StartsWith("Default__"))
            .Select(subObj => new SubObjectMetadata
            {
                Name = subObj.ObjectName.ToString(),
                ClassName = GetExportClassName(subObj),
                OuterIndex = cdoIndex + 1,
                ExportIndex = _asset.Exports.IndexOf(subObj) + 1
            })
            .ToList();

        return subObjects.Count > 0 ? subObjects : null;
    }

    private List<string>? ExtractPreservedProperties(NormalExport cdoExport)
    {
        // These are properties that should be preserved during compilation
        // They are typically special blueprint properties
        var preservedProps = new List<string>();

        if (cdoExport.Data != null)
        {
            foreach (var prop in cdoExport.Data)
            {
                var propName = prop.Name.ToString();
                // Preserve special blueprint properties
                if (propName == "SimpleConstructionScript" ||
                    propName == "InheritableComponentHandler" ||
                    propName == "UberGraphFramePointerProperty" ||
                    propName == "UberGraphFunction" ||
                    propName == "bLegacyNeedToPurgeSkelRefs" ||
                    propName.StartsWith("GeneratedClass") ||
                    propName.StartsWith("Skeleton"))
                {
                    preservedProps.Add(propName);
                }
            }
        }

        return preservedProps.Count > 0 ? preservedProps : null;
    }
}
