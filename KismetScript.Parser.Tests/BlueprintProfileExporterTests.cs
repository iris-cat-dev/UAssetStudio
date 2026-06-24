using KismetScript.Syntax.Blueprint;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KismetScript.Parser.Tests;

[TestClass]
public class BlueprintProfileExporterTests
{
    [TestMethod]
    public void Exporter_ProducesStableBridgeDocument()
    {
        var unit = BlueprintAuthoringParserTests.ParseText(BlueprintAuthoringParserTests.FullModernDoorSample());
        var document = BlueprintProfileExporter.Export(unit, "/tmp/BP_Door.kms", "abc123");
        var blueprint = document.Blueprints.Single();

        Assert.AreEqual("kms-bp-export-v1", document.SchemaVersion);
        Assert.AreEqual("/tmp/BP_Door.kms", document.SourcePath);
        Assert.AreEqual("abc123", document.SourceSha256);
        Assert.AreEqual("BP_Door", blueprint.Name);
        Assert.AreEqual("Actor", blueprint.ParentType);
        Assert.AreEqual("/Game/Generated/BP_Door_Gen", blueprint.AssetPath);

        var root = blueprint.Components.Single(x => x.Name == "Root");
        Assert.IsTrue(root.IsRoot);
        Assert.AreEqual("SceneComponent", root.Type);

        var mesh = blueprint.Components.Single(x => x.Name == "Mesh");
        Assert.AreEqual("Root", mesh.AttachTarget);
        var staticMesh = mesh.Properties.Single(x => x.Name == "StaticMesh");
        Assert.AreEqual("call", staticMesh.Value.Kind);
        Assert.AreEqual("asset", staticMesh.Value.Name);
        Assert.AreEqual("StaticMesh", staticMesh.Value.TypeArguments.Single());

        var openAngle = blueprint.Variables.Single(x => x.Name == "OpenAngle");
        Assert.IsTrue(openAngle.IsEditable);
        Assert.AreEqual("Door", openAngle.Category);
        Assert.AreEqual("literal", openAngle.Initializer?.Kind);

        var beginPlay = blueprint.Procedures.Single(x => x.Name == "BeginPlay");
        Assert.AreEqual("event", beginPlay.Kind);
        Assert.AreEqual("BeginPlay", beginPlay.EventName);
        Assert.AreEqual("block", beginPlay.Body?.Kind);
        Assert.AreEqual("expression", beginPlay.Body?.Statements.Single().Kind);
    }

    [TestMethod]
    public void Exporter_EmitsAllowedStatementAndExpressionKinds()
    {
        var unit = BlueprintAuthoringParserTests.ParseSample("BpDoor_SyntaxExpressions.kms");
        var document = BlueprintProfileExporter.Export(unit, "/tmp/BP_Door_Syntax.kms", "syntax");
        var blueprint = document.Blueprints.Single();
        var exercise = blueprint.Procedures.Single(x => x.Name == "Exercise");
        var shouldOpen = blueprint.Procedures.Single(x => x.Name == "ShouldOpen");

        Assert.AreEqual("/Game/Generated/BP_Door_Syntax_Gen", blueprint.AssetPath);
        Assert.AreEqual("callable", exercise.Kind);
        CollectionAssert.AreEquivalent(
            new[] { "block", "var", "expression", "if", "while", "break", "continue" },
            FlattenStatements(exercise.Body!).Select(x => x.Kind).Distinct().ToArray());

        var localVariables = exercise.Body!.Statements.Where(x => x.Kind == "var").ToDictionary(x => x.Name!);
        Assert.AreEqual("array", localVariables["Values"].Initializer?.Kind);
        Assert.AreEqual("object", localVariables["Config"].Initializer?.Kind);
        Assert.AreEqual("cast", localVariables["LocalFloat"].Initializer?.Kind);
        Assert.AreEqual("float", localVariables["LocalFloat"].Initializer?.Type);
        Assert.AreEqual("typeof", localVariables["TypeName"].Initializer?.Kind);
        Assert.AreEqual("float", localVariables["TypeName"].Initializer?.Type);

        var expressionKinds = FlattenExpressions(exercise.Body!).Select(x => x.Kind).Distinct().ToArray();
        CollectionAssert.Contains(expressionKinds, "subscript");
        CollectionAssert.Contains(expressionKinds, "binary");
        CollectionAssert.Contains(expressionKinds, "call");

        var returnValue = shouldOpen.Body!.Statements.Single(x => x.Kind == "return").Value;
        Assert.AreEqual("conditional", returnValue?.Kind);
    }

    private static IEnumerable<KmsBpStatementDto> FlattenStatements(KmsBpStatementDto statement)
    {
        yield return statement;

        foreach (var child in statement.Statements.SelectMany(FlattenStatements))
            yield return child;

        if (statement.Then != null)
        {
            foreach (var child in FlattenStatements(statement.Then))
                yield return child;
        }

        if (statement.Else != null)
        {
            foreach (var child in FlattenStatements(statement.Else))
                yield return child;
        }

        if (statement.Body != null)
        {
            foreach (var child in FlattenStatements(statement.Body))
                yield return child;
        }
    }

    private static IEnumerable<KmsBpExpressionDto> FlattenExpressions(KmsBpStatementDto statement)
    {
        foreach (var expression in new[]
        {
            statement.Initializer,
            statement.Expression,
            statement.Condition,
            statement.Value
        }.Where(x => x != null).SelectMany(x => FlattenExpressions(x!)))
        {
            yield return expression;
        }

        foreach (var child in FlattenStatements(statement).Where(x => !ReferenceEquals(x, statement)))
        {
            foreach (var expression in FlattenExpressions(child))
                yield return expression;
        }
    }

    private static IEnumerable<KmsBpExpressionDto> FlattenExpressions(KmsBpExpressionDto expression)
    {
        yield return expression;

        foreach (var child in expression.Arguments.SelectMany(FlattenExpressions))
            yield return child;
        foreach (var child in expression.Items.SelectMany(FlattenExpressions))
            yield return child;
        foreach (var child in expression.Entries.SelectMany(entry => FlattenExpressions(entry.Key).Concat(FlattenExpressions(entry.Value))))
            yield return child;

        foreach (var child in new[]
        {
            expression.Context,
            expression.Member,
            expression.Left,
            expression.Right,
            expression.Operand,
            expression.Index,
            expression.Condition,
            expression.Then,
            expression.Else
        }.Where(x => x != null).SelectMany(x => FlattenExpressions(x!)))
        {
            yield return child;
        }
    }
}
