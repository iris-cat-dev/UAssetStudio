using KismetScript.Parser;
using KismetScript.Syntax;
using KismetScript.Syntax.Blueprint;
using KismetScript.Syntax.Statements;
using KismetScript.Syntax.Statements.Declarations;
using KismetScript.Syntax.Statements.Expressions;
using KismetScript.Syntax.Statements.Expressions.Binary;
using KismetScript.Syntax.Statements.Expressions.Unary;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KismetScript.Parser.Tests;

[TestClass]
public class BlueprintAuthoringParserTests
{
    [TestMethod]
    public void ParsesBlueprintHeader_WithParentAndAssetPath()
    {
        var blueprint = ParseFirstBlueprint("BpDoor_Minimal.kms");

        Assert.AreEqual("BP_Door", blueprint.Identifier.Text);
        Assert.AreEqual("Actor", blueprint.BaseClassIdentifier?.Text);
        Assert.AreEqual("/Game/Generated/BP_Door_Gen", blueprint.PackagePath.Value);
        Assert.AreEqual(KmsProfile.Blueprint, KmsProfileDetector.Detect(new CompilationUnit([blueprint])));
    }

    [TestMethod]
    public void ParsesRootComponentDecorator()
    {
        var blueprint = ParseFirstBlueprint("BpDoor_Components.kms");
        var root = blueprint.Declarations.OfType<ComponentDeclaration>().Single(x => x.Identifier.Text == "Root");

        Assert.AreEqual("SceneComponent", root.ClassIdentifier.Text);
        Assert.IsTrue(root.Decorators.Any(x => x.Identifier.Text == "root"));
    }

    [TestMethod]
    public void ParsesAttachComponentDecorator()
    {
        var blueprint = ParseFirstBlueprint("BpDoor_Components.kms");
        var mesh = blueprint.Declarations.OfType<ComponentDeclaration>().Single(x => x.Identifier.Text == "Mesh");
        var attach = mesh.Decorators.Single(x => x.Identifier.Text == "attach");

        Assert.AreEqual("StaticMeshComponent", mesh.ClassIdentifier.Text);
        Assert.IsInstanceOfType(attach.Arguments[0].Expression, typeof(Identifier));
        Assert.AreEqual("Root", ((Identifier)attach.Arguments[0].Expression).Text);
    }

    [TestMethod]
    public void ParsesComponentBody_DefaultAssignments()
    {
        var blueprint = ParseFirstBlueprint("BpDoor_Components.kms");
        var mesh = blueprint.Declarations.OfType<ComponentDeclaration>().Single(x => x.Identifier.Text == "Mesh");

        var staticMesh = mesh.ComponentProperties.Single(x => x.Name.Text == "StaticMesh");
        Assert.IsInstanceOfType(staticMesh.Value, typeof(CallOperator));
        var call = (CallOperator)staticMesh.Value;
        Assert.AreEqual("asset", call.Identifier.Text);
        Assert.AreEqual("StaticMesh", call.TypeArguments.Single().Text);

        var mobility = mesh.ComponentProperties.Single(x => x.Name.Text == "Mobility");
        Assert.AreEqual("byte", mobility.Type?.Text);
    }

    [TestMethod]
    public void ParsesBpVar_TypeAfterName()
    {
        var blueprint = ParseFirstBlueprint("BpDoor_Variables.kms");
        var openAngle = blueprint.Declarations.OfType<VariableDeclaration>().Single(x => x.Identifier.Text == "OpenAngle");

        Assert.IsTrue(openAngle.IsBlueprintStyle);
        Assert.AreEqual("float", openAngle.Type.Text);
        Assert.IsNotNull(openAngle.Initializer);
    }

    [TestMethod]
    public void ParsesEditableAndCategoryDecorators()
    {
        var blueprint = ParseFirstBlueprint("BpDoor_Variables.kms");
        var openAngle = blueprint.Declarations.OfType<VariableDeclaration>().Single(x => x.Identifier.Text == "OpenAngle");

        CollectionAssert.AreEquivalent(
            new[] { "editable", "category" },
            openAngle.Decorators.Select(x => x.Identifier.Text).ToArray());

        var category = openAngle.Decorators.Single(x => x.Identifier.Text == "category");
        Assert.IsInstanceOfType(category.Arguments[0].Expression, typeof(KismetScript.Syntax.Statements.Expressions.Literals.StringLiteral));
        Assert.AreEqual("Door", ((KismetScript.Syntax.Statements.Expressions.Literals.StringLiteral)category.Arguments[0].Expression).Value);
    }

