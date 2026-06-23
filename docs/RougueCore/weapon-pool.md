# RogueCore 武器池 / 手雷池分析

> 游戏资源路径：`/Users/bytedance/Project/RogueCore`  
> Mappings：`maps/RogueCore-5.6.1-143055+main-7f7cc36f_by_Iris.usmap`  
> UE 版本：`VER_UE5_6`

## 概述

RogueCore 的武器池 / 手雷池**不是** CSV/JSON/INI 文本配置，而是通过 UE DataAsset（`BXEUnlockPool`）维护的 `Unlock_Item_*` / `Unlock_Grenade_*` 引用列表。

术语说明：

| 游戏概念 | 按键 | 配置目录 |
|----------|------|----------|
| **Secondary Weapon（副武器）** | `<Equip2/>` | `SecondariesWithAttributes/` |
| **Grenade（手雷）** | `<Equip3/>` | `GrenadesWithAttributes/`、`Gear/Grenades/` |
| **Support Tool（辅助工具）** | `<Equip4/>` | `Gear/Tools/`、`UP_TraversalEquipment*` |

本文「武器池中的辅助武器」指 **Secondary Weapon（副武器槽）**，不是 Support Tool。

## 配置架构

```
武器箱 / 手雷箱 / 工作台 UI
  └─ UCT_*（CollectionTag，池标签）
       └─ UP_*（BXEUnlockPool，池内容）
            ├─ Unlock_Item_*（BXEUnlockItem，武器池条目）
            │    └─ ID_*_BXE / WPN_*_BXE
            └─ Unlock_Grenade_*（BXEUnlockItem，手雷池条目）
                 └─ ID_Grenade_*（实际手雷资产）
```

运行时引用链示例：

```
BP_BXE_DroneActivatedWeaponCrate
  → UCT_WeaponsInitialPool
    → UP_Weapons_InitialPool
      → Unlock_Item_BurstPistol / Pistol / ...
        → ID_Pistol_BXE
```

## 核心配置文件

| 用途 | 路径（相对 `Content/`） |
|------|-------------------------|
| 常规武器箱 / 掉落池（主+副） | `Unlocks/Collections/WeaponsToolsGear/UP_Weapons_InitialPool.uasset` |
| 工作台选枪池 | `Unlocks/Collections/WeaponsToolsGear/UP_Workbench_Weapons.uasset` |
| 池标签（运行时引用） | `Unlocks/Collections/CollectionTags/UCT_WeaponsInitialPool.uasset` |
| 重型武器箱（含额外副武器） | `Unlocks/Collections/WeaponsToolsGear/UP_HeavyWeapons.uasset` |
| 扩展武器池 | `Unlocks/Collections/WeaponsToolsGear/UP_Weapons.uasset` |
| 全局池注册表 | `Unlocks/Collections/UPC_AllUnlockPools.uasset` |
| 进度 / 稀有度绑定 | `Game/GameData/BXESettings/Progression/GD_BXE_ProgressionSettings.uasset` |
| 武器稀有度权重 | `Game/GameData/BXESettings/Progression/RaritySettings/RW_Weapons.uasset` |
| 工作台武器稀有度 | `Game/GameData/BXESettings/Progression/RaritySettings/RW_WorkbenchWeapons.uasset` |
| **起始房手雷池** | `Unlocks/Collections/WeaponsToolsGear/UP_StartingGrenades.uasset` |
| **全量手雷池** | `Unlocks/Collections/WeaponsToolsGear/UP_Grenades.uasset` |
| **教程手雷池** | `Unlocks/Collections/WeaponsToolsGear/UP_StartingGrenades_Tutorial.uasset` |
| **手雷池标签** | `Unlocks/Collections/CollectionTags/UCT_StartingGrenades.uasset` |
| **手雷稀有度权重** | `Game/GameData/BXESettings/Progression/RaritySettings/RW_Default.uasset`（`ERarityWeightType::Grenades`） |

### 运行时逻辑（谁从池里抽武器 / 手雷）

| 路径 | 说明 |
|------|------|
| `Game/BP_PlayerState.uasset` | `ApplyUnlockFromPool`、`PickRandomUnlocksFromTag` |
| `GameElements/RewardGivers/Supplies/CoopStation/BP_BXE_UnlockContainer_Base.uasset` | 解锁容器基类 |
| `GameElements/RewardGivers/Supplies/CoopStation/BP_BXE_DroneActivatedWeaponCrate.uasset` | 武器箱，引用 `UCT_WeaponsInitialPool` / `UP_HeavyWeapons` |
| `GameElements/RewardGivers/Supplies/CoopStation/BP_BXE_DroneActivatedWeaponAndTraversalCrate.uasset` | 武器 + Traversal 混合箱 |
| `UI/Menus/Menu_Workbench/Workbench_Weapon.uasset` | 工作台武器选择 UI |
| `GameElements/RewardGivers/Supplies/CoopStation/BP_BXE_GrenadeCrateStartingRoom.uasset` | 起始房手雷箱，引用 `UCT_StartingGrenades` |
| `GameElements/Missions/RiskVectors/BonusGreanadeCrate/RV_BonusGrenadeCrate.uasset` | 风险向量：额外手雷箱事件 |

