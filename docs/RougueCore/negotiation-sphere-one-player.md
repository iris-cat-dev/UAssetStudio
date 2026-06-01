# RogueCore 谈判圈一人触发分析

> 游戏资源路径：`/Users//Project/RogueCore`  
> Mappings：`maps/RogueCore-5.6.1-140986+main-0196ef29.usmap`  
> UE 版本：`VER_UE5_6`  
> 分析产物：`UAssetStudio/output/rogue_selection/BP_PlayerNegotiationSphere.*`

## 结论

武器/技能选择前的“玩家进入蓝色圈并等待”逻辑可以改成 **只要 1 个玩家在圈内就开始**。

关键资产是：

```text
GameElements/RewardGivers/BP_PlayerNegotiationSphere.uasset
```

当前逻辑并不是写在武器池或技能池里，而是在 `BP_PlayerNegotiationSphere_C.OnRep_PlayerCount` 里做人数判断：

```text
PlayerCount > 0
  → GetPlayersForNegotiationStart(this)
  → RequiredPlayers = Array_Length(ReturnValue)
  → PlayerCount == RequiredPlayers
  → OnAllPlayersInside()
  → SetComponentTickEnabled(true)
  → ChargeUpStartTime = GetTimeSeconds()
  → 读条结束后广播 OnNegotiationStart
```

所以要改成一人触发，核心就是把：

```text
PlayerCount == Array_Length(GetPlayersForNegotiationStart(this))
```

改成：

```text
PlayerCount > 0
```

或者在 UE4SS 里绕过这个判断，直接执行“所有玩家已进圈”的后续逻辑。

## 关键蓝图逻辑

`CheckGrounded` 负责统计当前在圈内且落地的玩家数量，并写入 `PlayerCount`：

```kms
GroundedCount = 0;
for Player in PlayersInside:
    if Player.CharacterMovement.IsMovingOnGround():
        GroundedCount += 1;

if GroundedCount != PlayerCount:
    PlayerCount = GroundedCount;
    OnRep_PlayerCount();
```

`OnRep_PlayerCount` 是实际开始等待/读条的入口：

```kms
OnInsidePlayerCountChanged.Broadcast(PlayerCount);
HUD_Widget.UpdateDefenderBlocks(PlayerCount);
HUD_Widget.ProgressUpdated(0);

if PlayerCount > 0:
    if !IsWidgetActive:
        SetWidgetActive(true);

    RequiredPlayers = Array_Length(BXENegotiationManager.GetPlayersForNegotiationStart(this));

    if PlayerCount == RequiredPlayers:
        HUD_Widget.OnAllPlayersInside();

        if !IsComponentTickEnabled():
            SetComponentTickEnabled(true);
            ChargeUpStartTime = GameplayStatics.GetTimeSeconds(this);
            ChargeUpSound = GameplayStatics.SpawnSound2D(...);
else:
    SetWidgetActive(false);
    SetComponentTickEnabled(false);
```

`ReceiveTick` 负责读条，`Alpha > 1` 后触发真正开始谈判：

```kms
Alpha = (GetTimeSeconds(this) - ChargeUpStartTime) / ChargeUpTime;
HUD_Widget.ProgressUpdated(Alpha);

if Alpha > 1:
    SetWidgetActive(false);
    SetComponentTickEnabled(false);
    Delay(0.3);
    OnNegotiationStart.Broadcast();
```

## UE4SS 推荐改法

优先 Hook `OnRep_PlayerCount`。这是最小改法，不需要改池，也不需要改 `RunManager.BeginNegotiation`。

Hook 点：

```text
/Game/GameElements/RewardGivers/BP_PlayerNegotiationSphere.BP_PlayerNegotiationSphere_C:OnRep_PlayerCount
```

伪代码：

```lua
local function ForceOnePlayerNegotiationStart(sphere)
    if sphere == nil then
        return
    end

    if sphere.PlayerCount <= 0 then
        return
    end

    -- 确保等待 UI 显示。
    if sphere.SetWidgetActive ~= nil then
        sphere:SetWidgetActive(true)
    end

    -- 告诉 HUD 已满足“所有玩家进入”的状态。
    if sphere.HUD_Widget ~= nil and sphere.HUD_Widget.OnAllPlayersInside ~= nil then
        sphere.HUD_Widget:OnAllPlayersInside()
    end

    -- 启动读条 tick。
    if sphere.IsComponentTickEnabled ~= nil and sphere.SetComponentTickEnabled ~= nil then
        if not sphere:IsComponentTickEnabled() then
            sphere:SetComponentTickEnabled(true)
        end
    end

    -- 尽量把读条起点设为当前时间；具体 GameplayStatics 调用方式需按 UE4SS dump 调整。
    -- sphere.ChargeUpStartTime = GameplayStatics.GetTimeSeconds(sphere)
end

RegisterHook(
    "/Game/GameElements/RewardGivers/BP_PlayerNegotiationSphere.BP_PlayerNegotiationSphere_C:OnRep_PlayerCount",
    function(Context)
        local sphere = Context:get()
        ForceOnePlayerNegotiationStart(sphere)
    end
)
```

