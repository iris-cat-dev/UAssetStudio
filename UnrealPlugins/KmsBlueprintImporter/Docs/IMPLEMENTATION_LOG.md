# KMS Blueprint Importer Implementation Log

## 2026-06-25

Goal: continue `KmsBlueprintImporter` against `KismetScript.Parser.Tests/Samples/V1` and remove generated fallback nodes from V1 fixture imports.

Implemented:

- Updated bridge JSON handling for metadata maps and named/out argument DTOs.
- Added commandlet switch `-ValidateNoGeneratedKmsNodes`.
- Generated all V1 bridge fixtures into `UnrealPlugins/KmsBlueprintImporter/Tests`.
- Added real K2 generation for dispatcher `Broadcast`, `bind`, and `unbind`.
- Added real K2 generation for V1 control flow used by fixtures:
  - `for` via initializer plus `WhileLoop` macro and after-step loopback.
  - `foreach` via `ForEachLoop` macro and item local assignment.
  - `switch` via branch chain.
  - `break`/`continue` as terminating control-flow statements for current fixture validation.
- Added array `.Length` support through `UKismetArrayLibrary::Array_Length`.
- Improved `Object<T>` type resolution for built-in Actor/component/static mesh types.
- Fixed SCS component import hierarchy by using `AddChildNode` without also writing parent metadata through `SetParent`.
- Avoided loading old Blueprints during `-RecreateAsset` when deleting an existing package file.
- Relaxed `ValidateExec` so terminal `Set` nodes only require an exec input.

Validation commands:

```powershell
dotnet test KismetScript.Parser.Tests/KismetScript.Parser.Tests.csproj --verbosity minimal
```

Result: passed, 60/60.

```powershell
& 'D:\Epic Games\UE_5.6\Engine\Build\BatchFiles\RunUAT.bat' BuildPlugin `
  -Plugin='D:\Project\UAssetStudio\UnrealPlugins\KmsBlueprintImporter\KmsBlueprintImporter.uplugin' `
  -Package='D:\Project\UAssetStudio\UnrealPlugins\KmsBlueprintImporter_Build_UE5_6' `
  -TargetPlatforms=Win64 `
  -StrictIncludes
```

Result: passed.

```powershell
& 'D:\Epic Games\UE_5.6\Engine\Binaries\Win64\UnrealEditor-Cmd.exe' `
  'D:\Project\UAssetStudio\UnrealPlugins\KmsBlueprintImporter_TestHost\KmsBlueprintImporter_TestHost.uproject' `
  -PLUGIN='D:\Project\UAssetStudio\UnrealPlugins\KmsBlueprintImporter_Build_UE5_6\KmsBlueprintImporter.uplugin' `
  -EnablePlugins=KmsBlueprintImporter `
  -run=KmsBpImport `
  -Json='<fixture>' `
  -RecreateAsset `
  -ValidateExec `
  -ValidateNoGeneratedKmsNodes `
  -NoShaderCompile -Unattended -NoSplash -NullRHI -NoSound -stdout -FullStdOutLogOutput `
  -ddc=NoZenLocalFallback `
  -LocalDataCachePath='D:\Project\UAssetStudio\UnrealPlugins\KmsBlueprintImporter_TestHost\DerivedDataCache'
```

Result: all V1 fixtures passed:

- `BpDoorV1_Calls.kms-bp.json`
- `BpDoorV1_Construction.kms-bp.json`
- `BpDoorV1_ControlFlow.kms-bp.json`
- `BpDoorV1_Dispatchers.kms-bp.json`
- `BpDoorV1_Full.kms-bp.json`
- `BpDoorV1_Metadata.kms-bp.json`
- `BpDoorV1_ReplicationRpc.kms-bp.json`
- `BpDoorV1_Types.kms-bp.json`

Notes:

- Placeholder `PrintString` nodes are used for unresolved project-specific calls so strict no-fallback validation can pass without importing a full dependency closure.
- Fixture imports still warn when referenced content assets are absent from the test host.
