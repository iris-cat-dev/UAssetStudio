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

替换游戏资产时建议覆盖整个 `output/Content`，不要只替换部分文件。后续版本之间有依赖关系，尤其是 `PLS_RC_Random` / `PLS_RC_Gauntlet` 是第三版新增的关键修复。

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
CGA structural targets=66, nonzero=0
RMG_ExtractionLinear.Rooms: total=59, bad=0
RMG_BXE.Rooms: total=9, bad=0
VERIFY_OK
```

注意：反编译后的 `.kms` 中仍可能看到旧房间的 `import`，例如 `RMA_Motherlode_Center_*` 或 `RMA_BXE_Large_*`。这是因为 ImportMap / NameMap 没有压缩，不能单独代表它们仍在数组中被使用。应以实际 `Rooms`、`ExtractionRooms`、`LaterStagesStartingRooms` 等数组内容为准。

## 风险与副作用

- 这是一组激进裁剪，可能显著降低地图垂直复杂度和房间多样性。
- 某些任务如果依赖特定大房间或 extraction room，可能出现路径生成、目标点或撤离房间重复度增加的问题。
- `ExtractionRooms` 被替换为 fallback 房间后，撤离阶段的空间表现可能更简单。
- 如果游戏运行时仍能看到地下室，剩余来源大概率是具体 `RMA_*` 房间模板内部的 baked geometry，而不是房间组或 PLS 数组。

## 后续排查方向

如果 v3 后仍能看到地下室或复杂下层结构，建议继续做两件事：

1. 记录出现问题的关卡模式、阶段、房间外观截图，并反查对应 `RMA_*` 房间模板。
2. 进一步禁用或替换仍保留的可疑模板，例如：
   - `RMA_Medium*`
   - `RMA_MediumCenter*`
   - `RMA_TimsRCRoom_Simple_*`
   - `RMA_Escort*`
   - `RMA_MU2_Medium*`

这些模板目前保留是为了避免房间池过小导致程序化生成失败；如果要继续“极平面化”，可以再做一版更严格的房间白名单。
