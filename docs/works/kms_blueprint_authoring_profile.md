# KMS Blueprint Authoring Profile

## Summary

KMS 现在按双 profile 演进：

- `KMS-IR`：现有 bytecode IR / round-trip / patch / standalone metadata 编译路径，继续保持二进制一致性优先。
- `KMS-BP`：面向手写 Blueprint authoring 的文本语法，目标是后续生成普通 `UBlueprint`、K2 Graph 和 SCS component tree。

KMS-BP v0 采用 decorator-lite 风格：`blueprint`、`component`、`event`、`var` 是一等语法，`@...` 只承载轻量 Blueprint metadata。旧 `[Attribute]` 写法仍作为 legacy compatibility 入口保留，并通过 normalizer 映射到同一套 BP semantic model。

## KMS-BP v0 Syntax

### Complete Example

```kms
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

    callable void Toggle() {
        IsOpen = !IsOpen;
    }
}
```

### Top-Level Declarations

- `from "PackagePath" import { ... }`
- `from "PackagePath" import declaration`
- `blueprint Name : Parent at "AssetPath" { blueprintMember* }`
- Legacy KMS-IR declarations remain valid: `class`, `struct`, `enum`, `object`, type-first variables/functions.

### Blueprint Members

- `decorator* component Name: Type;`
- `decorator* component Name: Type { componentProperty* }`
- `decorator* var Name: Type;`
- `decorator* var Name: Type = expression;`
- `decorator* event Name(parameterList?) compoundStatement`
- `decorator* callable ReturnType Name(parameterList?) compoundStatement`
- `decorator* pure ReturnType Name(parameterList?) compoundStatement`
- Existing type-first `Type Name = expression;` remains accepted for compatibility but is not the recommended BP authoring style.

### Decorators

- `@root` only on `component`.
- `@attach(TargetComponent)` only on `component`.
- `@editable` only on `var`.
- `@category("Name")` only on `var`, `callable`, or `pure`.
- `@tooltip("Text")` is accepted v0 metadata for `component`, `var`, and BP procedures.
- Unknown decorators parse successfully, then the BP semantic checker reports `UnsupportedDecorator`.

### Component Properties

- `PropertyName = expression;`
- `PropertyName: Type = expression;`
- Existing object-style `Type PropertyName = expression;` remains for legacy `object` declarations, not recommended for BP `component` declarations.

### Parameters And Types

- KMS-BP preferred parameter style: `Name: Type`, `out Name: Type`, `ref Name: Type`.
- Legacy parameter style remains valid: `Type Name`.
- Constructed types: `Object<Type>`, `Array<Type>`.
- Builtins: `bool`, `byte`, `int`, `float`, `string`, `void`.

### Statements

Allowed in KMS-BP v0:

- compound block `{ statement* }`
- BP local variable: `var Name: Type = expression;`
- expression statement
- `if (...) statement else statement`
- `while (...) statement`
- `break;`
- `continue;`
- `return;` / `return expression;`

Rejected by KMS-BP semantic checker:

- `goto`
- `switch`
- `for`
- labels
- `new`
- low-level KMS-IR intrinsics such as `Context(...)`, `LetObj(...)`, `FinalFunction(...)`, `VirtualFunction(...)`

### Expressions

- literals: `true`, `false`, ints, floats, strings
- identifiers and member access: `obj.Property`, `obj.Method(...)`
- calls: `print("Door ready")`, `KismetSystemLibrary.PrintString(...)`
- typed asset reference: `asset<StaticMesh>("/Game/Props/SM_Door.SM_Door")`
- assignment and compound assignment
- arithmetic, comparison, equality, logical operators
- unary `!`, `-`, `++`, `--`
- subscript, cast, typeof, conditional expressions
- initializer lists and object literals

## Legacy Compatibility

The following legacy attribute-heavy style remains accepted and normalizes to the same BP model:

```kms
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
```

Formatter/docs should prefer decorator-lite syntax. Decompiled KMS-IR should not be automatically rewritten into KMS-BP syntax.

## Implementation Progress

Status as of 2026-06-23:

- Done: `KismetScript.g4` parses `blueprint`, `component`, BP-style `var`, `event`, `callable`, `pure`, decorators, BP-style parameters, and typed calls such as `asset<StaticMesh>(...)`.
- Done: AST nodes added for `BlueprintDeclaration`, `ComponentDeclaration`, `ComponentPropertyAssignment`, and `DecoratorDeclaration`.
- Done: `VariableDeclaration` and `ProcedureDeclaration` carry BP-style metadata.
- Done: `BlueprintProfileNormalizer` maps decorator-lite and legacy attribute style into one semantic model.
- Done: `BlueprintProfileSemanticChecker` reports invalid decorators, missing attach targets, banned BP statements, `new`, and low-level IR intrinsics.
- Done: `CompilationUnitWriter` emits decorator-lite syntax for BP declarations and leaves KMS-IR declarations in existing style.
- Done: `KismetScript.Parser.Tests` added to the solution with parser, normalizer, and semantic checker coverage.
- Done: CLI bridge commands added: `kms-bp validate` and `kms-bp export`.
- Done: `BlueprintProfileExporter` emits stable bridge JSON with `schemaVersion = "kms-bp-export-v1"`.
- Done: `BlueprintProfileExporter` emits stable DTOs for allowed BP statements plus literal, identifier, call, member, binary, unary, conditional, array, initializer, object, subscript, cast, typeof, and new-expression syntax.
- Done: UE plugin graph skeleton comments render exported KMS-BP statements and expressions instead of falling back to raw JSON kind names.
- Done: UE plugin imports callable/pure procedure parameters, `out` parameters, and non-void returns as real function entry/result pins.
- Done: UE 5.6 packaging and commandlet fixture validation pass with `UnrealPlugins/KmsBlueprintImporter/Tests/BpDoor_FunctionSignature.kms-bp.json`.
- Done: UE 5.6 packaging and commandlet validation pass with the syntax/expression fixture `UnrealPlugins/KmsBlueprintImporter/Tests/BpDoor_SyntaxExpressions.kms-bp.json`, producing `/Game/Generated/BP_Door_Syntax_Gen`.

