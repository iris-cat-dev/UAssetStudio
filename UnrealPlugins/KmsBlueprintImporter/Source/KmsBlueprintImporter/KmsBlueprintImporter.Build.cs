using UnrealBuildTool;

public class KmsBlueprintImporter : ModuleRules
{
    public KmsBlueprintImporter(ReadOnlyTargetRules Target) : base(Target)
    {
        PCHUsage = PCHUsageMode.UseExplicitOrSharedPCHs;

        PublicDependencyModuleNames.AddRange(new[]
        {
            "Core",
            "CoreUObject",
            "Engine"
        });

        PrivateDependencyModuleNames.AddRange(new[]
        {
            "AssetRegistry",
            "BlueprintGraph",
            "Json",
            "JsonUtilities",
            "Kismet",
            "KismetCompiler",
            "Projects",
            "UnrealEd"
        });
    }
}
