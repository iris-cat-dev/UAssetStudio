using System;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.IO;
using System.Linq;
using AssetRegistry.Serializer;

namespace UAssetStudio.Cli.CMD
{
    internal static class AssetRegistryCommandBuilder
    {
        internal static Command Create(Option<bool> json)
        {
            var pathOpt = new Option<string>("--path", () => Path.Join("script", "AssetRegistry.bin"), "Path to AssetRegistry.bin");

            var cmd = new Command("asset-registry", "Parse AssetRegistry.bin and print summary")
            {
                pathOpt
            };

            cmd.SetHandler((InvocationContext ctx) =>
            {
                var path = ctx.ParseResult.GetValueForOption(pathOpt)!;
                var asJson = ctx.ParseResult.GetValueForOption(json);

                ctx.ExitCode = CliOutput.Run("asset-registry", asJson,
                    new { path },
                    result =>
                    {
                        CliOutput.RequireFile(path, "AssetRegistry.bin");

                        var bytes = File.ReadAllBytes(path);
                        var reg = new AssetRegistry.Serializer.AssetRegistry();
                        try
                        {
                            reg.Read(bytes);
                        }
                        catch (Exception ex)
                        {
                            throw new CliException("ParseFailed", $"Failed to parse asset registry: {ex.GetType().Name}: {ex.Message}");
                        }

                        var take = Math.Min(5, reg.fAssetDatas.Count);
                        var sample = reg.fAssetDatas.Take(take).Select(item => new
                        {
                            objectPath = item.ObjectPath?.ToString(),
                            packageName = item.PackageName?.ToString(),
                            assetClass = item.AssetClass?.ToString(),
                            tags = item.TagAndValue.Count,
                            chunks = item.ChunkIDs.Count,
                        });

                        result.Data = new
                        {
                            header = BitConverter.ToString(reg.Header),
                            unknown = reg.Unknown,
                            stringTableSize = reg.keyValuePairs.Count,
                            entries = reg.fAssetDatas.Count,
                            sample,
                        };

                        result.Line($"Header: {BitConverter.ToString(reg.Header)}");
                        result.Line($"Unknown: {reg.Unknown}");
                        result.Line($"StringTable size: {reg.keyValuePairs.Count}");
                        result.Line($"Entries: {reg.fAssetDatas.Count}");
                        for (int idx = 0; idx < take; idx++)
                        {
                            var item = reg.fAssetDatas[idx];
                            result.Line($"[{idx}] ObjectPath={item.ObjectPath} PackageName={item.PackageName} AssetClass={item.AssetClass} Tags={item.TagAndValue.Count} Chunks={item.ChunkIDs.Count}");
                        }
                    });
            });

            return cmd;
        }
    }
}
