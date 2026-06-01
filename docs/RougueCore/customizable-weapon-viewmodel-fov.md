# CustomizableWeaponViewmodelFOV 原理与蓝图复现

> Mod 路径：`/Users//Desktop/CustomizableWeaponViewmodelFOV`  
> 分析输出：`analysis/customizable_weapon_viewmodel_fov`  
> UE 版本：`VER_UE4_27`  
> 核心资产：`CustomizeWeaponFOVBP`、`CustomizeWeaponFOVWidget`、`SavedPresets`、`YAxisSave_Struct`

## 概述

这个 mod 的核心不是修改相机 FOV，而是运行时修改当前装备物品的 **`Item.FPCameraOffset`**。

`FPCameraOffset` 是物品第一人称模型相对摄像机的位置偏移。修改它以后，武器/工具模型在屏幕里的远近和位置会变化，视觉效果类似 viewmodel FOV 调整。

整体结构：

```text
InitCave / InitSpaceRig
  -> BeginPlay 生成 CustomizeWeaponFOVBP
      -> 注册 OpenFOVWidget 输入
      -> 创建 CustomizeWeaponFOVWidget
      -> 按键显示/隐藏 Widget
          -> Widget 读取当前装备 Item
          -> 修改 Item.FPCameraOffset
          -> SaveGame 按 preset 保存每个物品的 offset
```

## 资产结构

| 资产 | 类型 | 作用 |
|------|------|------|
| `InitCave` | `Actor` | 洞穴场景初始化入口，`BeginPlay` 生成主控制 Actor |
| `InitSpaceRig` | `Actor` | Space Rig 初始化入口，逻辑与 `InitCave` 相同 |
| `CustomizeWeaponFOVBP` | `Actor` | 主控制 Actor，注册输入、创建 UI、切换鼠标和 Widget 显示 |
| `CustomizeWeaponFOVWidget` | `UserWidget` | 主 UI 和全部 offset 修改/保存逻辑 |
| `LIB_WT_SliderEditX/Y/Z` | `UserWidget` | 三个滑条控件，分别发出 `OnValueChanged` 和 `SliderReleased` |
| `SavedPresets` | `SaveGame` | 持久化保存所有 preset |
| `YAxisSave_Struct` | `UserDefinedStruct` | 单个 preset 的保存结构，内部保存物品名到 Vector 的 Map |

## 初始化入口

`InitCave` 和 `InitSpaceRig` 都只做一件事：

```text
Event BeginPlay
  -> MakeTransform(0,0,0)
  -> BeginDeferredActorSpawnFromClass(CustomizeWeaponFOVBP)
  -> FinishSpawningActor
```

蓝图复现时需要准备两个入口 Actor，分别用于游戏关卡和大厅场景。具体如何让游戏加载这两个入口 Actor，取决于你的 mod 装载/打包方式；从资产本身看，它们的职责只是把主控制 Actor 生出来。

## 主控制 Actor

`CustomizeWeaponFOVBP` 的职责是接入输入和 UI。

### BeginPlay 流程

蓝图节点逻辑：

```text
Event BeginPlay
  -> GetLIB_A_MainRef
  -> AddCustomActionMapping(
       ActionName = "OpenFOVWidget",
       InputChord = None,
       Description = "Opens custom FoV widget"
     )
  -> 循环等待 GetLocalPlayerCharacter 有效
  -> 循环等待 PlayerController 有效
  -> EnableInput(PlayerController)
  -> Delay(1.0)
  -> CreateWidget(CustomizeWeaponFOVWidget)
  -> 保存到变量 Widget Ref
  -> AddToViewport
```

其中等待玩家和 Controller 的逻辑是为了避免 mod 初始化早于本地玩家对象。原蓝图在对象无效时每 `0.2s` 重试。

### 打开/关闭 UI

输入事件是 `OpenFOVWidget`。

```text
InputAction OpenFOVWidget
  -> TempBool = NOT TempBool
  -> Branch TempBool
       true:
         GetFSDGameInstance
         GetLocalFSDPlayerController
         Set bShowMouseCursor = true
         Widget Ref.SetVisibility(Visible)
         Widget Ref.SetFocus()
       false:
         GetFSDGameInstance
         GetLocalFSDPlayerController
         Set bShowMouseCursor = false
         Widget Ref.SetVisibility(Hidden)
```

复现时建议把 `TempBool` 命名为 `IsWidgetOpen`，比原蓝图更直观。

## SaveGame 数据结构

`CustomizeWeaponFOVWidget` 使用的保存槽：

```text
Save Game Slot = "Mods/CustomizeWeaponFOV/Settings"
UserIndex = 0
```

`SavedPresets` 结构：

