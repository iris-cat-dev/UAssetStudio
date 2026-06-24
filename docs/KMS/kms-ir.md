# KMS-IR 语法说明书

状态：草案 v0.2  
Profile：Kismet bytecode IR / round-trip  
最后更新：2026-06-24

KMS-IR 是 UAssetStudio 的底层 Kismet Script 表示。它服务于：

- `.uasset/.umap` decompile 到 `.kms`
- `.kms` compile 回 Kismet bytecode
- patch 和 standalone metadata compile
- verify round-trip
- 最大限度保留原始 Blueprint bytecode 结构

KMS-IR 的设计目标是“忠实、可逆、可编译”，不是“最适合人手写”。如果目标是新写 Blueprint，请使用 [KMS-BP](./kms-bp.md)。

## 1. Profile 边界

KMS-IR 默认 profile：

- 文件里没有顶层 `blueprint ...`。
- 文件里没有 legacy `[Blueprint(...)] class ...`。
- 源文件以 `class`、`struct`、`object`、type-first variable/function、enum、label 等低层构造为主。

注意：

- Parser 共享一套 grammar，因此 KMS-IR parser 也可能接受 KMS-BP 的某些 token。
- Compiler/linker 是否支持某个 AST 构造，是 KMS-IR 的后端问题。
- KMS-IR 不应被 formatter 自动重写成 KMS-BP。

## 2. 文件结构

```text
kmsIrFile ::= declaration*
```

常见顶层声明：

- import declaration
- class / struct
- object
- enum
- procedure
- variable
- label

## 3. 词法结构

KMS-IR 与 KMS-BP 共用 lexer。

### 3.1 注释

```kms
// line comment

/*
   block comment
*/
```

### 3.2 标识符

```kms
OpenAngle
ReceiveBeginPlay
`class`
```

普通标识符：

```text
Identifier ::= (Letter | '_') (Letter | '_' | Digit)*
```

反引号标识符用于保留字或特殊名称：

```kms
int `default` = 1;
```

### 3.3 字面量

布尔：

```kms
true
false
```

整数：

```kms
0
-42
0xFF
10u
10L
10uL
```

浮点：

```kms
90.0
90.0f
90.0d
1e3
```

字符串：

```kms
"Door ready"
"line\nnext"
```

注意：

- `CharLiteral` 词法存在，但 AST parser 未稳定支持，不建议依赖。

## 4. 类型

内置类型：

```text
bool byte int float string void
```

命名类型：

```kms
Actor
StaticMeshComponent
DoorConfig
Object
```

构造类型：

```kms
Object<StaticMesh>
Array<int>
int[]
byte[4]
```

语法：

```text
typeIdentifier ::=
    Identifier '<' typeIdentifier '>' arraySignifier?
  | BuiltinTypeIdentifier arraySignifier?
  | Identifier arraySignifier?

arraySignifier ::= '[' IntLiteral? ']'
```

限制：

- 当前 `typeIdentifier` 稳定支持单个类型参数。
- `double` literal 可解析，但 `double` 不是内置类型 token。

## 5. Import

```kms
from "/Game/Common/DoorTypes" import struct DoorConfig {
    int Mode;
}

from "/Game/Common" import {
    enum DoorState {
        Closed = 0,
        Open = 1,
    };
}
```

语法：

```text
importDeclaration ::=
    'from' StringLiteral 'import' declaration
  | 'from' StringLiteral 'import' '{' declaration* '}'
```

## 6. Attribute

KMS-IR 使用 C# 风格 attribute 记录 metadata：

```kms
[Edit]
[Category("Door")]
[BlueprintCallable]
[Event("BeginPlay")]
[Component, Attach = Root]
```

语法：

```text
attributeList ::= '[' attribute (',' attribute)* ']'
attribute     ::= Identifier (argumentList | '=' expression)?
```

支持形式：

```kms
[Flag]
[Name("Value")]
[Name = Value]
[A, B("x"), C = 1]
```

说明：

- Attribute 是否有后端语义取决于 compiler/linker 或 BP normalizer。
- legacy `[Blueprint(...)]` 会触发 KMS-BP profile，而不是普通 KMS-IR profile。

## 7. Class / Struct

```kms
class BP_Door : Actor {
    float OpenAngle = 90.0f;

    void ReceiveBeginPlay() {
        print("Door ready");
    }
}

struct DoorConfig {
    int Mode;
}
```

语法：

```text
classDeclaration ::=
    attributeList* modifier* ('class' | 'struct' | Identifier)
    Identifier (':' Identifier (',' Identifier)*)?
    '{' declaration* '}'

classForwardDeclaration ::=
    attributeList* modifier* ('class' | 'struct')
    Identifier (':' Identifier (',' Identifier)*)? ';'
```

修饰符：

```text
public private protected sealed static virtual const local out ref
abstract override
```

说明：

- Grammar 中 `(Class | Struct | Identifier)` 支持 decompiler 输出中的特殊 declaration kind。
- `interface` 是 lexer keyword，但当前 class declaration rule 不把它作为一等 class/struct 分支。

## 8. Object

`object` 表示对象或组件模板的低层属性块。