    [TestMethod]
    public void ParsesEventDeclaration()
    {
        var blueprint = ParseFirstBlueprint("BpDoor_EventsFunctions.kms");
        var beginPlay = blueprint.Declarations.OfType<ProcedureDeclaration>().Single(x => x.Identifier.Text == "BeginPlay");

        Assert.IsTrue(beginPlay.IsBlueprintStyle);
        Assert.AreEqual(BlueprintProcedureKind.Event, beginPlay.BlueprintKind);
        Assert.AreEqual("void", beginPlay.ReturnType.Text);
        Assert.IsNotNull(beginPlay.Body);
    }

    [TestMethod]
    public void ParsesCallableAndPureDeclarations()
    {
        var blueprint = ParseFirstBlueprint("BpDoor_EventsFunctions.kms");
        var toggle = blueprint.Declarations.OfType<ProcedureDeclaration>().Single(x => x.Identifier.Text == "Toggle");
        var getIsOpen = blueprint.Declarations.OfType<ProcedureDeclaration>().Single(x => x.Identifier.Text == "GetIsOpen");

        Assert.AreEqual(BlueprintProcedureKind.Callable, toggle.BlueprintKind);
        Assert.AreEqual("void", toggle.ReturnType.Text);
        Assert.AreEqual(BlueprintProcedureKind.Pure, getIsOpen.BlueprintKind);
        Assert.AreEqual("bool", getIsOpen.ReturnType.Text);
    }

    [TestMethod]
    public void ParsesTypedAssetReference()
    {
        var blueprint = ParseFirstBlueprint("BpDoor_Components.kms");
        var mesh = blueprint.Declarations.OfType<ComponentDeclaration>().Single(x => x.Identifier.Text == "Mesh");
        var staticMesh = mesh.ComponentProperties.Single(x => x.Name.Text == "StaticMesh");
        Assert.IsInstanceOfType(staticMesh.Value, typeof(CallOperator));
        var call = (CallOperator)staticMesh.Value;

        Assert.AreEqual("asset", call.Identifier.Text);
        Assert.AreEqual("StaticMesh", call.TypeArguments.Single().Text);
        Assert.IsInstanceOfType(call.Arguments[0].Expression, typeof(KismetScript.Syntax.Statements.Expressions.Literals.StringLiteral));
        Assert.AreEqual("/Game/Props/SM_Door.SM_Door", ((KismetScript.Syntax.Statements.Expressions.Literals.StringLiteral)call.Arguments[0].Expression).Value);
    }

    [TestMethod]
    public void SampleFiles_AreBlueprintProfile()
    {
        var sampleDir = Path.Combine(AppContext.BaseDirectory, "Samples");
        var samples = Directory.GetFiles(sampleDir, "*.kms").Select(Path.GetFileName).ToArray();

        CollectionAssert.DoesNotContain(samples, "Ir_ExistingSyntax.kms");
        foreach (var sample in samples)
        {
            var unit = ParseSample(sample!);
            Assert.AreEqual(KmsProfile.Blueprint, KmsProfileDetector.Detect(unit), sample);
            Assert.IsTrue(unit.Declarations.OfType<BlueprintDeclaration>().Any(), sample);
        }
    }

