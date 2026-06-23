# RogueCore 联机人数限制研究

> 游戏资源路径：`/Users/bytedance/Project/RogueCore`  
> Mappings：`maps/RogueCore-5.6.1-143055+main-7f7cc36f_by_Iris.usmap`  
> UE 版本：`VER_UE5_6`  
> 分析目录：`analysis/roguecore_player_limit`

## 结论

如果目标是解除房间 / 队伍最大人数上限，核心不在普通蓝图资产默认值里，而在在线会话 native 逻辑：

```text
OnlineSessionSubSystem.GetMaxPublicConnections()
FSDCreateSessionCallbackProxy.FSDCreateSession(..., PublicConnections, ...)
FSDJoinSessionCallbackProxy.FSDJoinSession(...)
FSDServerListLibrary.ComputeLobbyStatus(...)
```

所有已找到的创建房间入口都不是直接写死 `4`，而是先调用：

```text
OnlineSessionSubSystem.GetMaxPublicConnections()
```

再把返回值作为 `FSDCreateSession` 的第三个参数：

```text
FSDCreateSession(this, PlayerController, GetMaxPublicConnections(), false, false)
```

因此单独改某个 UI 资产不够。真正要放大人数，优先目标应是让 `GetMaxPublicConnections()` 返回更高值，或在所有创建 session 的入口把传入 `FSDCreateSession` 的 `PublicConnections` 改成目标人数。

## 已确认的创建入口

这些资产会创建联机 session，并都使用 `GetMaxPublicConnections()`：

```text
Content/Game/SpaceRig/BP_SpaceRig_GamemOde.uasset
Content/UI/Menus/Menu_RunMap/WBP_RunMap_Menu.uasset
Content/UI/WeeklyChallengeTerminal/MENU_GauntletChallengeTerminal.uasset
Content/UI/WeeklyChallengeTerminal/MENU_GauntletChallengeWidget.uasset
Content/UI/Menus/Menu_Tab/HUD_TabPlayerList_InteractableV2.uasset
Content/UI/Menus/Menu_Cheats/Runs/Cheat_CustomRun.uasset
Content/UI/Menus/Menu_Cheats/Runs/Cheat_CustomRunFromSeed.uasset
```

典型调用形态：

```kms
CallFunc_GetMaxPublicConnections_ReturnValue =
  OnlineSessionSubSystem.GetMaxPublicConnections()

FSDCreateSessionCallbackProxy.FSDCreateSession(
  this,
  PlayerController,
  CallFunc_GetMaxPublicConnections_ReturnValue,
  false,
  false
)
```

这说明人数上限的第一层是“创建房间时公开连接数”。如果这里仍为 4，后续 UI 再怎么放行，也可能被在线服务判定满员。

## 加入与满员状态

服务器列表里，房间结构 `ServerListLobby` 只暴露：

```text
NumPlayers
IsJoinable
```

没有暴露 `MaxPlayers` 或 `MaxPublicConnections`。可加入状态由 native / 函数库计算：

```text
FSDServerListLibrary.ComputeLobbyStatus(...)
```

`ITM_ServerList_Entry.UpdateStatus` 中确认了状态枚举：

```text
Type 0 -> REJOINABLE
Type 1 -> LOBBY JOINABLE
Type 2 -> LATE JOINABLE
Type 3 -> MY LOBBY
Type 4 -> Clearance Required
Type 5 -> ACTIVE
Type 6 -> FULL
Type 7 -> ERROR
```

同时 `Type 6 / FULL` 会禁用按钮：

```text
Button_0.SetIsEnabled(false)
```

这里是第二层限制：UI 会把满员房间显示为 `FULL` 并禁用点击。可以资产 patch 成可点击，但如果 session 后端仍认为满员，`FSDJoinSession` 仍会失败。

## GameSession 层

mappings 中还有 UE 原生会话字段：

```text
GameSession.MaxPlayers
GameSession.MaxPartySize
FSDGameSession : GameSession
GameSessionSettings.MaxPlayers
JoinabilitySettings.MaxPlayers
JoinabilitySettings.MaxPartySize
```

但在 `/Users/bytedance/Project/RogueCore/Content` 下没有找到可直接修改的 `GameSessionSettings` / `OnlineSession` / ini 配置资产。`OnlineSessionSubSystem` schema 也没有 `MaxPublicConnections` 字段，只有：

