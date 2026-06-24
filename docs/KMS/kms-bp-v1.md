# KMS-BP v1 语法设计草案

状态：设计草案，parser/AST/semantic samples、CLI JSON export、UE import bridge 基础支持已落地  
Profile：Blueprint authoring  
最后更新：2026-06-24

KMS-BP v1 是在 [KMS-BP v0](./kms-bp.md) 之上的下一阶段语言设计。v0 锁定了 parser、semantic model、CLI JSON export 和 UE 插件 bridge 的最小闭环；v1 的目标是让 KMS-BP 从“能生成 Blueprint 壳子和 graph skeleton”推进到“能生成可运行的常用 K2 graph”。

注意：这里的 v1 指 KMS-BP language version，不等同于当前 CLI bridge JSON schema `kms-bp-export-v1`。如果 v1 语言需要破坏性 DTO 变更，再引入 `kms-bp-export-v2`。

当前实现进度：

- 已完成：`KismetScript.Parser.Tests/Samples/V1/*.kms` 全部纳入 parser + V1 semantic checker 测试，不再作为 skipped future fixtures。
- 已完成：grammar/AST 支持 `implements`、`construction`、`dispatcher`、`foreach`、delegate bind/unbind、多类型参数和表达式下标链。
- 已完成：V1 decorator semantic whitelist，包括 metadata、replication/RPC、override 等常用 authoring 标记。
- 已完成：CLI `kms-bp validate/export --language-version 1`、非破坏性 V1 DTO 扩展，以及 exporter 覆盖 `construction`、`dispatcher`、`for`、`foreach`、`switch`、delegate bind/unbind。
- 已完成：UE importer 识别 `languageVersion: "1"`，导入 Construction Script graph，创建 Event Dispatcher member variable + delegate signature graph，并遍历 V1 statement/expression 字段生成当前可用节点或 KMS generated fallback node。
- 未完成：V1 core 的完整真实 K2 node generation；`for`、`foreach`、`switch`、delegate bind/unbind/broadcast 仍可能降级为 generated fallback/comment。

## 1. 设计目标

v1 core 只覆盖最常见、最能形成端到端价值的 Blueprint authoring 能力：

- 真实 K2 call generation：普通调用、成员调用、静态库调用、`out/ref` 参数、返回值连接。
- 常用控制流节点：`if`、`while`、`for`、`foreach`、`switch`、`return`。
- Construction Script：组件和默认属性之外的构造期逻辑。
- Metadata 扩展：变量、函数、组件的常见 Details panel / Blueprint metadata。
- 类型扩展：`Array<T>`、`Map<K,V>`、`Set<T>`、`Class<T>`、soft references。
- Event Dispatcher：声明、bind、unbind、broadcast 的基础能力。
- Replication / RPC：常用网络标记语法。

v1 不追求覆盖所有 UE 图类型。UMG、Animation Blueprint、Material graph、Niagara graph、Macro graph 和完整 latent action 生态进入 v1.x 或后续 profile。

## 2. 与 v0 的关系

v1 继承 v0 的所有推荐语法：

```kms
blueprint BP_Door : Actor at "/Game/Generated/BP_Door_Gen" {
    @root
    component Root: SceneComponent;

    @editable
    var OpenAngle: float = 90.0;

    event BeginPlay() {
        print("Door ready");
    }
}
```

v1 仍保留 decorator-lite 原则：`blueprint`、`component`、`var`、`event`、`callable`、`pure`、`construction`、`dispatcher` 是一等语法，`@...` 只承载 metadata。

v1 semantic checker 应继续接受 v0 文件。v1 formatter 应继续优先输出 decorator-lite，不应把 decompiled KMS-IR 自动重写为 KMS-BP。

## 3. v1 完整示例

