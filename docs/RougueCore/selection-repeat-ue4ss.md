# RogueCore 谈判多选：UE4SS 改法

> 游戏资源路径：`/Users/bytedance/Project/RogueCore`  
> Mappings：`maps/RogueCore-5.6.1-140986+main-0196ef29.usmap`  
> UE 版本：`VER_UE5_6`  
> 相关文档：[资产 / 蓝图改法](./selection-repeat-asset.md)

## 结论

| 目标 | UE4SS | 说明 |
|------|-------|------|
| **所有**谈判卡支持多人各选 | ✅ 推荐 | 单 Hook `CreateUnlockButtons`，设 `CanBePickedMultipleTimes = true` |
| **少数**卡多选 | ⚠️ 可用但过重 | 更轻的做法见 [资产改法：加 BXEHealAction](./selection-repeat-asset.md) |
| 每人独立候选池 | ❌ 不建议 | 需改 index / 请求 / 发放整条链路 |

`CanBePickedMultipleTimes` 在 **`BXEUnlockInstance`** 上，由 `BeginNegotiation` / `PickRandomUnlocksFromTag` 等 **native** 在运行时赋值。UE4SS 可在 UI 建卡前直接 patch `NegotiationData.Unlocks`，无需改 `.uasset`。

原版参考：`Unlock_SkipForHealth` 默认可多人各选（含 `BXEHealAction`）。详见 [资产文档](./selection-repeat-asset.md#案例unlock_skipforhealth满血--最大生命-20)。

---

## 机制摘要

`UI_BXE_Negotiation_Entry`：

```kms
this.CanBePicked = Array_IsEmpty(this.SelectedBy) || this.Unlock.CanBePickedMultipleTimes;
```

Patch 目标：**`window.NegotiationData.Unlocks[i].CanBePickedMultipleTimes`**（及可选 `DroneUnlocks`）。不要只改 `SetData` 参数里的 struct 拷贝。

相关 native / 蓝图入口：

- `WND_BXE_Negotiation_C:CreateUnlockButtons`
- `WND_BXE_Negotiation_C:ReceiveUnlocksChanged`
- `/Script/RogueCore.BXENegotiationWidget:SelectUnlock`

---

## 改法一：单 Hook 全局多选（推荐）

```lua
local function patch_instances(array)
    if array == nil then return end
    for i = 1, #array do
        local inst = array[i]
        if inst ~= nil then
            inst.CanBePickedMultipleTimes = true
        end
    end
end

RegisterHook(
    "/Game/UI/Menus/Menu_Negotiation/WND_BXE_Negotiation.WND_BXE_Negotiation_C:CreateUnlockButtons",
    function(Context)
        local w = Context:get()
        if w == nil or w.NegotiationData == nil then return end
        patch_instances(w.NegotiationData.Unlocks)
        patch_instances(w.NegotiationData.DroneUnlocks)
    end
)
```

候选列表会刷新时，追加同逻辑：

```lua
RegisterHook(
    "/Game/UI/Menus/Menu_Negotiation/WND_BXE_Negotiation.WND_BXE_Negotiation_C:ReceiveUnlocksChanged",
    function(Context)
        local w = Context:get()
        if w == nil or w.NegotiationData == nil then return end
        patch_instances(w.NegotiationData.Unlocks)
        patch_instances(w.NegotiationData.DroneUnlocks)
    end
)
```

注意：

- `Context:get()`、数组遍历（`#array` / `Num()` / `Get()`）按 UE4SS 版本调整。
- UI 可点 ≠ 服务端一定接受；失败时用改法三。

---

## 改法二：兜底 UI（可选）

改法一后按钮仍灰掉时使用：

```lua
RegisterHook(
    "/Game/UI/Menus/Menu_Negotiation/UI_BXE_Negotiation_Entry.UI_BXE_Negotiation_Entry_C:SetSelectedBy",
    function(Context)
        local entry = Context:get()
        if entry ~= nil then
            entry.CanBePicked = true
            if entry.Unlock ~= nil then
                entry.Unlock.CanBePickedMultipleTimes = true
            end
        end
    end
)
```

仅保证本地可点；`SelectUnlock` 仍可能拒绝。

---

## 改法三：Hook SelectUnlock（失败时再用）

```text
/Script/RogueCore.BXENegotiationWidget:SelectUnlock
```

```lua
RegisterHook(
    "/Script/RogueCore.BXENegotiationWidget:SelectUnlock",
    function(Context, InUnlock, InSlot, InIndex)
        if InUnlock ~= nil then
            local unlock = InUnlock:get()
            if unlock ~= nil then
                unlock.CanBePickedMultipleTimes = true
            end
        end
    end
)
```

返回仍为 `false` 时，native 还检查 `SelectedIndex`、请求列表等 → 需 C++ Mod 或 `Server_ApplyUnlocksToPlayer` 绕过。

---

## 每个玩家独立池（不建议）

系统假设 `NegotiationData.Unlocks` 全员共享、index 全局一致。独立池需同时改显示、点击、`RequestUnlock` / `SelectUnlock` / `GetUnlockSelectedBy`，或绕过谈判直接 `Server_ApplyUnlocksToPlayer`（丢失请求/投票/动画）。

---

## 关键 Hook 路径

| 函数 | 用途 |
|------|------|
| `WND_BXE_Negotiation_C:CreateUnlockButtons` | **主 Hook**：patch 源数组 |
| `WND_BXE_Negotiation_C:ReceiveUnlocksChanged` | 候选刷新时再次 patch |
| `UI_BXE_Negotiation_Entry_C:SetSelectedBy` | UI 兜底 |
| `BXENegotiationWidget:SelectUnlock` | native 选择失败时 |

---

## 验证步骤

1. 安装 UE4SS mod，多人局触发谈判。
2. 日志确认 Hook 到 `CreateUnlockButtons`。
3. **基线**（可选）：A 选 `Unlock_SkipForHealth`，B 仍可点（原版）。
4. A 选**普通**技能卡，B 检查同卡是否仍可点。
5. B 点击，确认双方都获得 unlock。
6. 第 4 步失败 → 检查数组遍历 / Hook 路径；第 4 成功第 5 失败 → 加改法三。

---

## 推荐实施顺序

1. 仅 **改法一**（+ 可选 `ReceiveUnlocksChanged`）。
2. 失败 → **改法二** → **改法三**。
3. 若只需少数卡多选 → 优先 [资产：BXEHealAction](./selection-repeat-asset.md#改法一给目标-unlock-添加-bxehealaction推荐尝试)。
4. 仍失败 → C++ 或 `Server_ApplyUnlocksToPlayer`。

---

## 分析命令

反编译谈判窗口：

```bash
dotnet run --project UAssetStudio.Cli -- decompile \
  "/Users/bytedance/Project/RogueCore/Content/UI/Menus/Menu_Negotiation/WND_BXE_Negotiation.uasset" \
  --mappings maps/RogueCore-5.6.1-140986+main-0196ef29.usmap \
  --ue-version VER_UE5_6 \
  --outdir output/rogue_selection
```

反编译谈判卡片：

```bash
dotnet run --project UAssetStudio.Cli -- decompile \
  "/Users/bytedance/Project/RogueCore/Content/UI/Menus/Menu_Negotiation/UI_BXE_Negotiation_Entry.uasset" \
  --mappings maps/RogueCore-5.6.1-140986+main-0196ef29.usmap \
  --ue-version VER_UE5_6 \
  --outdir output/rogue_selection
```
