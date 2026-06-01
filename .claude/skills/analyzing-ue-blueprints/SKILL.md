---
name: analyzing-ue-blueprints
description: Analyze Unreal Engine Blueprint assets (.uasset) using UAssetStudio. Prefer the Windows x64 single-file CLI (UAssetStudio.Cli.exe) or dotnet run from source. Use when decompiling blueprints to .kms scripts, generating control flow graphs (CFG/DOT), validating asset integrity, converting assets to JSON, extracting metadata, or understanding Blueprint bytecode structure. Also use when the user asks about Kismet bytecode, Blueprint functions, or UE asset internals.
---

# Analyzing UE Blueprints

## Prerequisites

- **CLI:** Prefer the published **Windows x64 single-file** binary (self-contained, no .NET install on the target machine). Default artifact path (from repo root): `UAssetStudio.Cli.exe`. Example absolute path: `UAssetStudio.Cli.exe`. Run the `.exe` on **Windows x64** (or an environment that executes WinPE). On **macOS/Linux** for local runs, use `dotnet run --project UAssetStudio.Cli -- …` from the repository instead.
- For UE5+ assets: `.usmap` mappings file
- For UE4 assets: mappings optional

## Analysis Commands

Set `UAS_CLI` to your single-file exe (or rely on the default below). From repository root:

```bash
export UAS_CLI="${UAS_CLI:-UAssetStudio.Cli.exe}"
```

All commands use this base pattern (same flags as `dotnet run`; only the invoker changes):

```bash
"$UAS_CLI" <command> <asset> \
  [--mappings <usmap>] \
  --ue-version <VER_UE4_27|VER_UE5_6> \
  --outdir <dir>
```

**Development from source (any OS with .NET SDK):**

```bash
dotnet run --project UAssetStudio.Cli -- <command> <asset> \
  [--mappings <usmap>] \
  --ue-version <VER_UE4_27|VER_UE5_6> \
  --outdir <dir>
```

### 1. Decompile — Blueprint to Readable Script

Converts `.uasset` to `.kms` (Kismet Script), showing classes, functions, properties, and bytecode logic.

```bash
"$UAS_CLI" decompile <asset.uasset> \
  --ue-version VER_UE4_27 --outdir ./output

# With metadata extraction (captures full asset context):
"$UAS_CLI" decompile <asset.uasset> \
  --ue-version VER_UE5_6 --mappings <usmap> --outdir ./output --meta
```

**Output:** `<name>.kms` and optionally `<name>.kms.meta` (JSON metadata).

### 2. CFG — Control Flow Graph

Generates Graphviz DOT and text summary of bytecode control flow per function.

```bash
"$UAS_CLI" cfg <asset.uasset> \
  --ue-version VER_UE5_6 --mappings <usmap> --outdir ./output
```

**Output:** `<name>.dot` (Graphviz graph) + `<name>.txt` (text summary).

Render DOT to image:

```bash
dot -Tpng output/MyBlueprint.dot -o output/MyBlueprint.png
dot -Tsvg output/MyBlueprint.dot -o output/MyBlueprint.svg
```

### 3. Validate — Asset Integrity Check

Checks FPackageIndex consistency, class references, and optionally verifies imports exist on disk.

```bash
"$UAS_CLI" validate <asset.uasset> \
  --ue-version VER_UE4_27

# With import existence check (provide game Content directory):
"$UAS_CLI" validate <asset.uasset> \
  --ue-version VER_UE5_6 --mappings <usmap> \
  --game-content /path/to/Game/Content
```

**Error categories:** `InvalidIndex`, `MissingImport`, `NullClass`.

### 4. JSON — Structural Dump

Converts asset to JSON for programmatic inspection of all exports, imports, and properties.

```bash
"$UAS_CLI" json <asset.uasset> \
  --ue-version VER_UE4_27 --out ./output/asset.json
```

For editing UE5 Blueprint assets through JSON, import the modified JSON with the original asset path so the CLI can borrow schemas collected while reading the binary asset:

```bash
"$UAS_CLI" json modified.asset.json \
  --mappings DRG_RC_Mappings.usmap \
  --ue-version VER_UE5_6 \
  --asset /path/to/original.uasset \
  --out /path/to/output.uasset
```

