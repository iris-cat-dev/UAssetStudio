# KMS-BP 语法说明书

状态：草案 v0.2  
Profile：Blueprint authoring  
最后更新：2026-06-24

KMS-BP 是面向人手写的 Blueprint authoring 语法。它的目标不是直接表达 Kismet bytecode，而是把一份清晰、稳定的文本源文件转换为：

- 普通 `UBlueprint` asset
- SCS component tree
- Blueprint member variables
- Blueprint event/function graph skeleton
- 后续阶段的真实 K2 nodes

KMS-BP 使用 decorator-lite 风格：`blueprint`、`component`、`event`、`var` 是一等语法，`@...` 只承载轻量 metadata。

## 1. 最小示例

```kms
blueprint BP_Door : Actor at "/Game/Generated/BP_Door_Gen" {
    event BeginPlay() {
        print("Door ready");
    }
}
```

## 2. 完整示例

```kms
blueprint BP_Door : Actor at "/Game/Generated/BP_Door_Gen" {
    @root
    component Root: SceneComponent;

    @attach(Root)
    component Mesh: StaticMeshComponent {
        StaticMesh = asset<StaticMesh>("/Game/Props/SM_Door.SM_Door");
        Mobility: byte = 1;
    }

    @editable
    @category("Door")
    var OpenAngle: float = 90.0;

    var IsOpen: bool = false;

    event BeginPlay() {
        print("Door ready");
    }

    @category("Door")
    callable void Toggle() {
        IsOpen = !IsOpen;
    }

    pure bool GetIsOpen() {
        return IsOpen;
    }
}
```

## 3. 文件结构

一个 KMS-BP 文件通常包含一个或多个 `blueprint` 顶层声明，也可以包含 `from ... import ...`。

```text
kmsBpFile ::= importDeclaration* blueprintDeclaration+
```

导入：

```kms
from "/Game/Common/DoorTypes" import {
    struct DoorConfig {
        int Mode;
    }
}
```

语法：

```text
importDeclaration ::=
    'from' StringLiteral 'import' declaration
  | 'from' StringLiteral 'import' '{' declaration* '}'
```

说明：

- import 是 KMS source organization 语法，不等同于 C# using 或 TypeScript import。
- package 字符串是否对应真实 Unreal 包，由后续工具链决定。

## 4. Blueprint 声明

```kms
blueprint BP_Door : Actor at "/Game/Generated/BP_Door_Gen" {
    blueprintMember*
}
```

语法：

```text
blueprintDeclaration ::=
    'blueprint' Identifier ':' typeIdentifier 'at' StringLiteral
    '{' blueprintMember* '}'
```

字段：

- `Identifier`：Blueprint 类名，例如 `BP_Door`。
- `typeIdentifier`：父类，例如 `Actor`。
- `StringLiteral`：目标 Unreal asset path，例如 `/Game/Generated/BP_Door_Gen`。

约定：

- asset path 使用 Unreal long package path。
- path 不写对象名后缀；插件会把 `/Game/Generated/BP_Door_Gen` 视为 package path，并创建同名 asset。

## 5. Blueprint 成员

推荐成员：

```text
blueprintMember ::=
    componentDeclaration
  | bpVariableDeclaration
  | bpProcedureDeclaration
```

推荐成员关键字：

| 关键字 | 声明类别 | 归一化目标 | 说明 | 常用 decorator |
| --- | --- | --- | --- | --- |
| `component` | 组件声明 | Blueprint component model / SCS node | 声明一个挂在 Blueprint 上的组件实例，不是定义新的组件类型。组件体内可以写默认属性赋值。 | `@root`, `@attach`, `@tooltip` |
| `var` | 成员变量声明 | Blueprint member variable | 声明 Blueprint 成员变量和可选默认值。写在函数体内的 `var` 是 local var，不属于 Blueprint 成员。 | `@editable`, `@category`, `@tooltip` |
| `event` | 事件入口声明 | Blueprint event graph entry | 声明引擎事件或 Blueprint event 入口；源码中不写 return type，语义上固定为 `void`。 | `@tooltip` |
| `callable` | 可调用函数声明 | Blueprint callable function | 声明带执行语义的 Blueprint function；需要显式 return type，`void` 表示无返回值。 | `@category`, `@tooltip` |
| `pure` | 纯函数声明 | Blueprint pure function | 声明无执行 pin 的 Blueprint pure function；需要显式 return type，语义上应避免副作用。 | `@category`, `@tooltip` |

