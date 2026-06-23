# RogueCore 联机人物重复选择分析与资产补丁

> 游戏资源路径：`/Users/bytedance/Project/RogueCore`  
> Mappings：`maps/RogueCore-5.6.1-143055+main-7f7cc36f_by_Iris.usmap`  
> UE 版本：`VER_UE5_6`  
> 分析目录：`analysis/roguecore_character_repeat`  
> 输出目录：`output/Content/Game`

## 结论

联机人物 / 职业能否重复选择，核心门禁在：

```text
Content/Game/BP_PlayerControllerBase.uasset
```

函数：

```text
CanPlayerPickClass(Class, out IsFree)
```

原逻辑是：

```text
IsFree =
  GetFreePlayerCharacterIds(this, PlayerState).Contains(Class)
  || GameState.ArePlayersAllowedToPickSameClass()
  || IsValid(WorldSettings.DefaultCharacterClass)
```

因此默认多人局里，如果某个职业已经被其他玩家占用，`GetFreePlayerCharacterIds()` 不会包含它，且 `ArePlayersAllowedToPickSameClass()` 默认返回 false，换人请求会失败。

本次资产补丁把 `CanPlayerPickClass` 改为恒定允许：

```kms
protected void CanPlayerPickClass(Object<PlayerCharacterID> Class, out bool IsFree) {
    IsFree = (bool)(true);
}
```

这样 UI 或角色选择器提交 `RequestChangeToPlayerClass(CurrentCharacter)` 后，服务端/控制器侧会允许重复职业，并继续执行 `ChangeCharacter`。

## 输出资产

已按原始游戏路径结构输出到：

```text
output/Content/Game/BP_PlayerControllerBase.uasset
output/Content/Game/BP_PlayerControllerBase.uexp
```

打包 mod 时保持相对路径：

```text
Content/Game/BP_PlayerControllerBase.uasset
Content/Game/BP_PlayerControllerBase.uexp
```

## 关键调用链

角色选择 Widget 侧：

```text
ITM_Wardrobe_ClassSelector.TrySwitchCharacter()
  -> FSDPlayerController.RequestChangeToPlayerClass(CurrentCharacter)
```

SpaceRig 控制器侧：

```text
BP_PlayerController_SpaceRig.RequestChangeToPlayerClass(RequestClass)
  -> PlayerChangeToCharacterFromCharacterSelector(RequestClass)
  -> CanPlayerPickClass(RequestClass, IsFree)
  -> if IsFree:
       ChangeCharacter(RequestClass)
       BroadcastPlayerClassChange(true)
     else:
       BroadcastPlayerClassChange(false)
```

初始网络选择流程也会使用同一个门禁：

```text
BP_NetworkPlayerController.RequestPlayerCharacterSelect(PlayerClass)
  -> CanPlayerPickClass(PlayerClass, IsFree)
  -> if IsFree:
       PlayerState.SetSelectedCharacterID(PlayerClass)
       PlayerState.CharacterSelected()
     else:
       OpenCharacterSelection()
```

## UI 显示说明

`ITM_CharacterCard` 会遍历所有 Active PlayerState，并比较：

```text
PlayerState.GetSelectedCharacterID() == this.Character ID
```

如果某张人物卡已被玩家选中，UI 会显示玩家名、改边框/图标状态。这个逻辑主要是“显示占用”，不是最终拒绝重复选择的服务端门禁。

`ITM_Wardrobe_ClassSelector.CanSwitchToCharacter()` 在当前反编译结果里只返回 `AllowSwitch`，没有按 `GetFreePlayerCharacterIds()` 禁用切换按钮。

## 是否需要 UE4SS

不一定需要。当前补丁是资产级改法，只替换 `BP_PlayerControllerBase` 的一个函数。

UE4SS 仍可作为运行时方案，例如 hook：

```text
FSDGameState.ArePlayersAllowedToPickSameClass()
```

