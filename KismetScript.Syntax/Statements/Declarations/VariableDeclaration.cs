using System.Text;
using KismetScript.Syntax.Statements.Expressions;
using KismetScript.Syntax.Statements.Expressions.Identifiers;
using KismetScript.Syntax.Statements.Expressions.Literals;

namespace KismetScript.Syntax.Statements.Declarations;

public class VariableDeclaration : Declaration
{
    public VariableModifier Modifiers { get; set; }

    public TypeIdentifier Type { get; set; } = null!;

    public Expression? Initializer { get; set; }

    public virtual bool IsArray => false;

    public bool IsBlueprintStyle { get; set; }

    public VariableDeclaration() : base(DeclarationType.Variable) { }

    public VariableDeclaration(VariableModifier modifier, TypeIdentifier type, Identifier identifier, Expression initializer)
        : base(DeclarationType.Variable, identifier)
    {
        Modifiers = modifier;
        Type = type;
        Initializer = initializer;
    }

    public override string ToString()
    {
        var builder = new StringBuilder();

        builder.Append($"{Modifiers} ");

        builder.Append($"{Type} {Identifier}");
        if (Initializer != null)
        {
            builder.Append($" = {Initializer}");
        }

        return builder.ToString();
    }
}

public class ArrayVariableDeclaration : VariableDeclaration
{
    public IntLiteral Size { get; set; } = null!;

    public override bool IsArray => true;

    public ArrayVariableDeclaration()
    {
    }

    public ArrayVariableDeclaration(VariableModifier modifier, TypeIdentifier type, Identifier identifier, IntLiteral size, Expression initializer)
        : base(modifier, type, identifier, initializer)
    {
        Size = size;
    }

    public override string ToString()
    {
        var builder = new StringBuilder();

        builder.Append($"{Modifiers} ");

        builder.Append($"{Type} {Identifier}[{Size}]");
        if (Initializer != null)
        {
            builder.Append($" = {Initializer}");
        }

        return builder.ToString();
    }
}
