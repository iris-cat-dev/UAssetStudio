using System.Globalization;
using KismetScript.Parser;
using KismetScript.Syntax;
using KismetScript.Syntax.Statements;
using KismetScript.Syntax.Statements.Declarations;
using KismetScript.Syntax.Statements.Expressions;
using KismetScript.Syntax.Statements.Expressions.Identifiers;
using KismetScript.Syntax.Statements.Expressions.Literals;
using UAssetAPI;

namespace UAssetStudio.Patching
{
    /// <summary>
    /// Computes a structured diff between the KMS generated from the original asset and an edited KMS.
    /// Function boundaries are discovered from the AST, while function bodies are compared from source
    /// snippets because several statement nodes do not have a canonical AST string representation yet.
    /// </summary>
    internal static class KmsDiff
    {
        public static KmsPatchPlan CreatePlan(UAsset originalAsset, string editedKmsPath)
        {
            if (!File.Exists(editedKmsPath))
                throw new FileNotFoundException("KMS script not found", editedKmsPath);

            var baselineText = KmsDecompiler.DecompileToString(originalAsset);
            var editedText = File.ReadAllText(editedKmsPath);
            return CreatePlan(baselineText, editedText);
        }

        internal static KmsPatchPlan CreatePlan(string baselineText, string editedText)
        {
            var baseline = ParsedKms.Parse(baselineText);
            var edited = ParsedKms.Parse(editedText);
            var report = new KmsPatchReport();

            foreach (var fn in edited.Functions.Keys.Except(baseline.Functions.Keys, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal))
                report.NewFunctions.Add(fn);

            foreach (var fn in baseline.Functions.Keys.Except(edited.Functions.Keys, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal))
                report.RemovedFunctions.Add(fn);

            foreach (var name in baseline.Functions.Keys.Intersect(edited.Functions.Keys, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal))
            {
                var before = NormalizeSnippet(baseline.Functions[name].Source);
                var after = NormalizeSnippet(edited.Functions[name].Source);
                if (!string.Equals(before, after, StringComparison.Ordinal))
                    report.ChangedFunctions.Add(new ChangedFunction { Name = name });
                else
                    report.PreservedFunctions.Add(name);
            }

            DiffProperties(baseline, edited, report);
            return new KmsPatchPlan(report);
        }

