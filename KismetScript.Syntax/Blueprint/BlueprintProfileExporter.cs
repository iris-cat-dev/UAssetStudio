using KismetScript.Syntax.Statements;
using KismetScript.Syntax.Statements.Declarations;
using KismetScript.Syntax.Statements.Expressions;
using KismetScript.Syntax.Statements.Expressions.Binary;
using KismetScript.Syntax.Statements.Expressions.Identifiers;
using KismetScript.Syntax.Statements.Expressions.Literals;
using KismetScript.Syntax.Statements.Expressions.Unary;

namespace KismetScript.Syntax.Blueprint;

public static class BlueprintProfileExporter
{
    public const string SchemaVersion = "kms-bp-export-v1";

    public static KmsBpExportDocument Export(
        CompilationUnit compilationUnit,
        string? sourcePath = null,
        string? sourceSha256 = null,
        KmsBpLanguageVersion languageVersion = KmsBpLanguageVersion.V0)
    {
        var model = BlueprintProfileNormalizer.Normalize(compilationUnit);
        return new KmsBpExportDocument
        {
            SchemaVersion = SchemaVersion,
            LanguageVersion = languageVersion == KmsBpLanguageVersion.V1 ? "1" : "0",
            SourcePath = sourcePath,
            SourceSha256 = sourceSha256,
            Blueprints = model.Blueprints.Select(ExportBlueprint).ToList()
        };
    }

    private static KmsBpBlueprintDto ExportBlueprint(BlueprintModel blueprint)
    {
        var declarations = (blueprint.Source as ClassDeclaration)?.Declarations ?? new List<Declaration>();
        var componentByName = declarations.OfType<ComponentDeclaration>().ToDictionary(x => x.Identifier.Text, StringComparer.OrdinalIgnoreCase);

        return new KmsBpBlueprintDto
        {
            Name = blueprint.Name,
            ParentType = blueprint.ParentType,
            AssetPath = blueprint.AssetPath,
            Source = ExportSource(blueprint.Source),
            Components = blueprint.Components.Select(component => ExportComponent(component, componentByName)).ToList(),
            Variables = blueprint.Variables.Select(ExportVariable).ToList(),
            Procedures = blueprint.Procedures.Select(ExportProcedure).ToList()
        };
    }

    private static KmsBpComponentDto ExportComponent(ComponentModel component, IReadOnlyDictionary<string, ComponentDeclaration> componentByName)
    {
        componentByName.TryGetValue(component.Name, out var declaration);
        return new KmsBpComponentDto
        {
            Name = component.Name,
            Type = component.Type,
            IsRoot = component.IsRoot,
            AttachTarget = component.AttachTarget,
            Source = ExportSource(component.Source),
            Properties = declaration?.ComponentProperties.Select(ExportComponentProperty).ToList() ?? new()
        };
    }

    private static KmsBpComponentPropertyDto ExportComponentProperty(ComponentPropertyAssignment property)
    {
        return new KmsBpComponentPropertyDto
        {
            Name = property.Name.Text,
            Type = property.Type != null ? FormatType(property.Type) : null,
            Value = ExportExpression(property.Value),
            Source = ExportSource(property)
        };
    }

    private static KmsBpVariableDto ExportVariable(VariableModel variable)
    {
        return new KmsBpVariableDto
        {
            Name = variable.Name,
            Type = FormatType(variable.Type),
            IsEditable = variable.IsEditable,
            Category = variable.Category,
            Initializer = variable.Source.Initializer != null ? ExportExpression(variable.Source.Initializer) : null,
            Source = ExportSource(variable.Source)
        };
    }

    private static KmsBpProcedureDto ExportProcedure(ProcedureModel procedure)
    {
        return new KmsBpProcedureDto
        {
            Name = procedure.Name,
            Kind = procedure.Kind.ToString().ToLowerInvariant(),
            ReturnType = FormatType(procedure.Source.ReturnType),
            EventName = procedure.EventName,
            Category = procedure.Category,
            Parameters = procedure.Source.Parameters.Select(ExportParameter).ToList(),
            Body = procedure.Source.Body != null ? ExportStatement(procedure.Source.Body) : null,
            Source = ExportSource(procedure.Source)
        };
    }

