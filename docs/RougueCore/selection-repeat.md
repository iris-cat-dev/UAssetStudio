# RogueCore 谈判多选（索引）

> 游戏资源路径：`/Users/bytedance/Project/RogueCore`  
> Mappings：`maps/RogueCore-5.6.1-140986+main-0196ef29.usmap`  
> UE 版本：`VER_UE5_6`

多人谈判里，同一张技能卡能否被多个玩家各选一次，由运行时 **`BXEUnlockInstance.CanBePickedMultipleTimes`** 控制。原版仅 **`Unlock_SkipForHealth`**（满血 + 最大生命 +20）等含 **`BXEHealAction`** 的卡默认可多选。

## 文档拆分

| 文档 | 适用 |
|------|------|
| [selection-repeat-asset.md](./selection-repeat-asset.md) | 改 `.uasset`：给 unlock 加 `BXEHealAction`、池子说明、蓝图局限 |
| [selection-repeat-ue4ss.md](./selection-repeat-ue4ss.md) | 运行时 Hook：全局设 `CanBePickedMultipleTimes = true` |

## 快速选型

| 需求 | 推荐 |
|------|------|
| 所有卡都能多人各选 | [UE4SS 改法一](./selection-repeat-ue4ss.md#改法一单-hook-全局多选推荐) |
| 仅部分卡多选 | 先试 [资产：BXEHealAction](./selection-repeat-asset.md#改法一给目标-unlock-添加-bxehealaction推荐尝试)，失败再用 UE4SS |
| 每人独立候选池 | 两种改法都不够，需改整条谈判系统（两篇文档均标注不建议） |
