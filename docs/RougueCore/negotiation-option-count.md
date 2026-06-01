# RogueCore 局内谈判选项数量分析

> 游戏资源路径：`/Users/bytedance/Project/RogueCore`  
> Mappings：`maps/RogueCore-5.6.1-143055+main-7f7cc36f_by_Iris.usmap`  
> UE 版本：`VER_UE5_6`  
> 分析产物：`analysis_out/roguecore_negotiation/*.kms`

## 结论

局内升级时弹出的“谈判”选项数量，不是在 `Menu_Negotiation` 的 Widget 蓝图里硬编码的。

`WND_BXE_Negotiation` 只是读取原生 `BXENegotiationWidget.NegotiationData` 里的两个数组：

```text
NegotiationData.Unlocks
NegotiationData.DroneUnlocks
```

然后按数组长度动态创建按钮。也就是说：数组里有几个 unlock，就显示几个选项。

目前在资源资产里没有找到可直接修改“选项数量”的字段，例如 `OptionCount`、`ChoiceCount`、`MaxOptions`、`SelectableCards` 之类。实际数量更像是在 `/Script/RogueCore` 的原生逻辑里生成 `BXENegotiationData` 时决定的。

## 关键 UI 逻辑

核心窗口：

```text
Content/UI/Menus/Menu_Negotiation/WND_BXE_Negotiation.uasset
```

`CreateUnlockButtons` 的关键流程：

```text
Unlocks = NegotiationData.Unlocks
DroneUnlocks = NegotiationData.DroneUnlocks

for each Unlocks:
  Create UI_BXE_Negotiation_Entry
  Add to UnlockWidgets

Unlocks_Row.SetEntries(UnlockWidgets, 0)

for each DroneUnlocks:
  Create ITM_CooperNegotiatedUpgrade
  Add to DroneUnlockWidgets

Unlocks_Drone_Row.SetEntries(DroneUnlockWidgets, Array_Length(DroneUnlockWidgets))
```

因此 `WND_BXE_Negotiation` 只负责展示，不负责决定抽几个。

## 容易误判的字段

`Content/UI/Menus/Menu_Negotiation/UI_Negotiation_Row.uasset` 里有：

```text
PreviewCount = 6
```

这个字段只是 Widget 设计时预览用的数量。真实局内逻辑使用 `SetEntries(InEntries, InPadToCount)` 传入的数组，不读取 `PreviewCount`。改它不会改变游戏内谈判选项数量。

## 可修改的内容

谈判可抽到的升级池在：

```text
Content/Unlocks/Collections/UP_NegotiatedUpgradeUnlocks.uasset
```

关键字段：

```text
Unlocks = [...]
SkipReward = Unlock_SkipForHealth
RarityWeightType = NegotiatedUnlocks
NegotiationCompleteStat = MS_ExpeniteUpgradesAquired
AddDroneUnlocks = true
```

这里可以修改“池子里有哪些升级”“是否添加 DroneUnlocks”“跳过奖励是什么”，但不能直接修改一次谈判展示几个选项。

池标签引用链：

```text
Content/Unlocks/Collections/CollectionTags/UCT_NegotiatedUnlocks.uasset
  -> DefaultCollection = UP_NegotiatedUpgradeUnlocks

Content/Game/GameData/BXESettings/Progression/GD_BXE_ProgressionSettings.uasset
  -> CollectionTags 包含 UCT_NegotiatedUnlocks
```

## 全局设置检查

`Content/Game/GameData/BXESettings/GD_BXESettings.uasset` 只暴露了谈判窗口类：

```text
NegotiationMenuSettings = {
  NegotiationWidget: "WND_BXE_Negotiation_C"
}
```

没有发现选项数量字段。

作弊窗口 `Content/UI/Menus/Menu_Cheats/UI_BXE_Cheat_Negotiation.uasset` 调用的是：

```text
Cheat_StartNegotiation(this, Negotiation Collection)
```

它只传入 `UP_NegotiatedUpgradeUnlocks` 这样的 unlock pool，也没有传入数量参数。

## 特殊风险向量

`Content/GameElements/Missions/Mutators/JumbleNegotiation/MUT_JumbleNegotiation.uasset` 会覆盖谈判池：

```text
NegotiationOverride = UPC_AllUnlockPools
```

`RV_Jumble.uasset` 引用该 mutator。这个风险向量会影响“从哪个池抽”，但仍没有暴露“抽几个”的配置。

## 修改方向

如果目标是“改变可出现的升级内容”，优先改：

```text
Content/Unlocks/Collections/UP_NegotiatedUpgradeUnlocks.uasset
```

如果目标是“把一次谈判选项数从 6 改成 8 / 10”，目前判断不能只靠现有 DataAsset 配置完成，需要 patch 原生逻辑里生成 `BXENegotiationData.Unlocks` / `DroneUnlocks` 的数量。

## 分析命令

示例反编译：

```bash
dotnet run --project UAssetStudio.Cli -- decompile \
  "/Users/bytedance/Project/RogueCore/Content/UI/Menus/Menu_Negotiation/WND_BXE_Negotiation.uasset" \
  --mappings "maps/RogueCore-5.6.1-143055+main-7f7cc36f_by_Iris.usmap" \
  --ue-version VER_UE5_6 \
  --outdir "analysis_out/roguecore_negotiation" \
  --meta
```

重点反编译资产：

```text
Content/UI/Menus/Menu_Negotiation/WND_BXE_Negotiation.uasset
Content/UI/Menus/Menu_Negotiation/UI_Negotiation_Row.uasset
Content/UI/Menus/Menu_Cheats/UI_BXE_Cheat_Negotiation.uasset
Content/Unlocks/Collections/UP_NegotiatedUpgradeUnlocks.uasset
Content/Unlocks/Collections/CollectionTags/UCT_NegotiatedUnlocks.uasset
Content/Game/GameData/BXESettings/GD_BXESettings.uasset
Content/Game/GameData/BXESettings/Progression/GD_BXE_ProgressionSettings.uasset
Content/GameElements/Missions/Mutators/JumbleNegotiation/MUT_JumbleNegotiation.uasset
Content/GameElements/Missions/RiskVectors/Jumble/RV_Jumble.uasset
```
