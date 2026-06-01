# RogueCore 扳机纪律局外升级分析

> 游戏资源路径：`/Users/bytedance/Project/RogueCore`  
> Mappings：`maps/RogueCore-5.6.1-143055+main-7f7cc36f_by_Iris.usmap`  
> UE 版本：`VER_UE5_6`  
> 分析产物：`UAssetStudio/analysis/trigger_discipline/`

## 结论

**Trigger Discipline / 扳机纪律当前没有添加到局外升级树。**

资源包里存在 `Trigger Discipline I` ~ `Trigger Discipline IV` 的本地化文本，但没有对应的局外升级 `PerkAsset` 被挂到 Enhancement Reward Tree。当前局外升级树中只有相近的射击类升级，例如 `SteadyAim`、`Marksman`、`DeadEye` 和 `Bullseye`。

---

## 局外升级入口

RogueCore 的局外升级使用 Enhancement Tree：

| 角色 | 资产 |
|------|------|
| 全局 BXE 设置 | `Content/Game/GameData/BXESettings/GD_BXESettings.uasset` |
| Enhancement Tree | `Content/GameElements/Enhancements/EnhancementTrees/RewardTree_ClosedAlpha_ver4-CheapGates.uasset` |
| UI 入口 | `Content/UI/Menus/Menu_RC_StartSelection/Enhancements/Shop/Menu_Enhancements_Shop.uasset` |
| 局内/菜单展示 | `Content/UI/Menus/Menu_Enhancements/WND_RewardTree.uasset` |

`GD_BXESettings` 引用了：

```text
/Game/GameElements/Enhancements/EnhancementTrees/RewardTree_ClosedAlpha_ver4-CheapGates
```

因此判断是否加入局外升级，核心看 `RewardTree_ClosedAlpha_ver4-CheapGates` 的 `Nodes[*].Reward -> EnhancementReward -> Perk` 是否引用目标升级。

---

## 分析结果

将 `RewardTree_ClosedAlpha_ver4-CheapGates.uasset` 转为 JSON 后，共解析到：

| 项目 | 数量 |
|------|------|
| `RewardTreeNode` | 92 |
| `EnhancementReward` | 92 |
| `PerkAsset` 引用 | 92 |
| `Trigger Discipline` 命中 | 0 |

命中的相近射击类升级如下：

| 系列 | 资产路径 |
|------|----------|
| `Steady Aim I-IV` | `Content/GameElements/Abilities/Mods/SteadyAim/MOD_SteadyAim_Tier*.uasset` |
| `Marksman I-IV` | `Content/GameElements/Abilities/Mods/Marksman/MOD_Marksman_Tier*.uasset` |
| `Deadeye I-IV` | `Content/GameElements/Abilities/Mods/DeadEye/MOD_DeadEye_Tier*.uasset` |
| `Bullseye I-IV` | `Content/GameElements/Abilities/Mods/MOD_CritChance*.uasset` |

没有发现以下命名或路径：

```text
TriggerDiscipline
Trigger_Discipline
MOD_TriggerDiscipline
Trigger Discipline
```

---

## Trigger Discipline 实际出现位置

全文搜索 `Trigger Discipline I` ~ `Trigger Discipline IV`，命中位置主要是本地化和喊话资源：

| 类型 | 资产 |
|------|------|
| 英文与多语言文本 | `Content/Localization/Game/*/Game.locres` |
| 友军伤害喊话 | `Content/Character/Shouts/Shout_RC_FriendlyFire.uexp` |
| 对应语音 | `Content/Audio/Characters/Voices/Dwarves/Dwarf_01/RC_Speak/RC_VA_FriendlyFire_54.uexp` |

这说明 `Trigger Discipline` 更像是友军伤害语音/文本条目，而不是已实现的局外升级项。

---

## 复现命令

```bash
mkdir -p analysis/trigger_discipline

dotnet run --project UAssetStudio.Cli -- json \
  "/Users/bytedance/Project/RogueCore/Content/GameElements/Enhancements/EnhancementTrees/RewardTree_ClosedAlpha_ver4-CheapGates.uasset" \
  --mappings "maps/RogueCore-5.6.1-143055+main-7f7cc36f_by_Iris.usmap" \
  --ue-version VER_UE5_6 \
  --out "analysis/trigger_discipline/RewardTree_ClosedAlpha_ver4-CheapGates.json"

rg -a -i --files-with-matches \
  "Trigger Discipline I|Trigger Discipline II|Trigger Discipline III|Trigger Discipline IV|Trigger discipline, dirt hog" \
  "/Users/bytedance/Project/RogueCore/Content"
```

---

## 如果要接入局外升级

需要补齐的点大致是：

1. 新建或复用 `PerkAsset`，例如 `Content/GameElements/Abilities/Mods/TriggerDiscipline/MOD_TriggerDiscipline_Tier*.uasset`。
2. 在 `RewardTree_ClosedAlpha_ver4-CheapGates` 中添加 `EnhancementReward` 并接入 `Nodes`。
3. 确认 `PerkAsset` 的效果类型、`PawnStat` 或自定义效果是否存在。
4. 补充图标、本地化文本和树节点位置/连接。

当前版本没有这些接入痕迹，所以不能只靠本地化文本让它出现在局外升级中。
