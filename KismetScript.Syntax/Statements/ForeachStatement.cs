using KismetScript.Syntax.Statements.Expressions;
using KismetScript.Syntax.Statements.Expressions.Identifiers;

namespace KismetScript.Syntax.Statements;

public class ForeachStatement : Statement, IBlockStatement
{
    public Identifier Identifier { get; set; } = null!;

    public TypeIdentifier Type { get; set; } = null!;

    public Expression Collection { get; set; } = null!;

    public CompoundStatement Body { get; set; } = null!;

    IEnumerable<CompoundStatement> IBlockStatement.Blocks => new[] { Body }.Where(x => x != null);
}