## 手雷池（Grenade Pool）

手雷条目定义目录：

```
Content/Unlocks/Gear/WeaponsUpgraded/GrenadesWithAttributes/   ← 带属性升级的手雷
Content/Unlocks/Gear/Grenades/                               ← 基础手雷（NeedleSprayer、Pheromone、StickySmall）
Content/WeaponsNTools/Grenades/                              ← 实际手雷实现（ID_Grenade_*）
```

运行时引用链：

```
BP_BXE_GrenadeCrateStartingRoom
  → UCT_StartingGrenades
    → UP_StartingGrenades
      → Unlock_Grenade_StickySmall / Cluster / ...
        → ID_Grenade_StickySmall
```

`BP_PlayerState` 也通过 `UCT_StartingGrenades` 参与手雷解锁逻辑；`GD_BXE_ProgressionSettings` 将 `ERarityWeightType::Grenades` 绑定到该标签。

### 起始房手雷池（6 种）— 常规可选

来源：`UP_StartingGrenades.uasset`（已注册到 `UPC_AllUnlockPools`）

| Unlock ID | DRG 手雷名 | 稀有度 |
|-----------|-----------|--------|
| `Unlock_Grenade_StickySmall` | **Sticky Grenade（粘性手雷）** | Common |
| `Unlock_Grenade_Bouncy` | **Plasma Burster（等离子弹跳手雷）** | Common |
| `Unlock_Grenade_Cluster` | **Cluster Grenade（集束手雷）** | Common |
| `Unlock_Grenade_Freeze` | **Cryo Grenade（冷冻手雷）** | Common |
| `Unlock_Grenade_HighExplosive` | **High Explosive Grenade（高爆手雷）** | Common |
| `Unlock_Grenade_Incendiary` | **Incendiary Grenade（燃烧手雷）** | Common |

### 教程手雷池（3 种）

来源：`UP_StartingGrenades_Tutorial.uasset`

StickySmall、Cluster、HighExplosive

### 全量手雷池（15 种）— 含高阶手雷

来源：`UP_Grenades.uasset`（**当前仅被 `Menu_RC_Quickcheats` 引用，未注册到 `UPC_AllUnlockPools`**，更像完整定义 / 调试池）

| Unlock ID | DRG 手雷名 | 稀有度 |
|-----------|-----------|--------|
| `Unlock_Grenade_NeedleSprayer` | **Needle Sprayer（针剂喷射器）** | Common |
| `Unlock_Grenade_Pheromone` | **Pheromone Canister（信息素罐）** | Common |
| `Unlock_Grenade_BoomerangBouncy` | **Plasma Boomerang（等离子回旋镖）** | Common |
| `Unlock_Grenade_Bouncy` | **Plasma Burster** | Common |
| `Unlock_Grenade_Cluster` | **Cluster Grenade** | Common |
| `Unlock_Grenade_Freeze` | **Cryo Grenade** | Common |
| `Unlock_Grenade_HighExplosive` | **High Explosive Grenade** | Common |
| `Unlock_Grenade_IFG` | **IFG（抑制力场发生器）** | Common |
| `Unlock_Grenade_Incendiary` | **Incendiary Grenade** | Common |
| `Unlock_Grenade_Neurotoxin` | **Neurotoxin Grenade（神经毒素手雷）** | Common |
| `Unlock_Grenade_StickySmall` | **Sticky Grenade** | Common |
| `Unlock_Grenade_ImpactAxe` | **Impact Axe（冲击斧）** | Uncommon |
| `Unlock_Grenade_WallSaw` | **Wall Saw（墙锯）** | Uncommon |
| `Unlock_Grenade_Lure` | **L.U.R.E.（诱饵手雷）** | Rare |
| `Unlock_Grenade_ShredderSwarm` | **Shredder Swarm（护甲粉碎蜂群）** | Epic |

池中还有一个非手雷条目 `Unlock_MaxAmmo_Skip`（跳过最大弹药升级），不影响手雷选择。

### 装备混合池中的额外手雷

`UP_Equipment.uasset` 还包含以下手雷（与工具 / 消耗品混池）：

