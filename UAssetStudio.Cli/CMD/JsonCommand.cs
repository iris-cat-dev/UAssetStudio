using System.CommandLine;
using UAssetAPI;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;

namespace UAssetStudio.Cli.CMD;

internal static class JsonCommandBuilder
{
    internal static Command Create(Option<EngineVersion> ueVersion, Option<string?> mappings)
    {
        var inputArg = new Argument<string>("input", description: "Path to .uasset/.umap/.json");
        var outputOpt = new Option<string?>("--out", description: "Output file; default is derived from the input path");
        var sourceAssetOpt = new Option<string?>("--asset", description: "Original .uasset/.umap to borrow schemas from when importing JSON");

        var json = new Command("json", "Convert assets between binary (.uasset/.umap) and JSON")
        {
            inputArg,
            outputOpt,
            sourceAssetOpt,
        };

        json.AddOption(ueVersion);
        json.AddOption(mappings);

        json.SetHandler((EngineVersion version, string? mappingsPath, string inputPath, string? outputPath, string? sourceAssetPath) =>
        {
            if (!File.Exists(inputPath))
            {
                Console.Error.WriteLine($"Input file not found: {inputPath}");
                Environment.ExitCode = 1;
                return;
            }

            var extension = Path.GetExtension(inputPath).ToLowerInvariant();
            var isAsset = extension == ".uasset" || extension == ".umap";
            var isJson = extension == ".json";

            if (!isAsset && !isJson)
            {
                Console.Error.WriteLine($"Unsupported input extension: {extension}. Expected .uasset, .umap, or .json.");
                Environment.ExitCode = 1;
                return;
            }

            try
            {
                if (isAsset)
                {
                    ConvertAssetToJson(version, mappingsPath, inputPath, outputPath);
                }
                else
                {
                    ConvertJsonToAsset(version, mappingsPath, inputPath, outputPath, sourceAssetPath);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Conversion failed: {ex.GetType().Name}: {ex.Message}");
                if (isJson && IsMissingSchemaException(ex))
                {
                    Console.Error.WriteLine("Hint: JSON import for inherited Blueprint assets may need --asset <original .uasset/.umap> so the CLI can borrow schemas collected while reading the original binary asset.");
                }
                Environment.ExitCode = 1;
            }
        }, ueVersion, mappings, inputArg, outputOpt, sourceAssetOpt);

        return json;
    }

    private static void ConvertAssetToJson(EngineVersion version, string? mappingsPath, string inputPath, string? outputPath)
    {
        var asset = CliHelpers.LoadAsset(version, mappingsPath, inputPath);
        var defaultOutput = Path.Join(Path.GetDirectoryName(inputPath) ?? ".", Path.GetFileName(inputPath) + ".json");
        var outPath = outputPath ?? defaultOutput;
        EnsureOutputDirectory(outPath);
        var json = asset.SerializeJson(true);
        File.WriteAllText(outPath, json);
        Console.WriteLine($"Exported JSON: {outPath}");
    }

    private static void ConvertJsonToAsset(EngineVersion version, string? mappingsPath, string inputPath, string? outputPath, string? sourceAssetPath)
    {
        var jsonText = File.ReadAllText(inputPath);
        var asset = UAsset.DeserializeJson(jsonText);

        if (!string.IsNullOrEmpty(sourceAssetPath))
        {
            if (!File.Exists(sourceAssetPath))
            {
                throw new FileNotFoundException("Source asset file not found", sourceAssetPath);
            }

            var sourceAsset = CliHelpers.LoadAsset(version, mappingsPath, sourceAssetPath);
            asset.Mappings = sourceAsset.Mappings;
        }
        else if (!string.IsNullOrEmpty(mappingsPath))
        {
            if (!File.Exists(mappingsPath))
            {
                throw new FileNotFoundException("Mappings file not found", mappingsPath);
            }
            asset.Mappings = new Usmap(mappingsPath);
        }

        var baseName = Path.GetFileNameWithoutExtension(inputPath);
        if (!baseName.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) &&
            !baseName.EndsWith(".umap", StringComparison.OrdinalIgnoreCase))
        {
            baseName += ".uasset";
        }

        var defaultOutput = Path.Join(Path.GetDirectoryName(inputPath) ?? ".", baseName);
        var outPath = outputPath ?? defaultOutput;
        EnsureOutputDirectory(outPath);
        asset.Write(outPath);
        Console.WriteLine($"Generated asset: {outPath}");
    }

    private static void EnsureOutputDirectory(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }

    private static bool IsMissingSchemaException(Exception ex)
    {
        return ex is FormatException &&
            ex.Message.Contains("Failed to find a valid schema", StringComparison.OrdinalIgnoreCase);
    }
}
