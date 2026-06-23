# UAssetAPI.Validation - 资产验证库

用于验证 UAsset 文件结构完整性的独立库，依赖 [UAssetAPI](../UAssetAPI/UAssetAPI/UAssetAPI.csproj)。

## 功能

1. **FPackageIndex 验证** - 确保所有索引（ClassIndex, TemplateIndex, SuperIndex, OuterIndex）指向有效的导入或导出
2. **导入路径验证** - 检查导入路径是否指向游戏中实际存在的资产
3. **类引用验证** - 验证类引用的一致性

## 使用方法

### 1. 快速验证

```csharp
using UAssetAPI;
using UAssetAPI.Validation;

// 快速验证单个资产
var result = UAssetValidator.QuickValidate(
    "/path/to/asset.uasset",
    EngineVersion.VER_UE4_27,
    "/path/to/game/Content"  // 可选：游戏目录，用于验证导入
);

Console.WriteLine(result.GetSummary());
foreach (var error in result.Errors)
{
    Console.WriteLine(error);
}
```

### 2. 详细验证

```csharp
// 加载资产
var asset = new UAsset("/path/to/asset.uasset", EngineVersion.VER_UE4_27);

// 创建验证器（指定游戏目录以验证导入路径）
var validator = new UAssetValidator("/path/to/game/Content");
var result = validator.Validate(asset);

// 检查结果
if (result.IsValid)
{
    Console.WriteLine("✅ 资产验证通过！");
}
else
{
    Console.WriteLine($"❌ 发现 {result.Errors.Count} 个错误");
}
```

### 3. 使用扩展方法

```csharp
// 验证并打印结果
bool isValid = asset.ValidateAndPrint("/path/to/game/Content");

// 检查特定错误类型
if (result.HasInvalidIndexErrors())
{
    Console.WriteLine("存在无效索引错误");
}

if (result.HasMissingImportErrors())
{
    Console.WriteLine("存在缺失导入错误");
}
```

## CLI 使用

```bash
# 基本验证
dotnet run --project UAssetStudio.Cli -- validate /path/to/asset.uasset

# 带游戏目录验证
dotnet run --project UAssetStudio.Cli -- validate /path/to/asset.uasset -g /path/to/game/Content
```

## 验证错误类型

| 错误类型 | 说明 | 常见原因 |
|---------|------|---------|
| InvalidIndex | 索引指向不存在的位置 | templateIndex/superIndex 配置错误 |
| MissingImport | 导入的资产不存在 | 引用了游戏中不存在的资产 |
| NullClass | 导出项缺少类引用 | ClassIndex 为 null |
| LoadError | 无法加载资产 | 文件损坏或格式不支持 |

## 修复指南

### InvalidIndex 错误

**症状:**
```
❌ [InvalidIndex] Export[1]: TemplateIndex points to invalid import index -16
```

**原因:** `templateIndex` 指向了不存在的导入索引。

**修复:**
1. 检查实际导入数量
2. 将 `templateIndex` 改为有效值：
   - `1` = 指向第一个导出（通常是 Class）
   - `-1` = 指向第一个导入
   - `-2` = 指向第二个导入
   - ...以此类推

### MissingImport 错误

**症状:**
```
❌ [MissingImport] Import[3]: Import '/Game/Some/Path.Asset' not found in game assets
```

**原因:** 资产引用了游戏中不存在的路径。

**修复:**
1. 确认路径拼写正确
2. 确认被引用的资产存在于游戏目录
3. 如果引用的是自定义资产，需要同时打包该资产
