using KismetScript.Syntax;
using KismetScript.Syntax.Blueprint;
using KismetScript.Syntax.Statements;
using KismetScript.Syntax.Statements.Declarations;
using KismetScript.Syntax.Statements.Expressions;
using KismetScript.Syntax.Statements.Expressions.Binary;
using KismetScript.Syntax.Statements.Expressions.Identifiers;
using KismetScript.Syntax.Statements.Expressions.Literals;
using KismetScript.Syntax.Statements.Expressions.Unary;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KismetScript.Parser.Tests;

[TestClass]
public class BlueprintSyntaxSampleCoverageTests
{
    private static readonly string[] sExpectedV0SyntaxSamples =
    [
        "BpDoor_Calls.kms",
        "BpDoor_Components.kms",
        "BpDoor_DecoratorsAndImports.kms",
        "BpDoor_EventsFunctions.kms",
        "BpDoor_Full.kms",
        "BpDoor_LegacyAttributes.kms",
        "BpDoor_Minimal.kms",
        "BpDoor_Operators.kms",
        "BpDoor_SyntaxExpressions.kms",
        "BpDoor_Variables.kms"
    ];

    private static readonly string[] sExpectedV1SyntaxSamples =
    [
        "BpDoorV1_Calls.kms",
        "BpDoorV1_Construction.kms",
        "BpDoorV1_ControlFlow.kms",
        "BpDoorV1_Dispatchers.kms",
        "BpDoorV1_Full.kms",
        "BpDoorV1_Metadata.kms",
        "BpDoorV1_ReplicationRpc.kms",
        "BpDoorV1_Types.kms"
    ];

    [TestMethod]
    public void V0SampleDirectory_MatchesSyntaxFixtureManifest()
    {
        var actual = Directory
            .GetFiles(V0SampleDirectory, "*.kms")
            .Select(Path.GetFileName)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

        CollectionAssert.AreEqual(sExpectedV0SyntaxSamples.OrderBy(x => x, StringComparer.Ordinal).ToArray(), actual);
    }

    [TestMethod]
    public void V1SampleDirectory_MatchesSyntaxFixtureManifest()
    {
        var actual = Directory
            .GetFiles(V1SampleDirectory, "*.kms")
            .Select(Path.GetFileName)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

        CollectionAssert.AreEqual(sExpectedV1SyntaxSamples.OrderBy(x => x, StringComparer.Ordinal).ToArray(), actual);
    }

    [DataTestMethod]
    [DataRow("BpDoor_Calls.kms")]
    [DataRow("BpDoor_Components.kms")]
    [DataRow("BpDoor_DecoratorsAndImports.kms")]
    [DataRow("BpDoor_EventsFunctions.kms")]
    [DataRow("BpDoor_Full.kms")]
    [DataRow("BpDoor_LegacyAttributes.kms")]
    [DataRow("BpDoor_Minimal.kms")]
    [DataRow("BpDoor_Operators.kms")]
    [DataRow("BpDoor_SyntaxExpressions.kms")]
    [DataRow("BpDoor_Variables.kms")]
    public void V0Samples_ParseAsBlueprintProfile_AndPassBpSemanticCheck(string sampleName)
    {
        var unit = BlueprintAuthoringParserTests.ParseV0Sample(sampleName);
        var diagnostics = BlueprintProfileSemanticChecker.Check(unit);

        Assert.AreEqual(KmsProfile.Blueprint, KmsProfileDetector.Detect(unit), sampleName);
        Assert.AreEqual(
            0,
            diagnostics.Count,
            $"{sampleName} diagnostics:{Environment.NewLine}{string.Join(Environment.NewLine, diagnostics.Select(x => $"{x.Code}: {x.Message}"))}");
    }

