using System.CommandLine;
using System.CommandLine.Invocation;
using System.IO;
using KismetScript.Decompiler;
using KismetScript.Linker;
using KismetScript.Utilities.Metadata;
using UAssetAPI.UnrealTypes;

namespace UAssetStudio.Cli.CMD
{
    internal static class VerifyCommandBuilder
    {
        internal static Command Create(Option<EngineVersion> ueVersion, Option<string?> mappings, Option<bool> json)
        {
            var assetArg = new Argument<string>("asset", description: "Path to asset (.uasset/.umap)");
            var outdirOpt = new Option<string?>(new[] { "--outdir", "--out" }, description: "Output directory; default = asset directory");
            var metaOpt = new Option<bool>("--meta", () => false, "Generate .kms.meta file and use standalone compilation");
            var verify = new Command("verify", "Decompile asset to .kms, recompile, link, and write .new.uasset")
            {
                assetArg,
                outdirOpt,
                metaOpt
            };

            verify.AddOption(ueVersion);
            verify.AddOption(mappings);

            verify.SetHandler((InvocationContext ctx) =>
            {
                var ver = ctx.ParseResult.GetValueForOption(ueVersion);
                var mapPath = ctx.ParseResult.GetValueForOption(mappings);
                var assetPath = ctx.ParseResult.GetValueForArgument(assetArg);
                var outdir = ctx.ParseResult.GetValueForOption(outdirOpt);
                var generateMeta = ctx.ParseResult.GetValueForOption(metaOpt);
                var asJson = ctx.ParseResult.GetValueForOption(json);

                ctx.ExitCode = CliOutput.Run("verify", asJson,
                    new { asset = assetPath, mappings = mapPath, ueVersion = ver.ToString(), outdir, meta = generateMeta },
                    result =>
                    {
                        CliOutput.RequireFile(assetPath, "Asset file");

                        var asset = CliHelpers.LoadAsset(ver, mapPath, assetPath);
                        var dir = outdir ?? Path.GetDirectoryName(assetPath) ?? ".";
                        Directory.CreateDirectory(dir);

                        // 1) Decompile to .kms
                        var kmsPath = Path.Join(dir, Path.ChangeExtension(Path.GetFileName(assetPath), ".kms"));
                        CliHelpers.DecompileToKms(asset, kmsPath);
                        result.AddOutput(kmsPath);
                        result.Line($"Decompiled: {assetPath} -> {kmsPath}");

                        // 1.5) Generate metadata if requested
                        KmsMetadata? metadata = null;
                        if (generateMeta)
                        {
                            var metaPath = kmsPath + ".meta";
                            var extractor = new MetadataExtractor();
                            metadata = extractor.Extract(asset);
                            KmsMetadataSerializer.WriteToFile(metadata, metaPath);
                            result.AddOutput(metaPath);
                            result.Line($"Generated metadata: {metaPath}");
                        }

                        // 2) Compile .kms back
                        var script = CliHelpers.CompileKms(kmsPath, ver);

                        // 3) Link compiled script into asset
                        UAssetLinker linker;
                        if (generateMeta && metadata != null)
                        {
                            linker = UAssetLinker.FromMetadata(metadata);
                        }
                        else
                        {
                            linker = new UAssetLinker(asset);
                        }

                        var newAsset = linker.LinkCompiledScript(script).Build();

                        // 5) Write .new.uasset
                        var outFile = Path.Join(dir, Path.GetFileName(Path.ChangeExtension(assetPath, ".new.uasset")));
                        newAsset.Write(outFile);
                        result.AddOutput(outFile);

                        // 6) Verification (judgment step → exit 2 on mismatch)
                        try
                        {
                            if (generateMeta)
                            {
                                CliHelpers.VerifyStructural(assetPath, outFile, ver, mapPath);
                                result.Data = new { mode = "structural", verified = true };
                                result.Line($"Verified (structural): {assetPath} -> {kmsPath} -> {outFile}");
                            }
                            else
                            {
                                CliHelpers.VerifyOldAndNew(assetPath, outFile, ver, mapPath);
                                result.Data = new { mode = "binary", verified = true };
                                result.Line($"Verified: {assetPath} -> {kmsPath} -> {outFile}");
                            }
                        }
                        catch (InvalidOperationException ex)
                        {
                            result.Data = new { verified = false };
                            result.Fail("VerificationMismatch", ex.Message,
                                "Round-trip is not binary-equal; the .kms may be a lossy decompilation for this asset.");
                        }
                    });
            });

            return verify;
        }
    }
}
