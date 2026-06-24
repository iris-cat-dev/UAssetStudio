# KMS 语言文档

状态：草案 v0.2  
最后更新：2026-06-24

KMS 现在明确分成两个 profile，文档也分开维护：

- [KMS-BP 语法说明书](./kms-bp.md)：面向手写 Blueprint authoring，目标是通过 CLI JSON bridge 和 UE 插件生成普通 `UBlueprint`、SCS component tree、K2 graph。
- [KMS-BP v1 语法设计草案](./kms-bp-v1.md)：下一阶段 BP authoring 设计，目标是补齐真实 K2 call generation、metadata、Construction Script、常用控制流、Dispatcher、Replication/RPC 等能力。
- [KMS-IR 语法说明书](./kms-ir.md)：面向 decompile、bytecode round-trip、patch、standalone compile，目标是保留 Unreal Kismet bytecode 的低层结构和二进制一致性。

## Profile 选择

工具链按源文件内容选择 profile：

- 顶层出现 `blueprint Name : Parent at "AssetPath" { ... }` 时，按 `KMS-BP` 处理。
- 顶层出现 legacy `[Blueprint(...)] class ...` 时，也按 `KMS-BP` 处理，并通过 normalizer 映射到 BP semantic model。
- 其他 KMS 文件默认按 `KMS-IR` 处理。

## 共同事实来源

- `KismetScript.Parser/KismetScript.g4`
- `KismetScript.Parser/KismetScriptASTParser.cs`
- `KismetScript.Syntax/Blueprint/BlueprintProfileNormalizer.cs`
- `KismetScript.Syntax/Blueprint/BlueprintProfileSemanticChecker.cs`
- `KismetScript.Syntax/Blueprint/BlueprintProfileExporter.cs`
- `KismetScript.Parser.Tests/Samples/V0/*.kms`
- `KismetScript.Parser.Tests/Samples/V1/*.kms`

## 文档设计参考

这套文档参考了几类成熟语言说明书：

- [The Rust Reference](https://doc.rust-lang.org/reference/)：reference 不等于入门教程，章节应能独立回答具体语法问题。
- [The TypeScript Handbook](https://www.typescriptlang.org/docs/handbook/intro.html)：先给日常开发者可读的解释，再在 reference 中写精确规则。
- [Kotlin Syntax and Grammar](https://kotlinlang.org/spec/syntax-and-grammar.html) 和 [Kotlin Grammar](https://kotlinlang.org/grammar/)：明确区分 lexical rule 和 syntax rule。
- [C# Language Specification Grammar](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/language-specification/grammar)：保留 grammar annex 作为实现和文档之间的契约。
- [ECMAScript Lexical Grammar](https://tc39.es/ecma262/multipage/ecmascript-language-lexical-grammar.html)：先讲 tokenization，再讲 syntactic grammar。

## 记号约定

两个说明书都使用同一套简化 EBNF：

```text
Rule        ::= AlternativeA | AlternativeB
Item?       ::= optional item
Item*       ::= zero or more
Item+       ::= one or more
'token'     ::= literal token
Identifier  ::= lexer token
```

说明：

- 大写名通常表示词法 token，例如 `Identifier`、`StringLiteral`。
- 小写名通常表示句法规则，例如 `statement`、`expression`。
- 文档中的 EBNF 是说明性摘要，实际 parser 以 `KismetScript.g4` 为准。
