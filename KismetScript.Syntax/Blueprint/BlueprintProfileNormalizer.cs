using KismetScript.Syntax.Statements;
using KismetScript.Syntax.Statements.Declarations;
using KismetScript.Syntax.Statements.Expressions;
using KismetScript.Syntax.Statements.Expressions.Binary;
using KismetScript.Syntax.Statements.Expressions.Literals;

namespace KismetScript.Syntax.Blueprint;

public static class KmsProfileDetector
{
    public static KmsProfile Detect(CompilationUnit compilationUnit)
    {
        return compilationUnit.Declarations.Any(IsBlueprintRoot)
            ? KmsProfile.Blueprint
            : KmsProfile.Ir;
    }

    private static bool IsBlueprintRoot(Declaration declaration)
    {
        return declaration is BlueprintDeclaration
            || declaration.Attributes.Any(attribute => IsNamed(attribute, "Blueprint"));
    }

    private static bool IsNamed(AttributeDeclaration attribute, string name)
    {
        return string.Equals(attribute.Identifier.Text, name, StringComparison.OrdinalIgnoreCase);
    }
}

public static class BlueprintProfileNormalizer
{
    public static BlueprintProfileModel Normalize(CompilationUnit compilationUnit)
    {
        var model = new BlueprintProfileModel();
        foreach (var declaration in compilationUnit.Declarations)
        {
            var blueprint = NormalizeBlueprint(declaration);
            if (blueprint != null)
                model.Blueprints.Add(blueprint);
        }
        return model;
    }

    private static BlueprintModel? NormalizeBlueprint(Declaration declaration)
    {
        if (declaration is BlueprintDeclaration blueprintDeclaration)
        {
            return NormalizeBlueprintDeclaration(blueprintDeclaration);
        }

        if (declaration is ClassDeclaration classDeclaration && HasAttribute(classDeclaration, "Blueprint"))
        {
            return NormalizeLegacyBlueprintDeclaration(classDeclaration);
        }

        return null;
    }

    private static BlueprintModel NormalizeBlueprintDeclaration(BlueprintDeclaration declaration)
    {
        var blueprint = new BlueprintModel
        {
            Name = declaration.Identifier.Text,
            ParentType = declaration.BaseClassIdentifier?.Text ?? string.Empty,
            AssetPath = declaration.PackagePath.Value,
            Source = declaration
        };
        NormalizeMembers(declaration.Declarations, blueprint);
        return blueprint;
    }

    private static BlueprintModel NormalizeLegacyBlueprintDeclaration(ClassDeclaration declaration)
    {
        var blueprintAttribute = GetAttribute(declaration, "Blueprint");
        var blueprint = new BlueprintModel
        {
            Name = declaration.Identifier.Text,
            ParentType = declaration.BaseClassIdentifier?.Text ?? string.Empty,
            AssetPath = GetNamedStringArgument(blueprintAttribute, "Path")
                ?? GetFirstStringArgument(blueprintAttribute)
                ?? string.Empty,
            Source = declaration
        };
        NormalizeMembers(declaration.Declarations, blueprint);
        return blueprint;
    }

    private static void NormalizeMembers(IEnumerable<Declaration> declarations, BlueprintModel blueprint)
    {
        foreach (var declaration in declarations)
        {
            switch (declaration)
            {
                case ComponentDeclaration componentDeclaration:
                    blueprint.Components.Add(NormalizeComponent(componentDeclaration));
                    break;
                case ObjectDeclaration objectDeclaration when IsLegacyComponent(objectDeclaration):
                    blueprint.Components.Add(NormalizeLegacyComponent(objectDeclaration));
                    break;
                case VariableDeclaration variableDeclaration:
                    blueprint.Variables.Add(NormalizeVariable(variableDeclaration));
                    break;
                case ProcedureDeclaration procedureDeclaration:
                    blueprint.Procedures.Add(NormalizeProcedure(procedureDeclaration));
                    break;
            }
        }
    }

    private static ComponentModel NormalizeComponent(ComponentDeclaration declaration)
    {
        var attachDecorator = GetDecorator(declaration, "attach");
        return new ComponentModel
        {
            Name = declaration.Identifier.Text,
            Type = declaration.ClassIdentifier.Text,
            IsRoot = HasDecorator(declaration, "root"),
            AttachTarget = GetFirstIdentifierArgument(attachDecorator),
            Source = declaration
        };
    }

