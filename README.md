# UAssetStudio

UAssetStudio 是一个 .NET 9.0 工具链，用于分析、反编译、编译、验证和补丁化 Unreal Engine 资产（`.uasset` / `.umap`）。项目重点支持 Blueprint / Kismet 字节码分析和可复现 mod 工作流，当前主要面向 UE 4.27 与 UE 5.6，并覆盖 UE 4.13-5.6+ 的版本常量。

## 核心功能

1. **控制流图（CFG）生成**：从 Blueprint 字节码生成 Graphviz `.dot` 和文本摘要。
2. **Kismet 反编译**：将 `.uasset/.umap` 转换为可读的 `.kms`（Kismet Script）。
3. **KMS 编译**：将 `.kms` 链接回资产，支持完整重编译、metadata standalone 编译，以及 `--only-func` 单函数外科补丁。
4. **验证与校验**：验证 decompile -> compile -> link -> write 的往返结果，并检查资产结构完整性。
5. **JSON 检视**：导出资产结构 JSON；JSON import 仅建议作为兜底路径。
6. **Patching API**：通过 `AssetPatchSession.Load -> ReplaceFunctionBytecode / SetProperty -> Save` 做代码优先、内存内修改。
7. **Mods Recipe**：用 C# Recipe 加 checked-in KMS 固化可复现资产修改，例如 `rogue-class-repeat-select`。
8. **AssetRegistry 解析**：读取 `AssetRegistry.bin` 并输出摘要或结构化 JSON。

## 解决方案架构

```text
UAssetStudio.sln
├── UAssetStudio.Cli/                    # 主 CLI 入口点
├── UAssetStudio.Patching/               # 外科字节码补丁和属性修改 API
├── UAssetStudio.Patching.Tests/         # Patching 回归测试
├── UAssetStudio.Mods/                   # 可复现 mod recipe 与 checked-in KMS
├── UAssetAPI/                           # 核心资产读写库（git submodule）
│   ├── UAssetAPI/                       # 主库
│   ├── UAssetAPI.Tests/                 # 单元测试
│   └── UAssetAPI.Benchmark/             # 性能测试
├── KismetScript.Compiler/               # 基于 ANTLR 的 Kismet 脚本编译器
├── KismetScript.Decompiler/             # 资产反编译逻辑
├── KismetScript.Linker/                 # 资产链接和最终构建
├── KismetScript.Parser/                 # Kismet Script 语法解析
├── KismetScript.Syntax/                 # AST 和语法树定义
├── KismetScript.Utilities/              # 共享工具、metadata 模型
├── KismetAnalyzer.CFG/                  # 控制流图生成
├── AssetRegistry.Serializer/            # AssetRegistry.bin 序列化
└── UAsset.Localization/                 # 本地化支持
```

## 环境要求

- `.NET SDK 9.0+`
- UE5+ unversioned property 资产通常需要 `.usmap` 映射文件
- UE4 资产通常可以不传 `.usmap`，但具体取决于资产是否使用 unversioned properties

## 构建项目

```bash
# 构建整个解决方案
dotnet build

# 构建特定配置
dotnet build --configuration Release
dotnet build --configuration DebugTracing

# 运行所有测试
dotnet test
dotnet test --verbosity normal

# 运行重点测试项目
dotnet test UAssetAPI/UAssetAPI.Tests/UAssetAPI.Tests.csproj
dotnet test UAssetStudio.Patching.Tests/UAssetStudio.Patching.Tests.csproj
```

## CLI 总览

开发时从仓库根目录使用：

```bash
dotnet run --project UAssetStudio.Cli -- <command> [args] [options]
```

发布为可执行文件后，参数保持一致，只替换调用器：

```bash
./UAssetStudio.Cli <command> [args] [options]
```

Windows x64 单文件发布物通常可直接执行：

```bash
UAssetStudio.Cli.exe <command> [args] [options]
```

### 公共选项

