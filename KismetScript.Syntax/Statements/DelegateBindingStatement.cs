using KismetScript.Syntax.Statements.Expressions;

namespace KismetScript.Syntax.Statements;

public class DelegateBindingStatement : Statement
{
    public bool IsBind { get; set; }

    public Expression Target { get; set; } = null!;

    public Expression Handler { get; set; } = null!;
}
