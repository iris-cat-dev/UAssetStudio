using System.Reflection;

namespace UAssetStudio.Mods
{
    /// <summary>
    /// Discovers all <see cref="IAssetMod"/> implementations in this assembly.
    /// </summary>
    public static class ModRegistry
    {
        public static IReadOnlyList<IAssetMod> All()
        {
            return typeof(ModRegistry).Assembly.GetTypes()
                .Where(t => typeof(IAssetMod).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface)
                .Select(t => (IAssetMod)Activator.CreateInstance(t)!)
                .OrderBy(m => m.Name, StringComparer.Ordinal)
                .ToList();
        }

        public static IAssetMod? Find(string name) =>
            All().FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
    }
}