```kms
from "/Game/Common/DoorTypes" import {
    enum DoorState {
        Closed,
        Opening,
        Open
    };
}

@displayName("Generated Door")
blueprint BP_Door : Actor implements BPI_Interactable at "/Game/Generated/BP_Door_Gen" {
    @root
    component Root: SceneComponent;

    @attach(Root, socket = "DoorSocket")
    @tag("DoorVisual")
    component Mesh: StaticMeshComponent {
        StaticMesh = asset<StaticMesh>("/Game/Props/SM_Door.SM_Door");
        RelativeRotation = Rotator(0.0, 0.0, 0.0);
    }

    @editable
    @category("Door")
    @tooltip("Target open angle in degrees")
    @clamp(0.0, 180.0)
    @ui(0.0, 120.0)
    var OpenAngle: float = 90.0;

    @replicated
    @repNotify(OnRep_IsOpen)
    var IsOpen: bool = false;

    dispatcher OnDoorToggled(IsOpen: bool);

    construction {
        Mesh.SetRelativeRotation(Rotator(0.0, IsOpen ? OpenAngle : 0.0, 0.0));
    }

    event BeginPlay() {
        KismetSystemLibrary.PrintString(self, "Door ready");
        bind OnDoorToggled += HandleDoorToggled;
    }

    @category("Door")
    callable void Toggle() {
        SetOpen(!IsOpen);
    }

    @rpc(server, reliable)
    callable void ServerSetOpen(NewValue: bool) {
        SetOpen(NewValue);
    }

    callable void SetOpen(NewValue: bool) {
        IsOpen = NewValue;
        Mesh.SetRelativeRotation(Rotator(0.0, IsOpen ? OpenAngle : 0.0, 0.0));
        OnDoorToggled.Broadcast(IsOpen);
    }

    event OnRep_IsOpen() {
        Mesh.SetRelativeRotation(Rotator(0.0, IsOpen ? OpenAngle : 0.0, 0.0));
    }

    pure bool CanInteract(Actor Instigator) {
        return Instigator != none;
    }
}
```

## 4. 顶层声明

v1 顶层声明：

```text
topLevelDeclaration ::=
    importDeclaration
  | blueprintDeclaration
  | structDeclaration
  | enumDeclaration
```

Blueprint header：

```text
blueprintDeclaration ::=
    decorator*
    'blueprint' Identifier ':' typeIdentifier implementsClause?
    'at' StringLiteral
    '{' blueprintMember* '}'

implementsClause ::=
    'implements' typeIdentifier (',' typeIdentifier)*
```

说明：

- `implements` 映射到 Blueprint implemented interfaces。
- `blueprint` 上允许 metadata decorator，例如 `@displayName`、`@tooltip`。
- v1 不引入 `package` 或 `namespace` 作为 authoring 语义；source organization 仍使用 `from ... import ...`。

## 5. Blueprint 成员

v1 推荐成员：

```text
blueprintMember ::=
    componentDeclaration
  | bpVariableDeclaration
  | dispatcherDeclaration
  | constructionDeclaration
  | bpProcedureDeclaration
```

成员关键字：

| 关键字 | 目标 | v1 语义 |
| --- | --- | --- |
| `component` | SCS component node | 声明组件实例和默认属性。 |
| `var` | member variable 或 local variable | Blueprint 成员变量或函数内局部变量。 |
| `dispatcher` | Event Dispatcher | 声明 BlueprintAssignable dispatcher。 |
| `construction` | Construction Script | 声明 Construction Script body。 |
| `event` | Event graph entry | 声明事件入口或 override event。 |
| `callable` | Function graph | 声明可调用函数。 |
| `pure` | Pure function graph | 声明 pure function。 |

## 6. Decorator

Decorator 仍采用：

```text
decorator ::= '@' Identifier argumentList?
```

v1 支持的 metadata：

