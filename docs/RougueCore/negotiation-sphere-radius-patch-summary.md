# RogueCore 谈判圈范围与一人触发 Patch 总结

> 游戏资源路径：`/Users/bytedance/Project/RogueCore`  
> Mappings：`maps/RogueCore-5.6.1-143055+main-7f7cc36f_by_Iris.usmap`  
> UE 版本：`VER_UE5_6`  
> 输出根目录：`/Users/bytedance/Project/UAssetStudio/output`

## 结论

“武器获取”“技能升级”“升级就绪，等待全体复拓者到齐”最终都走同一类等待逻辑：`BP_PlayerNegotiationSphere` 统计当前进入圈内且落地的玩家数，再在 `OnRep_PlayerCount` 里判断是否满足开始条件。

单纯把碰撞半径改到 `9999` 不一定能解决截图里的问题。原因是远处玩家如果没有触发 overlap，`PlayersInside` 仍然只会包含当前进入圈的玩家，HUD 仍会显示“1 人在圈内、另 1 人未到齐”。因此最终有效修复是改人数判断：

```text
PlayerCount == Array_Length(GetPlayersForNegotiationStart(this))
```

改为：

```text
PlayerCount > 0
```

也就是只要 Host/服务端统计到至少 1 个落地玩家进入圈，就触发“全员已到齐”后的读条流程。

## 最终有效资产

### `GameElements/RewardGivers/BP_PlayerNegotiationSphere.uasset`

作用：所有谈判圈的核心组件，包括武器箱、BioBooster/技能升级等场景里显示“等待全员到齐”的蓝色圈。

已改内容：

```text
默认 SphereRadius: 350.0 -> 9999.0
OnRep_PlayerCount 内部二次判断:
  CallFunc_EqualEqual_IntInt_ReturnValue
  -> CallFunc_Greater_IntInt_ReturnValue
```

验证点：

```text
OnRep_PlayerCount 的 CodeOffset 751 分支条件
从 CallFunc_EqualEqual_IntInt_ReturnValue
变为 CallFunc_Greater_IntInt_ReturnValue
```

当前可安装输出：

```text
output/Content/GameElements/RewardGivers/BP_PlayerNegotiationSphere.uasset
output/Content/GameElements/RewardGivers/BP_PlayerNegotiationSphere.uexp
```

## 过程中排查过的范围补丁

下面这些修改扩大了可交互/发现范围，但不能替代 `BP_PlayerNegotiationSphere.OnRep_PlayerCount` 的一人触发逻辑。

### `GameElements/RewardGivers/Supplies/CoopStation/BP_BXE_DroneActivatedCoopStation.uasset`

用于武器箱、工具箱等 CoopStation 系列。

排查到的范围字段：

```text
BP_PlayerNegotiationSphere_GEN_VARIABLE.SphereRadius: 300.0 -> 9999.0
FindRange_GEN_VARIABLE.SphereRadius: 800.0 -> 9999.0
```

### `GameElements/BioBooster/BP_BioBooster_Frame.uasset`

用于技能升级 / BioBooster 交互。

排查到的范围字段：

```text
PlayerInterface_Collider_GEN_VARIABLE.SphereRadius: 175.0 -> 9999.0
TutorialTrigger_GEN_VARIABLE.SphereRadius: 600.0 -> 9999.0
ActivateTerminalUse_Collider_GEN_VARIABLE.BoxExtent: (60, 60, 100) -> (9999, 9999, 9999)
```

未改字段：

```text
DropDetection_Collider_GEN_VARIABLE.SphereRadius = 300.0
```

这个字段用于落地/放置检测，不是玩家交互或谈判等待范围。

### `GameElements/RewardGivers/Shop/BP_Cave_Workbench.uasset`

用于工作台类升级/修理交互。

排查到的范围字段：

```text
UsableCollision_GEN_VARIABLE.SphereRadius: 100.0 -> 9999.0
RepairUsableCollision_GEN_VARIABLE.SphereRadius: 120.0 -> 9999.0
TutorialTrigger_GEN_VARIABLE.SphereRadius: 600.0 -> 9999.0
```

## `Unlocks/NonGear/InUse` 结论

`Content/Unlocks/NonGear/InUse` 下的升级资产本身不是谈判圈蓝图，而是 `BXEUnlock*` / 逻辑升级定义。批量扫描这些 `.uasset` 后没有发现 `SphereRadius` 或交互碰撞组件。

唯一命中的类似距离字段是：

```text
Unlocks/NonGear/InUse/Weakpoint/Unlock_Sharpshooter.uasset
DistanceToTargetDamageCondition_0.MinimumDistance = 1000
```

这是技能自身的生效条件距离，不是谈判圈大小。

## 验证命令

校验最终核心资产：

```bash
dotnet run --project UAssetStudio.Cli -- validate \
  "output/Content/GameElements/RewardGivers/BP_PlayerNegotiationSphere.uasset" \
  --mappings "maps/RogueCore-5.6.1-143055+main-7f7cc36f_by_Iris.usmap" \
  --ue-version VER_UE5_6 \
  --game-content "/Users/bytedance/Project/RogueCore/Content"
```

导出 JSON 检查 `OnRep_PlayerCount` 分支条件：

```bash
dotnet run --project UAssetStudio.Cli -- json \
  "output/Content/GameElements/RewardGivers/BP_PlayerNegotiationSphere.uasset" \
  --mappings "maps/RogueCore-5.6.1-143055+main-7f7cc36f_by_Iris.usmap" \
  --ue-version VER_UE5_6 \
  --out "output/rogue_negotiation_radius/verify/BP_PlayerNegotiationSphere.oneplayer.verify.json"
```

然后确认 `CodeOffset: 751` 的 `BooleanExpression` 指向：

```text
CallFunc_Greater_IntInt_ReturnValue
```

## 游戏内验证

1. Host 安装输出资产。
2. 进入多人局，触发武器箱或 BioBooster/技能升级。
3. 只让 1 个玩家进入圈并保持落地。
4. HUD 不应长期停留在“等待全体复拓者到齐”。
5. 应进入“全员已到齐”状态并开始读条。
6. 读条结束后打开武器/技能选择界面。

如果仍卡在等待状态，优先确认安装包里是否包含最新的：

```text
Content/GameElements/RewardGivers/BP_PlayerNegotiationSphere.uasset
Content/GameElements/RewardGivers/BP_PlayerNegotiationSphere.uexp
```
