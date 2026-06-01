# RogueCore 职业技能升级与武器锻造台交互 Patch

> 游戏资源路径：`/Users/bytedance/Project/RogueCore`  
> Mappings：`maps/RogueCore-5.6.1-143055+main-7f7cc36f_by_Iris.usmap`  
> UE 版本：`VER_UE5_6`  
> 分析目录：`analysis/roguecore_e_unlock`  
> 输出根目录：`/Users/bytedance/Project/UAssetStudio/output`

## 结论

本次目标分成两部分：

1. 职业技能升级：玩家按 `E` 后直接打开原本的 2 选 1选择界面，不自动替玩家选择。
2. 武器锻造台：锻造时不再要求放入/拥有资源，只要 schematic 本身处于可锻造状态，就允许执行锻造。

最终输出资产：

```text
output/UnlockAll/Content/Game/GameData/BXESettings/PCS_Default.uasset
output/UnlockAll/Content/Game/GameData/BXESettings/PCS_Default.uexp
output/UnlockAll/Content/GameElements/BioBooster/BP_BioBooster_Frame.uasset
output/UnlockAll/Content/GameElements/BioBooster/BP_BioBooster_Frame.uexp
output/UnlockAll/Content/UI/HUD_SpaceRig/Forge/UI_Forge_Schematic.uasset
output/UnlockAll/Content/UI/HUD_SpaceRig/Forge/UI_Forge_Schematic.uexp
output/UnlockAll/Content/UI/Menus/Menu_Workbench/Workbench_Ability.uasset
output/UnlockAll/Content/UI/Menus/Menu_Workbench/Workbench_Ability.uexp
```

旧输出也曾写到：

```text
output/Content/UI/Menus/Menu_Workbench/Workbench_Ability.uasset
output/Content/UI/Menus/Menu_Workbench/Workbench_Ability.uexp
output/Content/UI/HUD_SpaceRig/Forge/UI_Forge_Schematic.uasset
output/Content/UI/HUD_SpaceRig/Forge/UI_Forge_Schematic.uexp
```

## 涉及资产

职业技能升级 UI：

```text
Content/UI/Menus/Menu_Workbench/Workbench_Ability.uasset
```

武器锻造台 schematic UI：

```text
Content/UI/HUD_SpaceRig/Forge/UI_Forge_Schematic.uasset
```

相关工作台入口曾排查：

```text
Content/GameElements/RewardGivers/Shop/BP_MidStation_ShopBase.uasset
Content/GameElements/RewardGivers/Shop/BP_MidStation_Workbench.uasset
Content/GameElements/RewardGivers/Shop/BP_Cave_Workbench.uasset
Content/UI/Menus/Menu_Workbench/Menu_Workbench.uasset
```

## 职业技能升级

### v2 修正：不能只覆盖 UI

只放 `Content/UI/Menus/Menu_Workbench/Workbench_Ability` 不足以跳过局内工作台小游戏。`Workbench_Ability` 只负责已经进入工作台窗口后的 2 选 1选项；真正决定局内生成“需要小游戏修理的工作台”还是“已打开工作台”的位置在：

```text
Content/Game/GameData/BXESettings/PCS_Default.uasset
```

`PCS_Default` 里的 Workbench soft class 已从：

```text
/Game/GameElements/RewardGivers/Shop/BP_Cave_Workbench
BP_Cave_Workbench_C
```

改为：

```text
/Game/GameElements/RewardGivers/Shop/BP_Cave_Workbench_Open
BP_Cave_Workbench_Open_C
```

因此 `UnlockAll` 包必须包含：

```text
Content/Game/GameData/BXESettings/PCS_Default.uasset
Content/Game/GameData/BXESettings/PCS_Default.uexp
```

否则游戏仍会按原始 `PCS_Default` 生成普通 `BP_Cave_Workbench`，就还是要做小游戏。

### v3 修正：强化剂分剂是 BioBooster

“强化剂分剂”不是 `Workbench` 生成项，而是 `PCS_Default` 里的 `BioBooster`：

```text
/Game/GameElements/BioBooster/BP_BioBooster_Frame
BP_BioBooster_Frame_C
```

