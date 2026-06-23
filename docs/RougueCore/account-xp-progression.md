# RogueCore 账号 XP / 等级进度配置

> 游戏资源路径：`/Users/bytedance/Project/RogueCore`  
> Mappings：`maps/RogueCore-5.6.1-143055+main-7f7cc36f_by_Iris.usmap`  
> UE 版本：`VER_UE5_6`  
> 分析产物：`GD_BXE_ProgressionSettings.uasset.json`（CLI `json` 导出）

## 结论

修改「升级所需经验」和「等级上限」，核心资产是：

```text
Content/Game/GameData/BXESettings/Progression/GD_BXE_ProgressionSettings.uasset
```

它不是 Blueprint，而是 **DataAsset**，类为 native C++：

```text
/Script/RogueCore.BXEProgressionSettings
```

- **所需经验**：改 `Levels[*].RequiredXP`
- **等级上限**：由 `Levels` 数组长度决定；当前版本为 **28 级**（索引 `0`–`27`）
- **数据流向**：数值保存在 `.uasset` → UE 加载为 C++ `UObject` 实例 → 升级 / 解锁逻辑从内存对象读取

因此：**改资源包 = 静态 mod**；**运行中实时改 = 需要 UE4SS / native hook**，不能只靠替换已加载会话里的文件。

---

## 与相关系统的区分

修改前务必区分以下概念（详见各文档）：

| 概念 | 实际含义 | 配置位置 |
|------|----------|----------|
| **账号 XP / 等级** | 任务内收集 Expenite 后的等级与解锁进度 | `GD_BXE_ProgressionSettings`（本文） |
| **局外 Enhancement 升级树** | Space Rig 花费资源点的永久强化 | `RewardTree_ClosedAlpha_ver4-CheapGates`（见 [`trigger-discipline-enhancement.md`](./trigger-discipline-enhancement.md)） |
| **Hostile Reading** | 任务内左上角敌意圆环 / 阶段计时 | `BXE_StageDifficultyProgression_2Step`（见 [`extraction-countdown.md`](./extraction-countdown.md)） |
| **任务难度** | 敌人强度、阶段修正 | `DFC_BXE_*`、`StageDifficulty_*`（见 [`difficulty.md`](./difficulty.md)） |

`GD_BXE_ProgressionSettings` 同时绑定了解锁池标签与稀有度权重，影响武器箱、手雷箱、谈判池等内容的解锁节奏（见 [`weapon-pool.md`](./weapon-pool.md)、[`negotiation-option-count.md`](./negotiation-option-count.md)）。

---

## 资产结构

### 类型

| 项目 | 值 |
|------|-----|
| 资产路径 | `/Game/Game/GameData/BXESettings/Progression/GD_BXE_ProgressionSettings` |
| C++ 类 | `BXEProgressionSettings` |
| 资产类型 | DataAsset（`NormalExport`，非 BlueprintGeneratedClass） |
| 修改方式 | JSON 导出/导入 或 `UAssetStudio.Mods` 结构化 Patch；**不适合** KMS 蓝图 compile |

### 顶层字段

| 字段 | 类型 | 说明 |
|------|------|------|
| `StartInventory` | `BXEInventoryList` | 起始物品列表 |
| `Levels` | `Array<BXEProgressionLevel>` | **等级表**：每级所需 XP 与可选解锁 |
| `SoloDroneCollection` | `BXEUnlockPool` | 指向 `UP_SoloDroneUnlocks` |
| `CollectionTags` | `Array<UnlockCollectionTag>` | 全局解锁池标签注册表 |
| `RarityWeights` | `RarityWeightsSelection` | 各解锁类型对应的稀有度权重表 |

### `BXEProgressionLevel` 结构

每一级数组元素包含：

| 字段 | 类型 | 说明 |
|------|------|------|
| `RequiredXP` | `int` | 升到该级所需经验 |
| `CollectionTag` | `UnlockCollectionTag` | 可选：该级绑定的解锁池标签 |
| `AutomaticallyUnlocked` | `Array<Unlock>` | 可选：到达该级自动解锁的条目 |