如果 UE4SS 无法方便调用 `GameplayStatics.GetTimeSeconds`，可以先不改 `ChargeUpStartTime`，看游戏自身在 `OnRep_PlayerCount` 原逻辑里是否已经设置过。若读条立即完成或卡住，再补时间设置。

## 更激进的 UE4SS 改法

如果只 Hook `OnRep_PlayerCount` 不稳定，可以在一人进圈时直接广播开始事件：

```lua
RegisterHook(
    "/Game/GameElements/RewardGivers/BP_PlayerNegotiationSphere.BP_PlayerNegotiationSphere_C:OnRep_PlayerCount",
    function(Context)
        local sphere = Context:get()
        if sphere == nil or sphere.PlayerCount <= 0 then
            return
        end

        if sphere.OnNegotiationStart ~= nil then
            sphere.OnNegotiationStart:Broadcast()
        end
    end
)
```

这个方式会跳过原本的 1 秒读条、音效和 UI 状态，风险更高。建议只用于确认 `OnNegotiationStart` 是否足以触发选择界面。

## 资产 Patch 改法

也可以直接修改 `BP_PlayerNegotiationSphere.uasset` 字节码。

目标函数：

```text
OnRep_PlayerCount
```

目标逻辑：

```text
EqualEqual_IntInt(PlayerCount, Array_Length(GetPlayersForNegotiationStart(this)))
```

替换成：

```text
Greater_IntInt(PlayerCount, 0)
```

或让对应分支恒为 true。

不过该资产的 `ExecuteUbergraph_BP_PlayerNegotiationSphere` 在 `.kms` 反编译时有错误，直接从 `.kms` 重新编译不适合作为第一选择。更建议先用 UE4SS 验证行为，再考虑做二进制 patch。

## 服务端注意事项

这个逻辑必须在 Host/服务端生效。

原因：

- `PlayersInside` 来自服务器 overlap；
- `PlayerCount` 是 replicated / repnotify 字段；
- `OnNegotiationStart` 最终会推动后续 `RunManager.BeginNegotiation` / 谈判界面流程；
- 只在普通客户端改 UI，很可能只能看到本地显示变化，不能真正开始选择。

多人联机时，Host 安装 UE4SS mod 最关键。客户端是否也要装，取决于后续 UI 是否需要本地同步显示。

## 验证步骤

1. Host 安装 UE4SS mod。
2. 多人局触发武器/技能选择圈。
3. 只让一个玩家进入圈并保持落地。
4. 看 HUD 是否出现“所有玩家已进入”状态。
5. 看读条是否开始，并在 `ChargeUpTime` 后打开谈判/选择界面。
6. 让其他玩家不进圈，确认流程仍能继续。

如果第 4 步不生效，说明 Hook 没命中或 `HUD_Widget` 不可用。  
如果第 4 步生效但第 5 步不开始，重点检查 `SetComponentTickEnabled(true)` 和 `ChargeUpStartTime`。  
如果第 5 步本地开始但其他玩家不同步，说明需要在服务端执行 Hook。

## 分析命令

反编译：

```bash
dotnet run --project UAssetStudio.Cli -- decompile \
  "/Users//Project/RogueCore/Content/GameElements/RewardGivers/BP_PlayerNegotiationSphere.uasset" \
  --mappings maps/RogueCore-5.6.1-140986+main-0196ef29.usmap \
  --ue-version VER_UE5_6 \
  --outdir output/rogue_selection
```

生成 CFG：

```bash
dotnet run --project UAssetStudio.Cli -- cfg \
  "/Users//Project/RogueCore/Content/GameElements/RewardGivers/BP_PlayerNegotiationSphere.uasset" \
  --mappings maps/RogueCore-5.6.1-140986+main-0196ef29.usmap \
  --ue-version VER_UE5_6 \
  --outdir output/rogue_selection
```

关键输出：

```text
output/rogue_selection/BP_PlayerNegotiationSphere.kms
output/rogue_selection/BP_PlayerNegotiationSphere.txt
output/rogue_selection/BP_PlayerNegotiationSphere.dot
```
