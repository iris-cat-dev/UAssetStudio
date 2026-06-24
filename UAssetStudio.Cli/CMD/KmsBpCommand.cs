using System.CommandLine;
using System.CommandLine.Invocation;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using KismetScript.Parser;
using KismetScript.Syntax;
using KismetScript.Syntax.Blueprint;

namespace UAssetStudio.Cli.CMD;

internal static class KmsBpCommandBuilder
{
    private static readonly JsonSerializerOptions ExportJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    internal static Command Create(Option<bool> json)
    {
        var command = new Command("kms-bp", "Validate and export KMS Blueprint authoring sources");
        command.Add(BuildValidateCommand(json));
        command.Add(BuildExportCommand(json));
        return command;
    }

    private static Command BuildValidateCommand(Option<bool> json)
    {
        var scriptArg = new Argument<string>("script", "Path to KMS-BP .kms source");
        var languageVersionOpt = new Option<int>(new[] { "--language-version" }, () => 0, "KMS-BP language version: 0 or 1");
        var command = new Command("validate", "Validate a KMS-BP source file")
        {
            scriptArg,
            languageVersionOpt
        };

        command.SetHandler((InvocationContext ctx) =>
        {
            var scriptPath = ctx.ParseResult.GetValueForArgument(scriptArg);
            var languageVersion = ParseLanguageVersion(ctx.ParseResult.GetValueForOption(languageVersionOpt));
            var asJson = ctx.ParseResult.GetValueForOption(json);

            ctx.ExitCode = CliOutput.Run("kms-bp validate", asJson,
                new { script = scriptPath, languageVersion = ToLanguageVersionString(languageVersion) },
                result =>
                {
                    var (unit, diagnostics) = LoadAndCheck(scriptPath, result, languageVersion);
                    var model = BlueprintProfileNormalizer.Normalize(unit);
                    PopulateValidationData(result, model, diagnostics, languageVersion);

                    if (diagnostics.Count > 0)
                    {
                        result.Fail("ValidationFailed",
                            $"KMS-BP validation failed with {diagnostics.Count} diagnostic(s).");
                        return;
                    }

                    result.Line($"KMS-BP validation passed: {scriptPath}");
                });
        });

        return command;
    }

    private static Command BuildExportCommand(Option<bool> json)
    {
        var scriptArg = new Argument<string>("script", "Path to KMS-BP .kms source");
        var outputOpt = new Option<string?>(new[] { "--out", "-o" }, "Output JSON file; default is <script>.kms-bp.json");
        var languageVersionOpt = new Option<int>(new[] { "--language-version" }, () => 0, "KMS-BP language version: 0 or 1");
        var command = new Command("export", "Export KMS-BP source to stable bridge JSON")
        {
            scriptArg,
            outputOpt,
            languageVersionOpt
        };

        command.SetHandler((InvocationContext ctx) =>
        {
            var scriptPath = ctx.ParseResult.GetValueForArgument(scriptArg);
            var outputPath = ctx.ParseResult.GetValueForOption(outputOpt);
            var languageVersion = ParseLanguageVersion(ctx.ParseResult.GetValueForOption(languageVersionOpt));
            var asJson = ctx.ParseResult.GetValueForOption(json);

            ctx.ExitCode = CliOutput.Run("kms-bp export", asJson,
                new { script = scriptPath, output = outputPath, languageVersion = ToLanguageVersionString(languageVersion) },
                result =>
                {
                    var (unit, diagnostics) = LoadAndCheck(scriptPath, result, languageVersion);
                    var model = BlueprintProfileNormalizer.Normalize(unit);
                    PopulateValidationData(result, model, diagnostics, languageVersion);

                    if (diagnostics.Count > 0)
                    {
                        result.Fail("ValidationFailed",
                            $"KMS-BP export blocked by {diagnostics.Count} diagnostic(s).");
                        return;
                    }

                    var resolvedOutput = outputPath ?? GetDefaultExportPath(scriptPath);
                    EnsureOutputDirectory(resolvedOutput);

                    var document = BlueprintProfileExporter.Export(
                        unit,
                        Path.GetFullPath(scriptPath),
                        ComputeSha256(scriptPath),
                        languageVersion);
                    var jsonText = JsonSerializer.Serialize(document, ExportJsonOptions);
                    File.WriteAllText(resolvedOutput, jsonText);

                    result.AddOutput(resolvedOutput);
                    result.Data = new
                    {
                        schemaVersion = document.SchemaVersion,
                        languageVersion = document.LanguageVersion,
                        blueprintCount = document.Blueprints.Count,
                        blueprints = document.Blueprints.Select(x => new
                        {
                            x.Name,
                            x.ParentType,
                            x.AssetPath,
                            componentCount = x.Components.Count,
                            variableCount = x.Variables.Count,
                            procedureCount = x.Procedures.Count
                        }),
                        diagnostics = Array.Empty<object>()
                    };
                    result.Line($"Exported KMS-BP bridge JSON: {resolvedOutput}");
                });
        });

        return command;
    }

