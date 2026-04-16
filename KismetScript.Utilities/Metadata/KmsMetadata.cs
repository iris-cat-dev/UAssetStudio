namespace KismetScript.Utilities.Metadata;

/// <summary>
/// Root metadata class for .kms.meta files.
/// KMS drives object content, metadata provides structural context.
/// - Imports: derived from KMS import statements + ClassPackages at link time
/// - Infrastructure exports (class, CDO, functions): kept in metadata
/// - Object exports: derived from KMS object declarations (adding/removing objects in KMS → changes in .uasset)
/// - NameMap: built dynamically from all used names
/// </summary>
public class KmsMetadata
{
    public int Version { get; set; } = 2;
    public DateTime? Generated { get; set; }
    public string? SourceAsset { get; set; }
    public PackageMetadata Package { get; set; } = new();
    public EngineVersionMetadata EngineVersion { get; set; } = new();

    /// <summary>
    /// Infrastructure exports only: class, CDO, and function exports.
    /// Object exports are NOT stored here — they're derived from KMS object declarations.
    /// </summary>
    public List<ExportMetadata> InfrastructureExports { get; set; } = new();

    /// <summary>
    /// Per-class defaults for object exports created from KMS.
    /// Keyed by the class name used in KMS object declarations.
    /// </summary>
    public Dictionary<string, ObjectClassDefaults> ObjectDefaults { get; set; } = new();

    /// <summary>
    /// Field path metadata for property pointer resolution.
    /// </summary>
    public Dictionary<string, Dictionary<string, FieldPathMetadata>> FieldPaths { get; set; } = new();

    /// <summary>
    /// CDO metadata.
    /// </summary>
    public Dictionary<string, CdoMetadata> CdoData { get; set; } = new();

    /// <summary>
    /// Struct field schemas for nested struct property type resolution.
    /// Key: struct type name (e.g., "AnimNode_Root", "PoseLink")
    /// Value: dictionary mapping field name to field type info
    /// </summary>
    public Dictionary<string, Dictionary<string, StructFieldMeta>> StructSchemas { get; set; } = new();
}

/// <summary>
/// Metadata for a struct field, capturing type information for nested struct resolution.
/// </summary>
public class StructFieldMeta
{
    /// <summary>
    /// The KMS type hint for this field (e.g., "int", "Struct<PoseLink>", "Enum<EAnimSyncGroupScope>").
    /// </summary>
    public string? TypeHint { get; set; }

    /// <summary>
    /// The UE property type name (e.g., "IntProperty", "StructProperty", "EnumProperty").
    /// </summary>
    public string? PropertyType { get; set; }

    /// <summary>
    /// For StructProperty: the struct type name.
    /// </summary>
    public string? StructType { get; set; }

    /// <summary>
    /// For EnumProperty/ByteProperty: the enum type name.
    /// </summary>
    public string? EnumType { get; set; }

    /// <summary>
    /// For ObjectProperty: the object class name.
    /// </summary>
    public string? ObjectClass { get; set; }
}

/// <summary>
/// Per-class defaults for objects of this class type.
/// Applied when creating export entries from KMS object declarations.
/// </summary>
public class ObjectClassDefaults
{
    /// <summary>
    /// Default object flags for instances of this class.
    /// </summary>
    public List<string>? ObjectFlags { get; set; }

    public bool? BIsAsset { get; set; }

    /// <summary>
    /// The class name for the template import (Default__ClassName).
    /// </summary>
    public string? TemplateClassName { get; set; }

    /// <summary>
    /// Property serialization hints for properties of this class.
    /// Keyed by property name, provides type info that KMS syntax cannot capture.
    /// </summary>
    public Dictionary<string, PropertySerializationMeta>? PropertyMeta { get; set; }
}

/// <summary>
/// Serialization metadata for a specific property.
/// Provides type information that the KMS syntax cannot express,
/// such as MapProperty key/value types or StructProperty custom struct types.
/// </summary>
public class PropertySerializationMeta
{
    /// <summary>
    /// The UE property type name (e.g., "MapProperty", "StructProperty", "ObjectProperty").
    /// </summary>
    public string? Type { get; set; }

    /// <summary>
    /// For MapProperty: the key type (e.g., "ObjectProperty").
    /// </summary>
    public string? KeyType { get; set; }

    /// <summary>
    /// For MapProperty: the value type (e.g., "IntProperty").
    /// </summary>
    public string? ValueType { get; set; }

    /// <summary>
    /// For StructProperty: the struct type name (e.g., "Guid", "Vector").
    /// </summary>
    public string? StructType { get; set; }
}

