# Expenite 改 Nitra 的客户端同步问题

## 结论

之前输出到 `output/` 的 Run 资产修改本身是有效的：`RUN_Generic`、`RUN_Gauntlet`、`RUN_Tutorial` 的 `DefaultResourceDistribution` 中，原本指向 Expenite 的 `ResourceSpawner.Resource` 已替换为 `RES_VEIN_Nitra`，并且三个输出资产都通过了 `validate`。

但多人表现不一致的原因是：RogueCore 的程序化地形不是把完整矿物结果从 Host 复制给客户端，而是客户端接收 `seed` / `rooms` / `obstacles` / `tunnels` 后，在本地调用地形生成逻辑。也就是说，客户端仍会用自己本地安装的 Run / ResourceData 资产解释同一份生成数据。

如果只有 Host 安装了修改后的 Run 资产，客户端没有安装同样的资产，就可能出现 Host 认为生成的是 Nitra，而客户端本地表现仍按 Expenite 或旧资源数据解释的情况。

## 已修改资产

输出位置：

```text
output/Content/GameElements/Runs/RUN_Generic.uasset
output/Content/GameElements/Runs/RUN_Generic.uexp
output/Content/GameElements/Runs/Gauntlet/RUN_Gauntlet.uasset
output/Content/GameElements/Runs/Gauntlet/RUN_Gauntlet.uexp
output/Content/GameElements/Runs/RUN_Tutorial.uasset
output/Content/GameElements/Runs/RUN_Tutorial.uexp
```

这些资产对应的原始资源分布：

```text
RUN_Generic   : RES_VEIN_Expenite          -> RES_VEIN_Nitra
RUN_Gauntlet  : RES_VEIN_Expenite          -> RES_VEIN_Nitra
RUN_Tutorial  : RES_VEIN_Expenite_Tutorial -> RES_VEIN_Nitra
```

验证命令：

```bash
dotnet run --project UAssetStudio.Cli -- --json validate \
  "output/Content/GameElements/Runs/RUN_Generic.uasset" \
  --mappings "maps/RogueCore-5.6.1-143055+main-7f7cc36f_by_Iris.usmap" \
  --ue-version VER_UE5_6 \
  --game-content "/Users/bytedance/Project/RogueCore/Content"
```

`RUN_Gauntlet` 和 `RUN_Tutorial` 同样通过验证，`errorCount = 0`、`warningCount = 0`。

## 同步链路

关键资产：

```text
Content/Landscape/BP_ProceduralController.uasset
Content/Landscape/PLS_CaveGenerator.uasset
Content/Game/BP_GameState.uasset
```

`BP_ProceduralController` 中客户端收到房间数据后，会在本地调用 `GenerateLandscapeFromData`：

```text
ReceivedRoomData(seed, rooms, obstacles)
  -> GetProceduralSetup()
  -> GenerateLandscapeFromData(seed, rooms, obstacles)

ReceivedTunnelData(tunnels)
  -> GetProceduralSetup().Tunnels = tunnels
  -> CarveTunnels()
```

`BP_ProceduralController` 也有服务端 RPC：

```text
Server_RequestPLSData
Server_RequestCarverData
Client_SendTunnelData
```

这说明服务端会提供生成输入数据，但不是把每个矿脉最终使用的 `ResourceData` 完整复制给客户端。客户端地形表现仍依赖本地资产。

`PLS_CaveGenerator` 继承自 `PLS_Base`，并挂有这些生成组件：

```text
ProceduralResources
ProceduralVeins
ProceduralTunnel
ConstructionSpawnerComponent
```

因此资源生成不是只看 `RUN_*` 的一个字段；`RUN_*` 给出默认资源分布，具体地形材质、碎块、掉落物、标题和图标仍由 `RES_VEIN_*` 资源资产决定。

## Nitra 与 Expenite 的资源差异

`RES_VEIN_Nitra` 的关键数据：

```text
Title          : Nitra
Description    : Ammunition_Resource
TerrainMaterial: TM_Nitra
Spawnable      : /Game/GameElements/Resources/Veins/ResourceChunks/BP_NitraChunk
Debris         : /Game/GameElements/Resources/Veins/DBR_Nitra
SavegameID     : {3A307796-43E7-BCB7-3829-878AE805AC72}
```

`RES_VEIN_Expenite` 的关键数据：

```text
Title          : Expenite
Description    : CraftingMaterial_Description
TerrainMaterial: TM_CaveXP
Spawnable      : /Game/GameElements/Resources/Expenite/BP_CaveXP_Chunk
Debris         : /Game/GameElements/Resources/Expenite/DBR_CaveXP
SavegameID     : {A8A69282-429C-5C4F-0670-33A24CC2A43D}
```

所以只要客户端本地仍按 Expenite 资产解释生成结果，表现、掉落块、资源类型和 UI 都可能与 Host 不一致。

## 安装建议

### 多人一致表现

Host 和所有客户端都需要安装同一套修改后的资产：

```text
Content/GameElements/Runs/RUN_Generic.uasset/.uexp
Content/GameElements/Runs/Gauntlet/RUN_Gauntlet.uasset/.uexp
Content/GameElements/Runs/RUN_Tutorial.uasset/.uexp
```

这是当前 DataAsset 修改方案下最可靠的做法。

### Host-only 方案

如果目标是 Host-only 生效，单纯改 `.uasset` 不够。需要进一步改或 Hook 运行时逻辑，让服务端把资源选择结果作为权威网络数据同步，或者把“采集后给什么资源”改为服务端权威逻辑。

可能的后续入口：

```text
BP_ProceduralController
ProceduralResources
ProceduralVeinsComponent
VeinResourceData / VeinResourceCreator
TeamResourcesComponent
```

这条路线更接近 UE4SS / 原生函数 Hook，而不是简单的 DataAsset 替换。

## 排查记录

已确认：

- `output` 中三个 Run 资产均通过 `validate`。
- `RUN_Generic.output.json` / `RUN_Gauntlet.output.json` / `RUN_Tutorial.output.json` 中 Expenite import 已替换为 `RES_VEIN_Nitra`。
- `DefaultResourceDistribution` 的 `ResourceSpawner` 结构只有 `Resource`、`AmountToSpawn`、`SpawnChanceMutator`，未发现额外客户端资源字段。
- `DBA_DeepCore` 没有发现 Expenite 分布入口，主要引用 Iron。
- `DBA_HollowBough` 引用的是 `RES_VEIN_Nitra`。

因此当前问题优先判断为安装/同步模型问题，而不是 Run 资产修改失败。