Current validation:

```bash
dotnet test KismetScript.Parser.Tests/KismetScript.Parser.Tests.csproj
```

Result at time of writing: 24 tests passing.

## CLI Bridge JSON Export

The UE Editor plugin should consume bridge JSON, not C# parser internals.

Validate a KMS-BP source:

```bash
dotnet run --project UAssetStudio.Cli -- kms-bp validate path/to/BP_Door.kms
dotnet run --project UAssetStudio.Cli -- --json kms-bp validate path/to/BP_Door.kms
```

Export bridge JSON:

```bash
dotnet run --project UAssetStudio.Cli -- kms-bp export path/to/BP_Door.kms
dotnet run --project UAssetStudio.Cli -- kms-bp export path/to/BP_Door.kms --out path/to/BP_Door.kms-bp.json
```

Default export path is `<script-name>.kms-bp.json` next to the input file.

Export behavior:

- Parses the `.kms` file.
- Requires KMS-BP profile detection (`blueprint ...` or legacy `[Blueprint] class`).
- Runs `BlueprintProfileSemanticChecker`.
- Blocks export when diagnostics are present.
- Writes a stable DTO JSON, not a serialized AST.
- Includes `sourcePath`, `sourceSha256`, and source spans for generated nodes.

Bridge JSON shape:

```json
{
  "schemaVersion": "kms-bp-export-v1",
  "sourcePath": "/abs/path/BP_Door.kms",
  "sourceSha256": "...",
  "blueprints": [
    {
      "name": "BP_Door",
      "parentType": "Actor",
      "assetPath": "/Game/Generated/BP_Door_Gen",
      "components": [
        {
          "name": "Root",
          "type": "SceneComponent",
          "isRoot": true,
          "properties": []
        }
      ],
      "variables": [
        {
          "name": "OpenAngle",
          "type": "float",
          "isEditable": true,
          "category": "Door",
          "initializer": {
            "kind": "literal",
            "type": "float",
            "value": 90
          }
        }
      ],
      "procedures": [
        {
          "name": "BeginPlay",
          "kind": "event",
          "returnType": "void",
          "eventName": "BeginPlay",
          "parameters": [],
          "body": {
            "kind": "block",
            "statements": []
          }
        }
      ]
    }
  ]
}
```

## TDD Coverage

Parser tests cover:

- `ParsesBlueprintHeader_WithParentAndAssetPath`
- `ParsesRootComponentDecorator`
- `ParsesAttachComponentDecorator`
- `ParsesComponentBody_DefaultAssignments`
- `ParsesBpVar_TypeAfterName`
- `ParsesEditableAndCategoryDecorators`
- `ParsesEventDeclaration`
- `ParsesCallableAndPureDeclarations`
- `ParsesTypedAssetReference`
- `SampleFiles_AreBlueprintProfile`
- `ParsesBpAllowedStatementsAndExpressions`

Semantic tests cover:

- `RootDecorator_OnVariable_ReportsError`
- `AttachDecorator_TargetMissing_ReportsError`
- `EditableDecorator_OnComponent_ReportsError`
- `UnsupportedDecorator_ReportsError`
- `BpProfile_Goto_ReportsError`
- `BpProfile_Switch_ReportsError`
- `BpProfile_For_ReportsError`
- `BpProfile_NewExpression_ReportsError`
- `BpProfile_IrIntrinsic_ReportsError`
- `BpProfile_SyntaxExpressionSample_HasNoDiagnostics`
- `LegacyAttributeBlueprint_NormalizesToBpModel`
- `Exporter_ProducesStableBridgeDocument`
- `Exporter_EmitsAllowedStatementAndExpressionKinds`

## Next Steps

1. UE plugin MVP is implemented under `UnrealPlugins/KmsBlueprintImporter`: it consumes `kms-bp-export-v1`, creates or updates Blueprint assets, builds SCS components, imports variables, creates graph skeleton comments, generates function signatures, compiles, and saves.
2. Next UE pass: generate real K2 nodes from exported statements/expressions; current coverage preserves them in imported graph skeleton comments.
3. Add an Unreal automation test that imports the Door fixture JSON and verifies the generated Blueprint structure.
4. Add a CLI-facing `--profile auto|ir|bp` option for existing `compile` workflows so BP authoring files are routed away from the bytecode compiler.
