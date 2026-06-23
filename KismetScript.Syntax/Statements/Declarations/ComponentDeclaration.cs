using KismetScript.Syntax.Statements.Expressions;
using KismetScript.Syntax.Statements.Expressions.Identifiers;

namespace KismetScript.Syntax.Statements.Declarations;

public class ComponentDeclaration : ObjectDeclaration
{
    public ComponentDeclaration() : base(DeclarationType.Component)
    {
    }

    public TypeIdentifier ComponentType
    {
        get => new(ClassIdentifier.Text);
        set => ClassIdentifier = new Identifier(value.Text);
    }

    public List<ComponentPropertyAssignment> ComponentProperties { get; init; } = new();
}

public class ComponentPropertyAssignment : SyntaxNode
{
    public TypeIdentifier? Type { get; set; }

    public Identifier Name { get; set; } = null!;

    public Expression Value { get; set; } = null!;

    public bool HasExplicitType => Type != null;
}
