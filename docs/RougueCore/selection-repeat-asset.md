# RogueCore 谈判多选：资产 / 蓝图改法

> 游戏资源路径：`/Users//Project/RogueCore`  
> Mappings：`maps/RogueCore-5.6.1-140986+main-0196ef29.usmap`  
> UE 版本：`VER_UE5_6`  
> 相关文档：[UE4SS 改法](./selection-repeat-ue4ss.md)

## 结论

| 目标 | 仅靠资产 / 蓝图 | 说明 |
|------|-----------------|------|
| 让**某几张**卡像治疗卡一样可多选 | ⚠️ 可能 | 给 unlock 加 `BXEHealAction` 子对象（待实测） |
| 让**所有**谈判卡支持多人各选 | ❌ | 需 [UE4SS](./selection-repeat-ue4ss.md) 改运行时 `CanBePickedMultipleTimes` |
| 每个玩家各自一套候选池 | ❌ | 要改整条谈判链路，资产改不了 |
| 只改 `UP_*` / `UCT_*` 池子 | ❌ | 只影响刷哪些卡，不控制互斥 |

多人能否点同一张卡，取决于运行时 **`BXEUnlockInstance.CanBePickedMultipleTimes`**。该字段在谈判开始时由 **native** 写入，**不在** `BXEUnlockGeneric` 或 `UP_*` 上。蓝图里出现的 `false` 只是预览默认值。

原版已有一张默认可多人各选的卡：**`Unlock_SkipForHealth`**。全游戏 `Content/Unlocks` 下**只有它**含 `BXEHealAction`，推断 native 据此把 `CanBePickedMultipleTimes` 设为 `true`。

---

## 机制（与改资产相关的部分）

### UI 如何判断可点

`UI_BXE_Negotiation_Entry.SetSelectedBy`（逻辑在蓝图里，字段来自运行时实例）：

```kms
this.SelectedBy = InPlayerState;
IsNotSelected = Array_IsEmpty(this.SelectedBy);
this.CanBePicked = IsNotSelected || this.Unlock.CanBePickedMultipleTimes;
```

| `SelectedBy` | `CanBePickedMultipleTimes` | 其他人能否再点 |
|--------------|----------------------------|----------------|
| 空 | 任意 | ✅ |
| 非空 | `false` | ❌ |
| 非空 | `true` | ✅ |

### 字段在哪

- **`CanBePickedMultipleTimes`**：仅存在于 **`BXEUnlockInstance`**（usmap 已确认）。
- **不在** `Unlock_*.uasset`、`UP_*` 池配置里。
- 无法在编辑器里给 `BXEUnlockGeneric` 直接勾选该 bool。

### 共享候选池

谈判使用全员共享的 `BXENegotiationData.Unlocks`。改池子不能实现「每人独立候选」。

```kms
class BXENegotiationData {
    public ObjectProperty Unlocks;
    public ObjectProperty DroneUnlocks;
    public ObjectProperty Participants;
    ...
}
```

容器开启谈判（`BP_BXE_UnlockContainer_Base`）：

```kms
Context(RunManager, FinalFunction("BeginNegotiation", CollectionTag, -1));
```

---

## 案例：`Unlock_SkipForHealth`（满血 + 最大生命 +20）

路径：`/Game/Unlocks/NonGear/InUse/Unlock_SkipForHealth`

```kms
object Unlock_SkipForHealth : BXEUnlockGeneric {
    Array<Object<BXEPawnStatAction>> Unlocks = [BXEPawnStatAction_0, BXEHealAction_0];
    ...
}

object BXEHealAction_0 : BXEHealAction {
    float HealPercent = 1.0f;
}

object BXEPawnStatAction_0 : BXEPawnStatAction {
    Object<PawnStat> PawnStat = PST_BaseHealth;
    float Value = 20.0f;
}
```

同池其它治疗向 unlock（`Unlock_Defibrillation`、`Unlock_Tiered_RedSugarHealBoost` 等）**没有** `BXEHealAction`，通常仍为一人选后他人不可点。

---

## 改法一：给目标 unlock 添加 `BXEHealAction`（推荐尝试）

**假设**：native 在生成 `BXEUnlockInstance` 时，若 unlock 含 `BXEHealAction`，则设 `CanBePickedMultipleTimes = true`。

### 操作步骤

1. 在编辑器或用 UAssetStudio 打开目标 `Unlock_*.uasset`（`BXEUnlockGeneric` 子类）。
2. 在 `Unlocks` 数组中**新增**子对象，类型 **`BXEHealAction`**。
3. 可参考 `Unlock_SkipForHealth`：
   - `Name` = `"heal"`
   - `HealPercent` = `0.0`（若只想触发多选、不要实际治疗）或 `1.0`（与 SkipForHealth 一致）
