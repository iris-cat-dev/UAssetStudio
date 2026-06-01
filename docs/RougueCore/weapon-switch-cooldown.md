# RogueCore 武器切换 CD 定位

> 游戏资源路径：`/Users/bytedance/Project/RogueCore`  
> Mappings：`maps/RogueCore-5.6.1-143055+main-7f7cc36f_by_Iris.usmap`  
> UE 版本：`VER_UE5_6`  
> 分析产物：`UAssetStudio/analysis/weapon_switch_cd/`

## 概述

RogueCore 的「武器切换 CD」不是一个单独命名为 `WeaponSwitchCooldown` 的全局配置。普通武器切换主要由 **Item 装备流程**控制，关键时间字段是 `AnimatedItem.EquipDuration`。

结论：

| 需求 | 优先看哪里 | 说明 |
|------|------------|------|
| 普通武器切换耗时 / 换枪 CD | `AnimatedItem.EquipDuration` | 实际值写在具体 `WPN_*` 蓝图 CDO 上 |
| 玩家当前能不能换武器 | `PlayerCharacter.CanChangeItems` | 原生角色逻辑中的切换门禁 bool |
| 物品槽 / 当前装备物品 | `InventoryComponent` / `InventoryBase` | `BP_PlayerCharacter` 持有 `Inventory` 组件 |
| 投掷物装备后的冷却 | `ThrowableItem.CooldownAfterEquip` | 只针对 ThrowableItem 类 |
| Crossbow 弹种 / 模式切换 | `Crossbow.SwitchTime` | 不是通用换武器 CD |

## 运行时结构

通用流程大致是：

```text
BP_PlayerCharacter
  └─ InventoryComponent / InventoryBase
       ├─ EquippedActor / ReplicatedEquippedActor
       ├─ ItemSlots
       └─ 切换到具体 Item / AnimatedItem
            ├─ FP_EquipAnimation / TP_EquipAnimation
            └─ EquipDuration
```

`BP_PlayerCharacter` 自身保存输入按钮和门禁状态；实际装备切换的资产对象由 `InventoryComponent` 管理，进入具体武器后由 `AnimatedItem.EquipDuration` 决定装备动画/切换耗时。

## 关键类与字段

来源：`.usmap` schema 查询。

| 类 | 字段 | 类型 | 含义 |
|----|------|------|------|
| `/Script/RogueCore.PlayerCharacter` | `CycleItemButton` | `HoldButton` | 切换物品输入按钮 |
| `/Script/RogueCore.PlayerCharacter` | `InventoryComponent` | `ObjectProperty` | 玩家物品/武器组件 |
| `/Script/RogueCore.PlayerCharacter` | `CanChangeItems` | `BoolProperty` | 是否允许切换物品 |
| `/Script/RogueCore.InventoryBase` | `EquippedActor` | `EquippedActorData` | 当前装备对象 |
| `/Script/RogueCore.InventoryBase` | `ReplicatedEquippedActor` | `EquippedActorData` | 网络同步的装备对象 |
| `/Script/RogueCore.InventoryComponent` | `ItemSlots` | `Array<ItemSlot>` | 物品槽 |
| `/Script/RogueCore.Item` | `CooldownRate` | `FloatProperty` | 物品自身冷却倍率，偏使用/过热逻辑 |
| `/Script/RogueCore.Item` | `ManualCooldownDelay` | `FloatProperty` | 手动冷却延迟，偏武器冷却/过热 |
| `/Script/RogueCore.AnimatedItem` | `EquipDuration` | `FloatProperty` | 普通装备/切换耗时，最像换武器 CD |
| `/Script/RogueCore.ThrowableItem` | `CooldownAfterEquip` | `FloatProperty` | 投掷物装备后冷却 |
| `/Script/RogueCore.Crossbow` | `SwitchTime` | `FloatProperty` | Crossbow 内部弹种切换耗时 |

## 资产位置

玩家入口：

```text
Content/Character/BP_PlayerCharacter.uasset
```

`BP_PlayerCharacter.kms` 中可以看到它持有 `Inventory` 组件：

```kms
object Inventory : InventoryComponent {
    Object<InventoryList> InventoryList = BP_GenericHeroInventory;
    Object<BlueprintGeneratedClass> ThrownGrenadeClass = ITM_GrenadeThrow_C;
    Object<BlueprintGeneratedClass> AmmoBagClass = ITM_AmmoBag_NonThrow_C;
}
```

并把该组件绑定到角色字段：

```kms
Object<InventoryComponent> InventoryComponent = Inventory;
```

武器资产通常分两层：

