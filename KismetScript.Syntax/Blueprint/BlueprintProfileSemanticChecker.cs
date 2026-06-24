using KismetScript.Syntax.Statements;
using KismetScript.Syntax.Statements.Declarations;
using KismetScript.Syntax.Statements.Expressions;

namespace KismetScript.Syntax.Blueprint;

public static class BlueprintProfileSemanticChecker
{
    private static readonly HashSet<string> sSupportedDecorators = new(StringComparer.OrdinalIgnoreCase)
    {
        "root",
        "attach",
        "editable",
        "category",
        "tooltip"
    };

    private static readonly HashSet<string> sIrIntrinsics = new(StringComparer.OrdinalIgnoreCase)
    {
        "Context",
        "LetObj",
        "FinalFunction",
        "VirtualFunction",
        "Let",
        "LetBool",
        "LetDelegate",
        "LetWeakObjPtr",
        "LetMulticastDelegate",
        "InstanceVariable",
        "LocalVariable",
        "DefaultVariable",
        "NoObject"
    };

    public static IReadOnlyList<BlueprintProfileDiagnostic> Check(CompilationUnit compilationUnit)
    {
        var diagnostics = new List<BlueprintProfileDiagnostic>();
        var model = BlueprintProfileNormalizer.Normalize(compilationUnit);

        foreach (var blueprint in model.Blueprints)
        {
            CheckBlueprint(blueprint, diagnostics);
        }

        return diagnostics;
    }

    private static void CheckBlueprint(BlueprintModel blueprint, List<BlueprintProfileDiagnostic> diagnostics)
    {
        if (blueprint.Source is ClassDeclaration classDeclaration)
        {
            foreach (var declaration in classDeclaration.Declarations)
            {
                CheckDeclaration(declaration, diagnostics);
            }
        }

        var componentNames = blueprint.Components.Select(component => component.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var component in blueprint.Components.Where(component => !string.IsNullOrWhiteSpace(component.AttachTarget)))
        {
            if (!componentNames.Contains(component.AttachTarget!))
            {
                diagnostics.Add(new BlueprintProfileDiagnostic(
                    "MissingAttachTarget",
                    $"Component '{component.Name}' attaches to unknown component '{component.AttachTarget}'.",
                    component.Source));
            }
        }
    }

    private static void CheckDeclaration(Declaration declaration, List<BlueprintProfileDiagnostic> diagnostics)
    {
        CheckDecorators(declaration, diagnostics);

        switch (declaration)
        {
            case ProcedureDeclaration procedureDeclaration:
                if (procedureDeclaration.Body != null)
                    CheckStatement(procedureDeclaration.Body, diagnostics);
                break;
            case ClassDeclaration classDeclaration:
                foreach (var child in classDeclaration.Declarations)
                    CheckDeclaration(child, diagnostics);
                break;
        }
    }

    private static void CheckDecorators(Declaration declaration, List<BlueprintProfileDiagnostic> diagnostics)
    {
        foreach (var decorator in declaration.Decorators)
        {
            var name = decorator.Identifier.Text;
            if (!sSupportedDecorators.Contains(name))
            {
                diagnostics.Add(new BlueprintProfileDiagnostic(
                    "UnsupportedDecorator",
                    $"Decorator '@{name}' is not supported in KMS-BP v0.",
                    decorator));
                continue;
            }

            if (!IsDecoratorAllowedOnDeclaration(name, declaration))
            {
                diagnostics.Add(new BlueprintProfileDiagnostic(
                    "InvalidDecoratorTarget",
                    $"Decorator '@{name}' cannot be applied to {declaration.DeclarationType}.",
                    decorator));
            }
        }
    }

    private static bool IsDecoratorAllowedOnDeclaration(string name, Declaration declaration)
    {
        return name.ToLowerInvariant() switch
        {
            "root" => declaration is ComponentDeclaration,
            "attach" => declaration is ComponentDeclaration,
            "editable" => declaration is VariableDeclaration,
            "category" => declaration is VariableDeclaration
                || declaration is ProcedureDeclaration { BlueprintKind: BlueprintProcedureKind.Callable or BlueprintProcedureKind.Pure },
            "tooltip" => declaration is ComponentDeclaration or VariableDeclaration or ProcedureDeclaration,
            _ => false
        };
    }

