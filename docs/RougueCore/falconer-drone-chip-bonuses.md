# RogueCore 御鹰者无人机局外芯片加成分析

> 游戏资源路径：`/Users/bytedance/Project/RogueCore`  
> Mappings：`maps/RogueCore-5.6.1-143055+main-7f7cc36f_by_Iris.usmap`  
> UE 版本：`VER_UE5_6`  
> 分析产物：`UAssetStudio/analysis/falconer_drone_analysis/`

## 结论

**御鹰者无人机伤害可以吃一部分局外芯片技能加成，但不是所有芯片都适用。**

无人机的攻击链路是：

```text
BP_ShockDrone
  -> ProjectileClass = PRJ_ShockDrone_C
  -> ImpactDamage = DMG_ShockDrone_Projectile
```

`DMG_ShockDrone_Projectile` 的两个伤害实例都带有：

```text
DamageTags = [DT_AbilityDamage]
DamageVector = Ranged
DamageType = DMG_Electric
DynamicBonusesEnabled = true
CanCrit = true
```

因此它不是纯环境伤害，而是走 RogueCore 的动态伤害结算链路；能被通用伤害、暴击、电属性、目标状态类的动态加成影响。依赖武器标签、弹匣、换弹、近战或玩家移动事件的芯片，则通常不会直接作用到无人机弹体。

## 核心资产

| 作用 | 资产 |
|------|------|
| 御鹰者无人机主体 | `Content/Unlocks/Items/AbilityItems/ShockDrone/BP_ShockDrone.uasset` |
| 无人机投射物 | `Content/Unlocks/Items/AbilityItems/ShockDrone/PRJ_ShockDrone.uasset` |
| 无人机伤害资产 | `Content/Unlocks/Items/AbilityItems/ShockDrone/DMG_ShockDrone_Projectile.uasset` |
| 伤害标签 | `Content/GameElements/Damage/Tags/DT_AbilityDamage.uasset` |
| 通用伤害加成 PawnStat | `Content/GameElements/PawnStats/PST_DamageBonus.uasset` |

`BP_ShockDrone.kms` 中关键字段：

```kms
Object<BlueprintGeneratedClass> ProjectileClass = PRJ_ShockDrone_C;
float AttackRange = 800.0f;
float AttackInterval = 0.12f;
Struct<RandRange> BurstInterval = { min: 1.6f, max: 1.6f };
```

`PRJ_ShockDrone.kms` 中关键字段：

```kms
Object<DamageAsset> ImpactDamage = DMG_ShockDrone_Projectile;
```

`DMG_ShockDrone_Projectile.kms` 中有两个伤害段：

| 段 | Damage | Method | Radius | 说明 |
|----|--------|--------|--------|------|
| 直击 | `10.0f` | `Direct` | `0.0f` | 命中目标本体 |
| 范围 | `25.0f` | `Radial` | `300.0f` | 爆炸/范围电击 |

两个伤害段都启用了动态加成和暴击：

```text
DynamicBonusesEnabled = true
CanCrit = true
```

## 能吃的加成类型

### 通用动态伤害加成

例如 `GunLink`：

```text
Unlock_GunLink_15p
  -> BP_BXE_LogicUnlock_GunLink_C
  -> STE_GunLink_C
  -> PST_DamageBonus
```

`STE_GunLink.kms`：

```kms
object DynamicStatChangeStatusEffectItem_1 : DynamicStatChangeStatusEffectItem {
    Object<PawnStat> Stat = PST_DamageBonus;
    float StatChange = 0.33f;
}
```

`PST_DamageBonus` 是 `Additive` 类型 PawnStat。只要伤害结算使用玩家作为 instigator 并读取玩家状态，这类通用伤害加成理论上会作用到无人机投射物。

### 暴击/弱点暴击参数

例如 `CriticalShot`：

```kms
object WeakpointCritDamageParamBonus_0 : WeakpointCritDamageParamBonus {
    float AdditionalCritChance = 0.3f;
}
```

无人机伤害资产 `CanCrit = true`，所以这类暴击参数加成有条件生效。

### 目标状态类伤害加成

例如 `FishInABarrel`：

```kms
object PercentDamageBonus_1 : PercentDamageBonus {
    float PercentBonus = 25.0f;
    Object<TargetStateDamageCondition> Condition = TargetStateDamageCondition_0;
}

object TargetStateDamageCondition_0 : TargetStateDamageCondition {
    Enum<ETargetStateDamageBonusType> TargetState = Staggered;
}
```

这类加成依赖目标状态，不依赖武器本身，因此无人机命中符合状态的目标时应可参与计算。

### 电属性相关逻辑

无人机伤害是 `DMG_Electric`，并且附带 `DMG_Electric_ElementOnly` 转换加成：

```kms
Object<DamageClass> DamageClass = DMG_Electric_ElementOnly;
```

所以电属性触发、监听或电元素相关的逻辑有机会被无人机命中触发。不过具体芯片还要看它是否监听伤害事件，还是绑定玩家移动、近战、武器等其它事件。

## 不太会吃的加成类型

### 武器标签 / 弹匣 / 换弹类

