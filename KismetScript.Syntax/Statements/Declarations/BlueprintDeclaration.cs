using KismetScript.Syntax.Statements.Expressions.Literals;

namespace KismetScript.Syntax.Statements.Declarations;

public class BlueprintDeclaration : ClassDeclaration
{
    public BlueprintDeclaration() : base(DeclarationType.Blueprint)
    {
    }

    public StringLiteral PackagePath { get; set; } = null!;
}
