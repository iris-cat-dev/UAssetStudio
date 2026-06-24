using KismetScript.Syntax.Blueprint;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace KismetScript.Parser.Tests;

[TestClass]
public class BlueprintProfileSemanticCheckerTests
{
    [TestMethod]
    public void RootDecorator_OnVariable_ReportsError()
    {
        var diagnostics = Check("""
            blueprint BP_Door : Actor at "/Game/Generated/BP_Door_Gen" {
                @root
                var OpenAngle: float = 90.0;
            }
            """);

        AssertHasDiagnostic(diagnostics, "InvalidDecoratorTarget");
    }

    [TestMethod]
    public void AttachDecorator_TargetMissing_ReportsError()
    {
        var diagnostics = Check("""
            blueprint BP_Door : Actor at "/Game/Generated/BP_Door_Gen" {
                @attach(MissingRoot)
                component Mesh: StaticMeshComponent;
            }
            """);

        AssertHasDiagnostic(diagnostics, "MissingAttachTarget");
    }

    [TestMethod]
    public void EditableDecorator_OnComponent_ReportsError()
    {
        var diagnostics = Check("""
            blueprint BP_Door : Actor at "/Game/Generated/BP_Door_Gen" {
                @editable
                component Mesh: StaticMeshComponent;
            }
            """);

        AssertHasDiagnostic(diagnostics, "InvalidDecoratorTarget");
    }

    [TestMethod]
    public void UnsupportedDecorator_ReportsError()
    {
        var diagnostics = Check("""
            blueprint BP_Door : Actor at "/Game/Generated/BP_Door_Gen" {
                @replicated
                var OpenAngle: float = 90.0;
            }
            """);

        AssertHasDiagnostic(diagnostics, "UnsupportedDecorator");
    }

    [TestMethod]
    public void BpProfile_Goto_ReportsError()
    {
        var diagnostics = Check("""
            blueprint BP_Door : Actor at "/Game/Generated/BP_Door_Gen" {
                event BeginPlay() {
                    goto Done;
                }
            }
            """);

        AssertHasDiagnostic(diagnostics, "BannedStatement");
    }

    [TestMethod]
    public void BpProfile_Switch_ReportsError()
    {
        var diagnostics = Check("""
            blueprint BP_Door : Actor at "/Game/Generated/BP_Door_Gen" {
                event BeginPlay() {
                    switch (IsOpen) {
                        default:
                            break;
                    }
                }
            }
            """);

        AssertHasDiagnostic(diagnostics, "BannedStatement");
    }

    [TestMethod]
    public void BpProfile_For_ReportsError()
    {
        var diagnostics = Check("""
            blueprint BP_Door : Actor at "/Game/Generated/BP_Door_Gen" {
                event BeginPlay() {
                    for (var Index: int = 0; Index < 3; Index++) {
                        print("tick");
                    }
                }
            }
            """);

        AssertHasDiagnostic(diagnostics, "BannedStatement");
    }

    [TestMethod]
    public void BpProfile_NewExpression_ReportsError()
    {
        var diagnostics = Check("""
            blueprint BP_Door : Actor at "/Game/Generated/BP_Door_Gen" {
                event BeginPlay() {
                    var Values: Array<int> = new Array<int> { 1, 2, 3 };
                }
            }
            """);

        AssertHasDiagnostic(diagnostics, "BannedExpression");
    }

    [TestMethod]
    public void BpProfile_IrIntrinsic_ReportsError()
    {
        var diagnostics = Check("""
            blueprint BP_Door : Actor at "/Game/Generated/BP_Door_Gen" {
                event BeginPlay() {
                    Context(this);
                }
            }
            """);

        AssertHasDiagnostic(diagnostics, "BannedIntrinsic");
    }

    [TestMethod]
    public void BpProfile_SyntaxExpressionSample_HasNoDiagnostics()
    {
        var unit = BlueprintAuthoringParserTests.ParseSample("BpDoor_SyntaxExpressions.kms");
        var diagnostics = BlueprintProfileSemanticChecker.Check(unit);

        Assert.AreEqual(0, diagnostics.Count, string.Join(Environment.NewLine, diagnostics.Select(x => x.Message)));
    }

    private static IReadOnlyList<BlueprintProfileDiagnostic> Check(string text)
    {
        var unit = BlueprintAuthoringParserTests.ParseText(text);
        return BlueprintProfileSemanticChecker.Check(unit);
    }

    private static void AssertHasDiagnostic(IReadOnlyList<BlueprintProfileDiagnostic> diagnostics, string code)
    {
        Assert.IsTrue(
            diagnostics.Any(x => x.Code == code),
            $"Expected diagnostic '{code}', got: {string.Join(", ", diagnostics.Select(x => x.Code))}");
    }
}