        private static void DiffProperties(ParsedKms baseline, ParsedKms edited, KmsPatchReport report)
        {
            foreach (var path in edited.Properties.Keys.Except(baseline.Properties.Keys, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal))
            {
                report.Warnings.Add(new PatchWarning
                {
                    Type = "NewPropertyIgnored",
                    Path = path,
                    Message = "New properties are not created by safe patch compile. Use --full if this is intentional.",
                });
            }

            foreach (var path in baseline.Properties.Keys.Except(edited.Properties.Keys, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal))
            {
                report.Warnings.Add(new PatchWarning
                {
                    Type = "RemovedPropertyIgnored",
                    Path = path,
                    Message = "Safe patch compile does not delete existing properties.",
                });
            }

            foreach (var path in baseline.Properties.Keys.Intersect(edited.Properties.Keys, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal))
            {
                var before = baseline.Properties[path];
                var after = edited.Properties[path];
                if (string.Equals(before.ValueText, after.ValueText, StringComparison.Ordinal) &&
                    string.Equals(before.TypeText, after.TypeText, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!TryConvertScalar(after.ValueExpression, after.TypeText, out var value, out var reason))
                {
                    throw new InvalidOperationException(
                        $"Unsupported KMS property patch at '{path}' ({after.TypeText}): {reason}. " +
                        "Safe patch compile currently supports scalar bool/int/int64/float/double/string/Name/Enum values.");
                }

                report.ChangedProperties.Add(new ChangedProperty
                {
                    ExportName = after.ExportName,
                    PropertyPath = after.PropertyPath,
                    PropertyType = after.TypeText,
                    OldValue = before.ValueText,
                    NewValue = after.ValueText,
                    ParsedValue = value,
                });
            }
        }

        private static bool TryConvertScalar(Expression expression, string typeText, out object? value, out string reason)
        {
            reason = "";
            value = null;

            switch (expression)
            {
                case BoolLiteral b:
                    value = b.Value;
                    return true;
                case IntLiteral i:
                    value = i.Value;
                    return true;
                case Int64Literal l:
                    value = l.Value;
                    return true;
                case UInt32Literal u32 when IsIntegralType(typeText):
                    value = checked((int)u32.Value);
                    return true;
                case FloatLiteral f:
                    value = f.Value;
                    return true;
                case DoubleLiteral d:
                    value = d.Value;
                    return true;
                case StringLiteral s when IsStringLikeType(typeText):
                    value = s.Value;
                    return true;
                case Identifier id when IsEnumType(typeText):
                    value = id.Text;
                    return true;
                case Identifier id when string.Equals(typeText, "Name", StringComparison.OrdinalIgnoreCase):
                    value = id.Text;
                    return true;
                default:
                    reason = $"expression '{expression}' is not a supported scalar initializer";
                    return false;
            }
        }

        private static bool IsIntegralType(string typeText) =>
            string.Equals(typeText, "int", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(typeText, "byte", StringComparison.OrdinalIgnoreCase);

        private static bool IsStringLikeType(string typeText) =>
            string.Equals(typeText, "string", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(typeText, "Name", StringComparison.OrdinalIgnoreCase) ||
            IsEnumType(typeText);

        private static bool IsEnumType(string typeText) =>
            typeText.StartsWith("Enum<", StringComparison.OrdinalIgnoreCase);

        private static string NormalizeSnippet(string value) =>
            value.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();

        private sealed class ParsedKms
        {
            public Dictionary<string, FunctionEntry> Functions { get; } = new(StringComparer.Ordinal);
            public Dictionary<string, PropertyEntry> Properties { get; } = new(StringComparer.Ordinal);

            public static ParsedKms Parse(string source)
            {
                var parser = new KismetScriptASTParser();
                var unit = parser.Parse(source);
                var parsed = new ParsedKms();
                foreach (var declaration in unit.Declarations)
                    parsed.Collect(declaration, source, currentClassName: null);
                return parsed;
            }

            private void Collect(Declaration declaration, string source, string? currentClassName)
            {
                switch (declaration)
                {
                    case ClassDeclaration cls:
                        var className = cls.Identifier.Text;
                        foreach (var child in cls.Declarations)
                            Collect(child, source, className);
                        break;

                    case ProcedureDeclaration proc when proc.Body != null:
                        Functions[proc.Identifier.Text] = new FunctionEntry
                        {
                            Name = proc.Identifier.Text,
                            Source = ExtractDeclarationSource(source, proc.SourceInfo?.Line ?? 1),
                        };
                        break;

                    case VariableDeclaration variable when currentClassName != null && variable.Initializer != null:
                        AddProperty(
                            exportName: $"Default__{currentClassName}",
                            propertyPath: variable.Identifier.Text,
                            type: variable.Type,
                            value: variable.Initializer);
                        break;

                    case ObjectDeclaration obj:
                        foreach (var prop in obj.Properties)
                        {
                            AddProperty(
                                exportName: obj.Identifier.Text,
                                propertyPath: prop.Name.Text,
                                type: prop.Type,
                                value: prop.Value);
                        }
                        break;
                }
            }

            private void AddProperty(string exportName, string propertyPath, TypeIdentifier type, Expression value)
            {
                var key = $"{exportName}.{propertyPath}";
                Properties[key] = new PropertyEntry
                {
                    ExportName = exportName,
                    PropertyPath = propertyPath,
                    TypeText = FormatType(type),
                    ValueText = FormatExpression(value),
                    ValueExpression = value,
                };
            }
        }

        private sealed class FunctionEntry
        {
            public string Name { get; set; } = "";
            public string Source { get; set; } = "";
        }

        private sealed class PropertyEntry
        {
            public string ExportName { get; set; } = "";
            public string PropertyPath { get; set; } = "";
            public string TypeText { get; set; } = "";
            public string ValueText { get; set; } = "";
            public Expression ValueExpression { get; set; } = null!;
        }

        private static string FormatType(TypeIdentifier type)
        {
            if (type.TypeParameter == null)
                return type.Text;
            return $"{type.Text}<{FormatType(type.TypeParameter)}>";
        }

        private static string FormatExpression(Expression expression) => expression switch
        {
            FloatLiteral f => f.Value.ToString("R", CultureInfo.InvariantCulture),
            DoubleLiteral d => d.Value.ToString("R", CultureInfo.InvariantCulture),
            StringLiteral s => s.Value,
            BoolLiteral b => b.Value ? "true" : "false",
            IntLiteral i => i.Value.ToString(CultureInfo.InvariantCulture),
            Int64Literal l => l.Value.ToString(CultureInfo.InvariantCulture),
            UInt32Literal u32 => u32.Value.ToString(CultureInfo.InvariantCulture),
            UInt64Literal u64 => u64.Value.ToString(CultureInfo.InvariantCulture),
            Identifier id => id.Text,
            _ => expression.ToString(),
        };

        private static string ExtractDeclarationSource(string source, int oneBasedLine)
        {
            var start = GetLineStartOffset(source, Math.Max(1, oneBasedLine));
            var brace = source.IndexOf('{', start);
            if (brace < 0)
                return ReadLine(source, start);

            var depth = 0;
            var inString = false;
            for (var i = brace; i < source.Length; i++)
            {
                var ch = source[i];
                if (ch == '"' && (i == 0 || source[i - 1] != '\\'))
                    inString = !inString;
                if (inString)
                    continue;

                if (ch == '{')
                    depth++;
                else if (ch == '}')
                {
                    depth--;
                    if (depth == 0)
                        return source.Substring(start, i - start + 1);
                }
            }

            return source.Substring(start);
        }

        private static int GetLineStartOffset(string source, int oneBasedLine)
        {
            if (oneBasedLine <= 1)
                return 0;

            var line = 1;
            for (var i = 0; i < source.Length; i++)
            {
                if (source[i] == '\n')
                {
                    line++;
                    if (line == oneBasedLine)
                        return i + 1;
                }
            }
            return source.Length;
        }

        private static string ReadLine(string source, int start)
        {
            var end = source.IndexOf('\n', start);
            return end < 0 ? source.Substring(start) : source.Substring(start, end - start);
        }
    }

    internal sealed class KmsPatchPlan
    {
        public KmsPatchPlan(KmsPatchReport report)
        {
            Report = report;
        }

        public KmsPatchReport Report { get; }
    }
}