兼容成员：

- `enum`
- type-first variable
- type-first procedure
- legacy `object`

兼容成员是为了迁移和 parser 复用，新写 KMS-BP 时应优先使用推荐成员。

## 6. Decorator

语法：

```text
decorator ::= '@' Identifier argumentList?
```

Decorator 有两层规则：

- parser placement：语法上 `decorator*` 可以出现在 `component`, `var`, `event`, `callable`, `pure` 前面。
- BP semantic target：BP profile 只接受下表中的 decorator 和目标组合。

支持的 decorator：

| Decorator | BP semantic 允许目标 | 样例覆盖 | 当前后端语义 |
| --- | --- | --- | --- |
| `@root` | `component` | `Samples/V0/BpDoor_Components.kms`, `Samples/V0/BpDoor_Full.kms` | 标记根组件 |
| `@attach(Target)` | `component` | `Samples/V0/BpDoor_Components.kms`, `Samples/V0/BpDoor_Full.kms` | 挂到目标组件 |
| `@editable` | `var` | `Samples/V0/BpDoor_Variables.kms`, `Samples/V0/BpDoor_Full.kms` | 生成可编辑 Blueprint 变量 |
| `@category("Name")` | `var`, `callable`, `pure` | `Samples/V0/BpDoor_Variables.kms`, `Samples/V0/BpDoor_EventsFunctions.kms`, `Samples/V0/BpDoor_Full.kms` | 设置 Blueprint 分类 |
| `@tooltip("Text")` | `component`, `var`, `event`, `callable`, `pure` | `Samples/V0/BpDoor_DecoratorsAndImports.kms` | parser 和 semantic checker 接受；v0 暂不 normalize/export |

语义规则：

- 未知 decorator 会 parse 成功，但 semantic checker 报 `UnsupportedDecorator`。
- decorator 放错目标会报 `InvalidDecoratorTarget`。
- `@attach(Target)` 找不到组件会报 `MissingAttachTarget`。
- `@category` 不允许放在 `event` 上；如果需要事件说明文本，用 `@tooltip`。

示例：

```kms
@editable
@category("Door")
var OpenAngle: float = 90.0;
```

## 7. Component

无属性组件：

```kms
@root
component Root: SceneComponent;
```

带属性组件：

```kms
@attach(Root)
component Mesh: StaticMeshComponent {
    StaticMesh = asset<StaticMesh>("/Game/Props/SM_Door.SM_Door");
    Mobility: byte = 1;
}
```

语法：

```text
componentDeclaration ::=
    decorator* 'component' Identifier ':' typeIdentifier
    (';' | '{' componentProperty* '}')

componentProperty ::=
    Identifier (':' typeIdentifier)? '=' expression ';'
```

属性写法：

```kms
StaticMesh = asset<StaticMesh>("/Game/Props/SM_Door.SM_Door");
Mobility: byte = 1;
```

说明：

- `PropertyName = expression;` 是推荐写法。
- `PropertyName: Type = expression;` 可用于显式说明属性类型。
- 旧式 `Type PropertyName = expression;` 属于 KMS-IR `object` 语法，不推荐写在 KMS-BP `component` 中。

## 8. Variable

```kms
@editable
@category("Door")
var OpenAngle: float = 90.0;

var IsOpen: bool = false;
```

语法：

```text
bpVariableDeclaration ::=
    decorator* 'var' Identifier ':' typeIdentifier ('=' expression)? ';'
```

说明：

- KMS-BP 推荐 `Name: Type`。
- type-first `float OpenAngle = 90.0;` 仍可解析，但只用于兼容。

## 9. Event / Callable / Pure

事件：

```kms
event BeginPlay() {
    print("Door ready");
}
```

可调用函数：

```kms
@category("Door")
callable void Toggle() {
    IsOpen = !IsOpen;
}
```

