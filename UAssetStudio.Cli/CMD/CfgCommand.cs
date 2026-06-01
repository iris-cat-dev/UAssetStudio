using System.CommandLine;
using System.CommandLine.Invocation;
using System.IO;
using UAssetAPI.UnrealTypes;

namespace UAssetStudio.Cli.CMD
{
    internal static class CfgCommandBuilder
    {
        internal static Command Create(Option<EngineVersion> ueVersion, Option<string?> mappings, Option<bool> json)
        {
            var inputArg = new Argument<string>("input", description: "Path to asset (.uasset/.umap)");
            var outdirOpt = new Option<string?>(new[] { "--outdir", "--out" }, description: "Output directory for CFG/dot; default = asset directory");

            var cfg = new Command("cfg", "Generate CFG and DOT for an asset")
            {
                inputArg,
                outdirOpt
            };

            cfg.AddOption(ueVersion);
            cfg.AddOption(mappings);

            cfg.SetHandler((InvocationContext ctx) =>
            {
                var ver = ctx.ParseResult.GetValueForOption(ueVersion);
                var mapPath = ctx.ParseResult.GetValueForOption(mappings);
                var input = ctx.ParseResult.GetValueForArgument(inputArg);
                var outdir = ctx.ParseResult.GetValueForOption(outdirOpt);
                var asJson = ctx.ParseResult.GetValueForOption(json);

                ctx.ExitCode = CliOutput.Run("cfg", asJson,
                    new { input, mappings = mapPath, ueVersion = ver.ToString(), outdir },
                    result =>
                    {
                        CliOutput.RequireFile(input, "Input asset");
                        var dir = outdir ?? Path.GetDirectoryName(input) ?? ".";
                        Directory.CreateDirectory(dir);
                        CliHelpers.GenCfg(ver, mapPath, input, dir);
                        var fileName = Path.GetFileName(input);
                        result.AddOutput(Path.Join(dir, Path.ChangeExtension(fileName, ".txt")));
                        result.AddOutput(Path.Join(dir, Path.ChangeExtension(fileName, ".dot")));
                        result.Line($"Generated CFG for {input} in {dir}");
                    });
            });

            return cfg;
        }
    }
}
