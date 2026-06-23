# RogueCore 地图平面化与生成物位置修改总结

> 游戏资源路径：`/Users/bytedance/Project/RogueCore`  
> Mappings：`maps/RogueCore-5.6.1-143055+main-7f7cc36f_by_Iris.usmap`  
> UE 版本：`VER_UE5_6`  
> 输出目录：`/Users/bytedance/Project/UAssetStudio/output/Content`  
> 分析产物：`UAssetStudio/analysis/roguecore_spawn/`

## 目标

本次修改目标是减少 RogueCore 程序化地图中的高架、过道、脚手架、梯子、金属平台和地下室/下层空间，并让武器锻造台、升级台、补给箱、桶等交互物尽量生成在平面区域，而不是高处、底部或复杂垂直结构上。

最终采用的是多层处理：

1. 限制交互物的可放置坡度。
2. 禁用 Construction 结构件的生成权重。
3. 禁用隧道金属装饰。
4. 裁剪高低层/平台/桥/斜坡房间模板。
5. 移除独立的地下室/下层中心房间数组。

## 关键结论

地图上的高架和地下室不是单一资产控制的，而是由以下几类资产共同生成：

| 类型 | 主要资产 | 作用 |
|------|----------|------|
| 交互物定位 | `BXE_*_Positioning` / `BP_VanityChest_Positioning` | 控制奖励物、补给、目标物能放在哪类表面上 |
| Construction 结构池 | `CGA_Default` | 控制洞穴内脚手架、平台、栏杆、墙、塔、地板、桥等 Construction 的权重 |
| 程序化关卡设置 | `PLS_RC_Base` | 控制第一段入口和隧道装饰，如 catwalk、floor、elevator、lab |
| 房间组 | `RMG_ExtractionLinear` / `RMG_BXE` | 控制可被随机选中的 `RMA_*` 房间模板 |
| 直接房间数组 | `PLS_RC_Random` / `PLS_RC_Gauntlet` | 额外指定 `ExtractionRooms`、`LaterStagesStartingRooms`，会绕过 `RMG_BXE` 的部分裁剪 |
| Boss 测试关卡 | `PLS_RC_Boss_NormalStage_Test` | 额外指定 Boss 阶段起始房间 |

## 第一版：限制交互物放置角度

第一步修改了多个 `DebrisPositioning` 资产，将 `MaxVerticalAngle` 从 `30.0f` 降到 `15.0f`，让关键交互物更偏向水平面。

涉及资产：

| 资产 | 修改 |
|------|------|
| `GameElements/RewardGivers/Supplies/BXE_Workbench_Positioning` | `MaxVerticalAngle = 15.0f` |
| `GameElements/RewardGivers/Supplies/BXE_BioBooster_Positioning` | `MaxVerticalAngle = 15.0f` |
| `GameElements/RewardGivers/Supplies/BXE_CoopCrate_Positioning` | `MaxVerticalAngle = 15.0f` |
| `GameElements/RewardGivers/Supplies/BXE_AmmoCrate_Positioning` | `MaxVerticalAngle = 15.0f` |
| `GameElements/RewardGivers/Supplies/BXE_DefenseTurret_Positioning` | `MaxVerticalAngle = 15.0f` |
| `GameElements/Objectives/Generic/BXE_GenericOBJ_Positioning` | `MaxVerticalAngle = 15.0f` |
| `GameElements/RewardGivers/Vanity/BP_VanityChest_Positioning` | `MaxVerticalAngle = 15.0f` |

这一版只能改善物品落点，不能移除地图几何中的高架、脚手架或地下室。

## 第二版：禁用金属高架与房间垂直结构

玩家反馈仍能看到大量金属高架和过道后，进一步确认来源不止 `DebrisPositioning`，还包括 Construction 结构池、隧道装饰和房间模板。

### `CGA_Default`

`CGA_Default` 中的 `Constructions` 数组控制洞穴内可生成的 Construction 类。第二版将 66 个结构类 Construction 的 `SpawnWeight` 置为 `0.0`。

重点禁用类型包括：

- `Railing`
- `Tower`
- `HangingHub`
- `CenterHub`
- `SubwayStation`
- `CaveRoomConstruction`
- `WallTunnelRoom`
- `Floor`
- `Wall`
- `Bridge`
- `Platform`
- `Scafolding`
- `Girder`
- `Mineshaft`
- `Bunker`
- `Quarry`
- `Hole`
- `Pillar`

### `PLS_RC_Base`

`PLS_RC_Base` 中的 `ForcedFirstTunnelDecorations` 和 `TunnelDecorations` 会生成入口与隧道内的金属结构。第二版将所有相关隧道装饰权重置零：

