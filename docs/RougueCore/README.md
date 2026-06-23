# RogueCore 资产分析文档索引

> 游戏资源：`/Users/bytedance/Project/RogueCore`  
> Mappings：`maps/RogueCore-5.6.1-143055+main-7f7cc36f_by_Iris.usmap`  
> UE 版本：`VER_UE5_6`  
> 分析工具：UAssetStudio CLI（`dotnet run --project UAssetStudio.Cli --`）

本目录收录 RogueCore（Deep Rock Galactic: Rogue Core）的 UE5.6 资产逆向分析与 Mod 修改记录。

---

## 游戏系统

| 文档 | 主题 |
|------|------|
| [minerals-and-resources.md](./minerals-and-resources.md) | **矿物与可采资源**：19 种注册矿物、`GDResources` 注册表、Vein/Carved/Embed 分类 |
| [weapon-pool.md](./weapon-pool.md) | 武器池 / 手雷池（`BXEUnlockPool`、`UP_Weapons_InitialPool`） |
| [enemy-pool.md](./enemy-pool.md) | 怪物池（`GD_BXE_EncounterSettings`、`RCEnemyPool` 运行时组装） |
| [difficulty.md](./difficulty.md) | 难度配置（`DFC_BXE_H4_A*`、`StageDifficulty`、深度绑定） |
| [account-xp-progression.md](./account-xp-progression.md) | **账号 XP / 等级**：`GD_BXE_ProgressionSettings`、`RequiredXP`、等级上限 |
| [extraction-countdown.md](./extraction-countdown.md) | 任务计时 / Hostile Reading / 撤离倒计时 |
| [elevator-defense-countdown.md](./elevator-defense-countdown.md) | 启动电梯防守倒计时 |
| [drop-pod-entry-location.md](./drop-pod-entry-location.md) | 入场空降仓位置 |
| [procedural-layout-flat-spawn-summary.md](./procedural-layout-flat-spawn-summary.md) | 地图平面化、禁用高架/地下室、交互物落点 |
| [player-count-limit.md](./player-count-limit.md) | 联机人数限制 |
| [player-movement-speed-1_5x.md](./player-movement-speed-1_5x.md) | 移动速度 1.5 倍修改 |
| [weapon-switch-cooldown.md](./weapon-switch-cooldown.md) | 武器切换 CD 定位 |
| [customizable-weapon-viewmodel-fov.md](./customizable-weapon-viewmodel-fov.md) | 武器 Viewmodel FOV |
| [falconer-drone-chip-bonuses.md](./falconer-drone-chip-bonuses.md) | 御鹰者无人机局外芯片加成 |
| [trigger-discipline-enhancement.md](./trigger-discipline-enhancement.md) | 扳机纪律局外升级 |
| [iron-will-legacy-passive-skills.md](./iron-will-legacy-passive-skills.md) | 钢铁意志与 DRG 旧被动技能接入可行性 |
| [ui-netinfo-packet-loss.md](./ui-netinfo-packet-loss.md) | HUD 丢包信息显示 |

## 谈判 / 多选

| 文档 | 主题 |
|------|------|
| [selection-repeat.md](./selection-repeat.md) | 谈判多选索引 |
| [selection-repeat-asset.md](./selection-repeat-asset.md) | 资产 / 蓝图改法 |
| [selection-repeat-ue4ss.md](./selection-repeat-ue4ss.md) | UE4SS 运行时改法 |
| [negotiation-option-count.md](./negotiation-option-count.md) | 局内谈判选项数量 |
| [negotiation-sphere-one-player.md](./negotiation-sphere-one-player.md) | 谈判圈一人触发 |
| [negotiation-sphere-radius-patch-summary.md](./negotiation-sphere-radius-patch-summary.md) | 谈判圈范围 Patch 总结 |
| [character-repeat-selection.md](./character-repeat-selection.md) | 联机人物重复选择 |

## Patch / Mod 记录

| 文档 | 主题 |
|------|------|
| [e-unlock-class-upgrade-forge.md](./e-unlock-class-upgrade-forge.md) | E 键解锁职业技能升级与锻造台 |
| [generated-supply-crate-unlock-heal.md](./generated-supply-crate-unlock-heal.md) | 地图生成补给仓默认可用 + 回血 |
| [expenite-to-nitra-client-sync.md](./expenite-to-nitra-client-sync.md) | Expenite 改 Nitra 后的客户端同步问题 |

## 数据文件

| 文件 | 说明 |
|------|------|
| [particle_effect_assets.csv](./particle_effect_assets.csv) | 粒子特效资产清单 |

---

## 常用 CLI 模式

```bash
# JSON 导出（程序化检查属性）
dotnet run --project UAssetStudio.Cli -- --json json <asset.uasset> \
  --mappings maps/RogueCore-5.6.1-143055+main-7f7cc36f_by_Iris.usmap \
  --ue-version VER_UE5_6 --out tmp/output.json

# 反编译 Blueprint 逻辑
dotnet run --project UAssetStudio.Cli -- --json decompile <asset.uasset> \
  --mappings maps/RogueCore-5.6.1-143055+main-7f7cc36f_by_Iris.usmap \
  --ue-version VER_UE5_6 --outdir tmp/output --meta

# 运行已登记的 Mod 配方
dotnet run --project UAssetStudio.Cli -- --json mods run <mod-name> \
  --source /Users/bytedance/Project/RogueCore/Content \
  --out tmp/mods-out \
  --mappings maps/RogueCore-5.6.1-143055+main-7f7cc36f_by_Iris.usmap \
  --ue-version VER_UE5_6
```

## 关键全局资产速查

| 资产 | 路径 |
|------|------|
| 游戏数据总入口 | `Content/Game/BP_GameData.uasset` |
| 资源注册表 | `BP_GameData` → `GDResources` |
| 遭遇 / 怪物名册 | `Content/Game/GameData/BXESettings/GD_BXE_EncounterSettings.uasset` |
| 账号 XP / 等级 | `Content/Game/GameData/BXESettings/Progression/GD_BXE_ProgressionSettings.uasset` |
| 程序化地图 | `Content/Game/GameData/GD_ProceduralSettings.uasset` |
| 默认 Run | `Content/GameElements/Runs/RUN_Generic.uasset` |
