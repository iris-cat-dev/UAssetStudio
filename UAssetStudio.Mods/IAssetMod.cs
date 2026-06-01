using UAssetAPI.UnrealTypes;

namespace UAssetStudio.Mods
{
    /// <summary>
    /// A reproducible, version-controlled asset modification ("mod-as-code").
    /// Logic/bytecode changes live in a checked-in .kms next to the recipe;
    /// value tweaks are expressed inline via the patching API.
    /// </summary>
    public interface IAssetMod
    {
        /// <summary>Stable id used on the CLI (e.g. "rogue-class-repeat-select").</summary>
        string Name { get; }

        /// <summary>Human-readable description of what the mod does.</summary>
        string Description { get; }

        /// <summary>Subfolder (under the mods root) holding this mod's content files (.kms etc.).</summary>
        string Directory { get; }

        /// <summary>Build the patched asset(s) using the supplied context.</summary>
        void Build(PatchContext ctx);
    }

    /// <summary>
    /// Resolves paths and engine settings for a mod build. Created by the CLI runner.
    /// </summary>
    public sealed class PatchContext
    {
        public required EngineVersion UeVersion { get; init; }
        public string? Mappings { get; init; }

        /// <summary>Root of the original game assets (e.g. the game's project directory).</summary>
        public required string SourceRoot { get; init; }

        /// <summary>Root directory to write patched assets into.</summary>
        public required string OutputRoot { get; init; }

        /// <summary>Directory containing the current mod's content files (.kms).</summary>
        public required string ModFilesDir { get; init; }

        /// <summary>Records produced output paths for reporting.</summary>
        public List<string> Outputs { get; } = new();

        /// <summary>Resolve an original game asset path relative to <see cref="SourceRoot"/>.</summary>
        public string Asset(string relative) => Path.Combine(SourceRoot, relative);

        /// <summary>Resolve a content file (e.g. .kms) shipped with this mod.</summary>
        public string Local(string file) => Path.Combine(ModFilesDir, file);

        /// <summary>Resolve an output path relative to <see cref="OutputRoot"/>, recording it.</summary>
        public string Output(string relative)
        {
            var full = Path.Combine(OutputRoot, relative);
            Outputs.Add(full);
            return full;
        }
    }
}