    [TestMethod]
    public void ParsesBpAllowedStatementsAndExpressions()
    {
        var blueprint = ParseFirstBlueprint("BpDoor_SyntaxExpressions.kms");
        var exercise = blueprint.Declarations.OfType<ProcedureDeclaration>().Single(x => x.Identifier.Text == "Exercise");
        var shouldOpen = blueprint.Declarations.OfType<ProcedureDeclaration>().Single(x => x.Identifier.Text == "ShouldOpen");
        var body = (CompoundStatement)exercise.Body!;

        Assert.IsTrue(body.Statements.OfType<IfStatement>().Any());
        Assert.IsTrue(body.Statements.OfType<WhileStatement>().Any());

        var values = body.Statements.OfType<VariableDeclaration>().Single(x => x.Identifier.Text == "Values");
        var config = body.Statements.OfType<VariableDeclaration>().Single(x => x.Identifier.Text == "Config");
        var localFloat = body.Statements.OfType<VariableDeclaration>().Single(x => x.Identifier.Text == "LocalFloat");
        var typeName = body.Statements.OfType<VariableDeclaration>().Single(x => x.Identifier.Text == "TypeName");

        Assert.IsInstanceOfType(values.Initializer, typeof(InitializerList));
        Assert.IsInstanceOfType(config.Initializer, typeof(ObjectLiteral));
        Assert.IsInstanceOfType(localFloat.Initializer, typeof(CastOperator));
        Assert.IsInstanceOfType(typeName.Initializer, typeof(TypeofOperator));
        Assert.IsTrue(FindExpressions(body).OfType<SubscriptOperator>().Any());

        var returnStatement = ((CompoundStatement)shouldOpen.Body!).Statements.OfType<ReturnStatement>().Single();
        Assert.IsInstanceOfType(returnStatement.Value, typeof(ConditionalExpression));
    }

    [TestMethod]
    public void LegacyAttributeBlueprint_NormalizesToBpModel()
    {
        var modern = BlueprintProfileNormalizer.Normalize(ParseText(FullModernDoorSample())).Blueprints.Single();
        var legacy = BlueprintProfileNormalizer.Normalize(ParseText(LegacyAttributeDoorSample())).Blueprints.Single();

        Assert.AreEqual(modern.AssetPath, legacy.AssetPath);
        Assert.AreEqual(modern.ParentType, legacy.ParentType);
        Assert.IsTrue(legacy.Components.Single(x => x.Name == "Root").IsRoot);
        Assert.AreEqual("Root", legacy.Components.Single(x => x.Name == "Mesh").AttachTarget);
        Assert.IsTrue(legacy.Variables.Single(x => x.Name == "OpenAngle").IsEditable);
        Assert.AreEqual("Door", legacy.Variables.Single(x => x.Name == "OpenAngle").Category);
        Assert.AreEqual(BlueprintProcedureKind.Event, legacy.Procedures.Single().Kind);
        Assert.AreEqual("BeginPlay", legacy.Procedures.Single().EventName);
    }

    private static BlueprintDeclaration ParseFirstBlueprint(string sampleName)
    {
        return ParseSample(sampleName).Declarations.OfType<BlueprintDeclaration>().Single();
    }

    internal static CompilationUnit ParseSample(string sampleName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Samples", sampleName);
        return ParseText(File.ReadAllText(path));
    }

    internal static CompilationUnit ParseText(string text)
    {
        return new KismetScriptASTParser().Parse(text);
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

    internal static string FullModernDoorSample()
    {
        return """
            blueprint BP_Door : Actor at "/Game/Generated/BP_Door_Gen" {
                @root
                component Root: SceneComponent;

                @attach(Root)
                component Mesh: StaticMeshComponent {
                    StaticMesh = asset<StaticMesh>("/Game/Props/SM_Door.SM_Door");
                }

                @editable
                @category("Door")
                var OpenAngle: float = 90.0;

                event BeginPlay() {
                    print("Door ready");
                }
            }
            """;
    }

    internal static string LegacyAttributeDoorSample()
    {
        return """
            [Blueprint(Path = "/Game/Generated/BP_Door_Gen")]
            class BP_Door : Actor {
                [RootComponent]
                object Root : SceneComponent {
                }

                [Component, Attach = Root]
                object Mesh : StaticMeshComponent {
                    Object<StaticMesh> StaticMesh = asset<StaticMesh>("/Game/Props/SM_Door.SM_Door");
                }

                [Edit, Category("Door")]
                float OpenAngle = 90.0f;

                [Event("BeginPlay")]
                void ReceiveBeginPlay() {
                    print("Door ready");
                }
            }
            """;
    }
}