| 数组 | 处理数量 | 说明 |
|------|----------|------|
| `ForcedFirstTunnelDecorations` | 5 | `BP_Construction_Entrance1-5` |
| `TunnelDecorations` | 21 | catwalk、tunnel floor、elevator、lab、bridge 等 |

### `RMG_ExtractionLinear` / `RMG_BXE`

房间组中的 `Rooms` 数组决定随机地图能选哪些 `RMA_*` 房间模板。第二版移除高低层、桥、平台、斜坡、竖井等房间：

| 房间组 | 保留 | 移除 |
|--------|------|------|
| `RMG_ExtractionLinear` | 72 | 52 |
| `RMG_BXE` | 15 | 7 |

移除关键词包括：

- `Vertical`
- `Bridge`
- `Platform` / `Platforms`
- `Step` / `Steps`
- `Ramp` / `Ramps`
- `Slope`
- `Shaft`
- `Ravine`
- `CircularPit`
- `Plateaus`
- `MiniLevel`
- `WallPlatforms`
- `WallWithHoles`
- `CeilingHole`
- `Pillars`

## 第三版：移除地下室/下层房间来源

玩家反馈仍有地下室后，继续排查发现：`Basement` / `Underground` 等名称在资产中没有直接命中，地下室效果来自特定房间模板和独立数组。

关键漏点是：

```kms
Array<Object<RoomGenerator>> ExtractionRooms = [
    RMA_Motherlode_Center_01,
    RMA_Motherlode_Center_01_Variation,
    RMA_Motherlode_Center_03,
    RMA_Motherlode_Center_04,
    RMA_Motherlode_Center_05,
    RMA_Motherlode_Center_06,
    RMA_Motherlode_Center_07_New,
    RMA_Motherlode_Center_08_New,
    RMA_Motherlode_Center_09_New,
    RMA_Motherlode_Center_10_New
];
```

这些数组存在于 `PLS_RC_Random` 和 `PLS_RC_Gauntlet`，会绕过房间组 `RMG_BXE` 的裁剪。因此第三版直接处理 PLS 里的房间数组。

### `PLS_RC_Random`

| 字段 | 修改 |
|------|------|
| `ExtractionRooms` | 移除 10 个 `RMA_Motherlode_Center_*`，用较平的 fallback 房间替代 |
| `LaterStagesStartingRooms` | 移除 6 个 `RMA_BXE_Large_A-F` |

### `PLS_RC_Gauntlet`

| 字段 | 修改 |
|------|------|
| `ExtractionRooms` | 移除 10 个 `RMA_Motherlode_Center_*`，用较平的 fallback 房间替代 |
| `LaterStagesStartingRooms` | 移除 6 个 `RMA_BXE_Large_A-F` |

### `PLS_RC_Boss_NormalStage_Test`

| 字段 | 修改 |
|------|------|
| `BossStage_StartingRooms` | 移除 `RMA_S03_Start_Slope` 和 `RMA_S03_Start_Vertical` |

### 进一步裁剪房间组

第三版还继续从 `RMG_ExtractionLinear` / `RMG_BXE` 中移除大洞和大房间模板：

| 房间组 | 保留 | 移除 |
|--------|------|------|
| `RMG_ExtractionLinear` | 59 | 65 |
| `RMG_BXE` | 9 | 13 |

新增移除类型包括：

- `RMA_BigCave`
- `RMA_Big01` ~ `RMA_Big06`
- `RMA_BXE_Large_A` ~ `RMA_BXE_Large_F`

## 后续迭代：CoreHole、装饰设施与房间白名单

第三版后，游戏中仍然能看到地下室，并且物品仍可能生成在地下室内部。后续排查确认：剩余来源已经不主要是 `CGA_Default` 的大型 Construction、隧道装饰或 `Motherlode_Center`，而是更隐蔽的洞口装饰、房间式小设施，以及保留房间模板自身的 flood-fill/carve 几何。

### 第四版：移除 `CoreHole` 洞口装饰

`PLS_RC_Base` 的 `LevelDecorationComponent` 里仍保留：

```kms
Array<Struct> DirtDecoration = [
    { DirtDecoration: "BP_DirtDecoration_CoreHole_C", SpawnWeight: 1.0f }
];
```

该资产名称和表现都更接近地下洞口来源，因此第四版将其权重改为 `0.0f`。

验证结果：

```text
DirtDecoration: total=1, zero=1
ForcedFirstTunnelDecorations: total=5, zero=5
TunnelDecorations: total=21, zero=21
VERIFY_OK
```

### 第五版：清空剩余房间式 Construction

继续反馈地下室后，审计当前 `CGA_Default` 发现仍有一批非零 Construction，虽然不再是明显脚手架，但可能自带内部空间或让物品落入封闭/下层区域：