- `--ue-version <ver>`：Unreal Engine 版本，默认 `VER_UE4_27`。常用值包括 `VER_UE4_27`、`VER_UE5_6`。
- `--mappings <path>`：`.usmap` 文件路径，用于解析 unversioned properties。UE5+ 资产通常必须提供。
- `--json`：全局选项。命令成功解析并进入 handler 后，输出单个机器可解析的 `CommandResult` JSON 到 stdout；解析阶段错误仍遵循 System.CommandLine 行为。
- `--outdir <dir>` / `--out <path-or-dir>`：输出参数按命令区分。目录型命令通常接受 `--outdir`，并把 `--out` 作为别名；文件型命令通常接受 `--out`，并把 `--outdir` 作为兼容别名。

### JSON 输出与退出码

所有已注册命令都支持 `--json`。命令参数成功解析并进入 handler 后，JSON 模式会把业务错误和运行错误包装为单个 `CommandResult`，并保证 stdout 只输出该对象；人类可读错误写到 stderr。

未知命令、缺必填参数、非法枚举等解析阶段错误发生在 handler 之前，不经过 `CliOutput.Run` 包装，仍按 System.CommandLine 的默认行为输出，且不一定是 `CommandResult`。脚本应优先保证参数可解析，再按 `CommandResult` 处理业务结果。

```json
{
  "Command": "decompile",
  "Status": "ok",
  "Inputs": { "asset": "input.uasset", "ueVersion": "VER_UE5_6" },
  "Outputs": ["output/input.kms"],
  "Warnings": [],
  "Error": { "Type": "FileNotFound", "Message": "...", "Hint": "..." },
  "Data": { "commandSpecific": true }
}
```

退出码：

| Code | Status | 含义 |
| ---- | ------ | ---- |
| `0` | `ok` | 命令成功 |
| `1` | `error` | 参数、文件、加载、解析或 IO 错误 |
| `2` | `failed` | 判定失败，例如 `validate` 未通过或 `verify` 不等价 |

解析阶段错误通常也是非零退出，但输出格式不保证是 `CommandResult`。

Agent 使用建议：

- 对可解析的命令总是传 `--json`，按 `Status` 或退出码分支，不解析人类文本。
- 使用 `Outputs` 读取实际生成文件，不要猜测路径。
- 读取 `Data` 获取命令特定结果，例如 `compile --only-func` 的 `patchedFunctions`、`validate` 的 `errors/warnings`、`verify` 的 `verified`。

## CLI 命令

### `cfg`

用途：生成资产中函数字节码的 CFG 文本摘要和 Graphviz DOT。

语法：

```bash
dotnet run --project UAssetStudio.Cli -- cfg <asset.uasset|asset.umap> \
  --ue-version VER_UE5_6 \
  --mappings <mappings.usmap> \
  --outdir <dir> \
  --json
```

常用示例：

```bash
dotnet run --project UAssetStudio.Cli -- cfg \
  /path/to/WPN_ZipLineGun.uasset \
  --mappings /path/to/DRG_RC_Mappings.usmap \
  --ue-version VER_UE5_6 \
  --outdir ./output \
  --json
```

输出：

- `<AssetName>.txt`：指令级文本摘要。
- `<AssetName>.dot`：Graphviz 控制流图。
- JSON `Outputs` 包含 `.txt` 和 `.dot` 路径；当前没有额外 `Data`。

渲染 DOT：

```bash
dot -Tpng output/WPN_ZipLineGun.dot -o output/WPN_ZipLineGun.png
dot -Tsvg output/WPN_ZipLineGun.dot -o output/WPN_ZipLineGun.svg
```

### `decompile`

用途：把 `.uasset/.umap` 反编译为 `.kms`。加 `--meta` 时额外生成 `.kms.meta`，用于 standalone 编译路径。

语法：

```bash
dotnet run --project UAssetStudio.Cli -- decompile <asset.uasset|asset.umap> \
  --ue-version VER_UE5_6 \
  --mappings <mappings.usmap> \
  --outdir <dir> \
  [--meta] \
  --json
```

常用示例：

```bash
dotnet run --project UAssetStudio.Cli -- decompile \
  /path/to/WPN_ZipLineGun.uasset \
  --mappings /path/to/DRG_RC_Mappings.usmap \
  --ue-version VER_UE5_6 \
  --outdir ./output \
  --meta \
  --json
```

输出：

- `<AssetName>.kms`：Kismet Script。
- `<AssetName>.kms.meta`：metadata，仅在传 `--meta` 时生成。
- JSON `Outputs` 包含生成文件路径；当前没有额外 `Data`。

### `compile`