    private static KmsBpParameterDto ExportParameter(Parameter parameter)
    {
        return new KmsBpParameterDto
        {
            Name = parameter.Identifier.Text,
            Type = FormatType(parameter.Type),
            Modifier = FormatParameterModifier(parameter.Modifier),
            Source = ExportSource(parameter)
        };
    }

    private static string? FormatParameterModifier(ParameterModifier modifier)
    {
        if (modifier == ParameterModifier.None)
            return null;

        var parts = new List<string>();
        if (modifier.HasFlag(ParameterModifier.Const))
            parts.Add("const");
        if (modifier.HasFlag(ParameterModifier.Ref))
            parts.Add("ref");
        if (modifier.HasFlag(ParameterModifier.Out))
            parts.Add("out");

        return parts.Count == 0 ? null : string.Join(" ", parts);
    }

    private static KmsBpStatementDto ExportStatement(Statement statement)
    {
        return statement switch
        {
            CompoundStatement compound => new KmsBpStatementDto
            {
                Kind = "block",
                Statements = compound.Statements.Select(ExportStatement).ToList(),
                Source = ExportSource(statement)
            },
            VariableDeclaration variable => new KmsBpStatementDto
            {
                Kind = "var",
                Name = variable.Identifier.Text,
                Type = FormatType(variable.Type),
                Initializer = variable.Initializer != null ? ExportExpression(variable.Initializer) : null,
                Source = ExportSource(statement)
            },
            IfStatement ifStatement => new KmsBpStatementDto
            {
                Kind = "if",
                Condition = ExportExpression(ifStatement.Condition),
                Then = ExportStatement(ifStatement.Body),
                Else = ifStatement.ElseBody != null ? ExportStatement(ifStatement.ElseBody) : null,
                Source = ExportSource(statement)
            },
            WhileStatement whileStatement => new KmsBpStatementDto
            {
                Kind = "while",
                Condition = ExportExpression(whileStatement.Condition),
                Body = ExportStatement(whileStatement.Body),
                Source = ExportSource(statement)
            },
            ForStatement forStatement => new KmsBpStatementDto
            {
                Kind = "for",
                InitializerStatement = ExportStatement(forStatement.Initializer),
                Condition = ExportExpression(forStatement.Condition),
                After = ExportExpression(forStatement.AfterLoop),
                Body = ExportStatement(forStatement.Body),
                Source = ExportSource(statement)
            },
            ForeachStatement foreachStatement => new KmsBpStatementDto
            {
                Kind = "foreach",
                Name = foreachStatement.Identifier.Text,
                Type = FormatType(foreachStatement.Type),
                Collection = ExportExpression(foreachStatement.Collection),
                Body = ExportStatement(foreachStatement.Body),
                Source = ExportSource(statement)
            },
            SwitchStatement switchStatement => new KmsBpStatementDto
            {
                Kind = "switch",
                SwitchOn = ExportExpression(switchStatement.SwitchOn),
                Cases = switchStatement.Labels.Select(ExportSwitchCase).ToList(),
                Source = ExportSource(statement)
            },
            DelegateBindingStatement delegateBinding => new KmsBpStatementDto
            {
                Kind = delegateBinding.IsBind ? "bind" : "unbind",
                Target = ExportExpression(delegateBinding.Target),
                Handler = ExportExpression(delegateBinding.Handler),
                Source = ExportSource(statement)
            },
            BreakStatement => new KmsBpStatementDto { Kind = "break", Source = ExportSource(statement) },
            ContinueStatement => new KmsBpStatementDto { Kind = "continue", Source = ExportSource(statement) },
            ReturnStatement returnStatement => new KmsBpStatementDto
            {
                Kind = "return",
                Value = returnStatement.Value != null ? ExportExpression(returnStatement.Value) : null,
                Source = ExportSource(statement)
            },
            Expression expression => new KmsBpStatementDto
            {
                Kind = "expression",
                Expression = ExportExpression(expression),
                Source = ExportSource(statement)
            },
            _ => new KmsBpStatementDto
            {
                Kind = statement.GetType().Name,
                Source = ExportSource(statement)
            }
        };
    }

