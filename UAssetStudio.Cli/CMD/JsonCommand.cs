using System.CommandLine;
using System.CommandLine.Invocation;
using UAssetAPI;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;

namespace UAssetStudio.Cli.CMD;

internal static class JsonCommandBuilder
{
    internal static Command Create(Option<EngineVersion> ueVersion, Option<string?> mappings, Option<bool> json)
    {
        var inputArg = new Argument<string>("input", description: "Path to .uasset/.umap/.json");
        var outputOpt = new Option<string?>(new[] { "--out", "--outdir" }, description: "Output file; default is derived from the input path");
        var sourceAssetOpt = new Option<string?>("--asset", description: "Original .uasset/.umap to borrow schemas from when importing JSON");

        var command = new Command("json", "Convert assets between binary (.uasset/.umap) and JSON")
        {
            inputArg,
            outputOpt,
            sourceAssetOpt,
        };

        command.AddOption(ueVersion);
        command.AddOption(mappings);

        command.SetHandler((InvocationContext ctx) =>
        {
            var version = ctx.ParseResult.GetValueForOption(ueVersion);
            var mappingsPath = ctx.ParseResult.GetValueForOption(mappings);
            var inputPath = ctx.ParseResult.GetValueForArgument(inputArg);
            var outputPath = ctx.ParseResult.GetValueForOption(outputOpt);
            var sourceAssetPath = ctx.ParseResult.GetValueForOption(sourceAssetOpt);
            var asJson = ctx.ParseResult.GetValueForOption(json);

            ctx.ExitCode = CliOutput.Run("json", asJson,
                new { input = inputPath, mappings = mappingsPath, ueVersion = version.ToString(), asset = sourceAssetPath, output = outputPath },
                result =>
                {
                    CliOutput.RequireFile(inputPath, "Input file");

                    var extension = Path.GetExtension(inputPath).ToLowerInvariant();
                    var isAsset = extension == ".uasset" || extension == ".umap";
                    var isJson = extension == ".json";

                    if (!isAsset && !isJson)
                        throw new CliException("UnsupportedInput",
                            $"Unsupported input extension: {extension}. Expected .uasset, .umap, or .json.");

                    try
                    {
                        if (isAsset)
                            ConvertAssetToJson(version, mappingsPath, inputPath, outputPath, result);
                        else
                            ConvertJsonToAsset(version, mappingsPath, inputPath, outputPath, sourceAssetPath, result);
                    }
                    catch (Exception ex) when (isJson && IsMissingSchemaException(ex))
                    {
                        throw new CliException("MissingSchema", ex.Message,
                            "JSON import for inherited Blueprint assets may need --asset <original .uasset/.umap> so the CLI can borrow schemas collected while reading the original binary asset. Prefer the `patch` command for value edits.");
                    }
                });
        });

        return command;
    }

    private static void ConvertAssetToJson(EngineVersion version, string? mappingsPath, string inputPath, string? outputPath, CommandResult result)
    {
        var asset = CliHelpers.LoadAsset(version, mappingsPath, inputPath);
        var defaultOutput = Path.Join(Path.GetDirectoryName(inputPath) ?? ".", Path.GetFileName(inputPath) + ".json");
        var outPath = outputPath ?? defaultOutput;
        EnsureOutputDirectory(outPath);
        var jsonText = asset.SerializeJson(true);
        File.WriteAllText(outPath, jsonText);
        result.AddOutput(outPath);
        result.Line($"Exported JSON: {outPath}");
    }

    private static void ConvertJsonToAsset(EngineVersion version, string? mappingsPath, string inputPath, string? outputPath, string? sourceAssetPath, CommandResult result)
    {
        var jsonText = File.ReadAllText(inputPath);
        var asset = UAsset.DeserializeJson(jsonText);

        if (!string.IsNullOrEmpty(sourceAssetPath))
        {
            CliOutput.RequireFile(sourceAssetPath, "Source asset");
            var sourceAsset = CliHelpers.LoadAsset(version, mappingsPath, sourceAssetPath);
            asset.Mappings = sourceAsset.Mappings;
        }
        else if (!string.IsNullOrEmpty(mappingsPath))
        {
            CliOutput.RequireFile(mappingsPath, "Mappings file");
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
        result.AddOutput(outPath);
        result.Line($"Generated asset: {outPath}");
    }

    private static void EnsureOutputDirectory(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
    }

    private static bool IsMissingSchemaException(Exception ex)
    {
        return ex is FormatException &&
            ex.Message.Contains("Failed to find a valid schema", StringComparison.OrdinalIgnoreCase);
    }
}