让它返回 true，或运行时设置 `FSDGameState.bArePlayersAllowedToPickSameClass = true`。不过这需要脚本 / hook 环境；资产补丁更适合直接打包发布。

## UE4SS 方案

### 推荐方案：设置 GameState 开关

`BP_PlayerControllerBase.CanPlayerPickClass` 原本就支持一个全局开关：

```text
GameState.ArePlayersAllowedToPickSameClass()
```

该函数对应的底层字段在 mappings 中是：

```text
FSDGameState.bArePlayersAllowedToPickSameClass : BoolProperty
```

因此 UE4SS 最干净的做法不是改 UI，而是在运行时把当前 `FSDGameState` 的这个 bool 设成 true。这样原蓝图逻辑会自然通过：

```text
IsFree = FreeList.Contains(Class) || ArePlayersAllowedToPickSameClass()
```

示例 `Scripts/main.lua`：

```lua
local function log(msg)
    print("[RC Same Class] " .. msg .. "\n")
end

local function patch_game_state(gs)
    if gs == nil then return false end

    -- 字段名来自 usmap：FSDGameState.bArePlayersAllowedToPickSameClass
    gs.bArePlayersAllowedToPickSameClass = true
    log("patched GameState: bArePlayersAllowedToPickSameClass = true")
    return true
end

local function try_patch_existing_game_state()
    local gs = FindFirstOf("FSDGameState")
    if gs ~= nil then
        return patch_game_state(gs)
    end

    -- SpaceRig / 任务内可能是蓝图子类，按 dump 中实际类名兜底。
    gs = FindFirstOf("BP_GameState_SpaceRig_C")
    if gs ~= nil then
        return patch_game_state(gs)
    end

    gs = FindFirstOf("BP_GameState_C")
    if gs ~= nil then
        return patch_game_state(gs)
    end

    return false
end

ExecuteInGameThread(function()
    if not try_patch_existing_game_state() then
        log("GameState not ready yet; waiting for construction/hooks")
    end
end)

-- 新建 GameState 时再次设置，覆盖 SpaceRig / 任务切图 / 回大厅。
NotifyOnNewObject("/Script/RogueCore.FSDGameState", function(obj)
    ExecuteInGameThread(function()
        patch_game_state(obj)
    end)
end)

NotifyOnNewObject("/Game/Game/SpaceRig/BP_GameState_SpaceRig.BP_GameState_SpaceRig_C", function(obj)
    ExecuteInGameThread(function()
        patch_game_state(obj)
    end)
end)

NotifyOnNewObject("/Game/Game/BP_GameState.BP_GameState_C", function(obj)
    ExecuteInGameThread(function()
        patch_game_state(obj)
    end)
end)
```

如果 `NotifyOnNewObject` 路径在当前 UE4SS 版本里不命中，保留 `FindFirstOf`，再加一个延迟重试即可：

```lua
LoopAsync(1000, function()
    try_patch_existing_game_state()
    return false -- 持续重试；若只想成功一次后停止，可在成功时 return true
end)
```

注意：UE4SS 的对象查找 / 定时 API 会随版本和配置略有差异；如果 `LoopAsync` 不可用，就改成 UE4SS 当前版本支持的延迟/循环函数。核心动作只有一行：

```lua
gs.bArePlayersAllowedToPickSameClass = true
```

### 兜底方案：Hook CanPlayerPickClass

如果设置 `bArePlayersAllowedToPickSameClass` 后仍失败，说明该函数实际没有读这个字段，或者对象不是当前正在参与判定的 `FSDGameState`。可以直接 hook 蓝图函数：

```text
/Game/Game/BP_PlayerControllerBase.BP_PlayerControllerBase_C:CanPlayerPickClass
```

目标是把 out 参数 `IsFree` 改成 true。UE4SS 不同版本对 out 参数的 Lua 包装不同，常见写法需要按 dump 调整：

