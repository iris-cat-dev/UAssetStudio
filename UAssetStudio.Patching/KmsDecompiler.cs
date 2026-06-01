using System.Text;
using KismetScript.Decompiler;
using UAssetAPI;

namespace UAssetStudio.Patching
{
    /// <summary>
    /// Thin wrapper over <see cref="KismetDecompiler"/> for producing a .kms from a loaded asset.
    /// </summary>
    public static class KmsDecompiler
    {
        public static string DecompileToString(UAsset asset)
        {
            using var writer = new StringWriter();
            var decompiler = new KismetDecompiler(writer);
            decompiler.Decompile(asset);
            return writer.ToString();
        }

        public static void DecompileToFile(UAsset asset, string outPath)
        {
            using var writer = new StreamWriter(outPath, false, new UTF8Encoding(false));
            var decompiler = new KismetDecompiler(writer);
            decompiler.Decompile(asset);
        }
    }
}