当前版本中，全部 28 级的 `CollectionTag` 均为空（`0`），`AutomaticallyUnlocked` 均为空数组；解锁节奏主要由全局 `CollectionTags` + 等级索引 + C++ 逻辑共同决定。

### `CollectionTags` 注册的池标签

```text
UCT_AbilityUpgrades
UCT_Artifacts
UCT_Equipment
UCT_HeavyWeapons
UCT_NegotiatedUnlocks
UCT_PotentExpenite
UCT_StartingGrenades
UCT_TraversalTools_StartingRoom
UCT_WeaponsInitialPool
UCT_Workbench
UCT_Workbench_Weapon
```

### `RarityWeights` 绑定

| `ERarityWeightType` | 权重资产 |
|---------------------|----------|
| `Weapons` | `RW_Weapons` |
| `Equipment` | `RW_WeaponsAndEquipment` |
| `Grenades` | `RW_WeaponsAndEquipment`（与 Equipment 同引用） |
| `Jumble` | `RW_Jumble` |
| `WeaponsWorkbench` | `RW_WorkbenchWeapons` |
| 默认 | `RW_Default` |

---

## 当前 XP 曲线（28 级）

| 等级 | RequiredXP | 等级 | RequiredXP |
|------|------------|------|------------|
| 1 | 120 | 15 | 275 |
| 2 | 130 | 16 | 290 |
| 3 | 140 | 17 | 305 |
| 4 | 150 | 18 | 320 |
| 5 | 160 | 19 | 335 |
| 6 | 170 | 20 | 350 |
| 7 | 180 | 21 | 375 |
| 8 | 190 | 22 | 400 |
| 9 | 200 | 23 | 425 |
| 10 | 210 | 24 | 450 |
| 11 | 220 | 25 | 500 |
| 12 | 230 | 26 | 550 |
| 13 | 245 | 27 | 625 |
| 14 | 260 | 28 | 700 |

曲线特征：前 12 级每级 +10；13–20 级递增加幅约 +15；21 级后步进加大，末四级为 500 / 550 / 625 / 700。

---

## C++ 加载模型

```text
GD_BXE_ProgressionSettings.uasset
  → 反序列化为 BXEProgressionSettings (UObject)
  → 游戏逻辑 / UI / PlayerState 等读取 Levels、CollectionTags、RarityWeights
```

要点：

1. **XP 数值不是硬编码在 C++ 里**，而是 C++ 类 + DataAsset 数据的组合。
2. 游戏启动或进入相关流程后，配置会 **加载并缓存在内存**；已运行会话中替换 `.uasset` 通常不会立即生效。
3. C++ 侧 **可能** 对等级做额外 clamp（例如 UI 最大显示、存档校验）；提高 `Levels` 长度后需进游戏验证 UI 与存档是否同步支持。

---

## 修改方案

### 方案 A：静态资源 Patch（推荐）

适用：让升级更快、调整 XP 曲线、提高等级上限（追加 `Levels` 元素）。

```bash
# 1. 导出 JSON
dotnet run --project UAssetStudio.Cli -- --json json \
  "/Users/bytedance/Project/RogueCore/Content/Game/GameData/BXESettings/Progression/GD_BXE_ProgressionSettings.uasset" \
  --mappings "maps/RogueCore-5.6.1-143055+main-7f7cc36f_by_Iris.usmap" \
  --ue-version VER_UE5_6 \
  --out tmp/GD_BXE_ProgressionSettings.json

# 2. 编辑 Exports[0].Data → Levels[*].RequiredXP
#    提高上限：在 Levels 数组末尾追加新的 BXEProgressionLevel 结构体

# 3. 导回 .uasset（需带原始资产以借用 schema）
dotnet run --project UAssetStudio.Cli -- --json json tmp/GD_BXE_ProgressionSettings.json \
  --mappings "maps/RogueCore-5.6.1-143055+main-7f7cc36f_by_Iris.usmap" \
  --ue-version VER_UE5_6 \
  --asset "/Users/bytedance/Project/RogueCore/Content/Game/GameData/BXESettings/Progression/GD_BXE_ProgressionSettings.uasset" \
  --out tmp/patched/GD_BXE_ProgressionSettings.uasset
```

