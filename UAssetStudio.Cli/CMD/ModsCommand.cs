using System.CommandLine;
using System.CommandLine.Invocation;
using System.IO;
using System.Linq;
using UAssetAPI.UnrealTypes;
using UAssetStudio.Mods;

namespace UAssetStudio.Cli.CMD
{
    internal static class ModsCommandBuilder
    {
        internal static Command Create(Option<EngineVersion> ueVersion, Option<string?> mappings, Option<bool> json)
        {
            var mods = new Command("mods", "List and run reproducible asset mods (code recipes + checked-in .kms)");

            mods.Add(BuildListCommand(json));
            mods.Add(BuildRunCommand(ueVersion, mappings, json));

            return mods;
        }

        private static Command BuildListCommand(Option<bool> json)
        {
            var list = new Command("list", "List available mods");
            list.SetHandler((InvocationContext ctx) =>
            {
                var asJson = ctx.ParseResult.GetValueForOption(json);
                ctx.ExitCode = CliOutput.Run("mods.list", asJson, null, result =>
                {
                    var all = ModRegistry.All();
                    result.Data = all.Select(m => new { name = m.Name, description = m.Description, directory = m.Directory }).ToList();
                    foreach (var m in all)
                        result.Line($"{m.Name}\t{m.Description}");
                });
            });
            return list;
        }

        private static Command BuildRunCommand(Option<EngineVersion> ueVersion, Option<string?> mappings, Option<bool> json)
        {
            var nameArg = new Argument<string>("name", "Mod id to run (see `mods list`)");
            var sourceOpt = new Option<string>(new[] { "--source", "-s" }, description: "Root directory of the original game assets") { IsRequired = true };
            var outOpt = new Option<string>(new[] { "--out", "--outdir" }, description: "Output root directory for patched assets") { IsRequired = true };
            var modsDirOpt = new Option<string?>("--mods-dir", description: "Directory holding mod content files (.kms); default = UAssetStudio.Mods");

            var run = new Command("run", "Run a mod recipe to produce patched assets")
            {
                nameArg,
                sourceOpt,
                outOpt,
                modsDirOpt,
            };
            run.AddOption(ueVersion);
            run.AddOption(mappings);

            run.SetHandler((InvocationContext ctx) =>
            {
                var ver = ctx.ParseResult.GetValueForOption(ueVersion);
                var mapPath = ctx.ParseResult.GetValueForOption(mappings);
                var name = ctx.ParseResult.GetValueForArgument(nameArg);
                var source = ctx.ParseResult.GetValueForOption(sourceOpt)!;
                var outRoot = ctx.ParseResult.GetValueForOption(outOpt)!;
                var modsDir = ctx.ParseResult.GetValueForOption(modsDirOpt);
                var asJson = ctx.ParseResult.GetValueForOption(json);

                ctx.ExitCode = CliOutput.Run("mods.run", asJson,
                    new { name, source, outRoot, modsDir, ueVersion = ver.ToString(), mappings = mapPath },
                    result =>
                    {
                        var mod = ModRegistry.Find(name)
                            ?? throw new CliException("ModNotFound",
                                $"Mod '{name}' not found.",
                                $"Available: {string.Join(", ", ModRegistry.All().Select(m => m.Name))}");

                        var resolvedModsDir = ResolveModsDir(modsDir);
                        var modFilesDir = Path.Combine(resolvedModsDir, mod.Directory);

                        var context = new PatchContext
                        {
                            UeVersion = ver,
                            Mappings = mapPath,
                            SourceRoot = source,
                            OutputRoot = outRoot,
                            ModFilesDir = modFilesDir,
                        };

                        mod.Build(context);

                        foreach (var output in context.Outputs)
                            result.AddOutput(output);
                        result.Data = new { mod = mod.Name, modFilesDir, outputs = context.Outputs };
                        result.Line($"Ran mod '{mod.Name}' -> {context.Outputs.Count} output(s)");
                    });
            });

            return run;
        }

        /// <summary>
        /// Resolves the directory containing mod content files. Prefers an explicit value,
        /// then a cwd-relative "UAssetStudio.Mods", then alongside the executing assembly.
        /// </summary>
        private static string ResolveModsDir(string? explicitDir)
        {
            if (!string.IsNullOrEmpty(explicitDir))
                return explicitDir;

            var cwdCandidate = Path.Combine(Directory.GetCurrentDirectory(), "UAssetStudio.Mods");
            if (Directory.Exists(cwdCandidate))
                return cwdCandidate;

            return AppContext.BaseDirectory;
        }
    }
}