纯函数：

```kms
pure bool GetIsOpen() {
    return IsOpen;
}
```

语法：

```text
bpProcedureDeclaration ::=
    decorator* 'event' Identifier parameterList compoundStatement
  | decorator* 'callable' typeIdentifier Identifier parameterList compoundStatement
  | decorator* 'pure' typeIdentifier Identifier parameterList compoundStatement
```

语义：

- `event` 用于 Blueprint 事件入口，例如 `BeginPlay`。源码中不写 return type，归一化后固定为 `void`。
- `callable` 用于带执行语义的 Blueprint function。源码必须写 return type，`void` 表示无返回值。
- `pure` 用于无执行 pin 的 Blueprint pure function。源码必须写 return type，语义上应避免修改对象状态。

## 10. 参数

推荐写法：

```kms
callable void Exercise(Value: int, out Result: int) {
    Result = Value + 1;
}
```

兼容写法：

```kms
callable void Legacy(int Value, out int Result) {
}
```

语法：

```text
parameterList ::= '(' parameter? (',' parameter)* ')'

parameter ::=
    attributeList? modifier* Identifier ':' typeIdentifier arraySignifier?
  | attributeList? modifier* typeIdentifier Identifier arraySignifier?
  | '...'
```

KMS-BP 推荐修饰符：

- `out`
- `ref`

## 11. 类型

内置类型：

```text
bool byte int float string void
```

UE 或用户类型：

```kms
Actor
SceneComponent
StaticMeshComponent
DoorConfig
```

构造类型：

```kms
Object<StaticMesh>
Array<int>
int[]
```

语法：

```text
typeIdentifier ::=
    Identifier '<' typeIdentifier '>' arraySignifier?
  | BuiltinTypeIdentifier arraySignifier?
  | Identifier arraySignifier?
```

说明：

- KMS-BP 推荐 `Array<Type>`。
- `Type[]` 也可解析。
- 当前 `typeIdentifier` 只稳定支持单个类型参数。

## 12. 语句

KMS-BP v0 允许：

```kms
{
    var Local: int = 1;
    print(Local);

    if (Local > 0) {
        print("positive");
    } else {
        print("zero or negative");
    }

    while (Local < 3) {
        Local += 1;
        continue;
    }

    return;
}
```

允许列表：

- compound block：`{ statement* }`
- local var：`var Name: Type = expression;`
- expression statement：`expression;`
- `if (...) statement else statement`
- `while (...) statement`
- `break;`
- `continue;`
- `return;`
- `return expression;`

语法摘要：

```text
statement ::=
    compoundStatement
  | bpVariableDeclaration
  | expression ';'
  | ifStatement
  | whileStatement
  | 'break' ';'
  | 'continue' ';'
  | 'return' expression? ';'
```

KMS-BP v0 拒绝：

- `goto`
- `switch`
- `for`
- label
- `new`
- 低层 KMS-IR intrinsic calls

低层 intrinsic：

```text
Context LetObj FinalFunction VirtualFunction Let LetBool LetDelegate
LetWeakObjPtr LetMulticastDelegate InstanceVariable LocalVariable
DefaultVariable NoObject
```

## 13. 表达式

### 13.1 字面量和标识符

```kms
true
false
123
90.0
"Door"
IsOpen
```

### 13.2 调用

```kms
print("Door ready")
asset<StaticMesh>("/Game/Props/SM_Door.SM_Door")
KismetSystemLibrary.PrintString("hello")
MathLibrary.Clamp<float>(OpenAngle, 0.0, 90.0)
DoorController.Open()
DoorController.Close()
TryGetAngle(out Result)
TryGetAngle(out float Scratch)
```

语法：

```text
callExpression ::= Identifier typeArgumentList? argumentList
typeArgumentList ::= '<' typeIdentifier (',' typeIdentifier)* '>'
```

说明：