| 资产 | 原权重 |
|------|--------|
| `BP_Construction_Cave_Fridge_C` | `0.5` |
| `BP_Construction_Cave_Hydroponics_C` | `0.5` |
| `BP_Constuction_Cave_Furnace_C` | `0.5` |
| `BP_Construction_Cave_FoodCourt_C` | `1.0` |
| `BP_Construction_Cave_Hut_C` | `0.75` |
| `BP_Construction_Cave_Dotty_C` | `0.4` |

同时，装饰性架子/墙管也被清零：

- `BP_Construction_Cave_ToolRack_C`
- `BP_Construction_Cave_PickaxeRack_C`
- `BP_Construction_Cave_ConstructionRack_C`
- `BP_Construction_Cave_wallPipe_A_C`
- `BP_Construction_Cave_wallPipe_B_C`

第五版后 `CGA_Default` 的 `Constructions` 数组所有条目的 `SpawnWeight` 均为 0：

```text
CGA Constructions: total=77, nonzero=0
```

### 第六版：极限排除法，只保留 `RMA_BXE_MediumFlat_1`

为了确认地下室是否来自剩余房间模板，曾做过一版极端测试，将 `RMG_BXE` 和 `RMG_ExtractionLinear` 都压缩到只剩：

```text
RMA_BXE_MediumFlat_1
```

验证结果：

```text
RMG_BXE: ['RMA_BXE_MediumFlat_1']
RMG_ExtractionLinear: ['RMA_BXE_MediumFlat_1']
CGA Constructions total=77, nonzero=0
VERIFY_OK
```

这版能最大限度排除复杂房间模板，但实际不可用：房间池太小，会显著降低程序化生成多样性，并可能导致地图生成重复或失败。因此它只作为排查实验，不作为推荐输出。

### 第七版：中等规模房间白名单

最终采用中等白名单方案：保留一批名字更偏平面、线性或必要连接的房间，同时继续排除高风险模板。

`RMG_BXE` 当前保留 4 个房间：

```text
RMA_2PTwoWindowsSPAWNER_BXE
RMA_RookieRandom_MediumA_BXE
RMA_BXE_NewLinearX_End01
RMA_BXE_MediumFlat_1
```

`RMG_ExtractionLinear` 当前保留 14 个房间：

```text
RMA_RookieRandom_MediumA_BXE
RMA_TinyFacingEdges
RMA_2PTwoWindowsSPAWNER_BXE
RMA_BXE_NewLinearX_End01
RMA_NewLinearX_End02
RMA_NewLinearY_Start01
RMA_NewLinearY_Start02
RMA_NewLinearY_Start03
RMA_NewLinearY_Start04
RMA_NewLinearY_Start05
RMA_BXE_MediumFlat_1
RMA_RookieRandom_StartB
RMA_S03_End_Medium
RMA_S03_End_Medium_02
```

继续排除的高风险房间包括：

- `Big*`
- `RMA_BXE_Large_*`
- `MediumB`
- `MediumCenter`
- `MU2_Medium*`
- `Escort*`
- `TimsRCRoom_Simple_*`
- `Alcoves`
- `Cannons`
- `EndA` ~ `EndI`
- `Vertical`
- `Bridge`
- `Platform`
- `Ramp` / `Slope`
- `Shaft`
- `Cave`
- `Hole`

第七版验证：

```text
RMG_BXE rooms=4
RMG_ExtractionLinear rooms=14
CGA Constructions total=77, nonzero=0
VERIFY_OK
```

## 输出资产

最终输出目录为：

```text
/Users/bytedance/Project/UAssetStudio/output/Content
```

关键输出资产如下：

| 输出路径 | 作用 |
|----------|------|
| `Art/Environments/Constructions/GroupAssets/CGA_Default.uasset/.uexp` | 禁用结构类 Construction 生成 |
| `Landscape/ProceduralLevelSetups/PLS_RC_Base.uasset/.uexp` | 禁用入口/隧道装饰 |
| `Landscape/ProceduralLevelSetups/PLS_RC_Random.uasset/.uexp` | 移除地下室 extraction rooms 和大起始房间 |
| `Landscape/ProceduralLevelSetups/PLS_RC_Gauntlet.uasset/.uexp` | 移除地下室 extraction rooms 和大起始房间 |
| `Landscape/ProceduralLevelSetups/PLS_RC_Boss_NormalStage_Test.uasset/.uexp` | 移除 Boss 起始斜坡/垂直房间 |
| `Maps/Rooms/RoomsGroups/BXE/RMG_BXE.uasset/.uexp` | 裁剪 BXE 房间组 |
| `Maps/Rooms/RoomsGroups/EarlyAccess/RMG_ExtractionLinear.uasset/.uexp` | 裁剪 ExtractionLinear 房间组 |
| `GameElements/RewardGivers/Supplies/*_Positioning.uasset/.uexp` | 降低交互物放置坡度 |
| `GameElements/Objectives/Generic/BXE_GenericOBJ_Positioning.uasset/.uexp` | 降低目标物放置坡度 |
| `GameElements/RewardGivers/Vanity/BP_VanityChest_Positioning.uasset/.uexp` | 降低 Vanity Chest 放置坡度 |

