namespace KismetScript.Syntax.Statements.Expressions;

public class SubscriptOperator : Expression, IOperator
{
    public Expression Operand { get; set; } = null!;

    public Expression Index { get; set; } = null!;

    public int Precedence => 2;

    public SubscriptOperator() : base(ValueKind.Unresolved)
    {
    }

    public override string ToString()
    {
        return $"{Operand}[ {Index} ]";
    }

    public override int GetDepth() => 1 + Operand.GetDepth() + Index.GetDepth();
}
