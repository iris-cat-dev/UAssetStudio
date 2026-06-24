using KismetScript.Syntax.Statements;
using KismetScript.Syntax.Statements.Declarations;
using KismetScript.Syntax.Statements.Expressions;

namespace KismetScript.Syntax.Blueprint;

public enum KmsBpLanguageVersion
{
    V0,
    V1
}

public static class BlueprintProfileSemanticChecker
{
    private static readonly HashSet<string> sV0SupportedDecorators = new(StringComparer.OrdinalIgnoreCase)
    {
        "root",
        "attach",
        "editable",
        "category",
        "tooltip"
    };

    private static readonly HashSet<string> sV1SupportedDecorators = new(sV0SupportedDecorators, StringComparer.OrdinalIgnoreCase)
    {
        "displayName",
        "tag",
        "clamp",
        "ui",
        "replicated",
        "repNotify",
        "exposeOnSpawn",
        "keywords",
        "callInEditor",
        "rpc",
        "override"
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

    public static IReadOnlyList<BlueprintProfileDiagnostic> Check(
        CompilationUnit compilationUnit,
        KmsBpLanguageVersion languageVersion = KmsBpLanguageVersion.V0)
    {
        var diagnostics = new List<BlueprintProfileDiagnostic>();
        var model = BlueprintProfileNormalizer.Normalize(compilationUnit);

        foreach (var blueprint in model.Blueprints)
        {
            CheckBlueprint(blueprint, diagnostics, languageVersion);
        }

        return diagnostics;
    }

    private static void CheckBlueprint(
        BlueprintModel blueprint,
        List<BlueprintProfileDiagnostic> diagnostics,
        KmsBpLanguageVersion languageVersion)
    {
        if (blueprint.Source is ClassDeclaration classDeclaration)
        {
            CheckDecorators(classDeclaration, diagnostics, languageVersion);
            foreach (var declaration in classDeclaration.Declarations)
            {
                CheckDeclaration(declaration, diagnostics, languageVersion);
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

    private static void CheckDeclaration(
        Declaration declaration,
        List<BlueprintProfileDiagnostic> diagnostics,
        KmsBpLanguageVersion languageVersion)
    {
        CheckDecorators(declaration, diagnostics, languageVersion);

        switch (declaration)
        {
            case ProcedureDeclaration procedureDeclaration:
                if (procedureDeclaration.Body != null)
                    CheckStatement(procedureDeclaration.Body, diagnostics, languageVersion);
                break;
            case ClassDeclaration classDeclaration:
                foreach (var child in classDeclaration.Declarations)
                    CheckDeclaration(child, diagnostics, languageVersion);
                break;
        }
    }

    private static void CheckDecorators(
        Declaration declaration,
        List<BlueprintProfileDiagnostic> diagnostics,
        KmsBpLanguageVersion languageVersion)
    {
        var supportedDecorators = languageVersion == KmsBpLanguageVersion.V1
            ? sV1SupportedDecorators
            : sV0SupportedDecorators;

        foreach (var decorator in declaration.Decorators)
        {
            var name = decorator.Identifier.Text;
            if (!supportedDecorators.Contains(name))
            {
                diagnostics.Add(new BlueprintProfileDiagnostic(
                    "UnsupportedDecorator",
                    $"Decorator '@{name}' is not supported in KMS-BP {languageVersion.ToString().ToLowerInvariant()}.",
                    decorator));
                continue;
            }

            if (!IsDecoratorAllowedOnDeclaration(name, declaration, languageVersion))
            {
                diagnostics.Add(new BlueprintProfileDiagnostic(
                    "InvalidDecoratorTarget",
                    $"Decorator '@{name}' cannot be applied to {declaration.DeclarationType}.",
                    decorator));
            }
        }
    }

    private static bool IsDecoratorAllowedOnDeclaration(
        string name,
        Declaration declaration,
        KmsBpLanguageVersion languageVersion)
    {
        var isV1 = languageVersion == KmsBpLanguageVersion.V1;
        return name.ToLowerInvariant() switch
        {
            "root" => declaration is ComponentDeclaration,
            "attach" => declaration is ComponentDeclaration,
            "editable" => declaration is VariableDeclaration,
            "category" => declaration is VariableDeclaration
                || declaration is ProcedureDeclaration { BlueprintKind: BlueprintProcedureKind.Callable or BlueprintProcedureKind.Pure or BlueprintProcedureKind.Dispatcher },
            "tooltip" => declaration is ComponentDeclaration or VariableDeclaration or ProcedureDeclaration
                || (isV1 && declaration is BlueprintDeclaration),
            "displayname" => isV1 && declaration is BlueprintDeclaration,
            "tag" => isV1 && declaration is ComponentDeclaration,
            "clamp" or "ui" or "replicated" or "repnotify" or "exposeonspawn" => isV1 && declaration is VariableDeclaration,
            "keywords" or "callineditor" => isV1 && declaration is ProcedureDeclaration,
            "rpc" => isV1 && declaration is ProcedureDeclaration { BlueprintKind: BlueprintProcedureKind.Callable },
            "override" => isV1 && declaration is ProcedureDeclaration,
            _ => false
        };
    }

    private static void CheckStatement(
        Statement statement,
        List<BlueprintProfileDiagnostic> diagnostics,
        KmsBpLanguageVersion languageVersion)
    {
        switch (statement)
        {
            case CompoundStatement compoundStatement:
                foreach (var child in compoundStatement.Statements)
                    CheckStatement(child, diagnostics, languageVersion);
                break;
            case GotoStatement:
                AddBannedStatement("goto", statement, diagnostics);
                break;
            case SwitchStatement switchStatement:
                if (languageVersion == KmsBpLanguageVersion.V0)
                    AddBannedStatement("switch", statement, diagnostics);
                CheckExpression(switchStatement.SwitchOn, diagnostics, languageVersion);
                foreach (var label in switchStatement.Labels)
                {
                    foreach (var child in label.Body)
                        CheckStatement(child, diagnostics, languageVersion);
                }
                break;
            case ForStatement forStatement:
                if (languageVersion == KmsBpLanguageVersion.V0)
                    AddBannedStatement("for", statement, diagnostics);
                CheckStatement(forStatement.Initializer, diagnostics, languageVersion);
                CheckExpression(forStatement.Condition, diagnostics, languageVersion);
                CheckExpression(forStatement.AfterLoop, diagnostics, languageVersion);
                if (forStatement.Body != null)
                    CheckStatement(forStatement.Body, diagnostics, languageVersion);
                break;
            case ForeachStatement foreachStatement:
                if (languageVersion == KmsBpLanguageVersion.V0)
                    AddBannedStatement("foreach", statement, diagnostics);
                CheckExpression(foreachStatement.Collection, diagnostics, languageVersion);
                if (foreachStatement.Body != null)
                    CheckStatement(foreachStatement.Body, diagnostics, languageVersion);
                break;
            case DelegateBindingStatement delegateBindingStatement:
                if (languageVersion == KmsBpLanguageVersion.V0)
                    AddBannedStatement(delegateBindingStatement.IsBind ? "bind" : "unbind", statement, diagnostics);
                CheckExpression(delegateBindingStatement.Target, diagnostics, languageVersion);
                CheckExpression(delegateBindingStatement.Handler, diagnostics, languageVersion);
                break;
            case LabelDeclaration:
                AddBannedStatement("label", statement, diagnostics);
                break;
            case IfStatement ifStatement:
                CheckExpression(ifStatement.Condition, diagnostics, languageVersion);
                if (ifStatement.Body != null)
                    CheckStatement(ifStatement.Body, diagnostics, languageVersion);
                if (ifStatement.ElseBody != null)
                    CheckStatement(ifStatement.ElseBody, diagnostics, languageVersion);
                break;
            case WhileStatement whileStatement:
                CheckExpression(whileStatement.Condition, diagnostics, languageVersion);
                if (whileStatement.Body != null)
                    CheckStatement(whileStatement.Body, diagnostics, languageVersion);
                break;
            case ReturnStatement returnStatement:
                if (returnStatement.Value != null)
                    CheckExpression(returnStatement.Value, diagnostics, languageVersion);
                break;
            case VariableDeclaration variableDeclaration:
                CheckDeclaration(variableDeclaration, diagnostics, languageVersion);
                if (variableDeclaration.Initializer != null)
                    CheckExpression(variableDeclaration.Initializer, diagnostics, languageVersion);
                break;
            case Expression expression:
                CheckExpression(expression, diagnostics, languageVersion);
                break;
        }
    }

    private static void CheckExpression(
        Expression expression,
        List<BlueprintProfileDiagnostic> diagnostics,
        KmsBpLanguageVersion languageVersion)
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
                    CheckExpression(argument.Expression, diagnostics, languageVersion);
                break;
            case MemberExpression memberExpression:
                CheckExpression(memberExpression.Context, diagnostics, languageVersion);
                CheckExpression(memberExpression.Member, diagnostics, languageVersion);
                break;
            case SubscriptOperator subscriptOperator:
                CheckExpression(subscriptOperator.Operand, diagnostics, languageVersion);
                CheckExpression(subscriptOperator.Index, diagnostics, languageVersion);
                break;
            case BinaryExpression binaryExpression:
                CheckExpression(binaryExpression.Left, diagnostics, languageVersion);
                CheckExpression(binaryExpression.Right, diagnostics, languageVersion);
                break;
            case UnaryExpression unaryExpression:
                CheckExpression(unaryExpression.Operand, diagnostics, languageVersion);
                break;
            case ConditionalExpression conditionalExpression:
                CheckExpression(conditionalExpression.Condition, diagnostics, languageVersion);
                CheckExpression(conditionalExpression.ValueIfTrue, diagnostics, languageVersion);
                CheckExpression(conditionalExpression.ValueIfFalse, diagnostics, languageVersion);
                break;
            case InitializerList initializerList:
                foreach (var item in initializerList.Expressions)
                    CheckExpression(item, diagnostics, languageVersion);
                break;
            case ObjectLiteral objectLiteral:
                foreach (var entry in objectLiteral.Entries)
                    CheckExpression(entry.Value, diagnostics, languageVersion);
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