```kms
object Mesh : StaticMeshComponent {
    Object<StaticMesh> StaticMesh = asset<StaticMesh>("/Game/Props/SM_Door.SM_Door");
    byte Mobility = 1;
}
```

语法：

```text
objectDeclaration ::=
    attributeList* 'object' Identifier ':' Identifier
    '{' objectPropertyAssignment* '}'

objectPropertyAssignment ::=
    typeIdentifier Identifier '=' expression ';'
```

说明：

- KMS-IR `object` 使用 type-first property。
- KMS-BP `component` 使用 `PropertyName: Type = expression;` 或 `PropertyName = expression;`。
- 不要把两种属性语法混用。

## 9. Enum

```kms
enum DoorState {
    Closed = 0,
    Open = 1,
};
```

语法：

```text
enumDeclaration ::= 'enum' Identifier enumValueList ';'?
enumValueList   ::= '{' enumValue? (enumValue ',')* (enumValue ','?)? '}'
enumValue       ::= Identifier ('=' expression)?
```

## 10. Type-first Variable

```kms
float OpenAngle = 90.0f;
bool IsOpen = false;
Object<StaticMesh> StaticMesh = asset<StaticMesh>("/Game/Props/SM_Door.SM_Door");
```

语法：

```text
variableDeclaration ::=
    attributeList* modifier* typeIdentifier Identifier ('=' expression)? ';'
```

说明：

- 这是 KMS-IR 推荐变量写法。
- KMS-BP 推荐 `var Name: Type`。

## 11. Type-first Procedure

```kms
void ReceiveBeginPlay() {
    print("Door ready");
}

int Add(int A, int B);
```

语法：

```text
procedureDeclaration ::=
    attributeList* modifier* typeIdentifier Identifier parameterList compoundStatement
  | attributeList* modifier* typeIdentifier Identifier parameterList ';'
```

参数：

```kms
int Add(int A, int B) {
    return A + B;
}

void GetValue(out int Result) {
    Result = 1;
}
```

语法：

```text
parameterList ::= '(' parameter? (',' parameter)* ')'

parameter ::=
    attributeList? modifier* typeIdentifier Identifier arraySignifier?
  | attributeList? modifier* Identifier ':' typeIdentifier arraySignifier?
  | '...'
```

说明：

- KMS-IR 自然写法是 `Type Name`。
- `Name: Type` 是为 KMS-BP authoring 加入的兼容 parser 能力。

## 12. 语句

KMS-IR parser 支持：

```text
statement ::=
    ';'
  | compoundStatement
  | declarationStatement
  | bpVariableDeclarationStatement
  | expression ';'
  | ifStatement
  | forStatement
  | whileStatement
  | breakStatement
  | continueStatement
  | returnStatement
  | gotoStatement
  | switchStatement
```

### 12.1 空语句和代码块

```kms
;

{
    int Local = 1;
}
```

### 12.2 声明语句

```kms
int Local = 1;
void LocalFunction() {
}
```

### 12.3 表达式语句

```kms
print("Door ready");
Value += 1;
```

### 12.4 if

```kms
if (Value > 0) {
    print("positive");
} else {
    print("other");
}
```

语法摘要：

```text
ifStatement ::= 'if' '(' expression ')' statement ('else' statement)*
```

注意：

- Grammar 允许多个 `else statement`，新写代码建议只写一个。

### 12.5 while

```kms
while (Value < 10) {
    Value += 1;
}
```

语法摘要：

```text
whileStatement ::= 'while' expression statement
```

### 12.6 for

```kms
for (int I = 0; I < 10; I++) {
    print(I);
}
```

语法摘要：

```text
forStatement ::= 'for' '(' statement expression ';' expression ')' statement
```

说明：

- Parser 接受。
- 是否能稳定编译回 bytecode 取决于 KMS-IR compiler/linker 支持。

### 12.7 break / continue / return

```kms
break;
continue;
return;
return Value;
```

### 12.8 goto 和 label

```kms
Start:
goto Start;
goto case 1;
goto case default;
```

语法：

```text
labelDeclaration ::= Identifier ':'

gotoStatement ::=
    'goto' Identifier ';'
  | 'goto' 'case' expression ';'
  | 'goto' 'case' 'default' ';'
```

说明：

- 主要用于底层控制流或 decompiler 输出。
- KMS-BP semantic checker 会拒绝。

### 12.9 switch

```kms
switch (Value) {
case 0:
    print("zero");
    break;
default:
    break;
}
```

语法：

```text
switchStatement ::= 'switch' '(' expression ')' '{' switchLabel+ '}'
switchLabel     ::= 'case' expression ':' statement*
                  | 'default' ':' statement*
```

## 13. 表达式

KMS-IR 使用和 KMS-BP 相同的 parser expression grammar，但允许底层 intrinsic。

### 13.1 Primary

```kms
true
123
90.0f
"Door"
Identifier
```

### 13.2 调用

```kms
print("Door ready")
Context(...)
FinalFunction(...)
asset<StaticMesh>("/Game/Props/SM_Door.SM_Door")
```

语法：