| Decorator | 允许目标 | v1 语义 |
| --- | --- | --- |
| `@displayName("Name")` | `blueprint`, `component`, `var`, `event`, `callable`, `pure`, `dispatcher` | DisplayName metadata。 |
| `@tooltip("Text")` | 所有 BP 成员 | Tooltip metadata，v1 必须 normalize/export/import。 |
| `@category("Name")` | `var`, `callable`, `pure`, `dispatcher` | Blueprint category。 |
| `@keywords("Text")` | `callable`, `pure` | Function search keywords。 |
| `@root` | `component` | Root SCS component。 |
| `@attach(Target)` | `component` | Attach to target component。 |
| `@attach(Target, socket = "SocketName")` | `component` | Attach to target component socket。 |
| `@tag("Name")` | `component`, `var` | Component tag 或 variable metadata tag。 |
| `@editable` | `var` | Instance editable / EditAnywhere。 |
| `@readOnly` | `var` | BlueprintReadOnly。 |
| `@readWrite` | `var` | BlueprintReadWrite。 |
| `@exposeOnSpawn` | `var` | ExposeOnSpawn metadata。 |
| `@private` | `var`, `callable`, `pure` | Private access metadata。 |
| `@advancedDisplay` | `var`, `callable`, `pure` | AdvancedDisplay metadata。 |
| `@clamp(Min, Max)` | numeric `var` | ClampMin / ClampMax。 |
| `@ui(Min, Max)` | numeric `var` | UIMin / UIMax。 |
| `@saveGame` | `var` | SaveGame flag。 |
| `@config` | `var` | Config flag。 |
| `@transient` | `var` | Transient flag。 |
| `@replicated` | `var` | Replicated variable。 |
| `@repNotify(FunctionName)` | `var` | RepNotify function。 |
| `@rpc(server, reliable)` | `callable` | Server RPC。 |
| `@rpc(client, reliable)` | `callable` | Client RPC。 |
| `@rpc(multicast, unreliable)` | `callable` | NetMulticast RPC。 |
| `@callInEditor` | `callable` | CallInEditor。 |
| `@override` | `event`, `callable`, `pure` | Override parent/interface function。 |

语义规则：

- Unknown decorator 仍应 parse 成功，但 semantic checker 报 `UnsupportedDecorator`。
- `@repNotify(F)` 要求同一 blueprint 内存在 `event F()` 或 `callable void F()`。
- `@rpc(...)` 只允许 `callable`，不允许 `pure`。
- `@clamp` 和 `@ui` 只允许 numeric variable。
- `@attach(Target, socket = "...")` 的 `Target` 必须是已声明 component。

## 7. Component v1

组件语法延续 v0：

```text
componentDeclaration ::=
    decorator* 'component' Identifier ':' typeIdentifier
    (';' | '{' componentProperty* '}')

componentProperty ::=
    Identifier (':' typeIdentifier)? '=' expression ';'
```

v1 约定：

- component property 允许 struct/function call 表达式，例如 `Vector(...)`、`Rotator(...)`、`Transform(...)`。
- `@attach(Target, socket = "Socket")` 映射 `SetupAttachment(Target, SocketName)`。
- component body 只表达默认值，不表达运行时逻辑；运行时逻辑写在 `construction`、`event` 或 `callable` 中。

示例：

```kms
@attach(Root, socket = "DoorSocket")
component Mesh: StaticMeshComponent {
    StaticMesh = asset<StaticMesh>("/Game/Props/SM_Door.SM_Door");
    RelativeLocation = Vector(0.0, 0.0, 0.0);
    RelativeRotation = Rotator(0.0, 90.0, 0.0);
    ComponentTags = ["Door", "Interactable"];
}
```

## 8. Variable v1

变量语法延续 v0：

```text
bpVariableDeclaration ::=
    decorator* 'var' Identifier ':' typeIdentifier ('=' expression)? ';'
```

新增语义：

- v1 exporter 必须保留变量 metadata。
- UE plugin 必须把变量类型、默认值、category、editable、tooltip、replication 语义写入 Blueprint。
- 成员变量和 local variable 都使用 `var`；scope 由所在位置决定。

示例：

```kms
@editable
@category("Door")
@tooltip("Angle in degrees")
@clamp(0.0, 180.0)
@ui(0.0, 120.0)
var OpenAngle: float = 90.0;

@replicated
@repNotify(OnRep_IsOpen)
var IsOpen: bool = false;
```

## 9. Dispatcher

v1 新增 Event Dispatcher：

```text
dispatcherDeclaration ::=
    decorator* 'dispatcher' Identifier parameterList ';'
```

示例：

```kms
@category("Door")
dispatcher OnDoorToggled(IsOpen: bool);
```

Dispatcher 操作语句：

```text
dispatcherStatement ::=
    'bind' expression '+=' Identifier ';'
  | 'unbind' expression '-=' Identifier ';'
  | expression '.Broadcast' argumentList ';'
```

示例：

