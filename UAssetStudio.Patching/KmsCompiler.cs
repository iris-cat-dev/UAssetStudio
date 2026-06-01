using System.Text;
using KismetScript.Compiler.Compiler;
using KismetScript.Compiler.Compiler.Processing;
using KismetScript.Parser;
using UAssetAPI.UnrealTypes;

namespace UAssetStudio.Patching
{
    /// <summary>
    /// Compiles a .kms script into a <see cref="CompiledScriptContext"/> using the
    /// ANTLR parser + type resolver + Kismet compiler pipeline.
    /// </summary>
    internal static class KmsCompiler
    {
        public static CompiledScriptContext Compile(string kmsPath, EngineVersion engineVersion)
        {
            var parser = new KismetScriptASTParser();
            using var reader = new StreamReader(kmsPath, Encoding.UTF8);
            var compilationUnit = parser.Parse(reader);
            var typeResolver = new TypeResolver();
            typeResolver.ResolveTypes(compilationUnit);
            var compiler = new KismetScriptCompiler { EngineVersion = engineVersion };
            return compiler.CompileCompilationUnit(compilationUnit);
        }
    }
}