    private static KmsBpSwitchCaseDto ExportSwitchCase(SwitchLabel label)
    {
        return new KmsBpSwitchCaseDto
        {
            IsDefault = label is DefaultSwitchLabel,
            Condition = label is ConditionSwitchLabel condition ? ExportExpression(condition.Condition) : null,
            Body = new KmsBpStatementDto
            {
                Kind = "block",
                Statements = label.Body.Select(ExportStatement).ToList(),
                Source = ExportSource(label)
            },
            Source = ExportSource(label)
        };
    }

    private static KmsBpExpressionDto ExportExpression(Expression expression)
    {
        return expression switch
        {
            Identifier identifier => new KmsBpExpressionDto
            {
                Kind = "identifier",
                Name = identifier.Text,
                Source = ExportSource(expression)
            },
            BoolLiteral literal => ExportLiteral("bool", literal.Value, expression),
            StringLiteral literal => ExportLiteral("string", literal.Value, expression),
            FloatLiteral literal => ExportLiteral("float", literal.Value, expression),
            DoubleLiteral literal => ExportLiteral("double", literal.Value, expression),
            IntLiteral literal => ExportLiteral("int", literal.Value, expression),
            Int64Literal literal => ExportLiteral("int64", literal.Value, expression),
            UInt32Literal literal => ExportLiteral("uint32", literal.Value, expression),
            UInt64Literal literal => ExportLiteral("uint64", literal.Value, expression),
            CallOperator call => new KmsBpExpressionDto
            {
                Kind = "call",
                Name = call.Identifier.Text,
                TypeArguments = call.TypeArguments.Select(FormatType).ToList(),
                Arguments = call.Arguments.Select(argument => ExportExpression(argument.Expression)).ToList(),
                Source = ExportSource(expression)
            },
            MemberExpression member => new KmsBpExpressionDto
            {
                Kind = "member",
                Op = member.Kind == MemberExpressionKind.Pointer ? "->" : ".",
                Context = ExportExpression(member.Context),
                Member = ExportExpression(member.Member),
                Source = ExportSource(expression)
            },
            SubscriptOperator subscript => new KmsBpExpressionDto
            {
                Kind = "subscript",
                Context = ExportExpression(subscript.Operand),
                Index = ExportExpression(subscript.Index),
                Source = ExportSource(expression)
            },
            BinaryExpression binary => new KmsBpExpressionDto
            {
                Kind = "binary",
                Op = GetBinaryOperator(binary),
                Left = ExportExpression(binary.Left),
                Right = ExportExpression(binary.Right),
                Source = ExportSource(expression)
            },
            CastOperator cast => new KmsBpExpressionDto
            {
                Kind = "cast",
                Type = FormatType(cast.TypeIdentifier),
                Operand = ExportExpression(cast.Operand),
                Source = ExportSource(expression)
            },
            TypeofOperator typeOf => new KmsBpExpressionDto
            {
                Kind = "typeof",
                Type = typeOf.Operand is TypeIdentifier typeIdentifier ? FormatType(typeIdentifier) : typeOf.Operand.ToString(),
                Source = ExportSource(expression)
            },
            UnaryExpression unary => new KmsBpExpressionDto
            {
                Kind = "unary",
                Op = GetUnaryOperator(unary),
                Operand = ExportExpression(unary.Operand),
                Source = ExportSource(expression)
            },
            ConditionalExpression conditional => new KmsBpExpressionDto
            {
                Kind = "conditional",
                Condition = ExportExpression(conditional.Condition),
                Then = ExportExpression(conditional.ValueIfTrue),
                Else = ExportExpression(conditional.ValueIfFalse),
                Source = ExportSource(expression)
            },
            InitializerList initializerList => new KmsBpExpressionDto
            {
                Kind = initializerList.Kind == InitializerListKind.Bracket ? "array" : "initializer",
                Items = initializerList.Expressions.Select(ExportExpression).ToList(),
                Source = ExportSource(expression)
            },
            ObjectLiteral objectLiteral => new KmsBpExpressionDto
            {
                Kind = "object",
                Entries = objectLiteral.Entries.Select(entry => new KmsBpObjectEntryDto
                {
                    Key = ExportExpression(entry.Key),
                    Value = ExportExpression(entry.Value)
                }).ToList(),
                Source = ExportSource(expression)
            },
            NewExpression newExpression => new KmsBpExpressionDto
            {
                Kind = "new",
                Type = FormatType(newExpression.TypeIdentifier),
                Items = newExpression.Initializer.Select(ExportExpression).ToList(),
                Source = ExportSource(expression)
            },
            _ => new KmsBpExpressionDto
            {
                Kind = expression.GetType().Name,
                Text = expression.ToString(),
                Source = ExportSource(expression)
            }
        };
    }

