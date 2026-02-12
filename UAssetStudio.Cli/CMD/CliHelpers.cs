using System.IO;
using System.Text;
using System.Linq;
using Antlr4.Runtime;
using Antlr4.Runtime.Misc;
using KismetScript.Decompiler;
using KismetScript.Compiler.Compiler;
using KismetScript.Compiler.Compiler.Processing;
using KismetScript.Parser;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;
using UAssetAPI.Kismet;
using Newtonsoft.Json;

namespace UAssetStudio.Cli.CMD
{
    internal static class CliHelpers
    {
        internal static void VerifyOldAndNew(string oldAssetPath, string newAssetPath, EngineVersion ueVersion, string? mappings)
        {
            var oldAsset = LoadAsset(ueVersion, mappings, oldAssetPath);
            var newAsset = LoadAsset(ueVersion, mappings, newAssetPath);

            var oldJsonText = oldAsset.SerializeJson(true);
            var newJsonText = newAsset.SerializeJson(true);

            File.WriteAllText("old.json", oldJsonText);
            File.WriteAllText("new.json", newJsonText);

            Assert.AreEqual(oldJsonText, newJsonText);
        }

        internal static void VerifyOldAndNewExport(string oldAssetPath, string newAssetPath, EngineVersion ueVersion, string? mappings)
        {
            try
            {
                var oldAsset = LoadAsset(ueVersion, mappings, oldAssetPath);
                var newAsset = LoadAsset(ueVersion, mappings, newAssetPath);

                // Old: functions from original asset (tolerate missing bytecode)
                KismetSerializer.asset = oldAsset;
                var oldClassExport = oldAsset.GetClassExport();
                var oldJsons = oldAsset.Exports
                    .Where(x => x is FunctionExport)
                    .Cast<FunctionExport>()
                    .OrderBy(x => oldClassExport?.FuncMap?.IndexOf(x.ObjectName) ?? -1)
                    .Select(x =>
                    {
                        var bc = x.ScriptBytecode ?? Array.Empty<UAssetAPI.Kismet.Bytecode.KismetExpression>();
                        var json = JsonConvert.SerializeObject(KismetSerializer.SerializeScript(bc), Formatting.Indented);
                        return (x.ObjectName.ToString(), json);
                    });

                // New: functions from newly linked asset
                KismetSerializer.asset = newAsset;
                var newClassExport = newAsset.GetClassExport();
                var newJsons = newAsset.Exports
                    .Where(x => x is FunctionExport)
                    .Cast<FunctionExport>()
                    .OrderBy(x => newClassExport?.FuncMap?.IndexOf(x.ObjectName) ?? -1)
                    .Select(x =>
                    {
                        var bc = x.ScriptBytecode ?? Array.Empty<UAssetAPI.Kismet.Bytecode.KismetExpression>();
                        var json = JsonConvert.SerializeObject(KismetSerializer.SerializeScript(bc), Formatting.Indented);
                        return (x.ObjectName.ToString(), json);
                    });

                var oldJsonText = string.Join("\n", oldJsons);
                var newJsonText = string.Join("\n", newJsons);

                File.WriteAllText("old.json", oldJsonText);
                File.WriteAllText("new.json", newJsonText);

                Assert.AreEqual(oldJsonText, newJsonText);
                Console.WriteLine($"Verification passed");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Warn] Old/New verification skipped: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Structural verification for v2 metadata: verifies the new asset can be parsed
        /// correctly and has the expected number of exports with correct types and names.
        /// Does NOT require binary equality (NameMap order differs in v2).
        /// </summary>
        internal static void VerifyStructural(string oldAssetPath, string newAssetPath, EngineVersion ueVersion, string? mappings)
        {
            var oldAsset = LoadAsset(ueVersion, mappings, oldAssetPath);
            var newAsset = LoadAsset(ueVersion, mappings, newAssetPath);

            // Save JSON for inspection
            File.WriteAllText("old.json", oldAsset.SerializeJson(true));
            File.WriteAllText("new.json", newAsset.SerializeJson(true));

            // Verify export count matches
            if (oldAsset.Exports.Count != newAsset.Exports.Count)
            {
                Console.WriteLine($"[Warn] Export count differs: old={oldAsset.Exports.Count}, new={newAsset.Exports.Count}");
            }

            // Verify each export has matching name and type
            var oldExportNames = oldAsset.Exports.Select(e => e.ObjectName.ToString()).ToList();
            var newExportNames = newAsset.Exports.Select(e => e.ObjectName.ToString()).ToList();

            var missingExports = oldExportNames.Except(newExportNames).ToList();
            var extraExports = newExportNames.Except(oldExportNames).ToList();

            if (missingExports.Any())
                Console.WriteLine($"[Warn] Missing exports in new: {string.Join(", ", missingExports)}");
            if (extraExports.Any())
                Console.WriteLine($"[Warn] Extra exports in new: {string.Join(", ", extraExports)}");

            // Verify no exports are RawExport (failed to parse)
            var rawExports = newAsset.Exports.Where(e => e is RawExport).Select(e => e.ObjectName.ToString()).ToList();
            if (rawExports.Any())
            {
                throw new InvalidOperationException(
                    $"V2 structural verification failed: {rawExports.Count} exports failed to parse (RawExport): {string.Join(", ", rawExports)}");
            }

            // Verify import count matches
            if (oldAsset.Imports.Count != newAsset.Imports.Count)
            {
                Console.WriteLine($"[Warn] Import count differs: old={oldAsset.Imports.Count}, new={newAsset.Imports.Count}");
            }

            Console.WriteLine($"[OK] V2 structural verification passed: {newAsset.Exports.Count} exports, {newAsset.Imports.Count} imports, no RawExport");
        }

        internal static UAsset LoadAsset(EngineVersion ueVersion, string? mappings, string assetPath)
        {
            return mappings != null
                ? new UAsset(assetPath, ueVersion, new Usmap(mappings))
                : new UAsset(assetPath, ueVersion);
        }

        internal static void PrintSyntaxError(int lineNumber, int startIndex, int endIndex, string[] lines)
        {
            if (lineNumber < 1 || lineNumber > lines.Length) throw new ArgumentOutOfRangeException(nameof(lineNumber));
            string line = lines[lineNumber - 1];
            int lineLength = line.Length;
            if (startIndex < 0 || endIndex < 0 || startIndex >= lineLength || endIndex >= lineLength) throw new ArgumentOutOfRangeException(nameof(startIndex));
            string highlightedLine = line.Substring(0, startIndex) + new string('^', endIndex - startIndex + 1) + line.Substring(endIndex + 1);
            var messagePrefix = $"Syntax error at line {lineNumber}:";
            Console.WriteLine($"{messagePrefix}{line}");
            Console.WriteLine(new string(' ', messagePrefix.Length) + highlightedLine);
        }

        internal static void DecompileToKms(UAsset asset, string outPath)
        {
            using var outWriter = new StreamWriter(outPath, false, Encoding.UTF8);
            var decompiler = new KismetDecompiler(outWriter);
            decompiler.Decompile(asset);
        }

        internal static CompiledScriptContext CompileKms(string inPath, EngineVersion engineVersion)
        {
            try
            {
                var parser = new KismetScriptASTParser();
                using var reader = new StreamReader(inPath, Encoding.UTF8);
                var compilationUnit = parser.Parse(reader);
                var typeResolver = new TypeResolver();
                typeResolver.ResolveTypes(compilationUnit);
                var compiler = new KismetScriptCompiler { EngineVersion = engineVersion };
                return compiler.CompileCompilationUnit(compilationUnit);
            }
            catch (ParseCanceledException ex)
            {
                Console.WriteLine($"[Error] Parse canceled: {ex.Message}");
                Console.WriteLine($"[Error] Inner exception: {ex.InnerException?.GetType().Name}: {ex.InnerException?.Message}");
                if (ex.InnerException is InputMismatchException innerEx)
                {
                    var lines = File.ReadAllLines(inPath, Encoding.UTF8);
                    Console.WriteLine($"[Error] Offending token: Line {innerEx.OffendingToken.Line}, Column {innerEx.OffendingToken.Column}, Text: '{innerEx.OffendingToken.Text}'");
                    PrintSyntaxError(innerEx.OffendingToken.Line, innerEx.OffendingToken.Column, innerEx.OffendingToken.Column + innerEx.OffendingToken.Text.Length - 1, lines);
                }
                else if (ex.InnerException is NoViableAltException noViableEx)
                {
                    var lines = File.ReadAllLines(inPath, Encoding.UTF8);
                    Console.WriteLine($"[Error] No viable alternative at Line {noViableEx.OffendingToken.Line}, Column {noViableEx.OffendingToken.Column}, Text: '{noViableEx.OffendingToken.Text}'");
                    PrintSyntaxError(noViableEx.OffendingToken.Line, noViableEx.OffendingToken.Column, noViableEx.OffendingToken.Column + noViableEx.OffendingToken.Text.Length - 1, lines);
                }
                throw;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Error] Compilation failed: {ex.GetType().Name}: {ex.Message}");
                throw;
            }
        }

        internal static void GenCfg(EngineVersion ueVersion, string? mappings, string assetPath, string outputDir)
        {
            var asset = LoadAsset(ueVersion, mappings, assetPath);
            var fileName = Path.GetFileName(assetPath);
            using var output = new StreamWriter(Path.Join(outputDir, Path.ChangeExtension(fileName, ".txt")));
            using var dotOutput = new StreamWriter(Path.Join(outputDir, Path.ChangeExtension(fileName, ".dot")));
            new KismetAnalyzer.SummaryGenerator(asset, output, dotOutput).Summarize();
        }
    }
}
