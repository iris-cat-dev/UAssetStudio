using KismetScript.Syntax.Statements;
using KismetScript.Syntax.Statements.Declarations;
using KismetScript.Syntax.Statements.Expressions;
using KismetScript.Syntax.Statements.Expressions.Identifiers;

namespace KismetScript.Syntax.Blueprint;

public enum KmsProfile
{
    Ir,
    Blueprint
}

public sealed class BlueprintProfileModel
{
    public List<BlueprintModel> Blueprints { get; } = new();
}

public sealed class BlueprintModel
{
    public string Name { get; init; } = string.Empty;

    public string ParentType { get; init; } = string.Empty;

    public string AssetPath { get; init; } = string.Empty;

    public Declaration Source { get; init; } = null!;

    public List<ComponentModel> Components { get; } = new();

    public List<VariableModel> Variables { get; } = new();

    public List<ProcedureModel> Procedures { get; } = new();
}

public sealed class ComponentModel
{
    public string Name { get; init; } = string.Empty;

    public string Type { get; init; } = string.Empty;

    public bool IsRoot { get; init; }

    public string? AttachTarget { get; init; }

    public Declaration Source { get; init; } = null!;
}

public sealed class VariableModel
{
    public string Name { get; init; } = string.Empty;

    public TypeIdentifier Type { get; init; } = null!;

    public bool IsEditable { get; init; }

    public string? Category { get; init; }

    public VariableDeclaration Source { get; init; } = null!;
}

public sealed class ProcedureModel
{
    public string Name { get; init; } = string.Empty;

    public BlueprintProcedureKind Kind { get; init; }

    public string? EventName { get; init; }

    public string? Category { get; init; }

    public ProcedureDeclaration Source { get; init; } = null!;
}

public sealed class BlueprintProfileDiagnostic
{
    public BlueprintProfileDiagnostic(string code, string message, SyntaxNode node)
    {
        Code = code;
        Message = message;
        Node = node;
    }

    public string Code { get; }

    public string Message { get; }

    public SyntaxNode Node { get; }

    public override string ToString()
    {
        return $"{Code}: {Message}";
    }
}
