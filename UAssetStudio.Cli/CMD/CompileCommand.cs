using System.CommandLine;
using KismetScript.Linker;
using KismetScript.Utilities.Metadata;
using UAssetAPI.UnrealTypes;

namespace UAssetStudio.Cli.CMD
{
    internal static class CompileCommandBuilder
    {
        internal static Command Create(Option<EngineVersion> ueVersion, Option<string?> mappings)
        {
            var scriptArg = new Argument<string>("script", description: "Path to input .kms");
            var assetOpt = new Option<string?>("--asset", description: "Original asset path (.uasset); defaults to script .uasset neighbor");
            var outdirOpt = new Option<string?>("--outdir", description: "Output directory; default = script directory");
            var outFileOpt = new Option<string?>("--out", description: "Output file path (overrides --outdir)");
            var compile = new Command("compile", "Compile .kms to .uasset")
            {
                scriptArg,
                assetOpt,
                outdirOpt,
                outFileOpt
            };

            compile.AddOption(ueVersion);
            compile.AddOption(mappings);

            compile.SetHandler((EngineVersion ver, string? mapPath, string scriptPath, string? assetPath, string? outdir, string? outFile) =>
            {
                if (!File.Exists(scriptPath))
                {
                    Console.WriteLine($"Error: Input file {scriptPath} does not exist");
                    return;
                }

                var metaPath = scriptPath + ".meta";
                var hasMetadata = File.Exists(metaPath);
                var originalAssetPath = assetPath ?? Path.ChangeExtension(scriptPath, ".uasset");
                var hasOriginalAsset = File.Exists(originalAssetPath);

                var script = CliHelpers.CompileKms(scriptPath, ver);

                UAssetLinker linker;

                if (hasMetadata && !hasOriginalAsset)
                {
                    // Standalone compilation mode - use metadata file
                    Console.WriteLine($"Using metadata file: {metaPath}");
                    var metadata = KmsMetadataSerializer.ReadFromFile(metaPath);
                    if (metadata == null)
                    {
                        Console.WriteLine($"Error: Failed to parse metadata file {metaPath}");
                        return;
                    }
                    linker = UAssetLinker.FromMetadata(metadata);
                }
                else if (hasOriginalAsset)
                {
                    // Traditional mode - use original asset
                    var asset = CliHelpers.LoadAsset(ver, mapPath, originalAssetPath);
                    linker = new UAssetLinker(asset);
                }
                else
                {
                    Console.WriteLine($"Error: Cannot find original asset file {originalAssetPath} for compilation");
                    Console.WriteLine($"Hint: Either provide an original .uasset file or generate a .kms.meta file using --meta option during decompilation");
                    return;
                }

                var newAsset = linker
                    .LinkCompiledScript(script)
                    .Build();

                string outputPath;
                if (!string.IsNullOrEmpty(outFile))
                {
                    // Use explicit output file path
                    outputPath = outFile;
                    var outDir = Path.GetDirectoryName(outputPath);
                    if (!string.IsNullOrEmpty(outDir) && !Directory.Exists(outDir))
                    {
                        Directory.CreateDirectory(outDir);
                    }
                }
                else
                {
                    var dir = outdir ?? Path.GetDirectoryName(scriptPath) ?? ".";
                    outputPath = Path.Join(dir, Path.GetFileName(Path.ChangeExtension(scriptPath, ".new.uasset")));
                }

                newAsset.Write(outputPath);
                Console.WriteLine($"Compiled: {scriptPath} -> {outputPath}");
            }, ueVersion, mappings, scriptArg, assetOpt, outdirOpt, outFileOpt);

            return compile;
        }
    }
}