用途：把 `.kms` 编译回 `.uasset`。此命令有三种模式：完整编译、standalone metadata 编译、单函数外科补丁。

#### 完整编译

完整编译会根据原始资产或 metadata 创建新资产。对复杂继承 Blueprint 不建议作为默认修改路径。

```bash
dotnet run --project UAssetStudio.Cli -- compile <script.kms> \
  --asset <original.uasset> \
  --ue-version VER_UE5_6 \
  --mappings <mappings.usmap> \
  --out <patched.uasset> \
  --json
```

如果不传 `--out`，默认输出为 `<script-name>.new.uasset`；也可用 `--outdir <dir>` 指定输出目录。

JSON `Data`：

```json
{ "mode": "full" }
```

#### Standalone metadata 编译

先反编译并生成 metadata：

```bash
dotnet run --project UAssetStudio.Cli -- decompile <asset.uasset> \
  --ue-version VER_UE5_6 \
  --mappings <mappings.usmap> \
  --outdir ./work \
  --meta
```

再用 `.kms.meta` 编译，不依赖原始资产：

```bash
dotnet run --project UAssetStudio.Cli -- compile ./work/Asset.kms \
  --meta ./work/Asset.kms.meta \
  --ue-version VER_UE5_6 \
  --out ./work/Asset.new.uasset \
  --json
```

如果 `script.kms.meta` 存在且找不到原始资产，CLI 会自动使用 metadata 路径。

#### `--only-func` 外科补丁

推荐用于逻辑/字节码修改。它会编译 `.kms`，但只替换指定函数的字节码；其他函数和默认属性从原始资产恢复，避免整文件重编译破坏无关函数。

```bash
dotnet run --project UAssetStudio.Cli -- compile <edited.kms> \
  --asset <original.uasset> \
  --ue-version VER_UE5_6 \
  --mappings <mappings.usmap> \
  --only-func CanSwitchToCharacter \
  --out <patched.uasset> \
  --json
```

`--only-func` 可重复或一次传多个函数名：

```bash
dotnet run --project UAssetStudio.Cli -- compile edited.kms \
  --asset original.uasset \
  --only-func Foo --only-func Bar \
  --out patched.uasset
```

约束：

- 必须提供可读取的原始 `--asset`。
- 与 standalone metadata 模式互斥。
- 目标函数必须存在于原始资产中。

JSON `Data`：

```json
{
  "mode": "surgical",
  "patchedFunctions": ["CanSwitchToCharacter"]
}
```

### `json`

用途：在二进制资产和 JSON 之间转换。推荐主要用于结构检视，不推荐作为复杂 UE5 继承 Blueprint 的主要编辑路径。

导出资产为 JSON：

```bash
dotnet run --project UAssetStudio.Cli -- json <asset.uasset|asset.umap> \
  --ue-version VER_UE5_6 \
  --mappings <mappings.usmap> \
  --out ./output/asset.json \
  --json
```

从 JSON 写回资产：

```bash
dotnet run --project UAssetStudio.Cli -- json <asset.json> \
  --ue-version VER_UE5_6 \
  --mappings <mappings.usmap> \
  --asset <original.uasset> \
  --out ./output/asset.uasset \
  --json
```

说明：

- `--out` 是输出文件路径；`--outdir` 在此命令中只是兼容别名，仍按输出文件解释。
- JSON import 遇到继承 Blueprint schema 缺失时，应传 `--asset <original.uasset>`，让 CLI 借用读取原始二进制资产时收集到的 schema。
- 对数值、默认属性、数组等持久修改，优先使用 `UAssetStudio.Patching` 或 `mods run`。
- JSON `Outputs` 包含生成的 `.json` 或 `.uasset` 路径；当前没有额外 `Data`。

### `verify`

用途：执行 decompile -> compile -> link -> write，并验证往返结果。

语法：

```bash
dotnet run --project UAssetStudio.Cli -- verify <asset.uasset|asset.umap> \
  --ue-version VER_UE5_6 \
  --mappings <mappings.usmap> \
  --outdir <dir> \
  [--meta] \
  --json
```

常用示例：

```bash
dotnet run --project UAssetStudio.Cli -- verify \
  /path/to/WPN_ZipLineGun.uasset \
  --mappings /path/to/DRG_RC_Mappings.usmap \
  --ue-version VER_UE5_6 \
  --outdir ./verify-output \
  --json
```

