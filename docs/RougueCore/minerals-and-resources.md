# RogueCore 矿物与可采资源分析

> 游戏资源路径：`/Users/bytedance/Project/RogueCore`  
> Mappings：`maps/RogueCore-5.6.1-143055+main-7f7cc36f_by_Iris.usmap`  
> UE 版本：`VER_UE5_6`  
> 分析产物：`UAssetStudio/tmp/roguecore-minerals-current/`

## 概述

RogueCore 的可采资源不是文本表配置，而是通过 `ResourceData` 子类资产（`RES_*`）定义，并在 `BP_GameData` 的 `GDResources` 结构体中统一注册。矿物按生成方式分为四类：

| 前缀 | 类型 | 基类 | 目录 |
|------|------|------|------|
| `RES_VEIN_*` | 矿脉 | `VeinResourceData` | `GameElements/Resources/Veins/` |
| `RES_CARVED_*` | 可凿块 | `CarvedResourceData` | `GameElements/Resources/Carved/` |
| `RES_EMBED_*` | 嵌入宝石 | `GemResourceData` | `GameElements/Resources/Embedded/` |
| `RES_COLLECT_*` | 地表采集物 | `CollectableResourceData` | `GameElements/Resources/Collectibles/` |

**当前游戏资源注册表（`BP_GameData` → `GDResources`）中共登记 19 种矿物/宝石**，涵盖 DRG 经典矿物 + RogueCore 新增 `Expenite`、`Quantrite`、`Iron` 等。

---

## 配置架构

```
BP_GameData (GDResources)
  ├─ GoldResource        → RES_VEIN_Gold
  ├─ NitraResource       → RES_VEIN_Nitra
  ├─ MOMResource         → RES_VEIN_Morkite
  ├─ HollomiteResource   → RES_CARVED_Hollomite
  ├─ ...
  └─ ExpeniteResource    → RES_VEIN_Expenite

RES_VEIN_Nitra (示例)
  ├─ VeinResourceCreator   ← 矿脉生成参数（长度、噪声、地形材质）
  ├─ DBR_Nitra             ← 碎石/残骸描述
  ├─ BP_NitraChunk         ← 掉落物 Blueprint
  └─ Title / Icon / Color  ← UI 显示（如 "Nitra"）
```

程序化洞穴生成通过 `GD_ProceduralSettings` → `ProceduralResources` 组件间接引用资源，不直接在设置资产里枚举矿物列表；**以 `BP_GameData.GDResources` 为权威注册表**。

---

## 一、当前注册的 19 种矿物

### 1.1 矿脉类（Vein）— 7 种

| 游戏名 | 资源资产 | 字段名（GDResources） | Chunk BP |
|--------|----------|----------------------|----------|
| Morkite | `RES_VEIN_Morkite` | `MOMResource` | `BP_MorkiteChunk` |
| Gold | `RES_VEIN_Gold` | `GoldResource` | `BP_GoldChunk` |
| Nitra | `RES_VEIN_Nitra` | `NitraResource` | `BP_NitraChunk` |
| Croppa | `RES_VEIN_Croppa` | `CropaniteResource` | `BP_CroppaChunk` |
| Dystrum | `RES_VEIN_Dystrum` | `DystrumResource` | `BP_DystrumChunk` |
| Iron | `RES_VEIN_Iron` | `IronResource` | `BP_IronChunk` |
| Expenite | `RES_VEIN_Expenite` | `ExpeniteResource` / `XPResource` | `BP_CaveXP_Chunk` |

> `Expenite` 在 `GDResources` 中同时占用 `XPResource` 与 `ExpeniteResource` 两个槽位，均指向 `RES_VEIN_Expenite`。另有教程专用变体 `RES_VEIN_Expenite_Tutorial`（仅 `RUN_Tutorial` 引用）。

### 1.2 可凿块类（Carved）— 8 种

