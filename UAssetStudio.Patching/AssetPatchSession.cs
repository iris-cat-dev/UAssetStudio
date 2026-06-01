using KismetScript.Linker;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.Kismet.Bytecode;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;

namespace UAssetStudio.Patching
{
    /// <summary>
    /// Stable, code-first asset patching engine: load -> mutate in memory -> write.
    ///
    /// Two safe primitives:
    ///   * <see cref="ReplaceFunctionBytecode"/> — single-function surgical bytecode patch.
    ///     Only the named functions are re-linked; every other function (including
    ///     decompilation-failed raw bytecode) and all default properties are restored
    ///     byte-for-byte from the original asset. Avoids the "whole-file recompile breaks
    ///     unrelated functions" crash.
    ///   * <see cref="SetProperty"/> — strongly-typed default/property value edit.
    ///     Loads via the binary read path so Blueprint/parent-class schemas are collected,
    ///     avoiding the JSON-roundtrip "missing parent schema" failure.
    /// </summary>
    public sealed class AssetPatchSession
    {
        private readonly EngineVersion _version;

        public UAsset Asset { get; }

        /// <summary>Names of functions whose bytecode was replaced.</summary>
        public List<string> PatchedFunctions { get; } = new();

        /// <summary>Property edits applied, for reporting (path / old -> new).</summary>
        public List<PropertyChange> PropertyChanges { get; } = new();

        private AssetPatchSession(UAsset asset, EngineVersion version)
        {
            Asset = asset;
            _version = version;
        }

        /// <summary>
        /// Loads an asset through the binary read path (collects Blueprint + parent-class schemas).
        /// </summary>
        public static AssetPatchSession Load(string assetPath, EngineVersion version, string? mappings)
        {
            if (!File.Exists(assetPath))
                throw new FileNotFoundException("Asset file not found", assetPath);

            var asset = mappings != null
                ? new UAsset(assetPath, version, new Usmap(mappings))
                : new UAsset(assetPath, version);

            return new AssetPatchSession(asset, version);
        }

        /// <summary>
        /// Surgically replaces the bytecode of the named functions by recompiling the supplied
        /// .kms, then restoring every other function and all NormalExport data from the original.
        /// </summary>
        public AssetPatchSession ReplaceFunctionBytecode(string kmsPath, params string[] functionNames)
        {
            if (functionNames == null || functionNames.Length == 0)
                throw new ArgumentException("At least one function name is required.", nameof(functionNames));
            if (!File.Exists(kmsPath))
                throw new FileNotFoundException("KMS script not found", kmsPath);

            var targets = new HashSet<string>(functionNames, StringComparer.Ordinal);

            // Snapshot original bytecode (incl. raw bytecode for decompile-failed functions).
            var bcSnap = new Dictionary<string, (KismetExpression[]? Code, byte[]? Raw, int Size)>(StringComparer.Ordinal);
            foreach (var fe in Asset.Exports.OfType<FunctionExport>())
                bcSnap[fe.ObjectName.ToString()] = (fe.ScriptBytecode, fe.ScriptBytecodeRaw, fe.ScriptBytecodeSize);

            // Validate targets exist before mutating anything.
            foreach (var target in targets)
            {
                if (!bcSnap.ContainsKey(target))
                    throw new InvalidOperationException(
                        $"Function '{target}' not found. Available: {string.Join(", ", bcSnap.Keys)}");
            }

            // Snapshot all default-property data so CDO/sub-object edits from full-file
            // linking can be reverted (function-only surgical patch must not touch defaults).
            var dataSnap = new Dictionary<NormalExport, List<PropertyData>?>();
            foreach (var ne in Asset.Exports.OfType<NormalExport>())
                dataSnap[ne] = ne.Data != null ? new List<PropertyData>(ne.Data) : null;

            var script = KmsCompiler.Compile(kmsPath, _version);
            new UAssetLinker(Asset).LinkCompiledScript(script).Build();

            // Restore every non-target function's original bytecode.
            foreach (var fe in Asset.Exports.OfType<FunctionExport>())
            {
                var name = fe.ObjectName.ToString();
                if (!targets.Contains(name) && bcSnap.TryGetValue(name, out var snap))
                {
                    fe.ScriptBytecode = snap.Code;
                    fe.ScriptBytecodeRaw = snap.Raw;
                    fe.ScriptBytecodeSize = snap.Size;
                }
            }

            // Restore all default-property data.
            foreach (var kv in dataSnap)
            {
                if (kv.Value != null)
                    kv.Key.Data = kv.Value;
            }

            foreach (var target in targets)
                if (!PatchedFunctions.Contains(target))
                    PatchedFunctions.Add(target);

            return this;
        }