输出：

- `<AssetName>.kms`：反编译结果。
- `<AssetName>.kms.meta`：仅 `--meta` 时生成。
- `<AssetName>.new.uasset`：重新写出的资产。
- JSON `Data` 成功时为 `{ "mode": "binary", "verified": true }`；`--meta` 时为 `{ "mode": "structural", "verified": true }`。
- 验证不通过时 `Status` 为 `failed`，退出码为 `2`。

### `validate`

用途：检查资产结构完整性，例如 FPackageIndex、类引用，以及可选的 import 文件存在性。

语法：

```bash
dotnet run --project UAssetStudio.Cli -- validate <asset.uasset|asset.umap> \
  --ue-version VER_UE5_6 \
  --mappings <mappings.usmap> \
  [--game-content <Game/Content>] \
  --json
```

常用示例：

```bash
dotnet run --project UAssetStudio.Cli -- validate \
  /path/to/BP_Player.uasset \
  --mappings /path/to/DRG_RC_Mappings.usmap \
  --ue-version VER_UE5_6 \
  --game-content /path/to/Game/Content \
  --json
```

JSON `Data`：

```json
{
  "isValid": true,
  "errorCount": 0,
  "warningCount": 0,
  "errors": [],
  "warnings": []
}
```

校验失败时 `Status` 为 `failed`，退出码为 `2`。

### `mods list`

用途：列出内置的可复现 mod recipe。

```bash
dotnet run --project UAssetStudio.Cli -- mods list --json
```

JSON `Command` 为 `mods.list`，`Data` 是 mod 列表，每项包含 `name`、`description`、`directory`。当前示例 mod：

- `rogue-class-repeat-select`：外科替换 `ITM_Wardrobe_ClassSelector::CanSwitchToCharacter`，允许重复选择同一职业。

### `mods run`

用途：运行 C# mod recipe，从原始游戏资产目录读取输入，并把补丁资产写到输出根目录。Recipe 可组合 `ReplaceFunctionBytecode` 和 `SetProperty`。

语法：

```bash
dotnet run --project UAssetStudio.Cli -- mods run <mod-name> \
  --source <original-game-root> \
  --out <patched-output-root> \
  --ue-version VER_UE5_6 \
  --mappings <mappings.usmap> \
  [--mods-dir <mods-root>] \
  --json
```

常用示例：

```bash
dotnet run --project UAssetStudio.Cli -- mods run rogue-class-repeat-select \
  --source /path/to/Game \
  --out ./patched \
  --mappings /path/to/DRG_RC_Mappings.usmap \
  --ue-version VER_UE5_6 \
  --json
```

说明：

- `--source` 是原始游戏资产根目录。
- `--out` / `--outdir` 是补丁资产输出根目录，必填。
- `--mods-dir` 默认优先使用当前工作目录下的 `UAssetStudio.Mods`，否则使用可执行文件所在目录。
- JSON `Command` 为 `mods.run`，`Data` 包含 `mod`、`modFilesDir`、`outputs`；`Outputs` 也会列出所有生成文件。

### `asset-registry`

用途：解析 `AssetRegistry.bin` 并输出摘要。

语法：

```bash
dotnet run --project UAssetStudio.Cli -- asset-registry \
  --path <AssetRegistry.bin> \
  --json
```

建议总是显式传 `--path <AssetRegistry.bin>`。如果不传，当前源码默认尝试相对路径 `script/AssetRegistry.bin`；这个默认只是兼容行为，不能假设仓库内一定存在该文件。

JSON `Data` 包含：

- `header`：文件头字节摘要。
- `unknown`：当前解析出的 unknown 字段。
- `stringTableSize`：字符串表大小。
- `entries`：资产条目数。
- `sample`：最多 5 条样例，包含 `objectPath`、`packageName`、`assetClass`、`tags`、`chunks`。

## 推荐资产修改工作流

### 逻辑或字节码改动

1. 使用 `decompile` 生成 `.kms`。
2. 只编辑目标函数。
3. 使用 `compile --only-func <Name>` 对原始资产做外科补丁。
4. 修改稳定后，将 `.kms` 和调用逻辑沉淀为 `UAssetStudio.Mods` 下的 C# Recipe。

