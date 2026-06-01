using System.CommandLine;
using System.CommandLine.Invocation;
using System.IO;
using System.Linq;
using KismetScript.Linker;
using KismetScript.Utilities.Metadata;
using UAssetAPI.UnrealTypes;
using UAssetStudio.Patching;

namespace UAssetStudio.Cli.CMD
{
    internal static class CompileCommandBuilder
    {
        internal static Command Create(Option<EngineVersion> ueVersion, Option<string?> mappings, Option<bool> json)
        {
            var scriptArg = new Argument<string>("script", description: "Path to input .kms");
            var assetOpt = new Option<string?>("--asset", description: "Original asset path (.uasset); defaults to script .uasset neighbor");
            var outdirOpt = new Option<string?>("--outdir", description: "Output directory; default = script directory");
            var outFileOpt = new Option<string?>("--out", description: "Output file path (overrides --outdir)");
            var metaOpt = new Option<string?>("--meta", description: "Path to .kms.meta metadata file for standalone compilation");
            var onlyFuncOpt = new Option<string[]>("--only-func", description: "Surgical patch: only replace bytecode of these function(s); restore everything else from the original asset. Repeatable.")
            {
                AllowMultipleArgumentsPerToken = true,
            };

            var compile = new Command("compile", "Compile .kms to .uasset (use --only-func for a surgical single-function patch)")
            {
                scriptArg,
                assetOpt,
                outdirOpt,
                outFileOpt,
                metaOpt,
                onlyFuncOpt,
            };

            compile.AddOption(ueVersion);
            compile.AddOption(mappings);

            compile.SetHandler((InvocationContext ctx) =>
            {
                var ver = ctx.ParseResult.GetValueForOption(ueVersion);
                var mapPath = ctx.ParseResult.GetValueForOption(mappings);
                var scriptPath = ctx.ParseResult.GetValueForArgument(scriptArg);
                var assetPath = ctx.ParseResult.GetValueForOption(assetOpt);
                var outdir = ctx.ParseResult.GetValueForOption(outdirOpt);
                var outFile = ctx.ParseResult.GetValueForOption(outFileOpt);
                var metaPath = ctx.ParseResult.GetValueForOption(metaOpt);
                var onlyFuncs = ctx.ParseResult.GetValueForOption(onlyFuncOpt) ?? Array.Empty<string>();
                var asJson = ctx.ParseResult.GetValueForOption(json);

                ctx.ExitCode = CliOutput.Run("compile", asJson,
                    new { script = scriptPath, asset = assetPath, mappings = mapPath, ueVersion = ver.ToString(), outdir, outFile, meta = metaPath, onlyFunc = onlyFuncs },
                    result =>
                    {
                        CliOutput.RequireFile(scriptPath, "Script file");

                        var effectiveMetaPath = metaPath ?? scriptPath + ".meta";
                        var hasMetadata = File.Exists(effectiveMetaPath);
                        var originalAssetPath = assetPath ?? Path.ChangeExtension(scriptPath, ".uasset");
                        var hasOriginalAsset = File.Exists(originalAssetPath);

                        var outputPath = ResolveOutputPath(outFile, outdir, scriptPath);

                        // Surgical single-function patch path.
                        if (onlyFuncs.Length > 0)
                        {
                            if (hasMetadata && !hasOriginalAsset)
                                throw new CliException("UnsupportedMode",
                                    "--only-func requires the original asset; it is incompatible with standalone metadata compilation.");
                            if (!hasOriginalAsset)
                                throw new CliException("FileNotFound",
                                    $"Original asset not found for surgical patch: {originalAssetPath}",
                                    "Provide --asset <original .uasset>.");

                            var session = AssetPatchSession.Load(originalAssetPath, ver, mapPath)
                                .ReplaceFunctionBytecode(scriptPath, onlyFuncs);
                            session.Save(outputPath);

                            result.AddOutput(outputPath);
                            result.Data = new { mode = "surgical", patchedFunctions = session.PatchedFunctions };
                            result.Line($"Surgically patched [{string.Join(", ", session.PatchedFunctions)}]: {scriptPath} -> {outputPath}");
                            result.Line("All other functions and default properties preserved byte-for-byte from the original.");
                            return;
                        }

                        // Full compile path.
                        var script = CliHelpers.CompileKms(scriptPath, ver);
                        UAssetLinker linker;

                        if (hasMetadata && !hasOriginalAsset)
                        {
                            var metadata = KmsMetadataSerializer.ReadFromFile(effectiveMetaPath)
                                ?? throw new CliException("MetadataParseFailed", $"Failed to parse metadata file {effectiveMetaPath}");
                            result.Line($"Using metadata file: {effectiveMetaPath}");
                            linker = UAssetLinker.FromMetadata(metadata);
                        }
                        else if (hasOriginalAsset)
                        {
                            var asset = CliHelpers.LoadAsset(ver, mapPath, originalAssetPath);
                            linker = new UAssetLinker(asset);
                        }
                        else
                        {
                            throw new CliException("FileNotFound",
                                $"Cannot find original asset file {originalAssetPath} for compilation",
                                "Either provide an original .uasset file or generate a .kms.meta via --meta during decompilation.");
                        }

                        var newAsset = linker.LinkCompiledScript(script).Build();
                        newAsset.Write(outputPath);
                        result.AddOutput(outputPath);
                        result.Data = new { mode = "full" };
                        result.Line($"Compiled: {scriptPath} -> {outputPath}");
                    });
            });

            return compile;
        }

        private static string ResolveOutputPath(string? outFile, string? outdir, string scriptPath)
        {
            if (!string.IsNullOrEmpty(outFile))
            {
                var outDir = Path.GetDirectoryName(outFile);
                if (!string.IsNullOrEmpty(outDir))
                    Directory.CreateDirectory(outDir);
                return outFile;
            }

            var dir = outdir ?? Path.GetDirectoryName(scriptPath) ?? ".";
            Directory.CreateDirectory(dir);
            return Path.Join(dir, Path.GetFileName(Path.ChangeExtension(scriptPath, ".new.uasset")));
        }
    }
}
