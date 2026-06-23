using System.Text;
using KismetScript.Syntax.Statements.Expressions.Identifiers;

namespace KismetScript.Syntax.Statements.Expressions;

public class CallOperator : Expression, IOperator
{
    public Identifier Identifier { get; set; } = null!;

    public List<Argument> Arguments { get; set; }

    public List<TypeIdentifier> TypeArguments { get; set; } = new();

    public int Precedence => 2;

    public CallOperator() : base(ValueKind.Unresolved)
    {
        Arguments = new List<Argument>();
    }

    public CallOperator(Identifier identifier, List<Argument> arguments) : base(ValueKind.Unresolved)
    {
        Identifier = identifier;
        Arguments = arguments;
    }

    public CallOperator(ValueKind valueKind, Identifier identifier, List<Argument> arguments) : base(valueKind)
    {
        Identifier = identifier;
        Arguments = arguments;
    }

    public CallOperator(Identifier identifier, params Argument[] arguments) : base(ValueKind.Unresolved)
    {
        Identifier = identifier;
        Arguments = arguments.ToList();
    }

    public override string ToString()
    {
        var builder = new StringBuilder();
        builder.Append(Identifier);
        if (TypeArguments.Count > 0)
        {
            builder.Append("<");
            builder.Append(TypeArguments[0]);
            for (int i = 1; i < TypeArguments.Count; i++)
            {
                builder.Append($", {TypeArguments[i]}");
            }
            builder.Append(">");
        }
        builder.Append("(");

        if (Arguments.Count > 0)
            builder.Append(Arguments[0]);

        for (int i = 1; i < Arguments.Count; i++)
        {
            builder.Append($", {Arguments[i]}");
        }

        builder.Append(")");

        return builder.ToString();
    }

    public override int GetDepth() => 1;
}
