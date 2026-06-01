using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.Kismet.Bytecode;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.UnrealTypes;
using UAssetStudio.Patching;

namespace UAssetStudio.Patching.Tests
{
    [TestClass]
    public class AssetPatchSessionTests
    {
        private const EngineVersion Ver = EngineVersion.VER_UE4_27;

        private static string AssetPath =>
            Path.Combine(AppContext.BaseDirectory, "TestAssets", "WPN_LockOnRifle.uasset");

        [TestMethod]
        public void ReplaceFunctionBytecode_OnlyTargetFunctionChanges()
        {
            Assert.IsTrue(File.Exists(AssetPath), $"Test asset missing: {AssetPath}");

            var session = AssetPatchSession.Load(AssetPath, Ver, null);

            // Decompile the loaded asset to a faithful .kms we can recompile.
            var kms = Path.Combine(Path.GetTempPath(), $"WPN_LockOnRifle_{Guid.NewGuid():N}.kms");
            KmsDecompiler.DecompileToFile(session.Asset, kms);

            // Pick a target function that actually has decompiled bytecode.
            var functions = session.Asset.Exports.OfType<FunctionExport>().ToList();
            var target = functions.FirstOrDefault(f => f.ScriptBytecode != null)?.ObjectName.ToString();
            Assert.IsNotNull(target, "No function with decompiled bytecode found in test asset.");

            // Snapshot current bytecode references for every function.
            var before = functions.ToDictionary(
                f => f.ObjectName.ToString(),
                f => (Code: f.ScriptBytecode, Raw: f.ScriptBytecodeRaw, Size: f.ScriptBytecodeSize));

            session.ReplaceFunctionBytecode(kms, target!);

            foreach (var fe in session.Asset.Exports.OfType<FunctionExport>())
            {
                var name = fe.ObjectName.ToString();
                var snap = before[name];
                if (name == target)
                {
                    // Target was recompiled: a fresh bytecode array.
                    Assert.IsNotNull(fe.ScriptBytecode, "Target function lost its bytecode.");
                    Assert.IsFalse(ReferenceEquals(fe.ScriptBytecode, snap.Code),
                        "Target function bytecode should have been replaced.");
                }
                else
                {
                    // Every other function must be byte-for-byte preserved (same reference).
                    Assert.IsTrue(ReferenceEquals(fe.ScriptBytecode, snap.Code),
                        $"Non-target function '{name}' bytecode was modified.");
                    Assert.IsTrue(ReferenceEquals(fe.ScriptBytecodeRaw, snap.Raw),
                        $"Non-target function '{name}' raw bytecode was modified.");
                    Assert.AreEqual(snap.Size, fe.ScriptBytecodeSize,
                        $"Non-target function '{name}' bytecode size changed.");
                }
            }

            Assert.IsTrue(session.PatchedFunctions.Contains(target!));

            // Patched asset must round-trip on disk: write + reload with no RawExport.
            var outPath = Path.Combine(Path.GetTempPath(), $"WPN_LockOnRifle_patched_{Guid.NewGuid():N}.uasset");
            session.Save(outPath);
            var reloaded = new UAsset(outPath, Ver);
            Assert.AreEqual(session.Asset.Exports.Count, reloaded.Exports.Count);
            Assert.IsFalse(reloaded.Exports.Any(e => e is RawExport), "Patched asset failed to reparse (RawExport present).");

            File.Delete(kms);
            File.Delete(outPath);
        }

        [TestMethod]
        public void ReplaceFunctionBytecode_UnknownFunction_Throws()
        {
            var session = AssetPatchSession.Load(AssetPath, Ver, null);
            var kms = Path.Combine(Path.GetTempPath(), $"WPN_LockOnRifle_{Guid.NewGuid():N}.kms");
            KmsDecompiler.DecompileToFile(session.Asset, kms);

            Assert.ThrowsException<InvalidOperationException>(
                () => session.ReplaceFunctionBytecode(kms, "ThisFunctionDoesNotExist_xyz"));

            File.Delete(kms);
        }