    private static ComponentModel NormalizeLegacyComponent(ObjectDeclaration declaration)
    {
        var componentAttribute = GetAttribute(declaration, "Component");
        var attachAttribute = GetAttribute(declaration, "Attach");
        return new ComponentModel
        {
            Name = declaration.Identifier.Text,
            Type = declaration.ClassIdentifier.Text,
            IsRoot = HasAttribute(declaration, "RootComponent"),
            AttachTarget = GetNamedIdentifierArgument(componentAttribute, "Attach")
                ?? GetFirstIdentifierArgument(attachAttribute),
            Source = declaration
        };
    }

    private static VariableModel NormalizeVariable(VariableDeclaration declaration)
    {
        var categoryDecorator = GetDecorator(declaration, "category");
        var categoryAttribute = GetAttribute(declaration, "Category");
        return new VariableModel
        {
            Name = declaration.Identifier.Text,
            Type = declaration.Type,
            IsEditable = HasDecorator(declaration, "editable") || HasAttribute(declaration, "Edit"),
            Category = GetFirstStringArgument(categoryDecorator)
                ?? GetFirstStringArgument(categoryAttribute),
            Source = declaration
        };
    }

    private static ProcedureModel NormalizeProcedure(ProcedureDeclaration declaration)
    {
        var eventAttribute = GetAttribute(declaration, "Event");
        var categoryDecorator = GetDecorator(declaration, "category");
        var categoryAttribute = GetAttribute(declaration, "Category");
        var kind = declaration.BlueprintKind;
        if (eventAttribute != null)
            kind = BlueprintProcedureKind.Event;
        else if (HasAttribute(declaration, "BlueprintCallable"))
            kind = BlueprintProcedureKind.Callable;
        else if (HasAttribute(declaration, "BlueprintPure"))
            kind = BlueprintProcedureKind.Pure;

        return new ProcedureModel
        {
            Name = declaration.Identifier.Text,
            Kind = kind,
            EventName = GetFirstStringArgument(eventAttribute)
                ?? (kind == BlueprintProcedureKind.Event ? declaration.Identifier.Text : null),
            Category = GetFirstStringArgument(categoryDecorator)
                ?? GetFirstStringArgument(categoryAttribute),
            Source = declaration
        };
    }

    private static bool IsLegacyComponent(ObjectDeclaration declaration)
    {
        return HasAttribute(declaration, "RootComponent") || HasAttribute(declaration, "Component");
    }

    internal static bool HasDecorator(Declaration declaration, string name)
    {
        return GetDecorator(declaration, name) != null;
    }

    internal static DecoratorDeclaration? GetDecorator(Declaration declaration, string name)
    {
        return declaration.Decorators.FirstOrDefault(decorator =>
            string.Equals(decorator.Identifier.Text, name, StringComparison.OrdinalIgnoreCase));
    }

    internal static bool HasAttribute(Declaration declaration, string name)
    {
        return GetAttribute(declaration, name) != null;
    }

    internal static AttributeDeclaration? GetAttribute(Declaration declaration, string name)
    {
        return declaration.Attributes.FirstOrDefault(attribute =>
            string.Equals(attribute.Identifier.Text, name, StringComparison.OrdinalIgnoreCase));
    }

    internal static string? GetFirstStringArgument(DecoratorDeclaration? decorator)
    {
        return decorator?.Arguments.Select(argument => argument.Expression).OfType<StringLiteral>().FirstOrDefault()?.Value;
    }

    internal static string? GetFirstStringArgument(AttributeDeclaration? attribute)
    {
        return attribute?.Arguments.Select(argument => argument.Expression).OfType<StringLiteral>().FirstOrDefault()?.Value;
    }

    internal static string? GetFirstIdentifierArgument(DecoratorDeclaration? decorator)
    {
        return decorator?.Arguments.Select(argument => argument.Expression).OfType<Identifier>().FirstOrDefault()?.Text;
    }

    internal static string? GetFirstIdentifierArgument(AttributeDeclaration? attribute)
    {
        return attribute?.Arguments.Select(argument => argument.Expression).OfType<Identifier>().FirstOrDefault()?.Text;
    }

    internal static string? GetNamedStringArgument(AttributeDeclaration? attribute, string name)
    {
        return GetNamedArgument(attribute, name) is StringLiteral literal ? literal.Value : null;
    }

    internal static string? GetNamedIdentifierArgument(AttributeDeclaration? attribute, string name)
    {
        return GetNamedArgument(attribute, name) is Identifier identifier ? identifier.Text : null;
    }

    private static Expression? GetNamedArgument(AttributeDeclaration? attribute, string name)
    {
        if (attribute == null)
            return null;

        foreach (var argument in attribute.Arguments)
        {
            if (argument.Expression is AssignmentOperator assignment
                && assignment.Left is Identifier identifier
                && string.Equals(identifier.Text, name, StringComparison.OrdinalIgnoreCase))
            {
                return assignment.Right;
            }
        }

        return null;
    }
}