`BP_BioBooster_Frame` 自带 `HackTerminalUsable`，原流程会要求玩家先完成 hacking 小游戏。该蓝图已有 `AlwaysOpen` 逻辑：

```text
ReceiveBeginPlay
  -> UpdateAlwaysOpen()
  -> if AlwaysOpen:
       HackTerminalUsable.SetIsHacked(true)
```

因此当前补丁通过 JSON 写回方式，把 `Default__BP_BioBooster_Frame_C` 的 `AlwaysOpen` 默认值写成 `true`，输出到：

```text
Content/GameElements/BioBooster/BP_BioBooster_Frame.uasset
Content/GameElements/BioBooster/BP_BioBooster_Frame.uexp
```

这样 BioBooster 生成后会直接进入已 hacked 状态，避免强化剂分剂小游戏。

### 2 选 1 UI

`Workbench_Ability` 的关键流程是：

```text
ReceiveInitializeOnce
  -> SetPlayerCharacterID(...)
  -> CreateChoices()
```

`CreateChoices()` 会按 `ChoiceCount` 创建选择按钮。当前需求是“按 E 后直接弹出 2 选 1界面”，所以这里应保留创建选择 UI 的行为，不应在初始化后自动选中第一个选项。

确认后的有效逻辑：

```text
ReceiveInitializeOnce_283:
  SetPlayerCharacterID(...)
  CreateChoices()
  return

OnChoiceClicked_346:
  Ability = Choices[InButton.DataIndex]
  继续执行选择/应用逻辑
```

之前尝试过在 `ReceiveInitializeOnce` 后追加：

```text
Ability = Choices[0]
goto OnSelected
```

这会导致进入 UI 时直接替玩家选中第一个升级，不符合“弹出 2 选 1选择界面”的目标，已撤销。

## 武器锻造台

原始 `UI_Forge_Schematic.TryBuildSchematic()` 逻辑同时检查两个条件：

```text
IsValid(Schematic)
CanAffordSchematic(this)
GetSchematicState(this) == 1
```

然后才调用：

```text
BuildSchematic(this)
```

本次修改保留原本的状态判断，只绕过资源检查：

```text
CallFunc_CanAffordSchematic_ReturnValue = true
CallFunc_GetSchematicState_ReturnValue = Schematic.GetSchematicState(this)
CallFunc_EqualEqual_ByteByte_ReturnValue = (CallFunc_GetSchematicState_ReturnValue == 1)
CallFunc_BooleanAND_ReturnValue =
  CallFunc_EqualEqual_ByteByte_ReturnValue &&
  CallFunc_CanAffordSchematic_ReturnValue

if CallFunc_BooleanAND_ReturnValue:
  Schematic.BuildSchematic(this)
  Success = true
else:
  Success = false
```

也就是说，UI 和锻造流程仍沿用原版结构，但 `CanAffordSchematic` 不再阻止锻造。这样比删除整个 `CanAffordSchematic` 分支更接近原蓝图结构，编译后的字节码也更稳定。

### v2 修正：处理 Matrix Core / state 2

第一次只绕过 `CanAffordSchematic()` 后，游戏里仍可能显示需要 1 个材料。原因是 schematic state `2` 会走 Matrix Core 显示分支，而原 `TryBuildSchematic()` 只允许 state `1`。

当前 `UnlockAll` 版本额外做了：

```text
Show Cost = false
state == 2 时强制切到 ITM_Overclock_Icon，不再显示 ITM_MatrixCore 的 1 个材料需求
TryBuildSchematic 允许 state == 1 或 state == 2
CanAffordSchematic 结果固定为 true
BuildSchematic(this)
```

CFG 验证点：

```text
TryBuildSchematic:
  CallFunc_CanAffordSchematic_ReturnValue = EX_True
  EqualEqual(GetSchematicState, 1)
  EqualEqual(GetSchematicState, 2)
  BooleanOR(...)
  BuildSchematic(this)

SetSchematic:
  Temp_bool_Variable = EX_True
  TypeSwitcher false -> ITM_MatrixCore
  TypeSwitcher true  -> ITM_Overclock_Icon
```

## 编译命令

职业技能升级：