- `asset<T>("...")` 是 KMS-BP bridge 约定，用于 UE asset reference。
- KMS-BP authoring 推荐只使用 `obj.Method(...)` 点号成员调用。
- `->` 属于 KMS-IR / legacy compatibility，不推荐写在 KMS-BP authoring 中。
- `out Name` 和 `out Type Name` 是调用参数语法，用于输出参数。
- 调用语法样例见 `KismetScript.Parser.Tests/Samples/V0/BpDoor_Calls.kms`。

### 13.3 成员访问和下标

```kms
Door.Angle
Door.Open()
Door.Mesh
Values[0]
```

### 13.4 cast / typeof

```kms
(float)Result
typeof(float)
```

### 13.5 初始化列表和对象字面量

```kms
[1, 2, 3]
{ 1, 2, 3 }
{ Mode: "Door", Count: 3 }
```

### 13.6 运算符

支持：

```text
! - ++ --
* / %
+ -
< > <= >=
== !=
& ^ |
&& ||
?:
= += -= *= /= %=
```

注意：

- `<<` / `>>` 的 AST 类型存在，但当前 grammar 未启用 shift 表达式。
- `new` 表达式 parser 可接受，但 KMS-BP semantic checker 会拒绝。

## 14. Legacy Blueprint 兼容

旧 attribute-heavy 写法仍可解析，并 normalizes 到同一套 BP semantic model：

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

映射关系：

| Legacy | KMS-BP model |
| --- | --- |
| `[Blueprint(Path = "...")] class Name : Parent` | `blueprint Name : Parent at "..."` |
| `[RootComponent] object Root : Type` | `@root component Root: Type` |
| `[Component, Attach = Root] object Mesh : Type` | `@attach(Root) component Mesh: Type` |
| `[Edit, Category("Door")] float OpenAngle` | `@editable @category("Door") var OpenAngle: float` |
| `[Event("BeginPlay")] void ReceiveBeginPlay()` | `event BeginPlay()` semantic model |
| `[BlueprintCallable]` | `callable` semantic model |
| `[BlueprintPure]` | `pure` semantic model |

说明：

- 新文档、formatter、示例应优先输出 decorator-lite。
- Decompiled KMS-IR 不应自动重写成 KMS-BP。

## 15. CLI 和 UE bridge

校验：

```bash
dotnet run --project UAssetStudio.Cli -- kms-bp validate path/to/BP_Door.kms
```

导出：

```bash
dotnet run --project UAssetStudio.Cli -- kms-bp export path/to/BP_Door.kms --out path/to/BP_Door.kms-bp.json
```

UE 插件导入：

```bash
UnrealEditor-Cmd <Project>.uproject -run=KmsBpImport -Json=/absolute/path/BP_Door.kms-bp.json
```

说明：

- JSON schema version 当前为 `kms-bp-export-v1`。
- Bridge JSON 是稳定 DTO，不是 AST 序列化。
- UE 插件当前已能创建/更新 Blueprint、组件、变量、函数签名和 graph skeleton comments。
- 真实 K2 node generation 属于后续阶段。

## 16. 已知限制

- `CharLiteral` 词法存在，但 AST parser 未稳定支持。
- `<<` / `>>` shift expression 当前 grammar 未启用。
- `double` literal 存在，但 `double` 不是内置类型 token。
- `typeIdentifier` 当前只支持单类型参数，`Map<K,V>` 这类类型语法尚未稳定。
- KMS-BP parser 能接受部分 KMS-IR 语句，但 semantic checker 会拒绝。
- UE 插件已能保留 statements/expressions 到 graph skeleton comments，真实 K2 node generation 仍待实现。

## 17. 关键字速查

本节按当前 `KismetScript.g4` 的 lexer keyword 总结。注意：decorator 名称不是关键字，`@root`, `@attach`, `@editable`, `@category`, `@tooltip` 都按 `Identifier` 解析后再由 BP semantic checker 校验。`asset`、`print`、`KismetSystemLibrary` 也是普通调用名，不是关键字。

KMS-BP 推荐使用：

