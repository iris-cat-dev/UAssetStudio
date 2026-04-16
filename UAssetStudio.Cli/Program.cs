using System.CommandLine;
using System.Text;
using UAssetAPI.UnrealTypes;
using UAssetStudio.Cli.CMD;

namespace UAssetStudio.Cli;

internal static class Program
{
    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.Unicode;
        var root = new RootCommand("UAssetStudio CLI (decompile/compile) by Iris");

        var ueVersion = new Option<EngineVersion>("--ue-version", () => EngineVersion.VER_UE4_27, "Unreal Engine version");
        var mappings = new Option<string?>("--mappings", description: "Path to .usmap for unversioned properties");

        var cfg = CfgCommandBuilder.Create(ueVersion, mappings);
        root.Add(cfg);

        var decompile = DecompileCommandBuilder.Create(ueVersion, mappings);
        root.Add(decompile);

        var compile = CompileCommandBuilder.Create(ueVersion, mappings);
        root.Add(compile);

        var json = JsonCommandBuilder.Create(ueVersion, mappings);
        root.Add(json);

        var verify = VerifyCommandBuilder.Create(ueVersion, mappings);
        root.Add(verify);

        var validate = ValidateCommand.Create(ueVersion, mappings);
        root.Add(validate);

        var assetRegistry = AssetRegistryCommandBuilder.Create();
        root.Add(assetRegistry);

        return root.Invoke(args);
    }
}
