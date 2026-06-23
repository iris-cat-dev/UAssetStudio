# RogueCore 钢铁意志与 DRG 旧被动技能接入分析

> 游戏资源路径：`/Users/bytedance/Project/RogueCore`  
> Mappings：`maps/RogueCore-5.6.1-143055+main-7f7cc36f_by_Iris.usmap`  
> UE 版本：`VER_UE5_6`  
> 分析产物：`UAssetStudio/tmp/ironwill-passive-analysis/`

## 结论

**钢铁意志（Iron Will）及一批 DRG 经典 KPI 被动技能，在当前 RogueCore 资源包中仍是完整可运行的 `PerkAsset` 实现，但默认玩法没有把它们发给玩家。**

- **技术上可以接入**：底层 `PerkComponent`、`PlayerHealthComponent.SetIronWillStatusEffect`、`STE_IronWill`、HUD/音效/图标均存在；反编译逻辑可正常 round-trip。
- **玩法上当前未启用**：6 个职业的 `PCD_*` 均未挂载 `Skill_IronWill`；局外 Enhancement Tree 也未引用任何旧 KPI 技能。
- **推荐接入路径**：把目标 `Skill_*` 写入某个职业的 `PlayerCharacterData.ClassSkills`（替换现有技能槽），比改 RewardTree 更简单；Gorgon 职业甚至没有 `ClassSkills`，可作为试验职业。

---

## 两套技能体系

RogueCore 当前并存两套 Perk 相关资产：

| 体系 | 路径前缀 | 代表技能 | 当前状态 |
|------|----------|----------|----------|
| DRG 旧 KPI 被动 | `GameElements/KPI/Perks/`、`GameElements/Skills/IronWill` 等 | Iron Will、Dash、Field Medic、Heightened Senses、Hover Boots | 资产完整，在 `GD_PerkSettings` 注册，**未挂到职业** |
| RogueCore 新职业技能 | `GameElements/Skills/<Name>/Skill_*` | Blitz、Thunder Rod、Rage、Armor Restore Beacon 等 | **已挂到各职业 `ClassSkills`** |

判断「能否加入当前游戏」，核心看三类引用：

1. `PCD_*` → `ClassSkills`（局内职业默认技能）
2. `RewardTree_ClosedAlpha_ver4-CheapGates` → `EnhancementReward` → `Perk`（局外升级树）
3. `GD_PerkSettings` → `Perks`（全局 Perk 注册表，供运行时查找）

仅有第 3 项不足以让玩家在游戏中获得该技能。

---

## 钢铁意志（Iron Will）资产链

| 角色 | 资产路径 |
|------|----------|
| Perk 数据 | `Content/GameElements/Skills/IronWill/Skill_IronWill.uasset` |
| 激活效果 | `Content/GameElements/Skills/IronWill/Skill_IronWill_ActivationEffect.uasset` |
| 激活 HUD | `Content/GameElements/Skills/IronWill/Skill_IronWill_ActivationHUD.uasset` |
| 状态效果 | `Content/GameElements/KPI/Perks/STE_IronWill.uasset` |
| 图标 | `Content/UI/Art/Icons/Icons_KPI_Perks/Icon_Iron_Will.uasset` |
| 粒子 | `Content/GameElements/KPI/Perks/PerkFX/NS_Iron_Will_Converted.uasset` |
| 激活喊话 | `Content/Character/Shouts/NewMarts2020/Shout_Perk_IronWill_Activation.uasset` |

`Skill_IronWill` 关键属性（JSON 导出）：

| 字段 | 值 |
|------|-----|
| `Title` | Iron Will |
| `MaxUseCharges` | 1 |
| `CoolDownBetweenUse` | 1.0 |
| 效果描述值 | 6.0（`PERK_IronWill` 字符串表条目） |
| 操作提示 | Hold \<JUMP/\> after going down to activate. |

激活逻辑（`Skill_IronWill_ActivationEffect` 反编译摘要）：

```text
Receive_ActivatePerk(character, value)
  → character.HealthComponent.SetIronWillStatusEffect(STE_IronWill_C)
  → statusEffect.Duration = value
```

玩家基类 `BP_PlayerCharacter` 仍保留 DRG 倒地/Perk 相关字段：

| 组件/字段 | 说明 |
|-----------|------|
| `CharacterPerkContainerComponent` (`PerkComponent`) | Perk 容器 |
| `Health.IronWillTimeToActivate` | 10.0（倒地后激活窗口） |
| `InstantReviveCue` / `InstantReviveParticleSystem` | Field Medic 瞬复特效残留 |
| `RunBoostActivationSound` / `RunBoostParticles` | Marathon Guy 残留 |
| `DashStatusEffect` / `DashInputWindow` 等 | Dash 残留 |
| `Receive_ShowFieldMedicInstantReviveEffects` | Field Medic 事件入口 |