| 关键字 | 用途 | 说明 |
| --- | --- | --- |
| `from` | import 声明 | 引入另一个 KMS package 中的声明。 |
| `import` | import 声明 | 与 `from` 组合使用。 |
| `blueprint` | 顶层 Blueprint 声明 | 定义一个可导出到 UE 的 Blueprint authoring unit。 |
| `at` | Blueprint asset path | 指定目标 Unreal package path。 |
| `component` | Blueprint 成员 | 声明组件实例。 |
| `var` | Blueprint 成员 / local var | 在 Blueprint body 中是成员变量，在 procedure body 中是局部变量。 |
| `event` | Blueprint procedure | 声明事件入口，语义上固定 `void`。 |
| `callable` | Blueprint procedure | 声明带执行语义的 Blueprint function。 |
| `pure` | Blueprint procedure | 声明 Blueprint pure function。 |

KMS-BP v0 允许的语句、参数和表达式关键字：

| 关键字 | 用途 | 说明 |
| --- | --- | --- |
| `if` | 条件语句 | `if (...) statement`。 |
| `else` | 条件分支 | 与 `if` 组合使用。 |
| `while` | 循环语句 | `while (...) statement`。 |
| `break` | 跳出循环 | 仅在循环上下文有意义。 |
| `continue` | 进入下一轮循环 | 仅在循环上下文有意义。 |
| `return` | 返回语句 | 可写 `return;` 或 `return expression;`。 |
| `out` | 参数修饰符 / argument marker | 用于输出参数。 |
| `ref` | 参数修饰符 | 用于引用参数。 |
| `typeof` | 表达式 | `typeof(Type)`。 |

内置类型和字面量关键字：

| 关键字 | 类别 | 说明 |
| --- | --- | --- |
| `bool` | built-in type | 布尔类型。 |
| `byte` | built-in type | 字节类型。 |
| `int` | built-in type | 整数类型。 |
| `float` | built-in type | 浮点类型。 |
| `string` | built-in type | 字符串类型。 |
| `void` | built-in type | 无返回值，只用于 procedure return type。 |
| `true` | bool literal | 布尔真值。 |
| `false` | bool literal | 布尔假值。 |

parser 可解析但 KMS-BP v0 semantic checker 会拒绝：

| 关键字 | parser 含义 | BP v0 状态 |
| --- | --- | --- |
| `for` | for loop | 拒绝，报告 `BannedStatement`。 |
| `switch` | switch statement | 拒绝，报告 `BannedStatement`。 |
| `case` | switch label / goto case | 随 `switch` / `goto` 拒绝。 |
| `default` | switch default label / goto default | 随 `switch` / `goto` 拒绝。 |
| `goto` | goto statement | 拒绝，报告 `BannedStatement`。 |
| `new` | new expression | 拒绝，报告 `BannedExpression`。 |

KMS-IR / legacy 兼容关键字：

| 关键字 | 用途 | KMS-BP 建议 |
| --- | --- | --- |
| `class` | legacy class declaration | 仅用于 legacy `[Blueprint] class ...` 兼容，新写用 `blueprint`。 |
| `struct` | struct declaration | import 或共享数据结构可用，Blueprint authoring 主体内慎用。 |
| `enum` | enum declaration | 可解析，用于共享枚举声明。 |
| `object` | legacy object declaration | 仅用于 legacy component/object 兼容，新写用 `component`。 |
| `public` | legacy modifier | KMS-BP v0 不推荐。 |
| `private` | legacy modifier | KMS-BP v0 不推荐。 |
| `protected` | legacy modifier | KMS-BP v0 不推荐。 |
| `sealed` | legacy modifier | KMS-BP v0 不推荐。 |
| `static` | legacy modifier | KMS-BP v0 不推荐。 |
| `virtual` | legacy modifier | KMS-BP v0 不推荐。 |
| `abstract` | legacy modifier | KMS-BP v0 不推荐。 |
| `override` | legacy modifier | KMS-BP v0 不推荐。 |
| `const` | legacy modifier | KMS-BP v0 不推荐。 |
| `local` | legacy modifier | KMS-BP v0 推荐用 procedure body 内的 `var` 声明局部变量。 |

lexer 已保留但 KMS-BP v0 没有 authoring 语义：

```text
package namespace using function global interface
```

这些词不能作为普通裸标识符使用；确实需要同名标识符时，应使用反引号转义标识符，例如 `` `using` ``。
