using System.Text.Json.Serialization;

namespace UAssetStudio.Patching
{
    /// <summary>
    /// Structured report for KMS-first safe patch compilation.
    /// This is returned in CLI JSON Data and used by tests to assert what changed.
    /// </summary>
    public sealed class KmsPatchReport
    {
        public string Mode { get; set; } = "patch";
        public List<ChangedFunction> ChangedFunctions { get; } = new();
        public List<string> NewFunctions { get; } = new();
        public List<string> RemovedFunctions { get; } = new();
        public List<string> PreservedFunctions { get; } = new();
        public List<ChangedProperty> ChangedProperties { get; } = new();
        public List<PatchWarning> Warnings { get; } = new();

        public bool HasChanges => ChangedFunctions.Count > 0 || ChangedProperties.Count > 0;
    }

    public sealed class ChangedFunction
    {
        public string Name { get; set; } = "";
    }

    public sealed class ChangedProperty
    {
        public string ExportName { get; set; } = "";
        public string PropertyPath { get; set; } = "";
        public string PropertyType { get; set; } = "";
        public string OldValue { get; set; } = "";
        public string NewValue { get; set; } = "";
        public string Path => $"{ExportName}.{PropertyPath}";

        [JsonIgnore]
        internal object? ParsedValue { get; set; }
    }

    public sealed class PatchWarning
    {
        public string Type { get; set; } = "";
        public string Message { get; set; } = "";
        public string? Path { get; set; }
    }
}
