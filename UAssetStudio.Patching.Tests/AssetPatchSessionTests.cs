using System.Linq;
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
    }
}