        /// <summary>
        /// Sets a property value by path. exportName may be a CDO (e.g. "Default__BP_X_C").
        /// propPath uses dots for struct fields and [i] for array elements, e.g. "Mesh.RelativeScale3D"
        /// or "Items[0].Count". The leaf value is written according to its actual PropertyData type.
        /// </summary>
        public AssetPatchSession SetProperty(string exportName, string propPath, object value)
        {
            var export = Asset.Exports.OfType<NormalExport>()
                .FirstOrDefault(e => e.ObjectName.ToString() == exportName)
                ?? throw new InvalidOperationException(
                    $"Export '{exportName}' not found (expected a NormalExport/CDO with Data).");

            var segments = ParsePath(propPath);
            List<PropertyData>? list = export.Data;
            PropertyData? current = null;

            for (int i = 0; i < segments.Count; i++)
            {
                var (name, index) = segments[i];
                current = list?.FirstOrDefault(p => p.Name.ToString() == name)
                    ?? throw new InvalidOperationException($"Property '{name}' not found in path '{propPath}'.");

                if (index.HasValue)
                {
                    if (current is ArrayPropertyData arr)
                    {
                        if (arr.Value == null || index.Value < 0 || index.Value >= arr.Value.Length)
                            throw new InvalidOperationException(
                                $"Index {index} out of range for array '{name}' (length {arr.Value?.Length ?? 0}).");
                        current = arr.Value[index.Value];
                    }
                    else
                    {
                        throw new InvalidOperationException($"Property '{name}' is not an array but was indexed.");
                    }
                }

                if (i < segments.Count - 1)
                {
                    if (current is StructPropertyData s)
                        list = s.Value;
                    else
                        throw new InvalidOperationException($"Cannot descend into '{name}'; it is not a struct.");
                }
            }

            var oldValue = DescribeLeaf(current!);
            ApplyValue(current!, value, propPath);
            var newValue = DescribeLeaf(current!);
            PropertyChanges.Add(new PropertyChange { Path = $"{exportName}.{propPath}", Old = oldValue, New = newValue });

            return this;
        }

        /// <summary>Writes the patched asset (and its .uexp) to outPath.</summary>
        public void Save(string outPath)
        {
            var dir = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            Asset.Write(outPath);
        }

        private static List<(string Name, int? Index)> ParsePath(string path)
        {
            var result = new List<(string, int?)>();
            foreach (var raw in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
            {
                var token = raw.Trim();
                int? index = null;
                var bracket = token.IndexOf('[');
                if (bracket >= 0)
                {
                    if (!token.EndsWith("]"))
                        throw new InvalidOperationException($"Malformed path segment '{token}'.");
                    var idxStr = token.Substring(bracket + 1, token.Length - bracket - 2);
                    if (!int.TryParse(idxStr, out var parsed))
                        throw new InvalidOperationException($"Invalid array index in '{token}'.");
                    index = parsed;
                    token = token.Substring(0, bracket);
                }
                if (token.Length == 0)
                    throw new InvalidOperationException($"Empty property name in path '{path}'.");
                result.Add((token, index));
            }
            if (result.Count == 0)
                throw new InvalidOperationException("Empty property path.");
            return result;
        }

        private void ApplyValue(PropertyData prop, object value, string path)
        {
            switch (prop)
            {
                case FloatPropertyData f: f.Value = Convert.ToSingle(value); break;
                case DoublePropertyData d: d.Value = Convert.ToDouble(value); break;
                case IntPropertyData i: i.Value = Convert.ToInt32(value); break;
                case Int64PropertyData l: l.Value = Convert.ToInt64(value); break;
                case BoolPropertyData b: b.Value = Convert.ToBoolean(value); break;
                case BytePropertyData by:
                    if (by.ByteType.ToString() == "FName")
                        throw new InvalidOperationException(
                            $"Property at '{path}' is an FName-backed byte and cannot be set as a scalar.");
                    by.Value = Convert.ToByte(value);
                    break;
                case StrPropertyData s: s.Value = new FString(value?.ToString() ?? ""); break;
                case NamePropertyData n: n.Value = new FName(Asset, value?.ToString() ?? "None"); break;
                case EnumPropertyData e: e.Value = new FName(Asset, value?.ToString() ?? "None"); break;
                default:
                    throw new InvalidOperationException(
                        $"Unsupported leaf property type '{prop.GetType().Name}' at '{path}'. " +
                        "Use a C# recipe with the UAssetAPI object model for complex edits.");
            }
        }

        private static string? DescribeLeaf(PropertyData prop) => prop switch
        {
            FloatPropertyData f => f.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            DoublePropertyData d => d.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            IntPropertyData i => i.Value.ToString(),
            Int64PropertyData l => l.Value.ToString(),
            BoolPropertyData b => b.Value.ToString(),
            BytePropertyData by => by.Value.ToString(),
            StrPropertyData s => s.Value?.ToString(),
            NamePropertyData n => n.Value?.ToString(),
            EnumPropertyData e => e.Value?.ToString(),
            _ => prop.GetType().Name,
        };

        public sealed class PropertyChange
        {
            public string Path { get; set; } = "";
            public string? Old { get; set; }
            public string? New { get; set; }
        }
    }
}