    [DataTestMethod]
    [DataRow("BpDoor_Calls.kms", "BP_Door_Calls", "/Game/Generated/BP_Door_Calls_Gen", 0, 2, 1)]
    [DataRow("BpDoor_Minimal.kms", "BP_Door", "/Game/Generated/BP_Door_Gen", 0, 0, 1)]
    [DataRow("BpDoor_Components.kms", "BP_Door", "/Game/Generated/BP_Door_Gen", 2, 0, 0)]
    [DataRow("BpDoor_Variables.kms", "BP_Door", "/Game/Generated/BP_Door_Gen", 0, 2, 0)]
    [DataRow("BpDoor_EventsFunctions.kms", "BP_Door", "/Game/Generated/BP_Door_Gen", 0, 0, 3)]
    [DataRow("BpDoor_Full.kms", "BP_Door", "/Game/Generated/BP_Door_Gen", 2, 2, 3)]
    [DataRow("BpDoor_LegacyAttributes.kms", "BP_Door", "/Game/Generated/BP_Door_Gen", 2, 1, 1)]
    [DataRow("BpDoor_SyntaxExpressions.kms", "BP_Door_Syntax", "/Game/Generated/BP_Door_Syntax_Gen", 0, 3, 2)]
    [DataRow("BpDoor_DecoratorsAndImports.kms", "BP_Door_Metadata", "/Game/Generated/BP_Door_Metadata_Gen", 2, 1, 3)]
    [DataRow("BpDoor_Operators.kms", "BP_Door_Operators", "/Game/Generated/BP_Door_Operators_Gen", 0, 1, 1)]
    public void V0Samples_NormalizeToExpectedBlueprintShape(
        string sampleName,
        string expectedName,
        string expectedAssetPath,
        int expectedComponentCount,
        int expectedVariableCount,
        int expectedProcedureCount)
    {
        var unit = BlueprintAuthoringParserTests.ParseV0Sample(sampleName);
        var blueprint = unit.Declarations.OfType<BlueprintDeclaration>().Single();
        var model = BlueprintProfileNormalizer.Normalize(unit).Blueprints.Single();

        Assert.AreEqual(expectedName, blueprint.Identifier.Text);
        Assert.AreEqual(expectedName, model.Name);
        Assert.AreEqual(expectedAssetPath, blueprint.PackagePath.Value);
        Assert.AreEqual(expectedAssetPath, model.AssetPath);

        Assert.AreEqual(expectedComponentCount, blueprint.Declarations.OfType<ComponentDeclaration>().Count(), sampleName);
        Assert.AreEqual(expectedVariableCount, blueprint.Declarations.OfType<VariableDeclaration>().Count(), sampleName);
        Assert.AreEqual(expectedProcedureCount, blueprint.Declarations.OfType<ProcedureDeclaration>().Count(), sampleName);

        Assert.AreEqual(expectedComponentCount, model.Components.Count, sampleName);
        Assert.AreEqual(expectedVariableCount, model.Variables.Count, sampleName);
        Assert.AreEqual(expectedProcedureCount, model.Procedures.Count, sampleName);
    }

    [DataTestMethod]
    [DataRow("BpDoorV1_Calls.kms")]
    [DataRow("BpDoorV1_Construction.kms")]
    [DataRow("BpDoorV1_ControlFlow.kms")]
    [DataRow("BpDoorV1_Dispatchers.kms")]
    [DataRow("BpDoorV1_Full.kms")]
    [DataRow("BpDoorV1_Metadata.kms")]
    [DataRow("BpDoorV1_ReplicationRpc.kms")]
    [DataRow("BpDoorV1_Types.kms")]
    public void V1Samples_ParseAsBlueprintProfile_AndPassV1SemanticCheck(string sampleName)
    {
        var unit = BlueprintAuthoringParserTests.ParseV1Sample(sampleName);
        var diagnostics = BlueprintProfileSemanticChecker.Check(unit, KmsBpLanguageVersion.V1);

        Assert.AreEqual(KmsProfile.Blueprint, KmsProfileDetector.Detect(unit), sampleName);
        Assert.AreEqual(
            0,
            diagnostics.Count,
            $"{sampleName} diagnostics:{Environment.NewLine}{string.Join(Environment.NewLine, diagnostics.Select(x => $"{x.Code}: {x.Message}"))}");
    }