说明引擎侧重写逻辑仍在，旧被动并非「只剩本地化文本」。

---

## 全局 Perk 注册：`GD_PerkSettings`

路径：`Content/Game/GameData/GD_PerkSettings.uasset`

`Perks` 结构体当前注册的条目：

| 键名 | 引用资产 |
|------|----------|
| `IronWill` | `Skill_IronWill` |
| `DashPerk` | `Skill_Dash` |
| `FieldMedic` | `Skill_FieldMedic` |
| `HeightenedSenses` | `Skill_HeightenedSense` |
| `HoverBoots` | `Skill_HoverBoots` |
| `ShieldLink` | `PERK_ShieldLink` |
| `Bezerk` | `PERK_Berzerker` |
| `JumpBoots` | `PERK_JumpBoots` |
| `DownedBomb` | `PERK_DownedBomb` |

`BP_GameData` 通过 `PerkSettings` 字段引用此资产。注册存在 ≠ 玩家自动获得。

---

## 当前职业 `ClassSkills` 挂载

各职业 `PlayerCharacterData`（`Content/Character/Reclaimers/PCD_*.uasset`）的 `ClassSkills` 数组：

| 职业 | 被动技能 1 | 被动技能 2 | 第 3 项（主能力 tooltip） |
|------|------------|------------|---------------------------|
| Slicer | `Skill_Blitz` | `Skill_ShieldProjectorBelt` | `AbilitySkill_Slicer_Slice` |
| Falconer | `Skill_RemoteDroneRevive` | `Skill_ThunderRod` | `AbilitySkill_Falconer_ShockDrone` |
| Guardian | `Skill_ArmorRestoreBeacon` | `Skill_RepelAbility` | `AbilitySkill_Guardian_ConcussionBarrage` |
| Retcon | `Skill_ContingencyPlan` | `Skill_Rage` | `AbilitySkill_Retcon_Rewind` |
| Spotter | `Skill_RangersPocket` | `Skill_SonarRadar` | `AbilitySkill_Spotter_CritDart` |
| Gorgon | *(无 `ClassSkills`)* | — | 仅有 `AbilityData` → `ID_Ability_StasisBeam` |

**没有任何职业引用** `Skill_IronWill`、`Skill_Dash`、`Skill_FieldMedic`、`Skill_HeightenedSense`、`Skill_HoverBoots`。

键位：`GD_KeyBindingSettings` 中职业技能绑定为 `Skill_1`、`Skill_2`，与每职业两个被动槽一致。

---

## 局外 Enhancement Tree

路径：`Content/GameElements/Enhancements/EnhancementTrees/RewardTree_ClosedAlpha_ver4-CheapGates.uasset`

对 RewardTree JSON 全文检索 `Skill_IronWill`、`Skill_Dash`、`Skill_FieldMedic` 等旧 KPI 技能：**0 命中**。

局外树当前 92 个节点均为 RogueCore 新 Enhancement（如 `SteadyAim`、`Marksman` 等），不含 DRG 经典被动。详见 [trigger-discipline-enhancement.md](./trigger-discipline-enhancement.md) 中对 RewardTree 的分析方法。

---

## 旧 KPI 被动技能资产完整度

| 技能 | `Skill_*` 主资产 | 效果/组件蓝图 | 备注 |
|------|------------------|---------------|------|
| Iron Will | ✅ | `Skill_IronWill_ActivationEffect` | 完整 |
| Dash | ✅ | `Skill_Dash_Logic` | 完整 |
| Field Medic | ✅ | `Skill_FieldMedic_Logic`、`Skill_FieldMedic_ActivationEffect` | 完整 |
| Heightened Senses | ✅ | `Skill_HeightenedSense_Component` | 完整 |
| Hover Boots | ✅ | `Skill_HoverBoots_BurnTrigger` | 完整 |
| Marathon Guy | ❌ 无 `Skill_MarathonGuy.uasset` | `Skill_MarathonGuy_RunBoostEffect/Time` 存在 | 缺主 PerkAsset，需重建或拼装 |
| Remote Drone Revive | ✅ `Skill_RemoteDroneRevive` | `BP_Skill_DroneRevive` | 已挂 Falconer |

RogueCore 新职业技能（Blitz、Rage、Thunder Rod 等）普遍通过 `BXELogicAction` + `LogicUnlockClass` 挂接 `BP_*PerkComponent`，与旧 KPI 的 `PerkEffect` 直挂模式不同，但共用 `PerkAsset` / `PerkComponent` 运行时。