```text
SessionType
SessionPassword
IsJoiningInvite
CanPlayOnline
DisconnectReason
DisconnectErrorCode
SessionUpdater
```

所以这些字段更像 native 运行时对象 / UE 在线服务结构，不是当前 Content 资源目录里的普通资产默认值。

## 可行方案

### 方案 A：资产补丁，改创建入口参数

把所有 `FSDCreateSession(..., GetMaxPublicConnections(), ...)` 的第三个参数改成目标人数，例如 `8`。

需要覆盖所有实际入口：

```text
BP_SpaceRig_GamemOde
WBP_RunMap_Menu
MENU_GauntletChallengeTerminal
MENU_GauntletChallengeWidget
HUD_TabPlayerList_InteractableV2
Cheat_CustomRun
Cheat_CustomRunFromSeed
```

可选地再 patch：

```text
ITM_ServerList_Entry.UpdateStatus
  FULL / Type 6 -> Button_0.SetIsEnabled(true)
```

风险：如果 `FSDCreateSession` 或在线服务内部仍 clamp 到 4，资产 patch 无法完全解除限制。

### 方案 B：UE4SS / C++ hook，推荐方向

更稳的方向是运行时 hook native：

```text
OnlineSessionSubSystem.GetMaxPublicConnections() -> 返回目标人数
FSDCreateSessionCallbackProxy.FSDCreateSession(...) -> 确认 PublicConnections 被改大
FSDGameSession.MaxPlayers / MaxPartySize -> 服务端 GameSession 同步改大
FSDServerListLibrary.ComputeLobbyStatus(...) -> 必要时修正 FULL 状态
```

Lua 能否直接改 `GetMaxPublicConnections()` 的返回值取决于 UE4SS 对该 UFunction 返回值的暴露情况。若 Lua hook 不能可靠覆盖返回值，就需要 UE4SS C++ mod 或 DLL hook。

伪逻辑：

```lua
local TargetMaxPlayers = 8

-- 创建 session 前：让公开连接数变大
-- 如果 Lua hook 支持修改返回值：
RegisterHook("/Script/RogueCoreOnlineServices.OnlineSessionSubSystem:GetMaxPublicConnections", function(Context)
    return TargetMaxPlayers
end)

-- 服务端 GameSession 层：防止 PreLogin / AtCapacity 仍按旧人数拒绝
NotifyOnNewObject("/Script/RogueCoreOnlineServices.FSDGameSession", function(Session)
    ExecuteInGameThread(function()
        Session.MaxPlayers = TargetMaxPlayers
        Session.MaxPartySize = TargetMaxPlayers
    end)
end)
```

实际 UE4SS 脚本需要在目标版本里验证函数路径和返回值写法；若返回值无法覆盖，改用 C++ hook。

## UI 显示问题

`ITM_ServerList_Entry_PlayerIcons` 只根据已有 child widget 循环刷新图标：

```text
GetAllChildrenOfClass(Icons_Box, UI_ReclaimerIcon_Small_C)
ForEach child -> FromCharacterArray(Index, NumPlayers, ...)
```

这意味着即使真正允许 5+ 人，服务器列表可能仍只显示已有数量的职业图标。这个不影响加入逻辑，但需要额外扩展 UI 才能完整显示 5+ 玩家。

## 另一个“人数限制”场景

如果你说的人数限制是“等待全员到齐 / 需要所有玩家进圈”的限制，不是房间最大人数，那么已有结论在：

```text
docs/RougueCore/negotiation-sphere-radius-patch-summary.md
```

核心 patch 是把：

```text
PlayerCount == Array_Length(GetPlayersForNegotiationStart(this))
```

改为：

```text
PlayerCount > 0
```

这个是谈判圈 / CoopStation / 技能升级等待逻辑，和房间最大玩家数是两条不同链路。

## 当前未完成项

本地 `/Users/bytedance/Project/RogueCore` 目录只看到 Content 资产，没有游戏 exe / dll，因此还没有做 native 反汇编确认 `GetMaxPublicConnections()` 是否内部写死 4 或按平台 / session type clamp。

下一步如果要实际落地，建议先做两个小实验：

```text
1. 资产 patch 一个主要创建入口，把 FSDCreateSession 的 PublicConnections 改成 8。
2. 同时 patch ITM_ServerList_Entry 的 FULL 按钮禁用逻辑，确认第 5 人 join 失败发生在 UI 还是在线服务。
```

如果第 5 人仍失败，再转 UE4SS C++ / DLL hook native。
