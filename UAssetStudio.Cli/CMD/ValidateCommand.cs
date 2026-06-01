using System.CommandLine;
using System.CommandLine.Invocation;
using System.IO;
using System.Linq;
using UAssetAPI;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Validation;

namespace UAssetStudio.Cli.CMD
{
    /// <summary>
    /// 验证 UAsset 文件命令
    /// </summary>
    public static class ValidateCommand
    {
        public static Command Create(Option<EngineVersion> ueVersion, Option<string?> mappings, Option<bool> json)
        {
            var command = new Command("validate", "Validate structural integrity of a .uasset file");

            var assetPathArg = new Argument<string>("asset", "Path to the .uasset file to validate");
            var gameContentPathOption = new Option<string?>(
                new[] { "--game-content", "-g" },
                () => null,
                "Game Content directory, used to verify imported assets exist on disk");

            command.AddArgument(assetPathArg);
            command.AddOption(gameContentPathOption);
            command.AddOption(ueVersion);
            command.AddOption(mappings);

            command.SetHandler((InvocationContext ctx) =>
            {
                var ver = ctx.ParseResult.GetValueForOption(ueVersion);
                var mapPath = ctx.ParseResult.GetValueForOption(mappings);
                var assetPath = ctx.ParseResult.GetValueForArgument(assetPathArg);
                var gameContentPath = ctx.ParseResult.GetValueForOption(gameContentPathOption);
                var asJson = ctx.ParseResult.GetValueForOption(json);

                ctx.ExitCode = CliOutput.Run("validate", asJson,
                    new { asset = assetPath, mappings = mapPath, ueVersion = ver.ToString(), gameContent = gameContentPath },
                    result =>
                    {
                        CliOutput.RequireFile(assetPath, "Asset file");

                        UAsset asset;
                        try
                        {
                            asset = CliHelpers.LoadAsset(ver, mapPath, assetPath);
                        }
                        catch (Exception ex)
                        {
                            throw new CliException("LoadFailed", $"Failed to load asset: {ex.Message}");
                        }

                        var validator = new UAssetValidator(gameContentPath);
                        var validation = validator.Validate(asset);

                        // Structured payload
                        result.Data = new
                        {
                            isValid = validation.IsValid,
                            errorCount = validation.Errors.Count,
                            warningCount = validation.Warnings.Count,
                            errors = validation.Errors.Select(e => new { category = e.Category, message = e.Message, exportIndex = e.ExportIndex, importIndex = e.ImportIndex }),
                            warnings = validation.Warnings.Select(w => new { category = w.Category, message = w.Message, exportIndex = w.ExportIndex, importIndex = w.ImportIndex }),
                        };

                        result.Line($"Validation Result: {(validation.IsValid ? "PASSED" : "FAILED")}");
                        foreach (var e in validation.Errors) result.Line(e.ToString());
                        foreach (var w in validation.Warnings) result.Line($"(warning) {w}");

                        if (!validation.IsValid)
                        {
                            result.Fail("ValidationFailed",
                                $"Asset failed validation with {validation.Errors.Count} error(s)");
                        }
                    });
            });

            return command;
        }
    }
}