BoomerangBouncy、IFG、ImpactAxe、Lure、ShredderSwarm、**StickyMine（粘性地雷，Epic）**、WallSaw

`Unlock_Grenade_StickyMine` 不在 `UP_Grenades` / `UP_StartingGrenades` 中，仅通过装备池出现。

### 单个手雷资产结构

```
Unlocks/Gear/WeaponsUpgraded/GrenadesWithAttributes/
  Unlock_Grenade_Cluster.uasset              ← 池条目（含 ItemID 引用）
  Unlock_Grenade_Cluster/
    AWP_Unlock_Grenade_Cluster.uasset      ← 属性权重池

WeaponsNTools/Grenades/Cluster/
  ID_Grenade_Cluster.uasset                ← 实际手雷定义
  Grenade_Cluster.uasset                   ← 手雷蓝图
```

## 副武器（Secondary Weapon）列表

副武器条目定义目录：

```
Content/Unlocks/Gear/WeaponsUpgraded/SecondariesWithAttributes/
```

### 常规池（5 把）

来源：`UP_Weapons_InitialPool.uasset`、`UP_Workbench_Weapons.uasset`（内容一致）

| Unlock ID | DRG 武器名 | 说明 |
|-----------|-----------|------|
| `Unlock_Item_BurstPistol` | **BRT7** | 三连发手枪 |
| `Unlock_Item_DualMP` | **NUK17 双持冲锋枪** | 双持，高射速低精度 |
| `Unlock_Item_Pistol` | **Deepcore 40P** (LARK S10) | 标准手枪 |
| `Unlock_Item_Revolver` | **Bulldog 重型左轮** | 四发大口径左轮 |
| `Unlock_Item_SawedoffShotgun` | **短管霰弹枪** | 截短霰弹，高爆发 |

### 重型武器箱额外副武器（+2 把）

来源：`UP_HeavyWeapons.uasset`（由 `BP_BXE_DroneActivatedWeaponCrate` 通过 `UCT_HeavyWeapons` 引用）

| Unlock ID | DRG 武器名 |
|-----------|-----------|
| `Unlock_Item_GrenadeLauncher` | **紧凑型榴弹发射器** |
| `Unlock_Item_LineCutter` | **Line Cutter（切割器）** |

**合计：全池最多 7 把副武器；常规武器箱只出前 5 把。**

## 单个副武器资产结构

每个副武器有两层资产：

```
Unlocks/Gear/WeaponsUpgraded/SecondariesWithAttributes/
  Unlock_Item_Pistol.uasset              ← 池条目（含 ItemID 引用）
  Unlock_Item_Pistol/
    AWP_Unlock_Item_Pistol.uasset        ← 属性权重池（BXEUnlockAttributeWeightPool）

Unlocks/Items/Weapons/Pistol/
  ID_Pistol_BXE.uasset                   ← 实际武器定义
  WPN_Pistol_BXE.uasset                  ← 武器蓝图
  DMG_Pistol_BXE.uasset                  ← 伤害定义
```

7 个副武器条目：

```
SecondariesWithAttributes/
├── Unlock_Item_BurstPistol/
├── Unlock_Item_DualMP/
├── Unlock_Item_Pistol/
├── Unlock_Item_Revolver/
├── Unlock_Item_SawedoffShotgun/
├── Unlock_Item_GrenadeLauncher/    ← 仅重型池
└── Unlock_Item_LineCutter/         ← 仅重型池
```

## 常规武器池完整内容（参考）

`UP_Weapons_InitialPool` 除 5 把副武器外，还包含：

**主武器（Primaries）：**

AssaultRifle, BattleRifle, CombatShotgun, ElectricSMG, M1000, Machinepistol, MagBlaster, PlasmaCarbine, Slugger, TwohandedSMG, FlameThrower, GooCannon, Cryospray, HeavyParticleCannon, Crossbow, CoilGun

**起始 / Ambient 变体：**

ChargeBlaster, ShockBlaster, AmbientFieldBlaster, Devastator_NEW

已追加的 DRG 武器条目：

- `Unlock_Item_FlameThrower` → `ID_FlameThrower_BXE`
- `Unlock_Item_GooCannon` → `ID_GooCannon_BXE`
- `Unlock_Item_Cryospray` → `ID_Cryospray_BXE`
- `Unlock_Item_HeavyParticleCannon` → `ID_HeavyParticleCannon_BXE`
- `Unlock_Item_Crossbow` → `ID_Crossbow_BXE`
- `Unlock_Item_CoilGun` → `ID_CoilGun_BXE`

## Support Tool（辅助工具，非副武器）