| 字段 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| `YAxisSaves` | `Map<String, YAxisSave_Struct>` | 含 `"Default"` | 所有 preset |
| `SelectedPreset` | `String` | `"Default"` | 当前选中的 preset |

`YAxisSave_Struct` 结构：

| 字段 | 类型 | 说明 |
|------|------|------|
| `YAxisPreset` | `Map<String, Vector>` | key 是 `Item.GetAnalyticsItemName()`，value 是该物品的 `FPCameraOffset` |

虽然字段名叫 `YAxis`，实际保存的是完整 `Vector(X,Y,Z)`，不是只保存 Y 轴。

## Widget 初始化

`CustomizeWeaponFOVWidget` 的关键变量：

| 变量 | 类型 | 作用 |
|------|------|------|
| `Item Ref` | `Item` | 当前装备物品 |
| `YAxisSave` | `Map<String, Vector>` | 当前 preset 下的所有物品 offset |
| `Save Slot` | `SavedPresets` | 当前 SaveGame 对象 |
| `TempCopyToAll` | `Vector` | “复制到所有物品”时暂存当前 offset |
| `TempRenameSave` | `Map<String, Vector>` | 重命名 preset 时暂存旧 preset 内容 |
| `PreRenameOption` | `String` | 重命名前的 preset 名 |

### Construct

```text
Event Construct
  -> SetVisibility(Hidden)
```

Widget 创建后默认隐藏，只在按键触发时显示。

### OnInitialized

```text
Event OnInitialized
  -> GetLIB_A_MainRef
  -> FindCustomAction("OpenFOVWidget")
  -> Branch: ActionChord == None
       true:
         TryRemap(InputChord Key=O)
       false:
         InputKeySelector.SetSelectedKey(ActionChord)
  -> Load Save
  -> Presets_Dropdown.ClearOptions
  -> Map_Keys(Save Slot.YAxisSaves)
  -> ForEach key:
       Presets_Dropdown.AddOption(key)
  -> Presets_Dropdown.SetSelectedOption(Save Slot.SelectedPreset)
  -> SetTimerDelegate("Refresh PlayerComponent", 3.0, Looping=true)
  -> Bind InventoryComponent.OnItemEquipped -> OnItemChanged
```

默认按键是 `O`。如果用户已经绑定过 `OpenFOVWidget`，则 UI 只显示已有绑定。

## 加载与保存

### Load Save

```text
Function Load Save
  -> DoesSaveGameExist(Save Game Slot, 0)
  -> Branch
       true:
         LoadGameFromSlot
         Cast to SavedPresets
         Save Slot = Cast Result
         Map_Find(Save Slot.YAxisSaves, Save Slot.SelectedPreset)
         YAxisSave = FoundStruct.YAxisPreset
       false:
         CreateSaveGameObject(SavedPresets)
         Save Slot = Created Object
```

注意：原蓝图在新建 SaveGame 后依赖 `SavedPresets` 的默认值包含 `"Default"` preset。

### SaveGame

```text
Function SaveGame
  -> Make YAxisSave_Struct
       YAxisPreset = YAxisSave
  -> Map_Add(Save Slot.YAxisSaves, Save Slot.SelectedPreset, Struct)
  -> SaveGameToSlot(Save Slot, Save Game Slot, 0)
```

所有 slider 释放、文本提交、preset 切换/创建/删除/重命名最终都会调用或间接调用这个保存流程。

## 当前物品刷新

`OnItemChanged` 是最核心的函数。

```text
Function OnItemChanged(Item)
  -> GetGameInstance
  -> GetFSDGameInstance
  -> GetLocalPlayerCharacter
  -> InventoryComponent.GetEquippedItem
  -> Item Ref = EquippedItem
  -> ItemName = Item Ref.GetAnalyticsItemName()
  -> Branch: ItemName == ""
       true:
         WeaponNameTextBox.SetText("No ID For Current Item")
         return
  -> Map_Find(YAxisSave, ItemName)
  -> Branch found
       true:
         Item Ref.FPCameraOffset = FoundVector
       false:
         DefaultOffset = GetObjectClass(Item Ref).Default.FPCameraOffset
         Map_Add(YAxisSave, ItemName, DefaultOffset)
         Item Ref.FPCameraOffset = DefaultOffset
  -> Change Sliders
  -> ChangeTextBoxes
  -> WeaponNameTextBox.SetText(ItemName)
```

这里的 key 不是资产路径，而是 `GetAnalyticsItemName()` 返回的字符串。这样同一种物品在不同职业/不同实例下可以共用同一条配置。

## 修改 X/Y/Z Offset

### Slider 回调

三个 slider 的逻辑完全一样，只是替换不同轴。