```lua
RegisterHook(
    "/Game/Game/BP_PlayerControllerBase.BP_PlayerControllerBase_C:CanPlayerPickClass",
    function(Context, Class, IsFree)
        -- 具体 out-param 写法取决于 UE4SS 版本：
        -- 有的版本是 IsFree:set(true)
        -- 有的版本是 IsFree.Value = true
        -- 也可能需要 post-hook 修改返回后的 out 参数。
        if IsFree ~= nil then
            if IsFree.set ~= nil then
                IsFree:set(true)
            else
                IsFree.Value = true
            end
        end
    end
)
```

如果 out 参数写法不工作，可以改 hook 调用者更稳：

```text
BP_PlayerController_SpaceRig.PlayerChangeToCharacterFromCharacterSelector(NewCharacter)
```

但这个函数内部还会调用 `ChangeCharacter`、`BroadcastPlayerClassChange`、`StartSpawnFlow`，直接复刻流程更容易漏状态，因此优先用 GameState bool 或资产补丁。

### 安装结构

示例 UE4SS mod 目录：

```text
RogueCore/Binaries/Win64/ue4ss/Mods/RCSameClass/
├── enabled.txt
├── Scripts/
│   └── main.lua
└── mods.txt   # 取决于 UE4SS 安装方式，有些版本在上级 Mods/mods.txt 注册
```

如果使用 `mods.txt`：

```text
RCSameClass : 1
```

### UE4SS 验证步骤

1. Host 安装并启用 UE4SS mod。
2. 进 SpaceRig 后检查 UE4SS 控制台 / 日志是否出现：

```text
[RC Same Class] patched GameState: bArePlayersAllowedToPickSameClass = true
```

3. 玩家 A 选择某职业。
4. 玩家 B 打开角色选择终端，选择同一职业。
5. 预期：B 成功切换，`OnPlayerClassChangeFailure` 不再触发。

多人同步建议从 Host 开始验证。这个门禁在控制器 / GameState 侧，Host 未安装时，只有客户端装 UE4SS 大概率不够。

## 编译与验证

编译命令：

```bash
mkdir -p output/Content/Game

dotnet run --project UAssetStudio.Cli -- compile \
  analysis/roguecore_character_repeat/BP_PlayerControllerBase.kms \
  --asset "/Users/bytedance/Project/RogueCore/Content/Game/BP_PlayerControllerBase.uasset" \
  --mappings "maps/RogueCore-5.6.1-143055+main-7f7cc36f_by_Iris.usmap" \
  --ue-version VER_UE5_6 \
  --out "/Users/bytedance/Project/UAssetStudio/output/Content/Game/BP_PlayerControllerBase.uasset" \
  --json
```

编译结果：

```text
Mode: patch
ChangedFunctions: CanPlayerPickClass
ChangedProperties: none
```

结构校验：

```bash
dotnet run --project UAssetStudio.Cli -- validate \
  output/Content/Game/BP_PlayerControllerBase.uasset \
  --mappings "maps/RogueCore-5.6.1-143055+main-7f7cc36f_by_Iris.usmap" \
  --ue-version VER_UE5_6 \
  --json
```

校验结果：

```text
Status: ok
errors: 0
warnings: 1
```

唯一 warning 是未提供 `--game-content`，因此跳过 import 路径存在性检查；资产结构本身通过。

## 测试建议

1. 将输出资产按 `Content/Game/...` 路径打包进 mod。
2. 开多人 SpaceRig。
3. 玩家 A 选择某个职业。
4. 玩家 B 打开角色选择终端，选择同一职业。
5. 预期：不再触发 `OnPlayerClassChangeFailure`，B 成功切换到相同职业。

如果目标还包括“自动初始分配也可以随机重复职业”，可能还需要额外修改使用 `GetFreePlayerCharacterIds()` 自动挑选空闲职业的流程；本次补丁重点解决手动选择 / 切换时的重复职业门禁。