    private static void CheckStatement(Statement statement, List<BlueprintProfileDiagnostic> diagnostics)
    {
        switch (statement)
        {
            case CompoundStatement compoundStatement:
                foreach (var child in compoundStatement.Statements)
                    CheckStatement(child, diagnostics);
                break;
            case GotoStatement:
                AddBannedStatement("goto", statement, diagnostics);
                break;
            case SwitchStatement switchStatement:
                AddBannedStatement("switch", statement, diagnostics);
                CheckExpression(switchStatement.SwitchOn, diagnostics);
                foreach (var label in switchStatement.Labels)
                {
                    foreach (var child in label.Body)
                        CheckStatement(child, diagnostics);
                }
                break;
            case ForStatement forStatement:
                AddBannedStatement("for", statement, diagnostics);
                CheckStatement(forStatement.Initializer, diagnostics);
                CheckExpression(forStatement.Condition, diagnostics);
                CheckExpression(forStatement.AfterLoop, diagnostics);
                if (forStatement.Body != null)
                    CheckStatement(forStatement.Body, diagnostics);
                break;
            case LabelDeclaration:
                AddBannedStatement("label", statement, diagnostics);
                break;
            case IfStatement ifStatement:
                CheckExpression(ifStatement.Condition, diagnostics);
                if (ifStatement.Body != null)
                    CheckStatement(ifStatement.Body, diagnostics);
                if (ifStatement.ElseBody != null)
                    CheckStatement(ifStatement.ElseBody, diagnostics);
                break;
            case WhileStatement whileStatement:
                CheckExpression(whileStatement.Condition, diagnostics);
                if (whileStatement.Body != null)
                    CheckStatement(whileStatement.Body, diagnostics);
                break;
            case ReturnStatement returnStatement:
                if (returnStatement.Value != null)
                    CheckExpression(returnStatement.Value, diagnostics);
                break;
            case VariableDeclaration variableDeclaration:
                CheckDeclaration(variableDeclaration, diagnostics);
                if (variableDeclaration.Initializer != null)
                    CheckExpression(variableDeclaration.Initializer, diagnostics);
                break;
            case Expression expression:
                CheckExpression(expression, diagnostics);
                break;
        }
    }

    private static void CheckExpression(Expression expression, List<BlueprintProfileDiagnostic> diagnostics)
    {
        switch (expression)
        {
            case NewExpression:
                diagnostics.Add(new BlueprintProfileDiagnostic(
                    "BannedExpression",
                    "KMS-BP v0 does not support 'new' expressions.",
                    expression));
                break;
            case CallOperator callOperator:
                if (sIrIntrinsics.Contains(callOperator.Identifier.Text))
                {
                    diagnostics.Add(new BlueprintProfileDiagnostic(
                        "BannedIntrinsic",
                        $"KMS-BP v0 does not support low-level IR intrinsic '{callOperator.Identifier.Text}'.",
                        expression));
                }
                foreach (var argument in callOperator.Arguments)
                    CheckExpression(argument.Expression, diagnostics);
                break;
            case MemberExpression memberExpression:
                CheckExpression(memberExpression.Context, diagnostics);
                CheckExpression(memberExpression.Member, diagnostics);
                break;
            case SubscriptOperator subscriptOperator:
                CheckExpression(subscriptOperator.Index, diagnostics);
                break;
            case BinaryExpression binaryExpression:
                CheckExpression(binaryExpression.Left, diagnostics);
                CheckExpression(binaryExpression.Right, diagnostics);
                break;
            case UnaryExpression unaryExpression:
                CheckExpression(unaryExpression.Operand, diagnostics);
                break;
            case ConditionalExpression conditionalExpression:
                CheckExpression(conditionalExpression.Condition, diagnostics);
                CheckExpression(conditionalExpression.ValueIfTrue, diagnostics);
                CheckExpression(conditionalExpression.ValueIfFalse, diagnostics);
                break;
            case InitializerList initializerList:
                foreach (var item in initializerList.Expressions)
                    CheckExpression(item, diagnostics);
                break;
            case ObjectLiteral objectLiteral:
                foreach (var entry in objectLiteral.Entries)
                    CheckExpression(entry.Value, diagnostics);
                break;
        }
    }

    private static void AddBannedStatement(string statementName, SyntaxNode statement, List<BlueprintProfileDiagnostic> diagnostics)
    {
        diagnostics.Add(new BlueprintProfileDiagnostic(
            "BannedStatement",
            $"KMS-BP v0 does not support '{statementName}' statements.",
            statement));
    }
}