```text
Content/Unlocks/Items/Weapons/<Weapon>/WPN_*_BXE.uasset   ← RogueCore/BXE 包装层
Content/WeaponsNTools/<Weapon>/WPN_*.uasset               ← 基础武器实现
```

例如手枪：

```text
Content/Unlocks/Items/Weapons/Pistol/WPN_Pistol_BXE.uasset
  → 继承 / 引用 Content/WeaponsNTools/Pistol/WPN_Pistol_A.uasset
```

`WPN_Pistol_A.kms` 中实际装备耗时：

```kms
class WPN_Pistol_A_C : BasicPistol {
    Object<AnimMontage> FP_EquipAnimation = `1P_Pistol_Shared_Equip_B_Montage`;
    Object<AnimMontage> TP_EquipAnimation = TP_Pistol_Equip_Montage;
    float EquipDuration = 0.5f;
}
```

Crossbow 是一个容易误判的例子。它有内部弹种切换字段：

```kms
class WPN_Crossbow_BXE_C : Crossbow {
    float SwitchTime = 0.15f;
    float SwitchTimeCof = 1.15f;
    Object<AnimMontage> SwitchMontage = WP_ANIM_Crossbow_AmmoSwap_A_Montage;
}
```

但普通装备/换武器时间仍然是：

```kms
float EquipDuration = 0.5f;
```

因此如果目标是「按 1/2/3/4 换武器的 CD」，优先改 `EquipDuration`；如果目标是 Crossbow 的箭矢/弹种切换，再改 `SwitchTime`。

## 修改建议

### 1. 改全体武器换枪手感

没有找到一个明确的全局 `WeaponSwitchCooldown` DataAsset。要统一降低换枪 CD，推荐批量检查并修改各武器基础蓝图里的：

```text
AnimatedItem.EquipDuration
```

常见位置：

```text
Content/WeaponsNTools/*/WPN_*.uasset
Content/Unlocks/Items/Weapons/*/WPN_*_BXE.uasset
Content/Unlocks/Items/TraversalTools/*/WPN_*_BXE.uasset
```

实际字段可能在基础武器层，也可能被 BXE 包装层覆盖。判断方式：反编译目标资产，搜索 `EquipDuration`。

### 2. 只改某把武器

以手枪为例，应该优先改：

```text
Content/WeaponsNTools/Pistol/WPN_Pistol_A.uasset
```

字段：

```text
EquipDuration = 0.5f
```

如果 BXE 包装层没有覆盖该字段，修改基础层会影响继承它的 BXE 版本。

### 3. 不建议优先改的字段

| 字段 | 原因 |
|------|------|
| `GDStats.WeaponCooldownRate` | 更像武器冷却/过热/属性倍率，不是装备切换门禁 |
| `Item.CooldownRate` | 更偏物品使用冷却，不是换枪动画时长 |
| `Item.ManualCooldownDelay` | 更偏手动冷却/散热逻辑 |
| `Crossbow.SwitchTime` | 只影响 Crossbow 内部弹种切换 |
| `PlayerCharacter.EquipLaserpointerHoldDuration` | 只影响按住激光指示器的装备时间 |

## 复查命令

反编译玩家入口：

```bash
dotnet run --project UAssetStudio.Cli -- decompile \
  /Users/bytedance/Project/RogueCore/Content/Character/BP_PlayerCharacter.uasset \
  --mappings maps/RogueCore-5.6.1-143055+main-7f7cc36f_by_Iris.usmap \
  --ue-version VER_UE5_6 \
  --outdir analysis/weapon_switch_cd --meta
```

反编译某把武器：

```bash
dotnet run --project UAssetStudio.Cli -- decompile \
  /Users/bytedance/Project/RogueCore/Content/WeaponsNTools/Pistol/WPN_Pistol_A.uasset \
  --mappings maps/RogueCore-5.6.1-143055+main-7f7cc36f_by_Iris.usmap \
  --ue-version VER_UE5_6 \
  --outdir analysis/weapon_switch_cd --meta
```

搜索关键字段：

```bash
rg "EquipDuration|SwitchTime|CanChangeItems|CooldownAfterEquip" analysis/weapon_switch_cd
```

## 当前判断

最可能的武器切换 CD 位置：

```text
/Script/RogueCore.AnimatedItem.EquipDuration
```

实际改点：

```text
具体武器的 WPN_* 蓝图 CDO
```

代表性实测：

```text
Content/WeaponsNTools/Pistol/WPN_Pistol_A.uasset
  EquipDuration = 0.5f

Content/Unlocks/Items/Weapons/Crossbow/WPN_Crossbow_BXE.uasset
  EquipDuration = 0.5f
  SwitchTime = 0.15f  ← Crossbow 内部切换，不是通用换枪
```