### Choosing an Asset Modification Workflow

- For simple bytecode or Blueprint logic edits, use `decompile` to `.kms`, edit the script, then `compile` back to an asset.
- For value, array, spawn-weight, or object-reference edits in complex UE5 Blueprint/DataAsset files, prefer JSON export, structured JSON edits, then JSON import with `--asset <original.uasset>`.
- Avoid using `.kms` compilation for large inherited Blueprints or large Ubergraphs when decompilation reports errors such as `Error decompiling function ... Sequence contains no matching element`; the `.kms` is not a reliable round-trip source in that case.
- The `--asset` argument matters for inherited UE5 Blueprints because JSON deserialization does not replay the binary read path that collects Blueprint-generated and parent-class schemas. Loading the original binary asset first lets JSON import reuse those collected schemas.

### 5. Verify — Round-trip Integrity

Tests decompile → compile → link → write and compares binary equality.

```bash
"$UAS_CLI" verify <asset.uasset> \
  --ue-version VER_UE4_27 --outdir ./output [--meta]
```

## Reading .kms Output

### Structure

```kms
// 1. Package imports
from "/Script/Engine" import {
    class Actor;
    class ActorComponent;
    function BeginPlay;
}

// 2. Class declaration with properties
class BP_MyActor_C : BlueprintGeneratedClass {
    float Health = 100.0f;
    Object<ActorComponent> MyComponent;

    // 3. Functions with parameters and bytecode
    void ReceiveTick(float DeltaSeconds) {
        float LocalVar;

        // Bytecode labels (jump targets)
        L_0:
        LocalVar = DeltaSeconds;
        LocalVirtualFunction("UpdateHealth", LocalVar);
    }

    // 4. Ubergraph (event graph, label-based entry points)
    void ExecuteUbergraph_BP_MyActor(int EntryPoint) {
        ReceiveBeginPlay_0:
        LocalVirtualFunction("Initialize");

        ReceiveTick_128:
        Context(this, InstanceVariable("Health"));
    }
}

// 5. Object exports (default subobjects, components)
object DefaultSceneRoot : SceneComponent {
    Struct<Vector> RelativeLocation = { X: 0.0, Y: 0.0, Z: 0.0 };
}
```

### Key Expression Patterns

| Expression | Meaning |
|------------|---------|
| `Context(obj, expr)` | Member access: `obj.expr` |
| `InstanceVariable("Name")` | Read instance property |
| `LocalVirtualFunction("Fn", args)` | Virtual function call |
| `LocalFinalFunction("Fn", args)` | Direct (non-virtual) function call |
| `MetaCast("Class", target)` | Cast to type |
| `InterfaceCast("IFace", target)` | Interface cast |
| `PushExecutionFlow(L_xxx)` | Push continuation address |
| `LetObj(var, value)` | Object assignment |
| `StructConst("Type", fields)` | Struct literal |
| `ArrayConst("ElemType", items)` | Array literal |
| `MapConst("K", "V", entries)` | Map literal |

### Control Flow in Ubergraph

The Ubergraph merges all event graph nodes. Entry points are labeled:

- `ReceiveBeginPlay_<offset>:` — BeginPlay event
- `ReceiveTick_<offset>:` — Tick event
- `ReceiveEndPlay_<offset>:` — EndPlay event
- Custom event labels follow same pattern

`PushExecutionFlow` + conditional jumps implement if/else and loops.

## Reading CFG Output

### Text Summary (.txt)

Lists each export with bytecode instructions and addresses:

```
=== FunctionExport: ExecuteUbergraph_BP_MyActor ===
Properties: EntryPoint (IntProperty)

0x0000: EX_PushExecutionFlow -> 0x0156
0x0005: EX_JumpIfNot -> 0x0089
0x000A: EX_Let ...
```

### DOT Graph (.dot)

Nodes represent instructions, edges represent control flow:

- **Normal edge** — sequential execution
- **Jump** — unconditional goto (red)
- **JumpTrue/JumpFalse** — conditional branch (labeled IF/IF NOT)
- **Push** — push continuation (labeled PUSH STACK)
- **Function** — function call edge

