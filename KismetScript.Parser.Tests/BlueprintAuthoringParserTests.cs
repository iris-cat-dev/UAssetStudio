using KismetScript.Parser;
using KismetScript.Syntax;
using KismetScript.Syntax.Blueprint;
using KismetScript.Syntax.Statements.Declarations;
using KismetScript.Syntax.Statements.Expressions;
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
    public void KeepsLegacyKmsIrSyntaxParsing()
    {
        var unit = ParseSample("Ir_ExistingSyntax.kms");

        Assert.AreEqual(KmsProfile.Ir, KmsProfileDetector.Detect(unit));
        Assert.IsTrue(unit.Declarations.OfType<ClassDeclaration>().Any(x => x.Identifier.Text == "BP_Door_C"));
        Assert.IsTrue(unit.Declarations.OfType<ObjectDeclaration>().Any(x => x.Identifier.Text == "Default__BP_Door_C"));
    }

    [TestMethod]
    public void LegacyAttributeBlueprint_NormalizesToBpModel()
    {
        var modern = BlueprintProfileNormalizer.Normalize(ParseText(FullModernDoorSample())).Blueprints.Single();
        var legacy = BlueprintProfileNormalizer.Normalize(ParseSample("BpDoor_LegacyAttributes.kms")).Blueprints.Single();

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
}