```text
callExpression ::= Identifier typeArgumentList? argumentList
typeArgumentList ::= '<' typeIdentifier (',' typeIdentifier)* '>'
```

说明：

- KMS-IR 可以出现低层 intrinsic calls。
- KMS-BP 会拒绝这些 intrinsic。

### 13.3 成员访问和下标

```kms
Obj.Property
Obj.Method()
Ptr->Field
ArrayValue[0]
```

### 13.4 Cast / typeof

```kms
(float)Value
typeof(float)
```

### 13.5 new

```kms
new DoorConfig { 1, 2, 3 }
```

说明：

- Parser 接受。
- KMS-IR 是否可编译取决于后端。
- KMS-BP 会拒绝。

### 13.6 初始化列表和对象字面量

```kms
{ 1, 2, 3 }
[1, 2, 3]
{ Mode: "Door", Count: 3 }
```

对象字面量 key：

```text
objectKey ::= Identifier | StringLiteral | constant | objectLiteral | '{}'
```

### 13.7 运算符

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

- `<<` / `>>` 相关 AST 类型存在，但当前 grammar 中 shift expression 被注释掉。

## 14. Legacy Blueprint attribute bridge

这种写法虽然是 attribute-heavy，但 profile detector 会把它归入 KMS-BP normalizer：

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

边界：

- 作为兼容输入，它属于 BP semantic model。
- 作为 decompiler 输出风格，它保留 KMS-IR 的 attribute/type-first/object 外观。
- 新写 Blueprint authoring 时不要继续扩展这套写法，使用 `blueprint/component/var/event/callable/pure`。

## 15. Round-trip 和修改原则

KMS-IR 的首要目标是 round-trip 稳定：

- 不随意删除 attribute。
- 不随意重排 object property。
- 不把 type-first 改成 BP-style `var Name: Type`。
- 不把 `object` 改成 `component`，除非明确切换到 KMS-BP。
- 低层 intrinsic 的顺序和参数应尽量保持原样。
- 修改后优先跑 verify 或相关 patching tests。

常用命令：

```bash
dotnet run --project UAssetStudio.Cli -- decompile <asset> --mappings <usmap> --ue-version VER_UE5_6 --outdir <dir> --meta
dotnet run --project UAssetStudio.Cli -- compile <script.kms> --asset <original.uasset> --mappings <usmap> --ue-version VER_UE5_6 --outdir <dir>
dotnet run --project UAssetStudio.Cli -- verify <asset> --mappings <usmap> --ue-version VER_UE5_6 --outdir <dir> --meta
```

## 16. 精简形式语法

```text
compilationUnit
    ::= declaration*

declaration
    ::= importDeclaration
     |  classDeclaration
     |  objectDeclaration
     |  enumDeclaration
     |  procedureDeclaration
     |  variableDeclaration
     |  labelDeclaration

classDeclaration
    ::= attributeList* modifier* ('class' | 'struct' | Identifier)
        Identifier (':' Identifier (',' Identifier)*)?
        '{' declaration* '}'
     |  attributeList* modifier* ('class' | 'struct')
        Identifier (':' Identifier (',' Identifier)*)? ';'

objectDeclaration
    ::= attributeList* 'object' Identifier ':' Identifier
        '{' objectPropertyAssignment* '}'

objectPropertyAssignment
    ::= typeIdentifier Identifier '=' expression ';'

enumDeclaration
    ::= 'enum' Identifier enumValueList ';'?

procedureDeclaration
    ::= attributeList* modifier* typeIdentifier Identifier parameterList compoundStatement
     |  attributeList* modifier* typeIdentifier Identifier parameterList ';'

variableDeclaration
    ::= attributeList* modifier* typeIdentifier Identifier ('=' expression)? ';'

statement
    ::= ';'
     |  compoundStatement
     |  declaration
     |  expression ';'
     |  ifStatement
     |  forStatement
     |  whileStatement
     |  breakStatement
     |  continueStatement
     |  returnStatement
     |  gotoStatement
     |  switchStatement

expression
    ::= primary
     |  '(' expression ')'
     |  Identifier typeArgumentList? argumentList
     |  Identifier '[' expression ']'
     |  expression ('.' | '->') expression
     |  '(' typeIdentifier ')' expression
     |  'typeof' '(' typeIdentifier ')'
     |  'new' typeIdentifier? arraySignifier? '{' expressionList? '}'
     |  expression binaryOperator expression
     |  unaryOperator expression
     |  expression postfixOperator
     |  expression '?' expression ':' expression
     |  expression assignmentOperator expression
     |  initializerList
     |  objectLiteral
```

## 17. 已知限制

- `CharLiteral` 词法存在，但 AST parser 未稳定支持。
- `<<` / `>>` shift expression 当前 grammar 未启用。
- `double` literal 存在，但 `double` 不是内置类型 token。
- `typeIdentifier` 当前只稳定支持单类型参数。
- `interface`、`function`、`global`、`namespace`、`using` 等 token 存在，但不是所有 token 都有完整 parser/backend 语义。
- Parser 接受不代表 compiler/linker 一定支持。KMS-IR 文档描述的是语法 surface，实际 round-trip 能力以测试为准。