/// <summary>
/// Package-level metadata including name, GUID, and flags.
/// </summary>
public class PackageMetadata
{
    public string Name { get; set; } = "";
    public string? Guid { get; set; }
    public List<string> Flags { get; set; } = new();
    public int LegacyFileVersion { get; set; } = -7;
    public bool UsesEventDrivenLoader { get; set; } = true;
    public bool IsUnversioned { get; set; } = true;
    public uint? PackageSource { get; set; }
    /// <summary>
    /// Base64 encoded BulkData (package checksum bytes).
    /// </summary>
    public string? BulkData { get; set; }
}

/// <summary>
/// Engine version information for serialization compatibility.
/// </summary>
public class EngineVersionMetadata
{
    public string ObjectVersion { get; set; } = "VER_UE4_FIX_WIDE_STRING_CRC";
    public string ObjectVersionUE5 { get; set; } = "UNKNOWN";
    public Dictionary<string, int> CustomVersions { get; set; } = new();
}

/// <summary>
/// Import table entry metadata.
/// </summary>
public class ImportMetadata
{
    public int Index { get; set; }
    public string ObjectName { get; set; } = "";
    public string ClassName { get; set; } = "";
    public string ClassPackage { get; set; } = "";
    public int OuterIndex { get; set; }
    public bool? BImportOptional { get; set; }
}

/// <summary>
/// Export table entry metadata.
/// </summary>
public class ExportMetadata
{
    public int Index { get; set; }
    public string ObjectName { get; set; } = "";
    public string ClassName { get; set; } = "";
    public int OuterIndex { get; set; }
    public int? SuperIndex { get; set; }
    public int? TemplateIndex { get; set; }
    public List<string> ObjectFlags { get; set; } = new();
    public List<string>? FunctionFlags { get; set; }
    public List<string>? ClassFlags { get; set; }
    public bool? IsCDO { get; set; }
    public bool? BNotAlwaysLoadedForEditorGame { get; set; }
    public bool? BIsAsset { get; set; }
    public ExportDependencies? Dependencies { get; set; }
    /// <summary>
    /// Base64 encoded extra binary data for the export.
    /// </summary>
    public string? Extras { get; set; }
    /// <summary>
    /// Loaded properties for FunctionExport/StructExport.
    /// </summary>
    public List<FPropertyMetadata>? LoadedProperties { get; set; }
}

/// <summary>
/// FProperty metadata for function/struct local variables.
/// </summary>
public class FPropertyMetadata
{
    public string SerializedType { get; set; } = "";
    public string Name { get; set; } = "";
    public List<string>? Flags { get; set; }
    public string? ArrayDim { get; set; }
    public int ElementSize { get; set; }
    public List<string>? PropertyFlags { get; set; }
    public int RepIndex { get; set; }
    public string? RepNotifyFunc { get; set; }
    public string? BlueprintReplicationCondition { get; set; }

    // Type-specific fields
    public int? PropertyClass { get; set; }  // FObjectProperty
    public int? MetaClass { get; set; }      // FClassProperty
    public int? SignatureFunction { get; set; }  // FDelegateProperty
    public int? InterfaceClass { get; set; }     // FInterfaceProperty
    public int? Struct { get; set; }         // FStructProperty
    public int? Enum { get; set; }           // FByteProperty, FEnumProperty

    // FBoolProperty
    public int? FieldSize { get; set; }
    public int? ByteOffset { get; set; }
    public int? ByteMask { get; set; }
    public int? FieldMask { get; set; }
    public bool? NativeBool { get; set; }
    public bool? BoolValue { get; set; }

    // Nested properties
    public FPropertyMetadata? Inner { get; set; }          // FArrayProperty
    public FPropertyMetadata? ElementProp { get; set; }    // FSetProperty
    public FPropertyMetadata? KeyProp { get; set; }        // FMapProperty
    public FPropertyMetadata? ValueProp { get; set; }      // FMapProperty
    public FPropertyMetadata? UnderlyingProp { get; set; } // FEnumProperty
}

/// <summary>
/// Export serialization dependencies.
/// </summary>
public class ExportDependencies
{
    public List<int>? SerializationBeforeSerialization { get; set; }
    public List<int>? CreateBeforeSerialization { get; set; }
    public List<int>? SerializationBeforeCreate { get; set; }
    public List<int>? CreateBeforeCreate { get; set; }
}

/// <summary>
/// Field path metadata for property pointer resolution.
/// </summary>
public class FieldPathMetadata
{
    public List<string> Path { get; set; } = new();
    public int ResolvedOwner { get; set; }
    public bool? EmptyPath { get; set; }
}

/// <summary>
/// Class Default Object (CDO) metadata.
/// </summary>
public class CdoMetadata
{
    public List<SubObjectMetadata>? SubObjects { get; set; }
    public List<string>? PreservedProperties { get; set; }
}

/// <summary>
/// Sub-object metadata within a CDO.
/// </summary>
public class SubObjectMetadata
{
    public string Name { get; set; } = "";
    public string ClassName { get; set; } = "";
    public int OuterIndex { get; set; }
    public int ExportIndex { get; set; }
}