## Reading Metadata (.kms.meta)

The `.kms.meta` JSON contains:

| Field | Content |
|-------|---------|
| `Package` | Package name, GUID, flags |
| `EngineVersion` | ObjectVersion, UE5 version, custom versions |
| `InfrastructureExports` | Class, CDO, function export metadata (flags, properties) |
| `ObjectDefaults` | Per-class default values for object exports |
| `FieldPaths` | Property pointer paths per function (bytecode references) |
| `CdoData` | Class Default Object sub-objects and preserved properties |
| `StructSchemas` | Struct field type hints (e.g., `int`, `float`, `Struct<PoseLink>`) |

## Analysis Workflows

### Workflow 1: Full Blueprint Analysis

Complete analysis pipeline — decompile, generate CFG, validate, and inspect:

```bash
export UAS_CLI="${UAS_CLI:-UAssetStudio.Cli.exe}"
ASSET="path/to/BP_MyActor.uasset"
VER="VER_UE4_27"
OUT="./analysis"
mkdir -p "$OUT"

# Decompile to readable script with metadata
"$UAS_CLI" decompile "$ASSET" --ue-version $VER --outdir "$OUT" --meta

# Generate control flow graph
"$UAS_CLI" cfg "$ASSET" --ue-version $VER --outdir "$OUT"

# Validate structure
"$UAS_CLI" validate "$ASSET" --ue-version $VER

# Dump to JSON for detailed inspection
"$UAS_CLI" json "$ASSET" --ue-version $VER --outdir "$OUT"
```

Then read the generated files:

1. `*.kms` — human-readable logic
2. `*.dot` / `*.txt` — control flow visualization
3. `*.kms.meta` — structural metadata
4. `*.json` — full property dump

### Workflow 2: Compare Two Assets

Decompile both and diff the .kms output:

```bash
"$UAS_CLI" decompile asset_v1.uasset --ue-version $VER --outdir ./v1
"$UAS_CLI" decompile asset_v2.uasset --ue-version $VER --outdir ./v2
diff ./v1/*.kms ./v2/*.kms
```

### Workflow 3: UE5 Asset with Mappings

UE5+ assets require `.usmap` for unversioned property parsing:

```bash
"$UAS_CLI" decompile BP_Player.uasset \
  --mappings DRG_RC_Mappings.usmap \
  --ue-version VER_UE5_6 \
  --outdir ./output --meta
```

### Workflow 4: Batch Analysis

Analyze all blueprints in a directory:

```bash
for f in /path/to/assets/BP_*.uasset; do
  name=$(basename "${f%.uasset}")
  "$UAS_CLI" decompile "$f" \
    --ue-version VER_UE4_27 --outdir "./output/$name"
done
```

## Version Reference

| Flag | Engine Version |
|------|----------------|
| `VER_UE4_27` | UE 4.27 (no mappings needed) |
| `VER_UE5_6` | UE 5.6 (requires `.usmap`) |

Full range supported: UE 4.13 — 5.6+. Version constants follow pattern `VER_UE4_XX` / `VER_UE5_X`.

## Key Source Files

| Purpose | Path |
|---------|------|
| CLI entry | `UAssetStudio.Cli/Program.cs` |
| All commands | `UAssetStudio.Cli/CMD/*.cs` |
| Decompiler | `KismetScript.Decompiler/KismetDecompiler.cs` |
| Expressions | `KismetScript.Decompiler/KismetDecompiler.Expressions.cs` |
| Metadata extraction | `KismetScript.Decompiler/MetadataExtractor.cs` |
| Package analysis | `KismetScript.Decompiler/Analysis/PackageAnalyser.cs` |
| CFG generation | `KismetAnalyzer.CFG/SummaryGenerator.cs` |
| DOT output | `KismetAnalyzer.CFG/Dot.cs` |
| Asset validation | `UAssetAPI/UAssetAPI/Validation/UAssetValidator.cs` |
| KMS grammar | `KismetScript.Parser/KismetScript.g4` |
| Metadata model | `KismetScript.Utilities/Metadata/KmsMetadata.cs` |