    private static KmsBpExpressionDto ExportLiteral(string type, object? value, SyntaxNode source)
    {
        return new KmsBpExpressionDto
        {
            Kind = "literal",
            Type = type,
            Value = value,
            Source = ExportSource(source)
        };
    }

    private static string FormatType(TypeIdentifier type)
    {
        if (type.TypeParameters.Count == 0)
            return type.TypeParameter == null
                ? type.Text
                : $"{type.Text}<{FormatType(type.TypeParameter)}>";

        return $"{type.Text}<{string.Join(", ", type.TypeParameters.Select(FormatType))}>";
    }

    private static KmsBpSourceDto? ExportSource(SyntaxNode node)
    {
        if (node.SourceInfo == null)
            return null;

        return new KmsBpSourceDto
        {
            Line = node.SourceInfo.Line,
            Column = node.SourceInfo.Column,
            File = IsUnknownSourceFile(node.SourceInfo.FileName) ? null : node.SourceInfo.FileName
        };
    }

    private static bool IsUnknownSourceFile(string fileName)
    {
        return string.IsNullOrWhiteSpace(fileName)
            || string.Equals(fileName, "<unknown>", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetBinaryOperator(BinaryExpression expression)
    {
        return expression switch
        {
            AssignmentOperator => "=",
            AdditionAssignmentOperator => "+=",
            SubtractionAssignmentOperator => "-=",
            MultiplicationAssignmentOperator => "*=",
            DivisionAssignmentOperator => "/=",
            ModulusAssignmentOperator => "%=",
            AdditionOperator => "+",
            SubtractionOperator => "-",
            MultiplicationOperator => "*",
            DivisionOperator => "/",
            ModulusOperator => "%",
            EqualityOperator => "==",
            NonEqualityOperator => "!=",
            LessThanOperator => "<",
            LessThanOrEqualOperator => "<=",
            GreaterThanOperator => ">",
            GreaterThanOrEqualOperator => ">=",
            LogicalAndOperator => "&&",
            LogicalOrOperator => "||",
            BitwiseAndOperator => "&",
            BitwiseOrOperator => "|",
            BitwiseXorOperator => "^",
            BitwiseShiftLeftOperator => "<<",
            BitwiseShiftRightOperator => ">>",
            _ => expression.GetType().Name
        };
    }

    private static string GetUnaryOperator(UnaryExpression expression)
    {
        return expression switch
        {
            LogicalNotOperator => "!",
            NegationOperator => "-",
            PrefixIncrementOperator => "++pre",
            PrefixDecrementOperator => "--pre",
            PostfixIncrementOperator => "post++",
            PostfixDecrementOperator => "post--",
            _ => expression.GetType().Name
        };
    }
}

public sealed class KmsBpExportDocument
{
    public string SchemaVersion { get; set; } = BlueprintProfileExporter.SchemaVersion;
    public string LanguageVersion { get; set; } = "0";
    public string? SourcePath { get; set; }
    public string? SourceSha256 { get; set; }
    public List<KmsBpBlueprintDto> Blueprints { get; set; } = new();
}

public sealed class KmsBpBlueprintDto
{
    public string Name { get; set; } = string.Empty;
    public string ParentType { get; set; } = string.Empty;
    public string AssetPath { get; set; } = string.Empty;
    public List<KmsBpComponentDto> Components { get; set; } = new();
    public List<KmsBpVariableDto> Variables { get; set; } = new();
    public List<KmsBpProcedureDto> Procedures { get; set; } = new();
    public KmsBpSourceDto? Source { get; set; }
}

public sealed class KmsBpComponentDto
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public bool IsRoot { get; set; }
    public string? AttachTarget { get; set; }
    public List<KmsBpComponentPropertyDto> Properties { get; set; } = new();
    public KmsBpSourceDto? Source { get; set; }
}

public sealed class KmsBpComponentPropertyDto
{
    public string Name { get; set; } = string.Empty;
    public string? Type { get; set; }
    public KmsBpExpressionDto Value { get; set; } = null!;
    public KmsBpSourceDto? Source { get; set; }
}

public sealed class KmsBpVariableDto
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public bool IsEditable { get; set; }
    public string? Category { get; set; }
    public KmsBpExpressionDto? Initializer { get; set; }
    public KmsBpSourceDto? Source { get; set; }
}