部署时将 patched 文件放入 mod 的 `Content/Game/GameData/BXESettings/Progression/` 对应路径。

**注意：**

- `Levels` 是 `StructProperty` 数组，结构较复杂，手工改 JSON 容易出错；稳定后可沉淀为 `UAssetStudio.Mods` recipe。
- 仅改 `RequiredXP` 风险较低；追加等级需确认新等级是否有对应解锁内容，否则可能出现「有等级无奖励」。
- 与 [`trigger-discipline-enhancement.md`](./trigger-discipline-enhancement.md) 中的局外 Enhancement Tree **无关**。

### 方案 B：运行时实时修改（UE4SS）

适用：按快捷键 / 脚本在已启动游戏中动态调整 XP 需求或等级上限。

思路（需在 UE4SS 中验证具体符号是否暴露）：

```lua
-- 伪代码：定位已加载的 ProgressionSettings 并改 Levels 数组
local Settings = FindObjectOfClass("BXEProgressionSettings")
if Settings and Settings.Levels then
    for i, Level in ipairs(Settings.Levels) do
        Level.RequiredXP = math.floor(Level.RequiredXP * 0.5)  -- 减半所需 XP
    end
end
```

或 hook 读取 `RequiredXP` / 计算当前等级上限的 UFunction，直接改返回值。

本地 `/Users/bytedance/Project/RogueCore` 仅有 Content 资产，**尚未对 native 读取函数做反汇编确认**；若 Lua 无法访问 `Levels` 数组，需 C++ UE4SS mod 或 DLL hook。

---

## 相关资产

| 资产 | 路径 | 关系 |
|------|------|------|
| 进度配置（本文） | `Game/GameData/BXESettings/Progression/GD_BXE_ProgressionSettings.uasset` | XP 曲线 + 解锁池注册 |
| 稀有度权重 | `Game/GameData/BXESettings/Progression/RaritySettings/RW_*.uasset` | 被 `RarityWeights` 引用 |
| Expenite 矿物 | `GameElements/Resources/Expenite/RES_VEIN_Expenite` | 任务内 XP 来源（见 [`minerals-and-resources.md`](./minerals-and-resources.md)） |
| 玩家状态 | `Game/BP_PlayerState.uasset` | 运行时解锁池抽取 |
| 全局 BXE 设置 | `Game/GameData/BXESettings/GD_BXESettings.uasset` | 不含 XP 字段 |

---

## 分析命令

```bash
dotnet run --project UAssetStudio.Cli -- --json json \
  "/Users/bytedance/Project/RogueCore/Content/Game/GameData/BXESettings/Progression/GD_BXE_ProgressionSettings.uasset" \
  --mappings "maps/RogueCore-5.6.1-143055+main-7f7cc36f_by_Iris.usmap" \
  --ue-version VER_UE5_6

# 快速提取 RequiredXP 表
python3 - <<'PY'
import json
path="/Users/bytedance/Project/RogueCore/Content/Game/GameData/BXESettings/Progression/GD_BXE_ProgressionSettings.uasset.json"
with open(path) as f:
    data = json.load(f)
levels = next(p for p in data["Exports"][0]["Data"] if p.get("Name") == "Levels")["Value"]
for i, lvl in enumerate(levels, 1):
    xp = next(p["Value"] for p in lvl["Value"] if p.get("Name") == "RequiredXP")
    print(f"{i:02d}: {xp}")
print("count =", len(levels))
PY
```