```bash
dotnet run --project UAssetStudio.Cli -- compile \
  "analysis/roguecore_e_unlock/workbench_widgets/Workbench_Ability/Workbench_Ability.kms" \
  --asset "/Users/bytedance/Project/RogueCore/Content/UI/Menus/Menu_Workbench/Workbench_Ability.uasset" \
  --mappings "maps/RogueCore-5.6.1-143055+main-7f7cc36f_by_Iris.usmap" \
  --ue-version VER_UE5_6 \
  --outdir "output"
```

武器锻造台：

```bash
dotnet run --project UAssetStudio.Cli -- compile \
  "analysis/roguecore_e_unlock/forge/UI_Forge_Schematic/UI_Forge_Schematic.kms" \
  --asset "/Users/bytedance/Project/RogueCore/Content/UI/HUD_SpaceRig/Forge/UI_Forge_Schematic.uasset" \
  --mappings "maps/RogueCore-5.6.1-143055+main-7f7cc36f_by_Iris.usmap" \
  --ue-version VER_UE5_6 \
  --outdir "output"
```

强化剂分剂 BioBooster：

```bash
dotnet run --project UAssetStudio.Cli -- json \
  "analysis/roguecore_e_unlock/biobooster_json/BP_BioBooster_Frame.uasset.json" \
  --mappings "maps/RogueCore-5.6.1-143055+main-7f7cc36f_by_Iris.usmap" \
  --out "output/UnlockAll/Content/GameElements/BioBooster/BP_BioBooster_Frame.uasset"
```

注意：编译器会先在 `output` 根目录生成：

```text
Workbench_Ability.new.uasset
Workbench_Ability.new.uexp
UI_Forge_Schematic.new.uasset
UI_Forge_Schematic.new.uexp
```

需要移动/覆盖到游戏目录结构：

```text
output/Content/UI/Menus/Menu_Workbench/Workbench_Ability.uasset
output/Content/UI/Menus/Menu_Workbench/Workbench_Ability.uexp
output/Content/UI/HUD_SpaceRig/Forge/UI_Forge_Schematic.uasset
output/Content/UI/HUD_SpaceRig/Forge/UI_Forge_Schematic.uexp
```

## 验证记录

反编译职业技能升级输出：

```bash
dotnet run --project UAssetStudio.Cli -- decompile \
  "output/Content/UI/Menus/Menu_Workbench/Workbench_Ability.uasset" \
  --mappings "maps/RogueCore-5.6.1-143055+main-7f7cc36f_by_Iris.usmap" \
  --ue-version VER_UE5_6 \
  --outdir "analysis/roguecore_e_unlock/verify_compiled"
```

期望 `ReceiveInitializeOnce_283` 只创建选择项，不自动选中：

```text
ReceiveInitializeOnce_283:
  SetPlayerCharacterID(...)
  CreateChoices()
  return
```

反编译武器锻造台输出：

```bash
dotnet run --project UAssetStudio.Cli -- decompile \
  "output/Content/UI/HUD_SpaceRig/Forge/UI_Forge_Schematic.uasset" \
  --mappings "maps/RogueCore-5.6.1-143055+main-7f7cc36f_by_Iris.usmap" \
  --ue-version VER_UE5_6 \
  --outdir "analysis/roguecore_e_unlock/verify_compiled"
```

当前锻造台资产能编译成功，字节码反编译轨迹中可看到资源判断被置为 `true`。但 UAssetStudio 对 `TryBuildSchematic` 的高级 KMS 还原仍可能出现：

```text
Error decompiling function TryBuildSchematic: Sequence contains no matching element
```

这表示反编译器没有成功把该函数还原成高级 KMS 文本，不等同于资产编译失败。实际判断以编译结果和游戏内行为为准。

## 游戏内测试建议

重点确认以下行为：

1. 对职业技能升级点按 `E` 后，应直接出现 2 选 1选择界面。
2. 打开职业技能升级界面时，不应自动选择第一个升级。
3. 点击任意一个职业技能选项后，应正常应用该升级。
4. 对武器锻造台选择可锻造 schematic 时，即使没有对应资源，也应能执行锻造。
5. 已锻造、未解锁或状态不是可锻造的 schematic 仍应遵循原本状态限制。

