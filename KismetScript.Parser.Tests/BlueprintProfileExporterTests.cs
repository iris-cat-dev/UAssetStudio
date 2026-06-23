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
}