如果你指的是 `<Equip4/>` 的 **Support Tool**，那是独立的 Traversal 工具系统：

| 池文件 | 说明 |
|--------|------|
| `UP_TraversalEquipment.uasset` | 全量工具池 |
| `UP_TraversalEquipment_StartingRoom.uasset` | 起始房工具池 |
| `UCT_TraversalTools_StartingRoom.uasset` | 起始房标签 |

**起始房可选工具：** DoubleDrills、FlareGun、PlatformGun、ZipLineGun、GravityLift

**全量工具池还包括：** GrapplingHook、JetBoots、AmmoBag 等

## 如何修改武器 / 手雷池

**副武器：**

1. 编辑 `UP_Weapons_InitialPool.uasset` 的 `Unlocks` 属性 — 影响武器箱掉落
2. 编辑 `UP_Workbench_Weapons.uasset` — 影响工作台选枪
3. 调整稀有度权重 — `RW_Weapons.uasset` / `RW_WorkbenchWeapons.uasset`
4. 新增武器需同时创建 `Unlock_Item_*` 条目和 `ID_*_BXE` / `WPN_*_BXE` 实现资产

**手雷：**

1. 编辑 `UP_StartingGrenades.uasset` 的 `Unlocks` 属性 — 影响起始房手雷箱（`BP_BXE_GrenadeCrateStartingRoom`）
2. 编辑 `UP_Grenades.uasset` — 全量手雷定义（当前主要用于 Quick Cheats）
3. 调整稀有度 — 各 `Unlock_Grenade_*` 条目上的 `GD_BXE_Rarity_*` 引用，以及 `RW_Default.uasset` 的 `Grenades` 权重
4. 新增手雷需创建 `Unlock_Grenade_*` 条目 + `ID_Grenade_*` 实现资产

## UAssetStudio 分析命令

```bash
# JSON 导出武器池
dotnet run --project UAssetStudio.Cli -- json \
  "/Users/bytedance/Project/RogueCore/Content/Unlocks/Collections/WeaponsToolsGear/UP_Weapons_InitialPool.uasset" \
  --mappings maps/RogueCore-5.6.1-143055+main-7f7cc36f_by_Iris.usmap \
  --ue-version VER_UE5_6 \
  --out output/rogue_weapon_pool/UP_Weapons_InitialPool.json

# JSON 导出手雷池
dotnet run --project UAssetStudio.Cli -- json \
  "/Users/bytedance/Project/RogueCore/Content/Unlocks/Collections/WeaponsToolsGear/UP_StartingGrenades.uasset" \
  --mappings maps/RogueCore-5.6.1-143055+main-7f7cc36f_by_Iris.usmap \
  --ue-version VER_UE5_6 \
  --out output/rogue_grenade_pool/UP_StartingGrenades.json

dotnet run --project UAssetStudio.Cli -- json \
  "/Users/bytedance/Project/RogueCore/Content/Unlocks/Collections/WeaponsToolsGear/UP_Grenades.uasset" \
  --mappings maps/RogueCore-5.6.1-143055+main-7f7cc36f_by_Iris.usmap \
  --ue-version VER_UE5_6 \
  --out output/rogue_grenade_pool/UP_Grenades.json

# 反编译单个 Unlock 条目
dotnet run --project UAssetStudio.Cli -- decompile \
  "/Users/bytedance/Project/RogueCore/Content/Unlocks/Gear/WeaponsUpgraded/SecondariesWithAttributes/Unlock_Item_Pistol.uasset" \
  --mappings maps/RogueCore-5.6.1-143055+main-7f7cc36f_by_Iris.usmap \
  --ue-version VER_UE5_6 \
  --out output/rogue_weapon_pool/Unlock_Item_Pistol.kms
```

## 建议反编译优先级

**武器：**

1. `UP_Weapons_InitialPool.uasset` — 读池内容
2. `UP_Workbench_Weapons.uasset` — 工作台池
3. `UPC_AllUnlockPools.uasset` — 全局注册
4. `GD_BXE_ProgressionSettings.uasset` — 进度绑定
5. `BP_PlayerState.uasset` — 运行时抽池逻辑
6. `SecondariesWithAttributes/Unlock_Item_*.uasset` — 单个副武器定义

**手雷：**

1. `UP_StartingGrenades.uasset` — 起始房手雷池（运行时主池）
2. `UP_Grenades.uasset` — 全量手雷定义
3. `UCT_StartingGrenades.uasset` — 池标签
4. `BP_BXE_GrenadeCrateStartingRoom.uasset` — 手雷箱逻辑
5. `GrenadesWithAttributes/Unlock_Grenade_*.uasset` — 单个手雷定义
