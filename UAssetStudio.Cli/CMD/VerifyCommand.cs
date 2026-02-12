using System.CommandLine;
using KismetScript.Decompiler;
using KismetScript.Linker;
using KismetScript.Utilities.Metadata;
using UAssetAPI.UnrealTypes;

namespace UAssetStudio.Cli.CMD
{
    internal static class VerifyCommandBuilder
    {
        internal static Command Create(Option<EngineVersion> ueVersion, Option<string?> mappings)
        {
            var assetArg = new Argument<string>("asset", description: "Path to asset (.uasset/.umap)");
            var outdirOpt = new Option<string?>("--outdir", description: "Output directory; default = asset directory");
            var metaOpt = new Option<bool>("--meta", () => false, "Generate .kms.meta file and use standalone compilation");
            var verify = new Command("verify", "Decompile asset to .kms, recompile, link, and write .new.uasset")
            {
                assetArg,
                outdirOpt,
                metaOpt
            };

            verify.AddOption(ueVersion);
            verify.AddOption(mappings);

            verify.SetHandler((EngineVersion ver, string? mapPath, string assetPath, string? outdir, bool generateMeta) =>
            {
                if (!File.Exists(assetPath))
                {
                    Console.WriteLine($"Error: Input file {assetPath} does not exist");
                    return;
                }

                var asset = CliHelpers.LoadAsset(ver, mapPath, assetPath);
                var dir = outdir ?? Path.GetDirectoryName(assetPath) ?? ".";

                // 1) Decompile to .kms
                var kmsPath = Path.Join(dir, Path.ChangeExtension(Path.GetFileName(assetPath), ".kms"));
                CliHelpers.DecompileToKms(asset, kmsPath);
                Console.WriteLine($"Decompiled: {assetPath} -> {kmsPath}");

                // 1.5) Generate metadata if requested
                KmsMetadata? metadata = null;
                if (generateMeta)
                {
                    var metaPath = kmsPath + ".meta";
                    var extractor = new MetadataExtractor();
                    metadata = extractor.Extract(asset);
                    KmsMetadataSerializer.WriteToFile(metadata, metaPath);
                    Console.WriteLine($"Generated metadata: {metaPath}");
                }

                // 2) Compile .kms back
                var script = CliHelpers.CompileKms(kmsPath, ver);
                Console.WriteLine($"Compiled script: {script}");

                // 3) Link compiled script into asset
                UAssetLinker linker;
                if (generateMeta && metadata != null)
                {
                    // Standalone: imports from KMS, object exports from KMS, infrastructure from metadata
                    linker = UAssetLinker.FromMetadata(metadata);
                    Console.WriteLine("Using metadata for linking (KMS-driven imports & exports)");
                }
                else
                {
                    // Use original asset
                    linker = new UAssetLinker(asset);
                }

                var newAsset = linker
                        .LinkCompiledScript(script)
                        .Build();

                // 5) Write .new.uasset
                var outFile = Path.Join(dir, Path.GetFileName(Path.ChangeExtension(assetPath, ".new.uasset")));
                newAsset.Write(outFile);

                // 6) Verification
                if (generateMeta)
                {
                    // Structural verification (NameMap order differs, no binary equality expected)
                    CliHelpers.VerifyStructural(assetPath, outFile, ver, mapPath);
                    Console.WriteLine($"Verified (structural): {assetPath} -> {kmsPath} -> {outFile}");
                }
                else
                {
                    // Normal: exact JSON comparison for binary equality
                    CliHelpers.VerifyOldAndNew(assetPath, outFile, ver, mapPath);
                    Console.WriteLine($"Verified: {assetPath} -> {kmsPath} -> {outFile}");
                }

            }, ueVersion, mappings, assetArg, outdirOpt, metaOpt);

            return verify;
        }
    }
}