        [TestMethod]
        public void SetProperty_ScalarValue_PersistsAfterReload()
        {
            var session = AssetPatchSession.Load(AssetPath, Ver, null);

            // Find the first top-level scalar leaf property in any NormalExport.
            string? exportName = null, propName = null;
            object? newValue = null;
            string? expected = null;

            foreach (var export in session.Asset.Exports.OfType<NormalExport>())
            {
                foreach (var prop in export.Data)
                {
                    switch (prop)
                    {
                        case FloatPropertyData f:
                            exportName = export.ObjectName.ToString(); propName = f.Name.ToString();
                            newValue = f.Value + 12.5f; expected = ((float)newValue).ToString(System.Globalization.CultureInfo.InvariantCulture);
                            break;
                        case IntPropertyData i:
                            exportName = export.ObjectName.ToString(); propName = i.Name.ToString();
                            newValue = i.Value + 7; expected = ((int)newValue).ToString();
                            break;
                        case BoolPropertyData b:
                            exportName = export.ObjectName.ToString(); propName = b.Name.ToString();
                            newValue = !b.Value; expected = ((bool)newValue).ToString();
                            break;
                    }
                    if (exportName != null) break;
                }
                if (exportName != null) break;
            }

            if (exportName == null)
            {
                Assert.Inconclusive("No top-level scalar property found in test asset to exercise SetProperty.");
                return;
            }

            session.SetProperty(exportName, propName!, newValue!);
            Assert.AreEqual(1, session.PropertyChanges.Count);
            Assert.AreEqual(expected, session.PropertyChanges[0].New);

            var outPath = Path.Combine(Path.GetTempPath(), $"WPN_LockOnRifle_prop_{Guid.NewGuid():N}.uasset");
            session.Save(outPath);

            var reloaded = new UAsset(outPath, Ver);
            var reExport = reloaded.Exports.OfType<NormalExport>().First(e => e.ObjectName.ToString() == exportName);
            var reProp = reExport.Data.First(p => p.Name.ToString() == propName);
            var actual = reProp switch
            {
                FloatPropertyData f => f.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                IntPropertyData i => i.Value.ToString(),
                BoolPropertyData b => b.Value.ToString(),
                _ => null,
            };
            Assert.AreEqual(expected, actual, "Property value did not persist after reload.");

            File.Delete(outPath);
        }

        [TestMethod]
        public void ApplyKmsPatch_AutoFunctionDiff_OnlyTargetFunctionChanges()
        {
            var session = AssetPatchSession.Load(AssetPath, Ver, null);
            var kms = KmsDecompiler.DecompileToString(session.Asset);
            var target = session.Asset.Exports.OfType<FunctionExport>()
                .First(f => f.ScriptBytecode != null)
                .ObjectName.ToString();
            var edited = InsertCommentIntoFunction(kms, target, "// safe patch test change");
            var editedPath = WriteTempKms(edited);

            var before = session.Asset.Exports.OfType<FunctionExport>().ToDictionary(
                f => f.ObjectName.ToString(),
                f => (Code: f.ScriptBytecode, Raw: f.ScriptBytecodeRaw, Size: f.ScriptBytecodeSize));

            var report = session.ApplyKmsPatch(editedPath);

            Assert.AreEqual("patch", report.Mode);
            CollectionAssert.Contains(report.ChangedFunctions.Select(f => f.Name).ToList(), target);
            Assert.AreEqual(0, report.ChangedProperties.Count);

            foreach (var fe in session.Asset.Exports.OfType<FunctionExport>())
            {
                var name = fe.ObjectName.ToString();
                var snap = before[name];
                if (name == target)
                {
                    Assert.IsFalse(ReferenceEquals(fe.ScriptBytecode, snap.Code),
                        "Changed function should have been re-linked.");
                }
                else
                {
                    Assert.IsTrue(ReferenceEquals(fe.ScriptBytecode, snap.Code),
                        $"Non-target function '{name}' changed.");
                    Assert.IsTrue(ReferenceEquals(fe.ScriptBytecodeRaw, snap.Raw),
                        $"Non-target function '{name}' raw bytecode changed.");
                    Assert.AreEqual(snap.Size, fe.ScriptBytecodeSize);
                }
            }

            File.Delete(editedPath);
        }

        [TestMethod]
        public void ApplyKmsPatch_ScalarProperty_PersistsAfterReload()
        {
            var session = AssetPatchSession.Load(AssetPath, Ver, null);
            var kms = KmsDecompiler.DecompileToString(session.Asset);
            if (!TryEditFirstClassScalarProperty(kms, out var edited, out var exportName, out var propName, out var expected))
                Assert.Inconclusive("No supported scalar class property found in test KMS.");

            var editedPath = WriteTempKms(edited);
            var report = session.ApplyKmsPatch(editedPath);

            Assert.AreEqual(0, report.ChangedFunctions.Count);
            Assert.AreEqual(1, report.ChangedProperties.Count);
            Assert.AreEqual($"{exportName}.{propName}", report.ChangedProperties[0].Path);
            Assert.AreEqual(expected, report.ChangedProperties[0].NewValue);

            var outPath = Path.Combine(Path.GetTempPath(), $"WPN_LockOnRifle_kmsprop_{Guid.NewGuid():N}.uasset");
            session.Save(outPath);

            var reloaded = new UAsset(outPath, Ver);
            var reExport = reloaded.Exports.OfType<NormalExport>().First(e => e.ObjectName.ToString() == exportName);
            var reProp = reExport.Data.First(p => p.Name.ToString() == propName);
            Assert.AreEqual(expected, DescribeTestValue(reProp));

            File.Delete(editedPath);
            File.Delete(outPath);
        }