```kms
bind OnDoorToggled += HandleDoorToggled;
unbind OnDoorToggled -= HandleDoorToggled;
OnDoorToggled.Broadcast(IsOpen);
```

语义：

- `bind D += Handler` 生成 Assign/Bind Event 节点。
- `unbind D -= Handler` 生成 Unbind Event 节点。
- `D.Broadcast(...)` 生成 Call dispatcher 节点。
- Handler 的参数列表必须兼容 dispatcher signature。

## 10. Construction Script

v1 新增 Construction Script：

```text
constructionDeclaration ::=
    decorator* 'construction' compoundStatement
```

示例：

```kms
construction {
    Mesh.SetRelativeRotation(Rotator(0.0, IsOpen ? OpenAngle : 0.0, 0.0));
}
```

语义：

- 一个 blueprint 最多一个 `construction`。
- `construction` 不能有参数和 return type。
- `construction` 中允许与 event/callable 相同的 statement subset，但禁止 latent calls。
- UE plugin 应映射到 Blueprint Construction Script graph。

## 11. Procedure v1

v1 procedure 仍使用：

```text
bpProcedureDeclaration ::=
    decorator* 'event' Identifier parameterList compoundStatement
  | decorator* 'callable' typeIdentifier Identifier parameterList compoundStatement
  | decorator* 'pure' typeIdentifier Identifier parameterList compoundStatement
```

新增规则：

- `event` 可用于 engine event、custom event、interface event 和 RepNotify event。
- `callable` 可加 `@rpc(...)`、`@callInEditor`、`@override`。
- `pure` 必须通过 semantic checker 禁止赋值、local state mutation、latent calls、dispatcher bind/broadcast。
- `@override` 的目标必须能在 parent/interface 中解析到。

示例：

```kms
event ReceiveActorBeginOverlap(Actor OtherActor) {
    print("overlap");
}

@override
callable void Interact(Actor Instigator) {
    Toggle();
}

@rpc(server, reliable)
callable void ServerInteract(Actor Instigator) {
    Interact(Instigator);
}
```

## 12. 类型系统 v1

v1 类型语法：

```text
typeIdentifier ::=
    builtinType
  | Identifier
  | Identifier '<' typeIdentifier (',' typeIdentifier)* '>'
  | typeIdentifier '[]'

builtinType ::= 'bool' | 'byte' | 'int' | 'int64' | 'float' | 'double' | 'string' | 'name' | 'text' | 'void'
```

推荐构造类型：

```kms
Array<int>
Set<string>
Map<string, int>
Object<Actor>
Class<Actor>
SoftObject<StaticMesh>
SoftClass<Actor>
Interface<BPI_Interactable>
```

类型映射：

| KMS-BP type | UE pin/property 目标 |
| --- | --- |
| `bool` | Boolean |
| `byte` | Byte |
| `int` | Integer |
| `int64` | Integer64 |
| `float` / `double` | Real |
| `string` | String |
| `name` | Name |
| `text` | Text |
| `Object<T>` | Object reference |
| `Class<T>` | Class reference |
| `SoftObject<T>` | Soft object reference |
| `SoftClass<T>` | Soft class reference |
| `Array<T>` | Array pin/container |
| `Set<T>` | Set pin/container |
| `Map<K,V>` | Map pin/container |

## 13. 表达式 v1

v1 保留 v0 表达式，并新增或正式化：

```text
expression ::=
    literal
  | Identifier
  | 'self'
  | 'super'
  | 'none'
  | expression '.' Identifier
  | callExpression
  | expression '[' expression ']'
  | unaryExpression
  | binaryExpression
  | conditionalExpression
  | arrayLiteral
  | objectLiteral
```

新增字面量：

| 字面量 | 语义 |
| --- | --- |
| `none` | Unreal object/class None。 |
| `self` | 当前 Blueprint self。 |
| `super` | parent implementation target。 |

调用表达式：

```text
callExpression ::=
    expression? Identifier typeArgumentList? argumentList

argumentList ::= '(' argument? (',' argument)* ')'

argument ::=
    expression
  | Identifier '=' expression
  | 'out' Identifier
  | 'out' typeIdentifier Identifier
```

示例：