    [TestMethod]
    public void DecoratorsAndImportsSample_ParsesImportBlockStructEnum()
    {
        var unit = BlueprintAuthoringParserTests.ParseV0Sample("BpDoor_DecoratorsAndImports.kms");
        var package = unit.Declarations.OfType<PackageDeclaration>().Single();

        Assert.AreEqual("/Game/Common/DoorTypes", package.Identifier.Text);

        var doorConfig = package.Declarations.OfType<ClassDeclaration>().Single(x => x.Identifier.Text == "DoorConfig");
        var mode = doorConfig.Declarations.OfType<VariableDeclaration>().Single();
        Assert.AreEqual("Mode", mode.Identifier.Text);
        Assert.AreEqual("int", mode.Type.Text);

        var doorState = package.Declarations.OfType<EnumDeclaration>().Single(x => x.Identifier.Text == "DoorState");
        CollectionAssert.AreEqual(
            new[] { "Closed", "Open" },
            doorState.Values.Select(x => x.Identifier.Text).ToArray());
    }

    [TestMethod]
    public void DecoratorsAndImportsSample_CoversSupportedDecoratorTargets()
    {
        var blueprint = ParseSingleBlueprint("BpDoor_DecoratorsAndImports.kms");

        var root = blueprint.Declarations.OfType<ComponentDeclaration>().Single(x => x.Identifier.Text == "Root");
        CollectionAssert.AreEquivalent(new[] { "root", "tooltip" }, DecoratorNames(root));
        AssertStringDecoratorArgument(root, "tooltip", "Scene root");

        var mesh = blueprint.Declarations.OfType<ComponentDeclaration>().Single(x => x.Identifier.Text == "Mesh");
        CollectionAssert.AreEquivalent(new[] { "attach", "tooltip" }, DecoratorNames(mesh));
        AssertStringDecoratorArgument(mesh, "tooltip", "Door mesh");

        var openAngle = blueprint.Declarations.OfType<VariableDeclaration>().Single(x => x.Identifier.Text == "OpenAngle");
        CollectionAssert.AreEquivalent(new[] { "editable", "category", "tooltip" }, DecoratorNames(openAngle));
        AssertStringDecoratorArgument(openAngle, "category", "Door");
        AssertStringDecoratorArgument(openAngle, "tooltip", "Door open angle");

        var beginPlay = blueprint.Declarations.OfType<ProcedureDeclaration>().Single(x => x.Identifier.Text == "BeginPlay");
        CollectionAssert.AreEquivalent(new[] { "tooltip" }, DecoratorNames(beginPlay));
        Assert.AreEqual(BlueprintProcedureKind.Event, beginPlay.BlueprintKind);

        var toggle = blueprint.Declarations.OfType<ProcedureDeclaration>().Single(x => x.Identifier.Text == "Toggle");
        CollectionAssert.AreEquivalent(new[] { "category", "tooltip" }, DecoratorNames(toggle));
        Assert.AreEqual(BlueprintProcedureKind.Callable, toggle.BlueprintKind);

        var isWideOpen = blueprint.Declarations.OfType<ProcedureDeclaration>().Single(x => x.Identifier.Text == "IsWideOpen");
        CollectionAssert.AreEquivalent(new[] { "category", "tooltip" }, DecoratorNames(isWideOpen));
        Assert.AreEqual(BlueprintProcedureKind.Pure, isWideOpen.BlueprintKind);
    }