X 轴：

```text
On LIB_WT_SliderEditX.OnValueChanged(NewX)
  -> BreakVector(Item Ref.FPCameraOffset) 得到 OldX, OldY, OldZ
  -> NewOffset = MakeVector(NewX, OldY, OldZ)
  -> ItemName = Item Ref.GetAnalyticsItemName()
  -> Map_Add(YAxisSave, ItemName, NewOffset)
  -> Map_Find(YAxisSave, ItemName)
  -> Item Ref.FPCameraOffset = FoundVector
  -> ChangeTextBoxes

On LIB_WT_SliderEditX.SliderReleased
  -> SaveGame
```

Y 轴：

```text
NewOffset = MakeVector(OldX, NewY, OldZ)
```

Z 轴：

```text
NewOffset = MakeVector(OldX, OldY, NewZ)
```

蓝图复现时，三个 slider 的 Min/Max 建议设为：

```text
Min Value = -40
Max Value = 40
Step Size 可按手感设置，例如 0.1 或 0.5
```

原文本输入的范围判断是 `-40` 到 `40`，但报错文本写的是 `"Number must be between -20 and 20"`，这是文案和实际范围不一致。

### 文本框提交

三个文本框也是同一套逻辑。

```text
On XTextBox.OnTextCommitted(Text)
  -> Conv_TextToString
  -> IsNumeric
  -> Conv_StringToFloat
  -> InRange(-40, 40, inclusive)
  -> BreakVector(Item Ref.FPCameraOffset)
  -> NewOffset = MakeVector(NewX, OldY, OldZ)
  -> Item Ref.FPCameraOffset = NewOffset
  -> Map_Add(YAxisSave, ItemName, Item Ref.FPCameraOffset)
  -> SaveGame
  -> Change Sliders
```

Y/Z 只替换对应轴。输入无效时原蓝图直接不处理；超出范围时把对应文本框改成错误提示。

## UI 同步函数

### Change Sliders

```text
Function Change Sliders
  -> BreakVector(Item Ref.FPCameraOffset)
  -> SliderX.MainSlider.SetValue(X)
  -> SliderY.MainSlider.SetValue(Y)
  -> SliderZ.MainSlider.SetValue(Z)
```

### ChangeTextBoxes

```text
Function ChangeTextBoxes
  -> BreakVector(Item Ref.FPCameraOffset)
  -> XTextBox.SetText(FloatToText(X))
  -> YTextBox.SetText(FloatToText(Y))
  -> ZTextBox.SetText(FloatToText(Z))
```

这两个函数让 slider、文本框和实际物品 offset 保持一致。

## Preset 操作

### 创建 preset

```text
CreateButton.OnClicked
  -> Name = Presets_NameBox.GetText()
  -> Struct.YAxisPreset = YAxisSave
  -> Map_Add(Save Slot.YAxisSaves, Name, Struct)
  -> Save Slot.SelectedPreset = Name
  -> 刷新 dropdown
  -> SaveGameToSlot
```

### 切换 preset

```text
Presets_Dropdown.UserChangedSelection(SelectedItem)
  -> Save Slot.SelectedPreset = SelectedItem
  -> Map_Find(Save Slot.YAxisSaves, SelectedItem)
  -> YAxisSave = FoundStruct.YAxisPreset
  -> OnItemChanged(None)
  -> SaveGame
```

### 删除 preset

```text
DeleteButton.OnClicked
  -> GetSelectedOption
  -> Map_Remove(Save Slot.YAxisSaves, SelectedOption)
  -> Dropdown.RemoveOption(SelectedOption)
  -> GetOptions
  -> Save Slot.SelectedPreset = Options[0]
  -> 刷新 dropdown
  -> SaveGame
```

复现时建议加保护：不要允许删除最后一个 preset，避免 `Options[0]` 为空。

### 重命名 preset

```text
RenameButton.OnClicked
  -> OldName = Dropdown.GetSelectedOption()
  -> Map_Find(Save Slot.YAxisSaves, OldName)
  -> TempRenameSave = FoundStruct.YAxisPreset
  -> Map_Remove(Save Slot.YAxisSaves, OldName)
  -> NewName = Presets_NameBox.GetText()
  -> NewStruct.YAxisPreset = TempRenameSave
  -> Map_Add(Save Slot.YAxisSaves, NewName, NewStruct)
  -> Save Slot.SelectedPreset = NewName
  -> 刷新 dropdown
  -> SaveGame
```

### 重置 Default

```text
DefaultButton.OnClicked
  -> Map_Add(Save Slot.YAxisSaves, "Default", EmptyStruct)
  -> Save Slot.SelectedPreset = "Default"
  -> 刷新 dropdown
  -> SaveGame
```