```kms
print("Door ready");
KismetSystemLibrary.PrintString(self, "Door ready");
MathLibrary.Clamp<float>(OpenAngle, 0.0, 90.0);
DoorController.Open();
DoorController.Close();
SetRelativeRotation(Target = Mesh, NewRotation = Rotator(0.0, 90.0, 0.0));
TryGetAngle(out Result);
TryGetAngle(out float Scratch);
super.ReceiveBeginPlay();
```

KMS-BP v1 authoring 不引入 `->`；UObject/reference 成员访问统一写 `.`。`->` 只保留给 KMS-IR / legacy compatibility。

调用解析顺序：

1. Local function / same blueprint procedure。
2. Imported declaration alias。
3. Known library alias，例如 `KismetSystemLibrary`、`KismetMathLibrary`、`MathLibrary`。
4. Member call target type lookup。
5. Explicit UE function path metadata。

如果多个 overload 匹配，semantic checker 必须要求用户提供 type arguments、named arguments 或 explicit import alias 来消歧。

## 14. 语句 v1

v1 allowed statements：

```text
statement ::=
    compoundStatement
  | localVariableDeclaration
  | expressionStatement
  | ifStatement
  | whileStatement
  | forStatement
  | foreachStatement
  | switchStatement
  | dispatcherStatement
  | breakStatement
  | continueStatement
  | returnStatement
```

### 14.1 for

```text
forStatement ::=
    'for' '(' forInitializer? ';' expression? ';' expression? ')' statement

forInitializer ::=
    localVariableDeclaration
  | expression
```

示例：

```kms
for (var Index: int = 0; Index < Doors.Length; Index += 1) {
    Doors[Index].Open();
}
```

### 14.2 foreach

```text
foreachStatement ::=
    'foreach' '(' Identifier ':' typeIdentifier 'in' expression ')' statement
```

示例：

```kms
foreach (Door: BP_Door in Doors) {
    Door.Toggle();
}
```

### 14.3 switch

```text
switchStatement ::=
    'switch' '(' expression ')' '{' switchCase* defaultCase? '}'

switchCase ::= 'case' expression ':' statement*
defaultCase ::= 'default' ':' statement*
```

示例：

```kms
switch (State) {
    case DoorState.Closed:
        Open();
        break;
    case DoorState.Open:
        Close();
        break;
    default:
        print("unknown state");
}
```

语义：

- v1 core 支持 switch on enum、int、string、name。
- `break` 只允许在 loop 或 switch 内。
- `continue` 只允许在 loop 内。
- `goto` 仍不进入 v1。

## 15. Latent 和执行流

v1 core 不引入新的 statement keyword 来表达 latent 节点。Latent 节点作为普通函数调用处理：

```kms
Delay(1.0);
Timeline.Play();
AsyncLoadAsset(Asset, out LoadedObject);
```

语义：

- latent call 只允许在 `event`、`callable`、`construction` 中。
- latent call 禁止出现在 `pure` 中。
- UE plugin 必须识别 latent UFunction 并连接 exec pins。
- 如果 latent function 需要 world context，优先自动连接 `self`；无法推导时报 semantic diagnostic。

v1.x 可再考虑 `sequence`、`doOnce`、`gate` 等专用语法。v1 core 先使用函数/宏调用映射。

## 16. K2 生成语义

v1 exporter 需要保持 statement/expression DTO 足够结构化，UE plugin 负责生成真实 K2 nodes。

核心映射：

| KMS-BP | K2 目标 |
| --- | --- |
| `var Local: T = expr;` | local variable + set node |
| `Name = expr;` | variable set |
| `Name += expr;` | get + math + set |
| `print(expr);` | `UKismetSystemLibrary::PrintString` |
| `A + B`, `A < B`, `A && B` | Kismet math/library function |
| `Call(...)` | `UK2Node_CallFunction` |
| `Obj.Method(...)` | `UK2Node_CallFunction` with target pin |
| `if` | `UK2Node_IfThenElse` |
| `while` | standard macro `WhileLoop` |
| `for` | standard macro `ForLoop` or generated loop graph |
| `foreach` | standard macro `ForEachLoop` |
| `switch` | switch K2 node by input type |
| `return expr;` | function result node |
| `dispatcher.Broadcast(...)` | event dispatcher call node |
| `bind D += H` | bind event node |

