# RogueCore 入场空降仓位置分析

> 游戏资源路径：`/Users/bytedance/Project/RogueCore`  
> Mappings：`maps/RogueCore-5.6.1-140986+main-0196ef29.usmap`  
> UE 版本：`VER_UE5_6`  
> 分析产物：`UAssetStudio/analysis/rc_entry/`

## 结论

进入任务时的空降仓位置 **可以修复**，但不建议直接改 `BP_BXE_EliteDropPod_Landing` 里的固定坐标。

当前链路中，第一关入场仓由 `GM_BXE` 指定为 `BP_BXE_EliteDropPod_Landing_C`，实际落点由程序化关卡生成流程在 `PLS_Base` / `PLS_RC_Base` 中计算，然后传给 `TeamTransport.DropToMission`。

目标“选武器的房间”对应 `Construction_Entrance` 起始房间链路。`PLS_RC_Base` 会强制第一段隧道装饰从 `BP_Construction_Entrance1-5` 中选择，这些蓝图包含起始房间内的武器/工具箱、协商球和玩家起点。

## 关键资产

| 作用 | 资产 |
|------|------|
| RogueCore GameMode | `Content/Game/GM_BXE.uasset` |
| 第一关入场空降仓类 | `Content/GameElements/DropPod/BP_BXE_EliteDropPod_Landing.uasset` |
| 起始房间程序化设置 | `Content/Landscape/ProceduralLevelSetups/PLS_RC_Base.uasset` |
| 程序化生成基类 | `Content/Landscape/PLS_Base.uasset` |
| 选武器/起始房间蓝图 | `Content/Art/Environments/Constructions/Construction_Entrance/BP_Construction_Entrance1-5.uasset` |

## 证据

`GM_BXE` 指定第一关使用 Elite DropPod，后续关卡入口才使用电梯：

```kms
SoftObject EntranceElevatorClass = "BP_Elevator_Start_C";
SoftObject FirstLevelEntranceDropPodClass = "BP_BXE_EliteDropPod_Landing_C";
SoftObject LastLevelEscapeDropPodClass = "BP_BXE_EliteDropPod_Escape_C";
```

`PLS_RC_Base` 指定第一段隧道装饰为 Construction Entrance：

```kms
Array<Struct> ForcedFirstTunnelDecorations = [
    { TunnelDecoration: "BP_Construction_Entrance1_C", SpawnWeight: 1.0f },
    { TunnelDecoration: "BP_Construction_Entrance2_C", SpawnWeight: 1.0f },
    { TunnelDecoration: "BP_Construction_Entrance3_C", SpawnWeight: 1.0f },
    { TunnelDecoration: "BP_Construction_Entrance4_C", SpawnWeight: 1.0f },
    { TunnelDecoration: "BP_Construction_Entrance5_C", SpawnWeight: 1.0f }
];
```

`BP_Construction_Entrance1` 引用了起始房间奖励/选项相关对象：

```kms
from "/Game/GameElements/RewardGivers/Supplies/CoopStation/BP_BXE_DroneActivatedWeaponAndTraversalCrate" import ...
from "/Game/GameElements/RewardGivers/Supplies/CoopStation/BP_BXE_GrenadeCrateStartingRoom" import ...
from "/Game/GameElements/RewardGivers/BP_PlayerNegotiationSphere" import class BP_PlayerNegotiationSphere_C;
from "/Game/Game/BP_PlayerStart" import class BP_PlayerStart_C;
```

## 推荐修改方案

推荐在 `PLS_RC_Base` 覆盖/调整第一关 `CreateSpawn` 逻辑：

1. 保留第一关判断：`RunManager.IsFirstStageActive()`。
2. 复用 `PLS_RC_Base.LoadBarrierTransform()` 得到的 `BarrierTransform`，或直接使用 `FirstTunnel.Entrance.Location` / `FirstTunnel.Entrance.Direction`。
3. 从房间门口沿入口方向加一个安全偏移，建议先从 `2500-4000` Unreal units 测试。
4. 将偏移后的点传入 `TeamTransport.AdjustLandingLocationToGround(..., 2000, true)`。
5. 用调整后的点调用 `TeamTransport.DropToMission` 生成 `BP_BXE_EliteDropPod_Landing_C`。
6. 对最终落点调用 `AddImportantLocation(location, 1000-1500)`，避免后续生成物占位。

## 风险点

- 空降仓体积大，落得太近会堵住 `Construction_Entrance` 门口或压到武器箱。
- 不能只改 `BP_BXE_EliteDropPod_Landing.DropHeight`；它只影响下落高度/动画，不决定最终目标点。
- 不建议把 `FirstLevelEntranceDropPodClass` 改成电梯类，否则会改变第一关入场表现和玩家出生链路。
- `BP_BXE_EliteDropPod_Landing` 反编译不稳定，位置逻辑应优先从 `PLS_Base` / `PLS_RC_Base` 侧修。

## 建议验证

1. 固定随机种子进入第一关，确认空降仓最终落点在选武器房间前。
2. 测试 1-4 人出生，确认 `BP_PlayerStart` 仍正常生效。
3. 检查空降仓开门方向是否朝向房间入口。
4. 确认武器箱、协商球、屏障、电缆起点没有被空降仓或落地 carve 破坏。
