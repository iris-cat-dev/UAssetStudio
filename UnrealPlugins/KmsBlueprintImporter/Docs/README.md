# KMS Blueprint Importer

Editor plugin for importing `kms-bp-export-v1` JSON produced by:

```bash
dotnet run --project UAssetStudio.Cli -- kms-bp export Samples/BpDoor_Minimal.kms --out BpDoor.kms-bp.json
dotnet run --project UAssetStudio.Cli -- kms-bp export KismetScript.Parser.Tests/Samples/V1/BpDoorV1_Full.kms --language-version 1 --out BpDoorV1.kms-bp.json
```

## Install

Copy or symlink `UnrealPlugins/KmsBlueprintImporter` into a UE project:

```text
<Project>/Plugins/KmsBlueprintImporter
```

Regenerate project files if needed, build the editor target, then enable the plugin.

For standalone plugin packaging:

```bash
<UE>/Engine/Build/BatchFiles/RunUAT.bat BuildPlugin \
  -Plugin=/path/to/KmsBlueprintImporter.uplugin \
  -Package=/tmp/KmsBlueprintImporterBuild \
  -TargetPlatforms=Win64 \
  -StrictIncludes
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
- Accepts KMS-BP language versions `0` and `1` through the JSON `languageVersion` field.
- Creates or updates `UBlueprint` assets at `assetPath`.
- Resolves basic parent/component classes from `/Script/Engine.*`, direct class paths, and built-in Actor/SCS component names.
- Creates SCS component nodes and attachment hierarchy.
- Applies component template properties for literal bool/number/string/name/text values and `asset<T>("...")` object references.
- Creates/updates member variables with type/default/category/editable metadata.
- Creates event/function graphs from KMS-BP bodies and preserves declaration summaries in generated comment nodes.
- Imports V1 `construction` declarations into the Blueprint User Construction Script graph.
- Imports V1 `dispatcher` declarations as `PC_MCDelegate` member variables plus delegate signature graphs.
- Generates real function entry/result pins for callable/pure procedure parameters, `out` parameters, and non-void return values.
- Generates real K2 nodes for core V1 statements and expressions used by the fixtures: variable set/get, assignments, print calls, math/library calls, array construction/get/length, if/while/for/foreach/switch-as-branches, dispatcher bind/unbind/broadcast, return nodes, named arguments, local variables, metadata, and replication/RPC flags.
- Provides `-ValidateExec` and `-ValidateNoGeneratedKmsNodes` commandlet checks for CI-style fixture validation.
- Compiles and saves generated Blueprint packages by default.

## UE 5.6 Validation

Package the plugin, then run the fixture import through a minimal host project:

```bash
<UE>/Engine/Build/BatchFiles/RunUAT.bat BuildPlugin \
  -Plugin=/path/to/UnrealPlugins/KmsBlueprintImporter/KmsBlueprintImporter.uplugin \
  -Package=/path/to/UnrealPlugins/KmsBlueprintImporter_Build_UE5_6 \
  -TargetPlatforms=Win64 \
  -StrictIncludes

<UE>/Engine/Binaries/Win64/UnrealEditor-Cmd.exe \
  /path/to/UnrealPlugins/KmsBlueprintImporter_TestHost/KmsBlueprintImporter_TestHost.uproject \
  -PLUGIN=/path/to/UnrealPlugins/KmsBlueprintImporter_Build_UE5_6/KmsBlueprintImporter.uplugin \
  -EnablePlugins=KmsBlueprintImporter \
  -run=KmsBpImport \
  -Json=/path/to/UnrealPlugins/KmsBlueprintImporter/Tests/BpDoor_FunctionSignature.kms-bp.json \
  -NoShaderCompile -Unattended -NoSplash -NullRHI -NoSound -stdout -FullStdOutLogOutput \
  -ddc=NoZenLocalFallback \
  -LocalDataCachePath=/path/to/UnrealPlugins/KmsBlueprintImporter_TestHost/DerivedDataCache
```

For a KMS-BP v1 fixture, use:

```bash
-Json=/path/to/UnrealPlugins/KmsBlueprintImporter/Tests/BpDoorV1_Full.kms-bp.json
```

Strict validation can be enabled with:

```bash
-ValidateExec -ValidateNoGeneratedKmsNodes
```

Current V1 fixtures verified with those switches:

- `BpDoorV1_Calls.kms-bp.json`
- `BpDoorV1_Construction.kms-bp.json`
- `BpDoorV1_ControlFlow.kms-bp.json`
- `BpDoorV1_Dispatchers.kms-bp.json`
- `BpDoorV1_Full.kms-bp.json`
- `BpDoorV1_Metadata.kms-bp.json`
- `BpDoorV1_ReplicationRpc.kms-bp.json`
- `BpDoorV1_Types.kms-bp.json`

Known limits:

- Calls to project-specific functions outside the imported closure are represented with real `PrintString` placeholder nodes so imports compile without `UK2Node_KmsGeneratedNode`.
- Missing fixture assets such as `/Game/Props/SM_Door.SM_Door` are reported as warnings unless present in the host project.
- `switch` is currently emitted as an equivalent branch chain for fixture coverage rather than a dedicated `UK2Node_SwitchEnum`.