    [TestMethod]
    public void CallsSample_CoversDirectTypedMemberAndOutCalls()
    {
        var blueprint = ParseSingleBlueprint("BpDoor_Calls.kms");
        var procedure = blueprint.Declarations.OfType<ProcedureDeclaration>().Single(x => x.Identifier.Text == "ExerciseCalls");
        var expressions = FindExpressions(procedure.Body!).ToArray();

        Assert.AreEqual(1, procedure.Parameters.Count);
        Assert.AreEqual("Result", procedure.Parameters[0].Identifier.Text);
        Assert.IsTrue(procedure.Parameters[0].Modifier.HasFlag(ParameterModifier.Out));

        var directPrint = expressions.OfType<CallOperator>().Single(x => x.Identifier.Text == "print");
        Assert.AreEqual(1, directPrint.Arguments.Count);
        Assert.IsInstanceOfType(directPrint.Arguments[0].Expression, typeof(StringLiteral));

        var assetCall = expressions.OfType<CallOperator>().Single(x => x.Identifier.Text == "asset");
        Assert.AreEqual("StaticMesh", assetCall.TypeArguments.Single().Text);
        Assert.IsInstanceOfType(assetCall.Arguments.Single().Expression, typeof(StringLiteral));

        var clampCall = expressions
            .OfType<MemberExpression>()
            .Single(x => x.Kind == MemberExpressionKind.Dot
                && x.Context is Identifier { Text: "MathLibrary" }
                && x.Member is CallOperator { Identifier.Text: "Clamp" });
        var clampMember = (CallOperator)clampCall.Member;
        Assert.AreEqual("float", clampMember.TypeArguments.Single().Text);
        Assert.AreEqual(3, clampMember.Arguments.Count);

        var staticPrint = expressions
            .OfType<MemberExpression>()
            .Single(x => x.Kind == MemberExpressionKind.Dot
                && x.Context is Identifier { Text: "KismetSystemLibrary" }
                && x.Member is CallOperator { Identifier.Text: "PrintString" });
        Assert.AreEqual(1, ((CallOperator)staticPrint.Member).Arguments.Count);

        Assert.IsTrue(expressions.OfType<MemberExpression>().Any(x =>
            x.Kind == MemberExpressionKind.Dot
            && x.Context is Identifier { Text: "DoorController" }
            && x.Member is CallOperator { Identifier.Text: "Open" }));

        Assert.IsTrue(expressions.OfType<MemberExpression>().Any(x =>
            x.Kind == MemberExpressionKind.Dot
            && x.Context is Identifier { Text: "DoorController" }
            && x.Member is CallOperator { Identifier.Text: "Close" }));

        var outCalls = expressions.OfType<CallOperator>().Where(x => x.Identifier.Text == "TryGetAngle").ToArray();
        Assert.AreEqual(2, outCalls.Length);
        Assert.IsInstanceOfType(outCalls[0].Arguments.Single(), typeof(OutArgument));
        Assert.AreEqual("Result", ((OutArgument)outCalls[0].Arguments.Single()).Identifier.Text);
        Assert.IsInstanceOfType(outCalls[1].Arguments.Single(), typeof(OutDeclarationArgument));
        var declarationArgument = (OutDeclarationArgument)outCalls[1].Arguments.Single();
        Assert.AreEqual("float", declarationArgument.Type.Text);
        Assert.AreEqual("Scratch", declarationArgument.Identifier.Text);
    }

    [TestMethod]
    public void SyntaxExpressionsSample_CoversParametersStatementsAndStructuredExpressions()
    {
        var blueprint = ParseSingleBlueprint("BpDoor_SyntaxExpressions.kms");
        var exercise = blueprint.Declarations.OfType<ProcedureDeclaration>().Single(x => x.Identifier.Text == "Exercise");
        var body = exercise.Body!;

        Assert.AreEqual(2, exercise.Parameters.Count);
        Assert.AreEqual("Value", exercise.Parameters[0].Identifier.Text);
        Assert.AreEqual("int", exercise.Parameters[0].Type.Text);
        Assert.AreEqual("Result", exercise.Parameters[1].Identifier.Text);
        Assert.IsTrue(exercise.Parameters[1].Modifier.HasFlag(ParameterModifier.Out));

        Assert.IsTrue(body.Statements.OfType<IfStatement>().Any());
        Assert.IsTrue(body.Statements.OfType<WhileStatement>().Any());
        Assert.IsTrue(FlattenStatements(body).OfType<BreakStatement>().Any());
        Assert.IsTrue(FlattenStatements(body).OfType<ContinueStatement>().Any());

        var values = body.Statements.OfType<VariableDeclaration>().Single(x => x.Identifier.Text == "Values");
        Assert.AreEqual("Array", values.Type.Text);
        Assert.AreEqual("int", values.Type.TypeParameter?.Text);
        var list = (InitializerList)values.Initializer!;
        Assert.AreEqual(InitializerListKind.Bracket, list.Kind);
        Assert.AreEqual(3, list.Expressions.Count);

        var config = body.Statements.OfType<VariableDeclaration>().Single(x => x.Identifier.Text == "Config");
        var literal = (ObjectLiteral)config.Initializer!;
        CollectionAssert.AreEquivalent(new[] { "Mode", "Count" }, literal.Entries.Select(x => x.KeyText).ToArray());

        var localFloat = body.Statements.OfType<VariableDeclaration>().Single(x => x.Identifier.Text == "LocalFloat");
        Assert.IsInstanceOfType(localFloat.Initializer, typeof(CastOperator));
        Assert.AreEqual("float", ((CastOperator)localFloat.Initializer!).TypeIdentifier.Text);

        var typeName = body.Statements.OfType<VariableDeclaration>().Single(x => x.Identifier.Text == "TypeName");
        Assert.IsInstanceOfType(typeName.Initializer, typeof(TypeofOperator));
        Assert.AreEqual("float", ((TypeIdentifier)((TypeofOperator)typeName.Initializer!).Operand).Text);

        var shouldOpen = blueprint.Declarations.OfType<ProcedureDeclaration>().Single(x => x.Identifier.Text == "ShouldOpen");
        var returnStatement = shouldOpen.Body!.Statements.OfType<ReturnStatement>().Single();
        Assert.IsInstanceOfType(returnStatement.Value, typeof(ConditionalExpression));
    }

