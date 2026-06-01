using System.CommandLine;
using System.Text;
using UAssetAPI.UnrealTypes;
using UAssetStudio.Cli.CMD;

namespace UAssetStudio.Cli;

internal static class Program
{
    public static int Main(string[] args)
    {
        // UTF-8 (no BOM) keeps stdout cleanly parseable for agents and tools.
        Console.OutputEncoding = new UTF8Encoding(false);
        var root = new RootCommand("UAssetStudio CLI (decompile/compile/patch) by Iris");

        var ueVersion = new Option<EngineVersion>("--ue-version", () => EngineVersion.VER_UE4_27, "Unreal Engine version");
        var mappings = new Option<string?>("--mappings", description: "Path to .usmap for unversioned properties");
        var json = new Option<bool>("--json", () => false, "Emit a single structured JSON result to stdout (machine-readable)");
        root.AddGlobalOption(json);

        root.Add(CfgCommandBuilder.Create(ueVersion, mappings, json));
        root.Add(DecompileCommandBuilder.Create(ueVersion, mappings, json));
        root.Add(CompileCommandBuilder.Create(ueVersion, mappings, json));
        root.Add(JsonCommandBuilder.Create(ueVersion, mappings, json));
        root.Add(VerifyCommandBuilder.Create(ueVersion, mappings, json));
        root.Add(ValidateCommand.Create(ueVersion, mappings, json));
        root.Add(ModsCommandBuilder.Create(ueVersion, mappings, json));
        root.Add(AssetRegistryCommandBuilder.Create(json));

        return root.Invoke(args);
    }
}