---

## 接入方案

### 方案 A：替换职业技能（推荐）

修改目标 `PCD_*.uasset` 的 `ClassSkills[0]` 或 `[1]`，将现有 `Skill_*` 换成 `Skill_IronWill`（或其他旧被动）。

优点：

- 改动面小，沿用现有 `Skill_1` / `Skill_2` 输入与 HUD
- 不需要动 RewardTree 或新建 Enhancement 节点

注意：

- 会替换该职业原有 RogueCore 技能，需权衡设计
- Gorgon 无 `ClassSkills`，需先补数组再挂技能

### 方案 B：局外升级树

在 `RewardTree_ClosedAlpha_ver4-CheapGates` 新增 `EnhancementReward` 节点，引用 `Skill_IronWill` 等 `PerkAsset`。

优点：不占用职业技能槽  
缺点：需补图标、本地化、树节点连线；玩家需先解锁才能获得

### 方案 C：运行时注入（UE4SS / Cheat）

利用 `PerkComponent` 在运行时 `GrantPerk` 类 API（需进一步逆向确认具体函数名）。适合快速验证，不适合长期 Mod 分发。

---

## 验证清单

接入 `Skill_IronWill` 后，建议在多人局中确认：

1. 职业选择界面 / 技能说明是否显示 Iron Will 图标与描述
2. 倒地后 HUD 是否出现激活提示（`Skill_IronWill_ActivationHUD`）
3. 按住跳跃是否在 `IronWillTimeToActivate`（10s）窗口内触发
4. 是否应用 `STE_IronWill` 状态效果并播放 `Shout_Perk_IronWill_Activation`
5. `MaxUseCharges = 1` 用完后冷却 UI 是否正常

---

## 复现命令

```bash
mkdir -p tmp/ironwill-passive-analysis/{assets,pcd,kms}

# RewardTree
dotnet run --project UAssetStudio.Cli -- --json json \
  "/Users/bytedance/Project/RogueCore/Content/GameElements/Enhancements/EnhancementTrees/RewardTree_ClosedAlpha_ver4-CheapGates.uasset" \
  --mappings "maps/RogueCore-5.6.1-143055+main-7f7cc36f_by_Iris.usmap" \
  --ue-version VER_UE5_6 \
  --out "tmp/ironwill-passive-analysis/RewardTree_ClosedAlpha_ver4-CheapGates.json"

# 全局 Perk 设置
dotnet run --project UAssetStudio.Cli -- --json json \
  "/Users/bytedance/Project/RogueCore/Content/Game/GameData/GD_PerkSettings.uasset" \
  --mappings "maps/RogueCore-5.6.1-143055+main-7f7cc36f_by_Iris.usmap" \
  --ue-version VER_UE5_6 \
  --out "tmp/ironwill-passive-analysis/GD_PerkSettings.json"

# 钢铁意志 Perk + 激活效果
dotnet run --project UAssetStudio.Cli -- --json json \
  "/Users/bytedance/Project/RogueCore/Content/GameElements/Skills/IronWill/Skill_IronWill.uasset" \
  --mappings "maps/RogueCore-5.6.1-143055+main-7f7cc36f_by_Iris.usmap" \
  --ue-version VER_UE5_6 \
  --out "tmp/ironwill-passive-analysis/assets/Skill_IronWill.json"

dotnet run --project UAssetStudio.Cli -- --json decompile \
  "/Users/bytedance/Project/RogueCore/Content/GameElements/Skills/IronWill/Skill_IronWill_ActivationEffect.uasset" \
  --mappings "maps/RogueCore-5.6.1-143055+main-7f7cc36f_by_Iris.usmap" \
  --ue-version VER_UE5_6 \
  --outdir "tmp/ironwill-passive-analysis/kms" --meta

# 所有职业数据
for f in /Users/bytedance/Project/RogueCore/Content/Character/Reclaimers/PCD_*.uasset; do
  name=$(basename "$f" .uasset)
  dotnet run --project UAssetStudio.Cli -- --json json "$f" \
    --mappings "maps/RogueCore-5.6.1-143055+main-7f7cc36f_by_Iris.usmap" \
    --ue-version VER_UE5_6 \
    --out "tmp/ironwill-passive-analysis/pcd/${name}.json"
done

# 检索 RewardTree / PCD 中的技能引用
rg "Skill_IronWill|Skill_Dash|Skill_FieldMedic|ClassSkills" tmp/ironwill-passive-analysis/
```