| 游戏名 | 资源资产 | 字段名 | 备注 |
|--------|----------|--------|------|
| Phazyonite | `RES_CARVED_Phazyonite` | `Fashionite` | 目录名 `Fashionite/`，字段沿用旧名 |
| Hollomite | `RES_CARVED_Hollomite` | `HollomiteResource` | |
| Magnite | `RES_CARVED_Magnite` | `MagniteResource` | |
| Bismor | `RES_CARVED_Bismor` | `BismorResource` | 另有废弃别名 `NeromiteResource` 指向同一资产 |
| Quantrite | `RES_CARVED_Quantrite` | `QuantriteResource` | RC 新增 |
| Umanite | `RES_CARVED_Umanite` | `UmaniteResource` | |
| Oil Shale | `RES_CARVED_OilShale` | `OilShaleResource` | `WPN_OilExtractor` 引用 |
| Red Sugar | `RES_CARVED_RedSugar` | `RedSugarResource` | `RUN_Generic` / `RUN_Gauntlet` 直接引用 |

### 1.3 嵌入宝石类（Embedded）— 4 种

| 游戏名 | 资源资产 | 字段名 | 备注 |
|--------|----------|--------|------|
| Enor Pearl | `RES_EMBED_Enor` | `EnorResource` | 目录 `EnorPearl/` |
| Jadiz | `RES_EMBED_Jadiz` | `JadizResource` | |
| Bittergem | `RES_EMBED_Bittergem` | `BittergemResource` | `DDMUT_Gems` 变异引用 |
| Aquarq | `RES_EMBED_Aquarq` | `MotherlodeGemResource` | 字段名沿用 DRG 母矿宝石 |

---

## 二、核心配置文件

| 用途 | 路径（相对 `Content/`） |
|------|-------------------------|
| **★ 资源注册表** | `Game/BP_GameData.uasset` → `GDResources` |
| 矿脉定义 | `GameElements/Resources/Veins/RES_VEIN_*.uasset` |
| 可凿块定义 | `GameElements/Resources/Carved/*/RES_CARVED_*.uasset` |
| 嵌入宝石 | `GameElements/Resources/Embedded/*/RES_EMBED_*.uasset` |
| Expenite（RC 专属） | `GameElements/Resources/Expenite/RES_VEIN_Expenite.uasset` |
| 地形材质 | `Landscape/Materials/TM_*.uasset` |
| 地形类型 | `Landscape/TerrainTypes/TTP_*.uasset` |
| 资源图标 | `UI/Art/Icons/Icons_Resources/New_Resource_Icons/` |
| 本地化文本 | `Game/Text/Resources.uasset` |
| 程序化设置 | `Game/GameData/GD_ProceduralSettings.uasset` |

### 2.1 字段名与资产名对照（改资源时易踩坑）

| GDResources 字段 | 实际矿物 | 说明 |
|------------------|----------|------|
| `MOMResource` | Morkite | Morkite 缩写 |
| `Fashionite` | Phazyonite | 旧项目代号 |
| `NeromiteResource` | Bismor | 与 `BismorResource` 重复指向 |
| `CropaniteResource` | Croppa | |
| `MotherlodeGemResource` | Aquarq | DRG 母矿术语 |
| `XPResource` | Expenite | 与 `ExpeniteResource` 重复指向 |

---

## 三、资产存在但未进入注册表的资源

以下 `RES_*` 资产在 `Content/` 中存在且有二进制引用，但**未**出现在 `BP_GameData.GDResources` 中，不算当前正式注册的矿物：

| 资产 | 引用情况 | 说明 |
|------|----------|------|
| `RES_EMBED_Compressed_Gold` | `DDMUT_Gems`、自身 DBR | 压缩金矿，变异事件用 |
| `RES_EMBED_UnknownArtifact` | `DDMUT_Gems`、ErrorCube BP | 未知神器（Error Cube） |
| `RES_EMBED_Artifact` | Artifact Gem BP、CompanionDrone | 神器宝石，非标准矿物 |
| `RES_COLLECT_Apoca_Bloom` | `BP_Collectible_Base` | 采集植物，非矿物 |
| `RES_CARVED_RedSugar_Tutorial` | `RUN_Tutorial` | 教程专用红糖变体 |

---

## 四、运行时引用（抽样）

全库二进制引用扫描（`RES_*.uasset` × `Content/**/*.uasset`）结果摘要：