这会把 `"Default"` preset 覆盖为空结构，之后当前物品会重新从 class default 读取 `FPCameraOffset`。

## 装备辅助按钮

Widget 里还有两个装备按钮：

| 按钮 | 行为 |
|------|------|
| `EquipLaserPointerOnButton` | `InventoryComponent.Equip(InventoryComponent.LaserPointerItem)` |
| `EquipPickaxeOnButton` / `Button_0` | `InventoryComponent.EquipCategory(byte 6)` |

`byte 6` 的具体枚举名需要在游戏的 `EItemCategory` 里确认。资产命名显示它大概率对应 pickaxe/工具类入口。

## 复制当前 Offset 到所有物品

`CopyOffsetsToAllItems` 的作用是把当前装备物品的 offset 复制到所有职业、所有目标物品。

流程：

```text
Function CopyOffsetsToAllItems
  -> CurrentItem = LocalPlayer.InventoryComponent.GetEquippedItem
  -> CurrentName = CurrentItem.GetAnalyticsItemName()
  -> TempCopyToAll = YAxisSave[CurrentName]
  -> ForEach GameData.CharacterSettings.PlayerCharacterIDs
       -> GetHeroInventoryList(CharacterID)
       -> 遍历若干 EItemCategory，收集 GetItemList(Category)
       -> AddUnique(LaserPointerItem)
  -> ForEach TempAllItemArray
       -> Item = ItemID.GetItem()
       -> Name = Item.GetAnalyticsItemName()
       -> Map_Add(YAxisSave, Name, TempCopyToAll)
```

原蓝图遍历的类别包括枚举值 `0, 1, 2, 3, 6`，并额外加入 `LaserPointerItem`。复现时建议使用可读的 `EItemCategory` 枚举名，不要直接写 byte，便于后续维护。

## Slider 控件复现

`LIB_WT_SliderEditX/Y/Z` 基本是同一个控件复制了三份。每个控件包含：

| 成员 | 作用 |
|------|------|
| `MainSlider` | 实际 UMG Slider |
| `Backfill` | 用于显示填充进度 |
| `OnValueChanged(float)` | slider 值变化时广播 |
| `SliderReleased()` | 鼠标/手柄释放时广播 |

事件逻辑：

```text
MainSlider.OnFloatValueChanged(Value)
  -> UpdateDisplayedValues
  -> Broadcast OnValueChanged(Value)

MainSlider.OnMouseCaptureEnd
  -> Broadcast SliderReleased

MainSlider.OnControllerCaptureEnd
  -> Broadcast SliderReleased
```

如果不追求完全复刻 UI，可以不用单独做 `LIB_WT_SliderEditX/Y/Z`，直接在主 Widget 里放三个 Slider，并绑定它们的 `OnValueChanged` 和 `OnMouseCaptureEnd`。

## 复现蓝图清单

最小可用版本只需要这些蓝图：

1. `BP_InitCave`：`BeginPlay` 生成主 Actor。
2. `BP_InitSpaceRig`：同上。
3. `BP_ViewmodelFOVController`：注册输入、创建 Widget、显示/隐藏 Widget。
4. `WBP_ViewmodelFOV`：读取当前装备，修改 `FPCameraOffset`，保存 preset。
5. `SG_ViewmodelFOVPresets`：`SaveGame`，保存 `Map<String, PresetStruct>`。
6. `ST_ViewmodelFOVPreset`：保存 `Map<String, Vector>`。

推荐先做最小功能：

```text
打开 Widget
  -> 显示当前装备名
  -> 三个 Slider 改 Item.FPCameraOffset
  -> SaveGame 保存当前 preset
  -> 切换装备时自动应用保存的 offset
```

Preset 创建/重命名/删除、复制到所有物品、按键重绑定可以后续再加。

## 注意事项

- `FPCameraOffset` 是运行时对象属性，改的是当前 `Item` 实例；要持久化必须自己保存并在装备变化时重新应用。
- 保存 key 使用 `GetAnalyticsItemName()`，如果某个物品返回空字符串，原蓝图会显示 `No ID For Current Item`，不保存。
- 文本输入实际允许 `-40` 到 `40`，但错误文案写成 `-20` 到 `20`。
- Preset 删除没有明显的“最后一个 preset”保护，复现时建议补上。
- `Refresh PlayerComponent` 用 3 秒循环 timer 重新绑定 `OnItemEquipped`，是为了应对玩家/InventoryComponent 在场景切换或装备状态变化后引用失效。
- 这个方案依赖游戏暴露的 `Item.FPCameraOffset` 可写。如果目标版本里该属性名或访问方式变了，需要重新反编译对应版本确认。