4. 保留原有 `BXEPawnStatAction` 等动作，勿删。
5. 打包 mod，多人局触发谈判验证。

### 适用场景

- 只希望**少数几张**卡支持多人各选。
- 不想依赖 UE4SS / 运行时 Hook。

### 风险

- 可能执行多余治疗逻辑（满血时行为取决于 `OnFullHealthCannotHeal` 等 native 规则）。
- 图标 / 描述可能仍按原 stat 显示，需进游戏确认。
- 若 native 并非只看 `BXEHealAction`，则无效 → 改用 [UE4SS](./selection-repeat-ue4ss.md)。

### 不宜做的资产修改

| 做法 | 结果 |
|------|------|
| 只改 `UP_NegotiatedUpgradeUnlocks` 成员列表 | 仅改变出现概率，不解除互斥 |
| 在 unlock 上添加不存在的 `CanBePickedMultipleTimes` 属性 | 类型上无此字段，无效 |
| 复制 `Unlock_SkipForHealth` 并重命名 | 新卡可多选，但不能让**已有**技能卡多选 |

---

## 改法二：蓝图层强制可点（不推荐单独使用）

`WND_BXE_Negotiation` / `UI_BXE_Negotiation_Entry` 可在 `SetSelectedBy` 或 `SetData` 里**写死** `CanBePicked = true`。

问题：

- 改的是 **Widget 本地变量**，不一定改 `NegotiationData.Unlocks[i].CanBePickedMultipleTimes`。
- `BXENegotiationWidget.SelectUnlock` 在 native 里可能仍拒绝第二次选择。
- 需 recompile 蓝图并处理 `.uasset` 与游戏版本对齐；工作量比 UE4SS 大、效果更不确定。

若坚持走蓝图，应同时在 `CreateUnlockButtons` 遍历 `NegotiationData.Unlocks` 并把每个实例的 `CanBePickedMultipleTimes` 设为 `true`——但 struct 在蓝图里改数组元素较繁琐，**仍不如 UE4SS 一行 Hook**。

---

## 关键资产

| 资产 | 作用 |
|------|------|
| `Unlocks/NonGear/InUse/Unlock_SkipForHealth.uasset` | 参考：唯一含 `BXEHealAction` 的谈判 unlock |
| `Unlocks/Collections/UP_NegotiatedUpgradeUnlocks.uasset` | 谈判技能池（只改列表 ≠ 多选） |
| `Unlocks/Collections/CollectionTags/UCT_NegotiatedUnlocks.uasset` | 池标签 |
| `UI/Menus/Menu_Negotiation/UI_BXE_Negotiation_Entry.uasset` | 读 `CanBePickedMultipleTimes` 的 UI |
| `GameElements/.../BP_BXE_UnlockContainer_Base.uasset` | 调用 `BeginNegotiation` |

---

## 验证步骤

1. 只 mod **一张**目标 unlock（加 `BXEHealAction`），其余保持原版。
2. 多人局触发谈判，刷出该卡。
3. 玩家 A 选择该卡。
4. 玩家 B 检查同一张是否仍可点击。
5. 玩家 B 点击后，确认双方都获得该 unlock。
6. 对比基线：未 mod 的普通技能卡应仍为 A 选后 B 不可点（除非同时做了 UE4SS）。

失败时：改用 [UE4SS 全局多选](./selection-repeat-ue4ss.md)。

---

## 分析命令

反编译参考 unlock：

```bash
dotnet run --project UAssetStudio.Cli -- decompile \
  "/Users//Project/RogueCore/Content/Unlocks/NonGear/InUse/Unlock_SkipForHealth.uasset" \
  --mappings maps/RogueCore-5.6.1-140986+main-0196ef29.usmap \
  --ue-version VER_UE5_6 \
  --outdir output/rogue_selection
```

反编译对比 unlock（无 HealAction）：

```bash
dotnet run --project UAssetStudio.Cli -- decompile \
  "/Users//Project/RogueCore/Content/Unlocks/NonGear/InUse/Healing/Unlock_Defibrillation.uasset" \
  --mappings maps/RogueCore-5.6.1-140986+main-0196ef29.usmap \
  --ue-version VER_UE5_6 \
  --outdir output/rogue_selection
```

搜索全库 `BXEHealAction` 引用：

```bash
rg -a -l "BXEHealAction" "/Users//Project/RogueCore/Content/Unlocks"
```

编译修改后的 unlock（若有 `.kms`）：

```bash
dotnet run --project UAssetStudio.Cli -- compile \
  output/rogue_selection/YourUnlock.kms \
  --asset "/Users//Project/RogueCore/Content/Unlocks/.../YourUnlock.uasset" \
  --mappings maps/RogueCore-5.6.1-140986+main-0196ef29.usmap \
  --ue-version VER_UE5_6 \
  --outdir output/rogue_selection
```