替换游戏资产时建议覆盖整个 `output/Content`，不要只替换部分文件。后续版本之间有依赖关系，尤其是 `CGA_Default`、`PLS_RC_Base`、`PLS_RC_Random`、`PLS_RC_Gauntlet`、`RMG_BXE` 和 `RMG_ExtractionLinear` 都是关键修复。

## 实现方式

### JSON 往返

对普通数据资产，使用 `UAssetStudio.Cli json` 导出 JSON，程序化修改字段后再导回 `.uasset/.uexp`。

适用资产：

- `CGA_Default`
- 多个 `BXE_*_Positioning`
- `BXE_GenericOBJ_Positioning`
- `BP_VanityChest_Positioning`

### UAssetAPI 直接 patch

`PLS_RC_Base`、`RMG_*`、`PLS_RC_Random`、`PLS_RC_Gauntlet` 等复杂蓝图资产不适合全部走 JSON 导回，曾遇到 schema / inheritance 相关问题。因此使用自定义 C# 工具直接加载资产并修改内存对象。

工具路径：

```text
analysis/roguecore_spawn/PatchPlsTool/
```

主要修改逻辑：

- 遍历 `ArrayPropertyData`
- 解析 `ObjectPropertyData` / `SoftObjectPropertyData`
- 根据房间名或 Construction 名过滤
- 修改 `FloatPropertyData.SpawnWeight`
- 或直接重写 `Rooms` / `ExtractionRooms` 等数组

## 验证记录

已做的验证包括：

1. 重新导出修改后的资产为 JSON，确认目标 `SpawnWeight` 为 0。
2. 反编译 v3 输出资产，确认关键数组不再包含目标房间。
3. `PatchPlsTool` 编译并运行成功。
4. `Program.cs` 无 linter 错误。

验证结果摘录：

```text
ForcedFirstTunnelDecorations: total=5, nonzero=0
TunnelDecorations: total=21, nonzero=0
CGA Constructions total=77, nonzero=0
RMG_ExtractionLinear.Rooms: total=14
RMG_BXE.Rooms: total=4
VERIFY_OK
```

注意：反编译后的 `.kms` 中仍可能看到旧房间的 `import`，例如 `RMA_Motherlode_Center_*` 或 `RMA_BXE_Large_*`。这是因为 ImportMap / NameMap 没有压缩，不能单独代表它们仍在数组中被使用。应以实际 `Rooms`、`ExtractionRooms`、`LaterStagesStartingRooms` 等数组内容为准。

## 风险与副作用

- 这是一组激进裁剪，可能显著降低地图垂直复杂度和房间多样性。
- 某些任务如果依赖特定大房间或 extraction room，可能出现路径生成、目标点或撤离房间重复度增加的问题。
- `ExtractionRooms` 被替换为 fallback 房间后，撤离阶段的空间表现可能更简单。
- v6 只保留 `RMA_BXE_MediumFlat_1` 的极端方案不可用，会导致房间池过小；当前推荐使用 v7 中等白名单。
- 如果 v7 后仍能看到地下室，剩余来源大概率是保留白名单中的具体 `RMA_*` 房间模板内部 flood-fill/carve 几何，或者 `PLS_RC_Base` 的起始房间链路，而不是 Construction 权重。

## 后续排查方向

如果 v7 后仍能看到地下室或复杂下层结构，建议继续做两件事：

1. 记录出现问题的关卡模式、阶段、房间外观截图，并反查对应 `RMA_*` 房间模板。
2. 优先在 v7 白名单中逐个排除可疑房间，而不是再次使用 v6 单房间方案。建议排查顺序：
   - `RMA_S03_End_Medium`
   - `RMA_S03_End_Medium_02`
   - `RMA_RookieRandom_MediumA_BXE`
   - `RMA_2PTwoWindowsSPAWNER_BXE`
   - `RMA_NewLinearY_Start01` ~ `RMA_NewLinearY_Start05`
   - `RMA_BXE_NewLinearX_End01` / `RMA_NewLinearX_End02`

如果要继续极平面化，应改为更细粒度地 patch 具体 `RMA_*` 内部的 `FloodFillLine` / `FloodFillPillar` 参数，而不是无限缩小房间组。