        [TestMethod]
        public void ApplyKmsPatch_FunctionAndProperty_BothPersist()
        {
            var session = AssetPatchSession.Load(AssetPath, Ver, null);
            var kms = KmsDecompiler.DecompileToString(session.Asset);
            var target = session.Asset.Exports.OfType<FunctionExport>()
                .First(f => f.ScriptBytecode != null)
                .ObjectName.ToString();

            var edited = InsertCommentIntoFunction(kms, target, "// function and property patch");
            if (!TryEditFirstClassScalarProperty(edited, out edited, out var exportName, out var propName, out var expected))
                Assert.Inconclusive("No supported scalar class property found in test KMS.");

            var editedPath = WriteTempKms(edited);
            var before = session.Asset.Exports.OfType<FunctionExport>().ToDictionary(
                f => f.ObjectName.ToString(),
                f => f.ScriptBytecode);

            var report = session.ApplyKmsPatch(editedPath);
            Assert.AreEqual(1, report.ChangedProperties.Count);
            CollectionAssert.Contains(report.ChangedFunctions.Select(f => f.Name).ToList(), target);
            Assert.IsFalse(ReferenceEquals(
                session.Asset.Exports.OfType<FunctionExport>().First(f => f.ObjectName.ToString() == target).ScriptBytecode,
                before[target]));

            var changedProp = session.Asset.Exports.OfType<NormalExport>()
                .First(e => e.ObjectName.ToString() == exportName)
                .Data.First(p => p.Name.ToString() == propName);
            Assert.AreEqual(expected, DescribeTestValue(changedProp));

            File.Delete(editedPath);
        }

        private static string WriteTempKms(string text)
        {
            var path = Path.Combine(Path.GetTempPath(), $"WPN_LockOnRifle_edit_{Guid.NewGuid():N}.kms");
            File.WriteAllText(path, text);
            return path;
        }

        private static string InsertCommentIntoFunction(string kms, string functionName, string comment)
        {
            var lines = kms.Replace("\r\n", "\n").Split('\n').ToList();
            for (var i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                if (line.Contains($"{functionName}(", StringComparison.Ordinal) && line.TrimEnd().EndsWith("{", StringComparison.Ordinal))
                {
                    lines.Insert(i + 1, $"        {comment}");
                    return string.Join('\n', lines);
                }
            }

            throw new InvalidOperationException($"Function '{functionName}' not found in KMS.");
        }

        private static bool TryEditFirstClassScalarProperty(
            string kms,
            out string edited,
            out string exportName,
            out string propertyName,
            out string expectedValue)
        {
            edited = kms;
            exportName = "";
            propertyName = "";
            expectedValue = "";

            var lines = kms.Replace("\r\n", "\n").Split('\n').ToList();
            var depth = 0;
            var targetClassName = Path.GetFileNameWithoutExtension(AssetPath) + "_C";
            var inTargetClass = false;
            var classRegex = new Regex(@"^\s*class\s+([A-Za-z_][A-Za-z0-9_]*)\s*:");
            var propRegex = new Regex(@"^\s*(bool|int|float|double|string|Name)\s+([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.+);\s*$");

            for (var i = 0; i < lines.Count; i++)
            {
                var beforeDepth = depth;
                var classMatch = classRegex.Match(lines[i]);
                if (classMatch.Success && classMatch.Groups[1].Value == targetClassName)
                    inTargetClass = true;

                if (inTargetClass && beforeDepth == 1)
                {
                    var propMatch = propRegex.Match(lines[i]);
                    if (propMatch.Success && TryBuildReplacement(propMatch.Groups[1].Value, propMatch.Groups[3].Value, out var replacement, out expectedValue))
                    {
                        propertyName = propMatch.Groups[2].Value;
                        exportName = $"Default__{targetClassName}";
                        lines[i] = Regex.Replace(lines[i], @"=\s*.+;\s*$", $"= {replacement};");
                        edited = string.Join('\n', lines);
                        return true;
                    }
                }

                depth += lines[i].Count(c => c == '{');
                depth -= lines[i].Count(c => c == '}');
                if (inTargetClass && depth == 0)
                    inTargetClass = false;
            }

            return false;
        }

        private static bool TryBuildReplacement(string type, string oldValue, out string replacement, out string expected)
        {
            replacement = "";
            expected = "";
            switch (type)
            {
                case "bool":
                    replacement = oldValue.Trim().Equals("true", StringComparison.OrdinalIgnoreCase) ? "false" : "true";
                    expected = replacement;
                    return true;
                case "int":
                    replacement = "1337";
                    expected = replacement;
                    return true;
                case "float":
                    replacement = "123.25f";
                    expected = "123.25";
                    return true;
                case "double":
                    replacement = "123.25d";
                    expected = "123.25";
                    return true;
                case "string":
                    replacement = "\"kms-first-test\"";
                    expected = "kms-first-test";
                    return true;
                case "Name":
                    replacement = "\"KmsFirstTest\"";
                    expected = "KmsFirstTest";
                    return true;
                default:
                    return false;
            }
        }

        private static string? DescribeTestValue(PropertyData prop) => prop switch
        {
            FloatPropertyData f => f.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            DoublePropertyData d => d.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            IntPropertyData i => i.Value.ToString(),
            BoolPropertyData b => b.Value.ToString().ToLowerInvariant(),
            StrPropertyData s => s.Value?.ToString(),
            NamePropertyData n => n.Value?.ToString(),
            _ => null,
        };
    }
}