例如 `LowAmmoBigDamage`：

```kms
object ClipBasedDamageBonus_0 : ClipBasedDamageBonus {
    Enum<EDamageBonusType> Type = Mutliply;
    bool InvertBonus = true;
    Object<WeaponTagCondition> Condition = WeaponTagCondition_0;
}

object WeaponTagCondition_0 : WeaponTagCondition {
    Array<Object<WeaponRangeTag>> HasTags = [WT_CloseRange, WT_LongRange, WT_MidRange];
    Enum<ETagConditionType> Type = HasAny;
}
```

无人机投射物不是玩家当前手持武器，也没有弹匣/换弹语义，因此这类加成通常不会应用到无人机伤害。

### 近战类

例如 `ArcLunge` 的选择条件包含近战、电属性以及 Solo Drone 分类条件，但逻辑类是独立触发：

```text
BP_BXE_LogicUnlock_ArcLunge_C
```

它不是修改所有电伤害，而是实现一个额外动作/逻辑，不能简单认为无人机每次电击都会吃到。

### 移动事件类

例如 `BoltDash` 绑定玩家移动/碰撞逻辑，自己的伤害实例虽然也是电属性动态伤害，但触发源是玩家移动事件，不是无人机弹体命中。

## 技能冷却类

`AbilityBooster` 不是伤害加成，而是监听玩家能力消耗后减少能力/强力攻击冷却：

```kms
AddMulticastDelegate(
    Context(CallFunc_GetAbilityComponent_ReturnValue_1, InstanceVariable("OnChargeConsumed")),
    K2Node_CreateDelegate_OutputDelegate
);
```

它可能影响御鹰者技能释放后的冷却，但不直接提高无人机弹体伤害。

## 判断规则

判断某个局外芯片是否影响御鹰者无人机，可以按这个顺序看：

1. 芯片是否是 `DamageBonusAction` / `DamageParamBonusAction`，且没有武器标签、弹匣、换弹、近战等限制。
2. 芯片是否依赖 `DamageTags`、`DamageVector`、`DamageType`、目标状态、暴击参数等伤害结算上下文。
3. 芯片是否只监听玩家自身事件，例如移动、换弹、能力消耗、近战命中。
4. 芯片是否通过 `StatusEffect` 给玩家挂 `PST_DamageBonus` 这类通用 PawnStat。

如果是第 1、2、4 类，较可能影响无人机；如果是第 3 类或依赖武器标签/弹匣，一般不会影响无人机。

## 复查命令

反编译无人机主体、投射物和伤害资产：

```bash
mkdir -p analysis/falconer_drone_analysis

dotnet run --project UAssetStudio.Cli -- decompile \
  "/Users/bytedance/Project/RogueCore/Content/Unlocks/Items/AbilityItems/ShockDrone/BP_ShockDrone.uasset" \
  --mappings "maps/RogueCore-5.6.1-143055+main-7f7cc36f_by_Iris.usmap" \
  --ue-version VER_UE5_6 \
  --outdir "analysis/falconer_drone_analysis" --meta

dotnet run --project UAssetStudio.Cli -- decompile \
  "/Users/bytedance/Project/RogueCore/Content/Unlocks/Items/AbilityItems/ShockDrone/PRJ_ShockDrone.uasset" \
  --mappings "maps/RogueCore-5.6.1-143055+main-7f7cc36f_by_Iris.usmap" \
  --ue-version VER_UE5_6 \
  --outdir "analysis/falconer_drone_analysis" --meta

dotnet run --project UAssetStudio.Cli -- decompile \
  "/Users/bytedance/Project/RogueCore/Content/Unlocks/Items/AbilityItems/ShockDrone/DMG_ShockDrone_Projectile.uasset" \
  --mappings "maps/RogueCore-5.6.1-143055+main-7f7cc36f_by_Iris.usmap" \
  --ue-version VER_UE5_6 \
  --outdir "analysis/falconer_drone_analysis" --meta
```

搜索关键字段：

```bash
rg "ProjectileClass|ImpactDamage|DT_AbilityDamage|DynamicBonusesEnabled|CanCrit|PST_DamageBonus|AdditionalCritChance" \
  analysis/falconer_drone_analysis
```

生成 CFG 摘要：

```bash
mkdir -p analysis/falconer_drone_analysis/cfg

dotnet run --project UAssetStudio.Cli -- cfg \
  "/Users/bytedance/Project/RogueCore/Content/Unlocks/Items/AbilityItems/ShockDrone/BP_ShockDrone.uasset" \
  --mappings "maps/RogueCore-5.6.1-143055+main-7f7cc36f_by_Iris.usmap" \
  --ue-version VER_UE5_6 \
  --outdir "analysis/falconer_drone_analysis/cfg"
```

## 当前判断

御鹰者无人机伤害应当被归为：

```text
AbilityDamage + Ranged + Electric + DynamicBonusEnabled + CanCrit
```

所以它能吃通用伤害/暴击/目标状态/电属性这类动态伤害加成；但不能默认吃所有局外芯片，尤其是武器标签、弹匣、换弹、近战和移动事件类芯片。
