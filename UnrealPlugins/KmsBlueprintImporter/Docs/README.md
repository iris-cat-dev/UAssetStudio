# KMS Blueprint Importer

Editor plugin for importing `kms-bp-export-v1` JSON produced by:

```bash
dotnet run --project UAssetStudio.Cli -- kms-bp export Samples/BpDoor_Minimal.kms --out BpDoor.kms-bp.json
```

## Install

Copy or symlink `UnrealPlugins/KmsBlueprintImporter` into a UE project:

```text
<Project>/Plugins/KmsBlueprintImporter
```

Regenerate project files if needed, build the editor target, then enable the plugin.

For standalone plugin packaging:

```bash
<UE>/Engine/Build/BatchFiles/RunUAT.sh BuildPlugin \
  -Plugin=/path/to/KmsBlueprintImporter.uplugin \
  -Package=/tmp/KmsBlueprintImporterBuild \
  -TargetPlatforms=Mac
```

The UE source checkout must have completed its normal setup step so bundled DotNet and UnrealBuildTool dependencies are available.

## Commandlet

```bash
UnrealEditor-Cmd <Project>.uproject -run=KmsBpImport -Json=/absolute/path/BpDoor.kms-bp.json
```

Useful switches:

- `-NoCompile`: import assets without compiling the generated Blueprint.
- `-NoSave`: import into the editor session without saving packages.
- `-KeepComponents`: keep existing SCS components instead of replacing them.
- `-NoGraphs`: skip event/function graph skeleton comments.

## Editor API

The plugin exposes:

```cpp
UKmsBpImporterLibrary::ImportKmsBlueprintJson(JsonPath, Options, Result)
```

The function can also be called from editor scripting Blueprints.

## MVP Scope

Implemented:

- Reads stable CLI JSON schema `kms-bp-export-v1`.
- Creates or updates `UBlueprint` assets at `assetPath`.
- Resolves basic parent/component classes from `/Script/Engine.*`, direct class paths, and built-in Actor/SCS component names.
- Creates SCS component nodes and attachment hierarchy.
- Applies component template properties for literal bool/number/string/name/text values and `asset<T>("...")` object references.
- Creates/updates member variables with type/default/category/editable metadata.
- Creates event/function graph skeletons and preserves KMS body text in generated comment nodes.
- Compiles and saves generated Blueprint packages by default.

Staged for the next pass:

- Translate KMS-BP statement/expression JSON into real K2 nodes instead of comment skeletons.
- Generate function parameters and return pins from `parameters` / `returnType`.
- Add an Unreal automation test fixture that imports the Door demo and inspects the resulting Blueprint.