Fallback：

- v1 importer 可以保留 `UK2Node_KmsGeneratedNode` 作为 unsupported fallback。
- v1 acceptance 不允许 fallback 覆盖 core features；测试要检查 core examples 生成真实 UE node。

## 17. Semantic Checker v1

v1 semantic checker 新增职责：

- Decorator target validation。
- Function/call resolution。
- Type compatibility 和 implicit conversion 检查。
- Pure function side-effect 检查。
- Latent call placement 检查。
- RepNotify/RPC signature 检查。
- Dispatcher handler signature 检查。
- `break` / `continue` context 检查。
- `construction` 唯一性检查。
- Component attachment DAG 检查，禁止 attach cycle。

诊断建议：

| Code | 场景 |
| --- | --- |
| `UnsupportedV1Feature` | parser 接受但 v1 core 未支持。 |
| `UnresolvedCall` | 找不到函数或 overload。 |
| `AmbiguousCall` | 多个 overload 匹配。 |
| `InvalidCallTarget` | member call target 类型不支持该函数。 |
| `InvalidDecoratorTarget` | decorator 目标错误。 |
| `InvalidPureSideEffect` | pure function 中出现赋值、dispatcher、latent call。 |
| `InvalidLatentContext` | latent call 出现在 pure 或不允许的上下文。 |
| `InvalidReplicationSignature` | RepNotify/RPC signature 不符合 UE 要求。 |
| `InvalidFlowControl` | `break` / `continue` 放错位置。 |

## 18. v1 新增关键字

v1 需要在 lexer/parser 层新增或正式化这些 authoring keyword：

| 关键字 | 用途 |
| --- | --- |
| `implements` | Blueprint implemented interfaces。 |
| `construction` | Construction Script 声明。 |
| `dispatcher` | Event Dispatcher 声明。 |
| `bind` | Dispatcher bind 语句。 |
| `unbind` | Dispatcher unbind 语句。 |
| `foreach` | ForEach loop。 |
| `in` | `foreach` collection separator。 |
| `self` | 当前 Blueprint 实例。 |
| `super` | parent implementation target。 |
| `none` | Unreal None/null object reference。 |

实现建议：

- `self`、`super`、`none` 可以先作为 reserved literals 处理，避免和用户变量冲突。
- `bind`、`unbind` 只在 statement 起始位置作为 keyword 生效。
- 如果要降低破坏性，可以允许反引号转义同名标识符，例如 `` `self` ``。

## 19. CLI / JSON / UE Bridge

v1 CLI 命令建议：

```bash
dotnet run --project UAssetStudio.Cli -- kms-bp validate path/to/BP_Door.kms --language-version 1
dotnet run --project UAssetStudio.Cli -- kms-bp export path/to/BP_Door.kms --language-version 1 --out path/to/BP_Door.kms-bp.json
```

Versioning：

- 如果 DTO 非破坏性扩展，可继续 `kms-bp-export-v1`，并在 document 内增加 `languageVersion: "1"`。
- 如果 call resolution、metadata 或 type DTO 需要破坏旧 importer，则升到 `kms-bp-export-v2`。
- UE plugin 必须拒绝高于自身支持版本的 document，并输出明确错误。

## 20. TDD 计划

新增 parser samples：

- `KismetScript.Parser.Tests/Samples/V1/BpDoorV1_Calls.kms`
- `KismetScript.Parser.Tests/Samples/V1/BpDoorV1_Metadata.kms`
- `KismetScript.Parser.Tests/Samples/V1/BpDoorV1_Construction.kms`
- `KismetScript.Parser.Tests/Samples/V1/BpDoorV1_ControlFlow.kms`
- `KismetScript.Parser.Tests/Samples/V1/BpDoorV1_Dispatchers.kms`
- `KismetScript.Parser.Tests/Samples/V1/BpDoorV1_ReplicationRpc.kms`
- `KismetScript.Parser.Tests/Samples/V1/BpDoorV1_Types.kms`
- `KismetScript.Parser.Tests/Samples/V1/BpDoorV1_Full.kms`

Parser tests：