    private static (CompilationUnit Unit, IReadOnlyList<BlueprintProfileDiagnostic> Diagnostics) LoadAndCheck(
        string scriptPath,
        CommandResult result,
        KmsBpLanguageVersion languageVersion)
    {
        CliOutput.RequireFile(scriptPath, "KMS-BP source");

        var text = File.ReadAllText(scriptPath);
        CompilationUnit unit;
        try
        {
            unit = new KismetScriptASTParser().Parse(text);
        }
        catch (Exception ex)
        {
            throw new CliException("ParseFailed", $"Failed to parse KMS source: {ex.Message}");
        }

        if (KmsProfileDetector.Detect(unit) != KmsProfile.Blueprint)
        {
            throw new CliException("UnsupportedProfile",
                "Input is not a KMS-BP source. Expected a `blueprint ...` declaration or legacy `[Blueprint] class` declaration.");
        }

        var diagnostics = BlueprintProfileSemanticChecker.Check(unit, languageVersion);
        foreach (var diagnostic in diagnostics)
        {
            result.Line($"{diagnostic.Code}: {diagnostic.Message}");
        }

        return (unit, diagnostics);
    }

    private static void PopulateValidationData(
        CommandResult result,
        BlueprintProfileModel model,
        IReadOnlyList<BlueprintProfileDiagnostic> diagnostics,
        KmsBpLanguageVersion languageVersion)
    {
        result.Data = new
        {
            schemaVersion = BlueprintProfileExporter.SchemaVersion,
            languageVersion = ToLanguageVersionString(languageVersion),
            blueprintCount = model.Blueprints.Count,
            blueprints = model.Blueprints.Select(x => new
            {
                x.Name,
                x.ParentType,
                x.AssetPath,
                componentCount = x.Components.Count,
                variableCount = x.Variables.Count,
                procedureCount = x.Procedures.Count
            }),
            diagnosticCount = diagnostics.Count,
            diagnostics = diagnostics.Select(x => new
            {
                x.Code,
                x.Message,
                source = ToSourceDto(x.Node)
            })
        };
    }

    private static KmsBpLanguageVersion ParseLanguageVersion(int languageVersion)
    {
        return languageVersion switch
        {
            0 => KmsBpLanguageVersion.V0,
            1 => KmsBpLanguageVersion.V1,
            _ => throw new CliException("UnsupportedLanguageVersion", $"Unsupported KMS-BP language version '{languageVersion}'. Expected 0 or 1.")
        };
    }

    private static string ToLanguageVersionString(KmsBpLanguageVersion languageVersion)
    {
        return languageVersion == KmsBpLanguageVersion.V1 ? "1" : "0";
    }

    private static object? ToSourceDto(SyntaxNode node)
    {
        if (node.SourceInfo == null)
            return null;

        return new
        {
            node.SourceInfo.Line,
            node.SourceInfo.Column,
            File = IsUnknownSourceFile(node.SourceInfo.FileName) ? null : node.SourceInfo.FileName
        };
    }

    private static bool IsUnknownSourceFile(string fileName)
    {
        return string.IsNullOrWhiteSpace(fileName)
            || string.Equals(fileName, "<unknown>", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetDefaultExportPath(string scriptPath)
    {
        var directory = Path.GetDirectoryName(scriptPath) ?? ".";
        var fileName = Path.GetFileNameWithoutExtension(scriptPath);
        return Path.Join(directory, $"{fileName}.kms-bp.json");
    }

    private static void EnsureOutputDirectory(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
