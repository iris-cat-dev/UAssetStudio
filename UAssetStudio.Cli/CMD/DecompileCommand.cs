using System.CommandLine;
using System.CommandLine.Invocation;
using System.IO;
using KismetScript.Decompiler;
using KismetScript.Utilities.Metadata;
using UAssetAPI.UnrealTypes;

namespace UAssetStudio.Cli.CMD
{
    internal static class DecompileCommandBuilder
    {
        internal static Command Create(Option<EngineVersion> ueVersion, Option<string?> mappings, Option<bool> json)
        {
            var assetArg = new Argument<string>("asset", description: "Path to asset (.uasset/.umap)");
            var outdirOpt = new Option<string?>(new[] { "--outdir", "--out" }, description: "Output directory; default = asset directory");
            var metaOpt = new Option<bool>("--meta", () => false, "Generate .kms.meta file for standalone compilation");
            var decompile = new Command("decompile", "Decompile .uasset/.umap to .kms")
            {
                assetArg,
                outdirOpt,
                metaOpt
            };

            decompile.AddOption(ueVersion);
            decompile.AddOption(mappings);

            decompile.SetHandler((InvocationContext ctx) =>
            {
                var ver = ctx.ParseResult.GetValueForOption(ueVersion);
                var mapPath = ctx.ParseResult.GetValueForOption(mappings);
                var assetPath = ctx.ParseResult.GetValueForArgument(assetArg);
                var outdir = ctx.ParseResult.GetValueForOption(outdirOpt);
                var generateMeta = ctx.ParseResult.GetValueForOption(metaOpt);
                var asJson = ctx.ParseResult.GetValueForOption(json);

                ctx.ExitCode = CliOutput.Run("decompile", asJson,
                    new { asset = assetPath, mappings = mapPath, ueVersion = ver.ToString(), outdir, meta = generateMeta },
                    result =>
                    {
                        CliOutput.RequireFile(assetPath, "Asset file");
                        var asset = CliHelpers.LoadAsset(ver, mapPath, assetPath);
                        var dir = outdir ?? Path.GetDirectoryName(assetPath) ?? ".";
                        var kmsPath = Path.Join(dir, Path.ChangeExtension(Path.GetFileName(assetPath), ".kms"));
                        CliHelpers.DecompileToKms(asset, kmsPath);
                        result.AddOutput(kmsPath);
                        result.Line($"Decompiled: {assetPath} -> {kmsPath}");

                        if (generateMeta)
                        {
                            var metaPath = kmsPath + ".meta";
                            var extractor = new MetadataExtractor();
                            var metadata = extractor.Extract(asset);
                            KmsMetadataSerializer.WriteToFile(metadata, metaPath);
                            result.AddOutput(metaPath);
                            result.Line($"Generated metadata: {metaPath}");
                        }
                    });
            });

            return decompile;
        }
    }
}