    [TestMethod]
    public void OperatorsSample_CoversArithmeticLogicalBitwiseUnaryAndAssignments()
    {
        var blueprint = ParseSingleBlueprint("BpDoor_Operators.kms");
        var procedure = blueprint.Declarations.OfType<ProcedureDeclaration>().Single(x => x.Identifier.Text == "ExerciseOperators");

        Assert.AreEqual(3, procedure.Parameters.Count);
        Assert.AreEqual("Counter", procedure.Parameters[1].Identifier.Text);
        Assert.IsTrue(procedure.Parameters[1].Modifier.HasFlag(ParameterModifier.Ref));
        Assert.AreEqual("Result", procedure.Parameters[2].Identifier.Text);
        Assert.IsTrue(procedure.Parameters[2].Modifier.HasFlag(ParameterModifier.Out));

        var expressions = FindExpressions(procedure.Body!).ToArray();

        AssertContainsExpression<MultiplicationOperator>(expressions);
        AssertContainsExpression<DivisionOperator>(expressions);
        AssertContainsExpression<ModulusOperator>(expressions);
        AssertContainsExpression<AdditionOperator>(expressions);
        AssertContainsExpression<SubtractionOperator>(expressions);
        AssertContainsExpression<BitwiseAndOperator>(expressions);
        AssertContainsExpression<BitwiseXorOperator>(expressions);
        AssertContainsExpression<BitwiseOrOperator>(expressions);
        AssertContainsExpression<NonEqualityOperator>(expressions);
        AssertContainsExpression<LessThanOrEqualOperator>(expressions);
        AssertContainsExpression<LogicalNotOperator>(expressions);
        AssertContainsExpression<LogicalOrOperator>(expressions);
        AssertContainsExpression<NegationOperator>(expressions);
        AssertContainsExpression<PrefixIncrementOperator>(expressions);
        AssertContainsExpression<PostfixIncrementOperator>(expressions);
        AssertContainsExpression<PrefixDecrementOperator>(expressions);
        AssertContainsExpression<PostfixDecrementOperator>(expressions);
        AssertContainsExpression<AssignmentOperator>(expressions);
        AssertContainsExpression<SubtractionAssignmentOperator>(expressions);
        AssertContainsExpression<MultiplicationAssignmentOperator>(expressions);
        AssertContainsExpression<DivisionAssignmentOperator>(expressions);
        AssertContainsExpression<ModulusAssignmentOperator>(expressions);
    }

    private static string V0SampleDirectory => Path.Combine(AppContext.BaseDirectory, "Samples", "V0");

    private static string V1SampleDirectory => Path.Combine(AppContext.BaseDirectory, "Samples", "V1");

    private static BlueprintDeclaration ParseSingleBlueprint(string sampleName)
    {
        return BlueprintAuthoringParserTests.ParseV0Sample(sampleName)
            .Declarations
            .OfType<BlueprintDeclaration>()
            .Single();
    }

    private static string[] DecoratorNames(Declaration declaration)
    {
        return declaration.Decorators.Select(x => x.Identifier.Text).ToArray();
    }

    private static void AssertStringDecoratorArgument(Declaration declaration, string decoratorName, string expectedValue)
    {
        var decorator = declaration.Decorators.Single(x => x.Identifier.Text == decoratorName);
        Assert.IsInstanceOfType(decorator.Arguments.Single().Expression, typeof(StringLiteral));
        Assert.AreEqual(expectedValue, ((StringLiteral)decorator.Arguments.Single().Expression).Value);
    }