示例：

```bash
dotnet run --project UAssetStudio.Cli -- decompile original.uasset \
  --ue-version VER_UE5_6 --mappings mappings.usmap --outdir ./work

dotnet run --project UAssetStudio.Cli -- compile ./work/original.kms \
  --asset original.uasset \
  --ue-version VER_UE5_6 --mappings mappings.usmap \
  --only-func TargetFunction \
  --out patched.uasset \
  --json
```

### 数值、默认属性或数组改动

使用 `UAssetStudio.Patching` 的内存内修改路径，或者写成 `mods run` Recipe。核心 API：

```csharp
AssetPatchSession
    .Load(assetPath, EngineVersion.VER_UE5_6, mappingsPath)
    .SetProperty("Default__BP_Item_C", "Config.Items[0].Count", 3)
    .Save(outputPath);
```

复杂编辑可以直接使用 `AssetPatchSession.Asset` 暴露的 UAssetAPI 对象模型；简单叶子属性可用 `SetProperty`，支持 `float/double/int/int64/bool/byte/string/name/enum` 等常见类型。

### JSON 的定位

JSON 适合检视结构、定位属性路径和做小范围实验。复杂 UE5 继承 Blueprint 的主要编辑路径应使用 Patching/Mods 的 `load -> modify in memory -> write`，因为二进制读取路径会收集 Blueprint 与父类 schema。JSON import 作为兜底路径时请传 `--asset <original.uasset>`。

## 重要注意

- **复杂 Blueprint 避免整文件 KMS 重编译**：大型 Ubergraph、继承蓝图或反编译有报错的资产，整文件重编译可能破坏无关函数字节码。优先使用 `compile --only-func`。
- **外科补丁保护未修改函数**：`ReplaceFunctionBytecode` 会恢复非目标函数的原始 bytecode/raw bytecode，并恢复默认属性，适合只改一个或少数函数。
- **UE5 需要 usmap**：UE5+ 的 unversioned properties 通常必须提供 `.usmap`。缺失 mappings 可能导致属性解析、JSON import 或写回失败。
- **二进制等价性很重要**：用于 modding 的资产应尽量保持未修改部分二进制等价。改动序列化或链接逻辑后，应使用 `verify`、相关测试和必要的二进制对比确认行为。
- **JSON import 不是默认编辑方案**：尤其是继承 Blueprint，缺父类 schema 时容易失败或丢失上下文。默认属性修改请优先使用 `SetProperty` 或 mod recipe。

## 测试脚本

仓库脚本目录是 `scripts/`：

```bash
# 验证测试
./scripts/test_verify_5.6.sh
./scripts/test_verify_4.27.sh
./scripts/test_batch_verify_4.27.sh
./scripts/test_batch_verify_roguecore_5.6_weapons.sh

# 编译、反编译、CFG、JSON 测试
./scripts/test_compile_5.6.sh
./scripts/test_compile_4.27.sh
./scripts/test_decompile_5.6.sh
./scripts/test_decompile_4.27.sh
./scripts/test_gen_dot_5.6.sh
./scripts/test_json_4.27.sh

# Windows 批处理示例
./scripts/windows/decompile.bat
./scripts/windows/compile.bat
./scripts/windows/cfg.bat
./scripts/windows/verify.bat
```

## 关键入口点

- `UAssetStudio.Cli/Program.cs`：CLI 根命令和命令注册。
- `UAssetStudio.Cli/CMD/*.cs`：各子命令实现、JSON 输出和退出码。
- `UAssetStudio.Patching/AssetPatchSession.cs`：外科字节码补丁和 `SetProperty`。
- `UAssetStudio.Mods/IAssetMod.cs`：mod recipe 接口和 `PatchContext`。
- `UAssetStudio.Mods/ModRegistry.cs`：mod 发现与列表。
- `KismetScript.Decompiler/KismetDecompiler.cs`：KMS 反编译入口。
- `KismetScript.Linker/UAssetLinker.cs`：KMS 链接回资产。
- `UAssetAPI/UAssetAPI/UAsset.cs`：资产加载、写回和 JSON 序列化。
- `UAssetAPI/UAssetAPI/Validation/UAssetValidator.cs`：资产结构校验。