public sealed class KmsBpProcedureDto
{
    public string Name { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string ReturnType { get; set; } = string.Empty;
    public string? EventName { get; set; }
    public string? Category { get; set; }
    public List<KmsBpParameterDto> Parameters { get; set; } = new();
    public KmsBpStatementDto? Body { get; set; }
    public KmsBpSourceDto? Source { get; set; }
}

public sealed class KmsBpParameterDto
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string? Modifier { get; set; }
    public KmsBpSourceDto? Source { get; set; }
}

public sealed class KmsBpStatementDto
{
    public string Kind { get; set; } = string.Empty;
    public string? Name { get; set; }
    public string? Type { get; set; }
    public KmsBpStatementDto? InitializerStatement { get; set; }
    public KmsBpExpressionDto? Initializer { get; set; }
    public KmsBpExpressionDto? Expression { get; set; }
    public KmsBpExpressionDto? Condition { get; set; }
    public KmsBpExpressionDto? After { get; set; }
    public KmsBpExpressionDto? Collection { get; set; }
    public KmsBpExpressionDto? Target { get; set; }
    public KmsBpExpressionDto? Handler { get; set; }
    public KmsBpExpressionDto? SwitchOn { get; set; }
    public KmsBpStatementDto? Then { get; set; }
    public KmsBpStatementDto? Else { get; set; }
    public KmsBpStatementDto? Body { get; set; }
    public KmsBpExpressionDto? Value { get; set; }
    public List<KmsBpStatementDto> Statements { get; set; } = new();
    public List<KmsBpSwitchCaseDto> Cases { get; set; } = new();
    public KmsBpSourceDto? Source { get; set; }
}

public sealed class KmsBpSwitchCaseDto
{
    public bool IsDefault { get; set; }
    public KmsBpExpressionDto? Condition { get; set; }
    public KmsBpStatementDto Body { get; set; } = new() { Kind = "block" };
    public KmsBpSourceDto? Source { get; set; }
}

public sealed class KmsBpExpressionDto
{
    public string Kind { get; set; } = string.Empty;
    public string? Name { get; set; }
    public string? Type { get; set; }
    public object? Value { get; set; }
    public string? Text { get; set; }
    public string? Op { get; set; }
    public List<string> TypeArguments { get; set; } = new();
    public List<KmsBpExpressionDto> Arguments { get; set; } = new();
    public KmsBpExpressionDto? Context { get; set; }
    public KmsBpExpressionDto? Member { get; set; }
    public KmsBpExpressionDto? Left { get; set; }
    public KmsBpExpressionDto? Right { get; set; }
    public KmsBpExpressionDto? Operand { get; set; }
    public KmsBpExpressionDto? Index { get; set; }
    public KmsBpExpressionDto? Condition { get; set; }
    public KmsBpExpressionDto? Then { get; set; }
    public KmsBpExpressionDto? Else { get; set; }
    public List<KmsBpExpressionDto> Items { get; set; } = new();
    public List<KmsBpObjectEntryDto> Entries { get; set; } = new();
    public KmsBpSourceDto? Source { get; set; }
}

public sealed class KmsBpObjectEntryDto
{
    public KmsBpExpressionDto Key { get; set; } = null!;
    public KmsBpExpressionDto Value { get; set; } = null!;
}

public sealed class KmsBpSourceDto
{
    public int Line { get; set; }
    public int Column { get; set; }
    public string? File { get; set; }
}