- `ParsesBlueprintImplementsClause`
- `ParsesBlueprintLevelDecorators`
- `ParsesConstructionDeclaration`
- `ParsesDispatcherDeclaration`
- `ParsesNamedArguments`
- `ParsesSelfSuperNone`
- `ParsesMapSetClassSoftTypes`
- `ParsesForForeachSwitch`
- `ParsesRpcAndReplicationDecorators`

Semantic tests：

- `PureFunction_Assignment_ReportsError`
- `PureFunction_LatentCall_ReportsError`
- `RpcDecorator_OnPure_ReportsError`
- `RepNotify_TargetMissing_ReportsError`
- `RepNotify_InvalidSignature_ReportsError`
- `DispatcherBind_HandlerSignatureMismatch_ReportsError`
- `BreakOutsideLoopOrSwitch_ReportsError`
- `ContinueOutsideLoop_ReportsError`
- `Construction_Duplicate_ReportsError`
- `Call_Unresolved_ReportsError`
- `Call_Ambiguous_ReportsError`

Exporter tests：

- `Exporter_EmitsLanguageVersion`
- `Exporter_EmitsMetadata`
- `Exporter_EmitsNamedArgumentsAndOutArguments`
- `Exporter_EmitsConstructionGraph`
- `Exporter_EmitsDispatcherDeclarations`
- `Exporter_EmitsReplicationAndRpcMetadata`
- `Exporter_EmitsMapSetClassSoftTypes`

UE plugin tests：

- Import v1 full door demo。
- Assert component hierarchy and socket attachment。
- Assert member variables include editable/category/tooltip/clamp/replication metadata。
- Assert function graph has real entry/result pins。
- Assert `construction` imports into User Construction Script。
- Assert `dispatcher` imports as `PC_MCDelegate` variable plus delegate signature graph。
- Assert `if/while/for/foreach/switch` generate real K2 nodes。
- Assert direct, library, and member calls generate `UK2Node_CallFunction`。
- Assert dispatcher bind/broadcast nodes exist。
- Assert no `UK2Node_KmsGeneratedNode` remains for v1 core sample。

Acceptance commands：

```bash
dotnet test KismetScript.Parser.Tests/KismetScript.Parser.Tests.csproj
dotnet run --project UAssetStudio.Cli -- kms-bp validate KismetScript.Parser.Tests/Samples/V1/BpDoorV1_Full.kms --language-version 1
dotnet run --project UAssetStudio.Cli -- kms-bp export KismetScript.Parser.Tests/Samples/V1/BpDoorV1_Full.kms --language-version 1 --out /tmp/BpDoorV1.kms-bp.json
UnrealEditor-Cmd <Project>.uproject -run=KmsBpImport -Json=/tmp/BpDoorV1.kms-bp.json -ValidateExec
```

## 21. v1 分阶段落地

建议按下面顺序做：

1. **Call v1**：完善 call DTO，支持 named/out/ref args，UE plugin 生成 direct/library/member call nodes。
2. **Metadata v1**：变量、函数、组件 metadata normalize/export/import。
3. **Construction v1**：新增 `construction` parser/AST/DTO/UE import。基础支持已完成。
4. **Control Flow v1**：`for`、`foreach`、`switch`，并补 break/continue context checker。
5. **Types v1**：多类型参数、Map/Set/Class/SoftObject/SoftClass。CLI/export + UE pin type 基础支持已完成。
6. **Dispatcher v1**：声明、bind、unbind、broadcast。dispatcher 声明基础导入已完成，bind/unbind/broadcast 真实节点待完成。
7. **Replication/RPC v1**：replicated variable、RepNotify、RPC decorators。

每一步都必须同时更新：

- `KismetScript.g4`
- AST parser
- syntax nodes
- semantic checker
- exporter DTO
- parser tests
- exporter tests
- UE plugin importer
- UE plugin validation fixture

## 22. v1 不包含

以下能力暂不纳入 v1 core：

- Blueprint Macro graph authoring。
- UMG widget designer tree。
- Animation Blueprint state machine。
- Material graph。
- Niagara graph。
- Behavior Tree / Environment Query authoring。
- 完整 C++ UHT specifier mirror。
- KMS-IR bytecode round-trip 语法重写。

这些能力可以作为单独 profile 或 v1.x extension 设计，避免 v1 core 失焦。
