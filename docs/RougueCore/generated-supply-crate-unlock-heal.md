# RogueCore 地图生成补给仓默认可用与回血 Patch

> 游戏资源路径：`/Users/bytedance/Project/RogueCore`  
> Mappings：`maps/RogueCore-5.6.1-143055+main-7f7cc36f_by_Iris.usmap`  
> UE 版本：`VER_UE5_6`  
> 输出根目录：`/Users/bytedance/Project/UAssetStudio/output`

## 结论

地图生成的补给仓/补给箱主要走：

```text
Content/GameElements/RewardGivers/Supplies/BP_BXE_SupplyCrate_Base.uasset
Content/GameElements/RewardGivers/Supplies/BP_BXE_AmmoCrate.uasset
```

`BP_BXE_AmmoCrate` 继承自 `BP_BXE_SupplyCrate_Base`。实际“需要修复/解锁”的机制在基类中，回血比例由子类默认属性覆盖。

最终有效版本是 **v3**：

```text
output/rogue_supply_unlock_50pct_heal_v3.zip
```

同时已覆盖更新：

```text
output/rogue_supply_unlock_50pct_heal.zip
```

## 最终修改

### `BP_BXE_SupplyCrate_Base`

目标：地图生成后直接进入已修复、可补给状态，并关闭原本的修复交互。

`ReceiveBeginPlay` 中新增逻辑顺序：

```text
SingleUsable_FixPod.SetCanUse(false)
FlushNetDormancy()
AllGood = true
MarkPropertyDirtyFromRepIndex(Self, 10, "AllGood")
OnRep_AllGood()
SingleUsable1.SetCanUse(true)
SingleUsable2.SetCanUse(true)
SingleUsable3.SetCanUse(true)
SingleUsable4.SetCanUse(true)
```

同时修改默认对象：

```text
SingleUsable_FixPod_GEN_VARIABLE.usable: true -> false
```

这一步很关键。只设置 `AllGood=true` 会让外观变成已解锁，但玩家靠近时仍可能命中 `SingleUsable_FixPod`，表现为“视觉已解锁，实际仍要求解锁/修复”。

### `BP_BXE_AmmoCrate`

目标：地图生成的补给仓使用时额外回复 50% 生命。

修改默认属性：

```text
Default__BP_BXE_AmmoCrate_C.HealPercentage: 0.0 -> 0.5
```

基类 `ResupplyUser` 已经存在逻辑：

```text
if HealPercentage > 0:
  User.HealthComponent.Resupply(HealPercentage)
```

所以这里只需要把 `BP_BXE_AmmoCrate` 覆盖掉的 `HealPercentage=0` 改回 `0.5`。

## 排查记录

### v1 问题：`AllGood` 复制不同步

早期版本只做了：

```text
AllGood = true
OnRep_AllGood()
SingleUsable1-4.SetCanUse(true)
```

但原版修复完成路径还会：

```text
FlushNetDormancy()
MarkPropertyDirtyFromRepIndex(Self, 10, "AllGood")
```

`AllGood` 是 `RepNotify` 网络复制属性。缺少这两步后，多人或客户端侧可能看不到服务端的真实 `AllGood=true`，或者被后续复制状态覆盖。

### v2 问题：视觉已解锁但靠近仍要解锁

v2 补上了复制通知，但没有关闭 `SingleUsable_FixPod`。

`SingleUsable_FixPod` 是修复/解锁交互点，默认 `usable=true`，玩家靠近时仍可能优先命中它。最终 v3 同时做了运行时关闭和默认值关闭：

```text
SingleUsable_FixPod.SetCanUse(false)
SingleUsable_FixPod_GEN_VARIABLE.usable = false
```

## 输出文件

安装用路径：

```text
output/Content/GameElements/RewardGivers/Supplies/BP_BXE_SupplyCrate_Base.uasset
output/Content/GameElements/RewardGivers/Supplies/BP_BXE_SupplyCrate_Base.uexp
output/Content/GameElements/RewardGivers/Supplies/BP_BXE_AmmoCrate.uasset
output/Content/GameElements/RewardGivers/Supplies/BP_BXE_AmmoCrate.uexp
```

打包文件：

```text
output/rogue_supply_unlock_50pct_heal_v3.zip
output/rogue_supply_unlock_50pct_heal.zip
```

## 验证点

### CFG 验证

```bash
dotnet run --project UAssetStudio.Cli -- cfg \
  output/Content/GameElements/RewardGivers/Supplies/BP_BXE_SupplyCrate_Base.uasset \
  --mappings maps/RogueCore-5.6.1-143055+main-7f7cc36f_by_Iris.usmap \
  --ue-version VER_UE5_6 \
  --outdir output/supply_unlock_verify_v3
```

期望 `ReceiveBeginPlay` 中出现：

```text
EX_Context
  EX_InstanceVariable [SingleUsable_FixPod]
  EX_FinalFunction ... SingleUsableComponent->SetCanUse
    EX_False
EX_FinalFunction ... Actor->FlushNetDormancy
EX_LetBool
  EX_InstanceVariable [AllGood]
  EX_True
EX_CallMath ... NetPushModelHelpers->MarkPropertyDirtyFromRepIndex
  EX_Self
  EX_IntConst 10
  EX_NameConst AllGood
EX_LocalVirtualFunction OnRep_AllGood
```

### JSON 验证

```bash
dotnet run --project UAssetStudio.Cli -- json \
  output/Content/GameElements/RewardGivers/Supplies/BP_BXE_SupplyCrate_Base.uasset \
  --mappings maps/RogueCore-5.6.1-143055+main-7f7cc36f_by_Iris.usmap \
  --ue-version VER_UE5_6 \
  --out output/supply_unlock_verify_v3/BP_BXE_SupplyCrate_Base.json
```

期望：

```text
SingleUsable_FixPod_GEN_VARIABLE:
  usable = false
```

`BP_BXE_AmmoCrate` 期望：

```text
Default__BP_BXE_AmmoCrate_C:
  HealPercentage = 0.5
```

### 结构校验

```bash
dotnet run --project UAssetStudio.Cli -- validate \
  output/Content/GameElements/RewardGivers/Supplies/BP_BXE_SupplyCrate_Base.uasset \
  --mappings maps/RogueCore-5.6.1-143055+main-7f7cc36f_by_Iris.usmap \
  --ue-version VER_UE5_6

dotnet run --project UAssetStudio.Cli -- validate \
  output/Content/GameElements/RewardGivers/Supplies/BP_BXE_AmmoCrate.uasset \
  --mappings maps/RogueCore-5.6.1-143055+main-7f7cc36f_by_Iris.usmap \
  --ue-version VER_UE5_6
```

当前结果均为：

```text
Validation Result: PASSED
```

未提供 `--game-content` 时会有一条 import path validation 跳过警告，不影响资产结构验证。

## 游戏内测试建议

重点确认以下行为：

1. 补给仓落地/生成后视觉上直接为已解锁状态。
2. 靠近后提示应为补给/Resupply，而不是 `Open`、修复或解锁。
3. 使用补给后对应补给点会消耗，且角色回复约 50% 生命。
4. 多人测试时，非 Host 客户端也应看到已解锁状态，并能直接使用补给。