| 资源 | 引用数 | 代表性引用方 |
|------|--------|--------------|
| `RES_VEIN_Nitra` | 8 | `BP_GameData`、`DBA_HollowBough`、`DDMUT_DeepDive_NitraVein`、`ENE_LootBug` |
| `RES_VEIN_Morkite` | 4 | `BP_GameData`、`Mut_Tut_ExtraMorkite` |
| `RES_VEIN_Gold` | 8 | `BP_GameData`、`ENE_LootBug`、`BP_Compressed_Gold` |
| `RES_VEIN_Iron` | 4 | `BP_GameData`、`DBA_DeepCore` |
| `RES_VEIN_Expenite` | 23 | `RUN_Generic`、`RUN_Gauntlet`、`ENE_LootBug_Expenite`、多种目标物 |
| `RES_CARVED_RedSugar` | 16 | `RUN_Generic`、`BXEMUT_NoRedSugar`、BioTransmuter |
| `RES_EMBED_Jadiz` | 101 | `BP_GameData`、大量 Vanity/武器升级消耗 |
| `RES_CARVED_Bismor` | 82 | `BP_GameData`、`GD_DailyDealSettings`、武器/外观解锁 |

矿脉类资源普遍被 `BP_GameData` + 对应 `TM_*` 地形材质 + `BP_*Chunk` 引用；高引用数（80+）多来自 Vanity 姿态、武器解锁、Daily Deal 等**局外消耗**，不代表地图生成权重。

---

## 五、其他已注册但非矿物的资源

`GDResources` 还登记了货币、采集物、元进度等，修改矿物时勿混淆：

| 类别 | 资产示例 |
|------|----------|
| 货币 | `RES_Credits`、`RES_Chips`、`RES_Merit`、`RES_MasteryXP` |
| 大麦（酿造） | `RES_COLLECT_Barley1`～`Barley4` |
| 相机（Intel 挑战） | `RES_COLLECT_Camera` |
| 健身 credits | `RES_SquatCredits`、`RES_DeadliftCredits` 等 |
| 其他 | `RES_Calories`、`RES_BlankSchematic`、`RES_DataTerminal` |

---

## 六、分析命令

从 UAssetStudio 根目录执行：

```bash
# 导出资源注册表
dotnet run --project UAssetStudio.Cli -- --json json \
  "/Users/bytedance/Project/RogueCore/Content/Game/BP_GameData.uasset" \
  --mappings maps/RogueCore-5.6.1-143055+main-7f7cc36f_by_Iris.usmap \
  --ue-version VER_UE5_6 \
  --out tmp/roguecore-minerals-current/BP_GameData.json

# 导出单个矿物定义（查看 Title、Spawnable、Vein 参数等）
dotnet run --project UAssetStudio.Cli -- --json json \
  "/Users/bytedance/Project/RogueCore/Content/GameElements/Resources/Veins/RES_VEIN_Nitra.uasset" \
  --mappings maps/RogueCore-5.6.1-143055+main-7f7cc36f_by_Iris.usmap \
  --ue-version VER_UE5_6 \
  --out tmp/roguecore-minerals-current/RES_VEIN_Nitra.json

# 列出所有 RES_* 资源资产
rg --files "/Users/bytedance/Project/RogueCore/Content/GameElements/Resources" \
  | rg '/RES_(VEIN|CARVED|EMBED|COLLECT)_[^/]+\.uasset$'
```

---

## 七、修改建议

| 目标 | 推荐入口 |
|------|----------|
| 新增/移除一种矿物 | `BP_GameData` → `GDResources` 对应字段 + 新建 `RES_*` 资产 |
| 调整矿脉生成密度/长度 | 对应 `RES_VEIN_*` 内的 `VeinResourceCreator` |
| 调整掉落物外观/物理 | `BP_*Chunk` / `DBR_*` / `RCR_*` |
| 地图生物群系矿物分布 | `Landscape/Biomes/Biomes_Ingame/*/DBA_*.uasset` |
| 变异事件额外矿物 | `GameElements/DeepDives/BaseMutators/DDMUT_*.uasset` |

改 `GDResources` 后建议用 `validate` 检查 `FPackageIndex` 一致性，并用实际 Run（`RUN_Generic`）验证地图生成是否仍刷出目标矿物。

多人联机时，Run / ResourceData 类资产修改需要 Host 和客户端安装一致版本；程序化地形会在客户端本地用 `seed` / 房间数据重新生成表现。只给 Host 替换资产可能导致客户端仍按旧资源表现渲染，详见 [`expenite-to-nitra-client-sync.md`](./expenite-to-nitra-client-sync.md)。