    private static void AssertContainsExpression<TExpression>(IEnumerable<Expression> expressions)
        where TExpression : Expression
    {
        Assert.IsTrue(
            expressions.OfType<TExpression>().Any(),
            $"Expected expression type {typeof(TExpression).Name}.");
    }

    private static IEnumerable<Statement> FlattenStatements(Statement statement)
    {
        yield return statement;

        switch (statement)
        {
            case CompoundStatement compound:
                foreach (var child in compound.Statements.SelectMany(FlattenStatements))
                    yield return child;
                break;
            case IfStatement ifStatement:
                if (ifStatement.Body != null)
                {
                    foreach (var child in FlattenStatements(ifStatement.Body))
                        yield return child;
                }

                if (ifStatement.ElseBody != null)
                {
                    foreach (var child in FlattenStatements(ifStatement.ElseBody))
                        yield return child;
                }
                break;
            case WhileStatement whileStatement when whileStatement.Body != null:
                foreach (var child in FlattenStatements(whileStatement.Body))
                    yield return child;
                break;
        }
    }

    private static IEnumerable<Expression> FindExpressions(Statement statement)
    {
        switch (statement)
        {
            case CompoundStatement compound:
                foreach (var child in compound.Statements.SelectMany(FindExpressions))
                    yield return child;
                break;
            case VariableDeclaration variable when variable.Initializer != null:
                foreach (var child in FindExpressions(variable.Initializer))
                    yield return child;
                break;
            case IfStatement ifStatement:
                foreach (var child in FindExpressions(ifStatement.Condition))
                    yield return child;
                if (ifStatement.Body != null)
                {
                    foreach (var child in FindExpressions(ifStatement.Body))
                        yield return child;
                }

                if (ifStatement.ElseBody != null)
                {
                    foreach (var child in FindExpressions(ifStatement.ElseBody))
                        yield return child;
                }
                break;
            case WhileStatement whileStatement:
                foreach (var child in FindExpressions(whileStatement.Condition))
                    yield return child;
                if (whileStatement.Body != null)
                {
                    foreach (var child in FindExpressions(whileStatement.Body))
                        yield return child;
                }
                break;
            case ReturnStatement returnStatement when returnStatement.Value != null:
                foreach (var child in FindExpressions(returnStatement.Value))
                    yield return child;
                break;
            case Expression expression:
                foreach (var child in FindExpressions(expression))
                    yield return child;
                break;
        }
    }

    private static IEnumerable<Expression> FindExpressions(Expression expression)
    {
        yield return expression;

        switch (expression)
        {
            case CallOperator call:
                foreach (var child in call.Arguments.SelectMany(argument => FindExpressions(argument.Expression)))
                    yield return child;
                break;
            case MemberExpression member:
                foreach (var child in FindExpressions(member.Context))
                    yield return child;
                foreach (var child in FindExpressions(member.Member))
                    yield return child;
                break;
            case SubscriptOperator subscript:
                foreach (var child in FindExpressions(subscript.Operand))
                    yield return child;
                foreach (var child in FindExpressions(subscript.Index))
                    yield return child;
                break;
            case BinaryExpression binary:
                foreach (var child in FindExpressions(binary.Left))
                    yield return child;
                foreach (var child in FindExpressions(binary.Right))
                    yield return child;
                break;
            case UnaryExpression unary:
                foreach (var child in FindExpressions(unary.Operand))
                    yield return child;
                break;
            case ConditionalExpression conditional:
                foreach (var child in FindExpressions(conditional.Condition))
                    yield return child;
                foreach (var child in FindExpressions(conditional.ValueIfTrue))
                    yield return child;
                foreach (var child in FindExpressions(conditional.ValueIfFalse))
                    yield return child;
                break;
            case InitializerList initializer:
                foreach (var child in initializer.Expressions.SelectMany(FindExpressions))
                    yield return child;
                break;
            case ObjectLiteral objectLiteral:
                foreach (var child in objectLiteral.Entries.SelectMany(entry => FindExpressions(entry.Value)))
                    yield return child;
                break;
        }
    }
}
