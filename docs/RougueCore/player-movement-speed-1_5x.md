# RogueCore 玩家移动速度 1.5 倍修改记录

> 游戏资源路径：`/Users/bytedance/Project/RogueCore`  
> Mappings：`maps/RogueCore-5.6.1-143055+main-7f7cc36f_by_Iris.usmap`  
> UE 版本：`VER_UE5_6`  
> 分析产物：`UAssetStudio/analysis/roguecore_player_speed/`

## 结论

玩家移动速度定义在：

```text
Content/Character/BP_PlayerCharacter.uasset
```

需要修改的是玩家基类 `BP_PlayerCharacter_C` 及其默认移动组件 `CharMoveComp`，不是敌人目录下的资产。职业子类继承 `BP_PlayerCharacter_C`，没有覆盖本次相关速度字段，因此修改玩家基类会影响所有玩家职业，但不会影响怪物。

本次改为原始值的 1.5 倍：

| 字段 | 原始值 | 修改后 |
|------|--------|--------|
| `CharMoveComp.MaxWalkSpeed` | `300.0f` | `450.0f` |
| `BP_PlayerCharacter_C.RunningSpeed` | `435.0f` | `652.5f` |

## 定位依据

反编译玩家基类：

```bash
dotnet run --project UAssetStudio.Cli -- decompile \
  "/Users/bytedance/Project/RogueCore/Content/Character/BP_PlayerCharacter.uasset" \
  --mappings "maps/RogueCore-5.6.1-143055+main-7f7cc36f_by_Iris.usmap" \
  --ue-version VER_UE5_6 \
  --outdir "analysis/roguecore_player_speed"
```

`BP_PlayerCharacter.kms` 中的关键字段：

```kms
object CharMoveComp : PlayerMovementComponent {
    float MaxWalkSpeed = 300.0f;
}

class BP_PlayerCharacter_C : PlayerCharacter {
    float RunningSpeed = 435.0f;
}
```

抽查职业子类：

```text
Content/Character/Reclaimers/BP_FalconeerCharacter.uasset
Content/Character/Reclaimers/BP_SlicerCharacter.uasset
Content/Character/Reclaimers/BP_GuardianCharacter.uasset
Content/Character/Reclaimers/BP_RetconCharacter.uasset
Content/Character/Reclaimers/BP_GorgonCharacter.uasset
Content/Character/Reclaimers/BP_SpotterCharacter.uasset
```

这些子类都继承 `BP_PlayerCharacter_C`，没有自己的 `MaxWalkSpeed` / `RunningSpeed` 覆盖值。

## 修改方式

尝试使用 `compile --asset` 从修改后的 KMS 编译时，命令可以成功生成资产，但二进制比较显示输出仍和原始资产一致。原因是当前编译链路没有把 KMS 中的默认属性改动写回 `NormalExport`，只重建脚本/字节码相关内容。

因此本次使用 UAssetAPI 直接修改默认属性：

```text
analysis/roguecore_player_speed/SpeedPatchTool/
```

工具写入两个 `FloatPropertyData`：

```text
CharMoveComp.MaxWalkSpeed: 300 -> 450
Default__BP_PlayerCharacter_C.RunningSpeed: 435 -> 652.5
```

输出文件：

```text
analysis/roguecore_player_speed/BP_PlayerCharacter_1_5x.uasset
analysis/roguecore_player_speed/BP_PlayerCharacter_1_5x.uexp
```

打包时需要把它们放回原游戏路径：

```text
Content/Character/BP_PlayerCharacter.uasset
Content/Character/BP_PlayerCharacter.uexp
```

## 验证

重新反编译输出资产：

```bash
dotnet run --project UAssetStudio.Cli -- decompile \
  "analysis/roguecore_player_speed/BP_PlayerCharacter_1_5x.uasset" \
  --mappings "maps/RogueCore-5.6.1-143055+main-7f7cc36f_by_Iris.usmap" \
  --ue-version VER_UE5_6 \
  --outdir "analysis/roguecore_player_speed"
```

验证结果：

```kms
float MaxWalkSpeed = 450.0f;
float RunningSpeed = 652.5f;
```

输出资产反编译后数值已落盘，说明修改生效。
