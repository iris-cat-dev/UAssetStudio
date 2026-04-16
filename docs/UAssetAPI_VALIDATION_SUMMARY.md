# UAssetAPI.Validation 库总结

## 创建的文件

### 1. 核心验证库
- `UAssetAPI/UAssetAPI/Validation/UAssetValidator.cs` - 验证器主类
- `UAssetAPI/UAssetAPI/Validation/ValidationExtensions.cs` - 扩展方法
- `UAssetAPI/UAssetAPI/Validation/README.md` - 使用文档

### 2. CLI 命令
- `UAssetStudio.Cli/CMD/ValidateCommand.cs` - validate 命令实现
- `UAssetStudio.Cli/Program.cs` - 更新以注册新命令

## 功能

### 1. FPackageIndex 验证
验证所有索引指向有效的位置：
- **ClassIndex** - 类引用
- **TemplateIndex** - 模板引用
- **SuperIndex** - 父类引用
- **OuterIndex** - 外部包引用
- **ClassWithin** (ClassExport) - 类内部引用
- **SuperStruct** (StructExport) - 父结构体引用

### 2. 导入路径验证
验证导入的资产路径在游戏中实际存在：
- 自动扫描游戏 Content 目录建立索引
- 支持 `/Game/` 和 `FSD/Content/` 路径格式
- 排除 `/Script/` 引擎内置类

### 3. 类引用验证
检查导出项的类引用一致性

## 使用方法

### CLI 命令
```bash
# 基本验证
dotnet run --project UAssetStudio.Cli -- validate <asset.uasset> --ue-version VER_UE4_27

# 带游戏目录验证
dotnet run --project UAssetStudio.Cli -- validate <asset.uasset> --ue-version VER_UE4_27 -g <game/Content>
```

### C# API
```csharp
using UAssetAPI;
using UAssetAPI.Validation;

// 快速验证
var result = UAssetValidator.QuickValidate(
    "/path/to/asset.uasset",
    EngineVersion.VER_UE4_27,
    "/path/to/game/Content"
);

// 详细验证
var asset = new UAsset("/path/to/asset.uasset", EngineVersion.VER_UE4_27);
var validator = new UAssetValidator("/path/to/game/Content");
var result = validator.Validate(asset);

// 检查结果
if (result.IsValid) {
    Console.WriteLine("验证通过！");
} else {
    foreach (var error in result.Errors) {
        Console.WriteLine(error);
    }
}
```

## 错误检测能力

### 已修复的错误类型
1. **InvalidIndex** - `templateIndex: -16` 指向不存在的导入
2. **MissingImport** - 引用了游戏中不存在的资产
3. **NullClass** - 导出项缺少类引用

### 测试验证
```bash
# 验证修复后的 mod 资产（PASSED）
✅ Asset validation passed with no issues!

# 验证原始游戏资产（PASSED）
✅ Asset validation passed with no issues!
```

## 关键修复

### TemplateIndex 修复
```json
// 错误
"templateIndex": -16  // 指向不存在的第16个导入

// 正确
"templateIndex": 1    // 指向第1个导出（Class）
```

### 导入路径修复
```json
// 错误 - 使用了不存在的自定义 PawnAffliction
"PawnAffliction": "PAF_HoverInvulnerable"

// 正确 - 使用游戏中已存在的
"PawnAffliction": "PAF_MarkedForDeath"
```

## 验证流程建议

1. **编译后验证** - 编译 KMS 后立即验证输出
2. **导入验证** - 提供游戏目录验证导入路径
3. **对比验证** - 与原始资产结构对比
4. **游戏测试** - 在游戏中测试加载

## 集成到工作流

```bash
#!/bin/bash
# mod_build.sh

ASSET="output/STE_HoverclockInvulnerable.uasset"
GAME_CONTENT="/path/to/game/Content"

# 1. 编译
dotnet run --project UAssetStudio.Cli -- compile STE_HoverclockInvulnerable.kms \
    --ue-version VER_UE4_27 --outdir output/

# 2. 验证
dotnet run --project UAssetStudio.Cli -- validate "$ASSET" \
    --ue-version VER_UE4_27 -g "$GAME_CONTENT"

if [ $? -ne 0 ]; then
    echo "验证失败！"
    exit 1
fi

echo "构建成功！"
```
