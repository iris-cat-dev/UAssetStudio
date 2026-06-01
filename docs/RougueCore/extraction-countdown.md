# RogueCore 任务计时系统分析

> 游戏资源路径：`/Users/bytedance/Project/RogueCore`  
> Mappings：`maps/RogueCore-5.6.1-143055+main-7f7cc36f_by_Iris.usmap`  
> UE 版本：`VER_UE5_6`  
> 分析产物：`UAssetStudio/analysis/rc_timer/`

## 概述：两套独立的计时系统

RogueCore 任务内至少存在 **两套互不相同的计时/进度系统**，修改前务必区分。战斗难度分层（DFC、StageDifficulty、Run 绑定）见 [`difficulty.md`](./difficulty.md)。

| 系统 | 玩家可见位置 | 触发时机 | 核心配置 | HUD 资产 |
|------|-------------|----------|----------|----------|
| **Hostile Reading 敌意读数** | 屏幕左上角圆环进度条 | 进入任务后立即开始 | `BXE_StageDifficultyProgression_2Step` | `HUD_HostileReading` |
| **电梯撤离倒计时** | 呼叫电梯后的专用倒计时 UI | 按下电梯按钮 / 目标完成后 | `BP_Elevator_Base.ElevatorCallDuration` | `HUD_Countdown_Extraction` |

以下 **不是** 上述任一套的秒数配置：

| 概念 | 实际含义 | 配置位置 |
|------|----------|----------|
| `MD_Duration_Short` | 任务选择界面「Short / Long」显示标签 | `MissionDescription/MD_Duration_Short` |
| `MD_Stages_4` | 任务关卡数量（4 关） | `MissionDescription/BXE/MD_Stages_4` |
| `StageDifficulty_Generic_Stage*` | 各阶段敌人数量/伤害/抗性修正 | `GameElements/StageDifficulty/` |
| `GD_BXE_ProgressionSettings` | 玩家账号 XP 升级 | `Game/GameData/BXESettings/Progression/` |

---

## 一、Hostile Reading（左上角进度条）

### 1.1 系统说明

进入任务后，屏幕左上角显示的 **圆环进度条（Hostile Reading）** 反映当前任务的 **Hostile Level 阶段进度**：

- 圆环随任务时间推进而填充
- 不同阶段切换颜色（Low → Insane / Red）
- 圆环上的小图标标记 **下一波敌人** 的预计到来时间
- 进度条满 / 到达 **Insane（Red）** 后，触发 End Mission 惩罚（无限刷怪 + 抓人触手）

这与电梯撤离 60 秒倒计时 **完全无关**。

### 1.2 架构与数据流

```
任务开始
  → BP_GameState.DifficultyControllerComponent（C++ DifficultyController）
  → StageDifficultyProgression = BXE_StageDifficultyProgression_2Step
  → 按 BXEDifficultyPoint 数组推进阶段（Low → Insane）
  → 广播 OnLevelLifeTimeUpdated / OnNewDifficulty / OnNextWaveLevelTimeChanged
  → HUD_HostileReading 读取并渲染圆环
```

**HUD 调用的关键 API（来自 `DifficultyController` / `FSDGameState`）：**

| API | 用途 |
|-----|------|
| `GetLevelLifeTime()` | 当前阶段相关时间 |
| `GetLevelLifeTimeForRedDifficulty()` | 到 Red（Insane）的总时间，用作进度条满格刻度 |
| `GetNextWaveLevelTime()` | 下一波次时间，用于圆环上的小图标角度 |
| `GetStageDifficultyProgression()` → `GetDifficulties()` | 读取 `BXEDifficultyPoint` 数组（颜色、名称等） |

**Gauntlet 模式** 使用独立 GameState 与 Progression：

- `BP_GameState_Gauntlet.uasset` → `BXE_StageDifficultyProgression_Gauntlet`

### 1.3 UI 资产

| 资产 | 路径 | 作用 |
|------|------|------|
| 主 Widget | `Content/UI/HUD_MainOnscreen/HostileReadings/HUD_HostileReading.uasset` | 圆环进度条、阶段颜色、波次图标、Red 预警文本 |
| 进度条材质 | `Content/UI/HUD_MainOnscreen/HostileReadings/M_HostileReading.uasset` | 接收 `NextDifficultyLevelTime`、`BarColor` 等材质参数 |
| 填充条 Widget | `Content/UI/HUD_MainOnscreen/HostileReadings/GUI_HostileReading_FilledProgressBar.uasset` | 进度条子组件 |
| 深度显示 | `Content/UI/HUD_MainOnscreen/HostileReadings/HUD_Depth_RC.uasset` | 关联深度 UI |

HUD 逻辑摘录（`analysis/rc_timer/HUD_HostileReading.kms`）：

- `UpdateProgressMaterial()` 调用 `GetLevelLifeTimeForRedDifficulty()`，写入材质参数 `NextDifficultyLevelTime`
- `UpdateBarColor()` 从 `BXEDifficultyPoint.Color` 读取阶段颜色
- `ComputeNextWaveAngleFraction()` 根据 `NextWaveLevelTime` 与 `NextDifficultyLevelTime` 计算波次图标角度
- 监听 `OnNewDifficulty` / `OnNewDifficultySoonWarning` / `OnNextWaveLevelTimeChanged`

### 1.4 运行时挂载点

**路径：** `Content/Game/BP_GameState.uasset`

```kms
object DifficultyController : DifficultyController {
    Object<StageDifficultyProgression> StageDifficultyProgression = BXE_StageDifficultyProgression_2Step;
    bool EnemyCountModifierUsed = true;
    Struct<RuntimeFloatCurve> EnemyCountModifierScaleOverTime = { ExternalCurve: CRV_BXE_Stepped_EnemyCountScaleOverTime };
}
```

`DifficultyController` 是 **C++ 组件**（`/Script/RogueCore.DifficultyController`），阶段计时逻辑在原生代码中运行；**时间数值** 来自下方的 DataAsset 配置。

阶段切换时，`GM_BXE.TriggerNewDifficulty()` 会：

1. 触发 `NewDifficulty.WaveAtStart` 波次
2. 播放 `NewDifficulty.StartMusic`
3. 播放 `NewDifficulty.MissionControlStartShout` 语音

### 1.5 阶段时间配置（核心）

**路径：** `Content/GameElements/Difficulty/BXE_StageDifficultyProgression_2Step.uasset`

类型：`StageDifficultyProgression`（RawExport 数据资产，非 Blueprint）

内含 **`BXEDifficultyPoint` 数组**。普通任务为 **2 步（2Step）**：

| 阶段名 | 含义 | 关键配置 |
|--------|------|----------|
| **Low** | 初始 Hostile Level | `LevelLifeTime`、起始音乐 `ML_GenericWave`、`WaveAtStart` |
| **Insane** | Hostile Level Red | `TimesPerPlayerCount`、Red 语音、触手波次 |

**Insane（Red）触发时间 — `TimesPerPlayerCount`（秒，从任务开始计）：**

| 玩家人数 | 到 Red 的时间 |
|----------|---------------|
| 1 人 | **660s（11 分钟）** |
| 2 人 | **600s（10 分钟）** |
| 3 人 | **570s（9.5 分钟）** |
| 4 人 | **540s（9 分钟）** |

**其他已解析数值：**

| 字段 | 数值 | 说明 |
|------|------|------|
| Low 阶段 `LevelLifeTime` | **25** | 需在 UE 编辑器中确认单位（阶段持续时间） |
| `WarningTimeBeforeNextDifficulty` | **~120s** | Red 前约 2 分钟预警，对应 `OnNewDifficultySoonWarning` |

**Insane 阶段引用的 Red 惩罚资源：**

- 语音：`Shout_RC_Omega_HostileLevelRed_Active` / `_SuggestExtraction` / `_Close`
- 触手波次：`EWC_RC_EndMission_CoreTentacles`
- 音乐：`ML_GenericWave`

> Gauntlet 变体 `BXE_StageDifficultyProgression_Gauntlet.uasset` 的 Insane 时间与上表基本一致（660/600/570/540/120s），挂载于 `BP_GameState_Gauntlet`。

### 1.6 圆环上的波次图标（非阶段边界）

圆环上的小图标 **不代表 Hostile Level 阶段切换**，而是 **下一波敌人的预计时间**：

| 来源 | 配置 | 说明 |
|------|------|------|
| `GetNextWaveLevelTime()` | `DifficultyController` 运行时计算 | HUD 用于 `ComputeNextWaveAngleFraction()` |
| `BXEDifficultyPoint.WaveAtStart` | 各阶段的起始波次控制器 | 阶段切换时由 `TriggerNewDifficulty()` 触发 |
| `DFC_BXE_H4_A*` 难度因子 | `EnemyWaveInterval` / `EnemyNormalWaveInterval` | 常规波次刷新间隔（如 255–330s），影响波次密度 |

示例：`DFC_BXE_H4_A1` 的 `EnemyWaveInterval` 范围为 255–330 秒。

### 1.7 到达 Red 后的惩罚链

进度条满 / Insane 阶段激活后，与电梯超时共用同一套 End Mission 惩罚资源：

```
Hostile Level Red（Insane）
  → EWC_RC_EndMission_CorespawnEndless（无限 Corespawn 循环）
  → EWC_RC_EndMission_CoreTentacles（抓人触手）
```

详见本文 **第三节、第四节**。

### 1.8 修改指南（Hostile Reading）

| 目标 | 修改位置 |
|------|----------|
| 改到 Red 的总时间（按人数） | `BXE_StageDifficultyProgression_2Step` → Insane 点 → **`TimesPerPlayerCount`** |
| 改 Low → Insane 切换时间 | 同文件 → Low 点 → **`LevelLifeTime`** |
| 改 Red 前预警时间 | 同文件 → **`WarningTimeBeforeNextDifficulty`**（约 120s） |
| Gauntlet 模式 | `BXE_StageDifficultyProgression_Gauntlet`（`BP_GameState_Gauntlet` 引用） |
| 改阶段颜色 / UI 样式 | `HUD_HostileReading` + `M_HostileReading` |
| 改 Red 后触手/无限刷怪 | Insane 点引用的 `EWC_RC_EndMission_*`（惩罚内容，非计时本身） |
| 改常规波次间隔 | `DFC_BXE_H4_A*` → `EnemyWaveInterval`（**不是**阶段边界时间） |
| 改各阶段敌人强度 | `StageDifficulty_Generic_Stage*`（**不含时间字段**） |

---

## 二、电梯撤离倒计时（60 秒）

### 2.1 系统说明

玩家完成目标并 **呼叫电梯** 后出现的专用倒计时 UI。60 秒内未全员登车则触发 End Mission 惩罚。

```
关卡目标完成 / 呼叫电梯
  → BP_Elevator_Base 启动倒计时（ElevatorCallDuration = 60s）
  → HUD_Countdown_Extraction 显示剩余时间
  → 60s 内全员进电梯？
       ├─ 是 → GM_BXE.EndRunNormally() 正常撤离
       └─ 否 → RecieveReturnTimerExpired()
              → GM_BXE.EndRunTimeAttack()
              → EWC_RC_EndMission_CorespawnEndless（无限 Corespawn）
              → BXE_StageDifficultyProgression_2Step（HostileLevelRed）
              → EWC_RC_EndMission_CoreTentacles（抓人触手）
```

### 2.2 核心配置

| 属性 | 默认值 | 资产 |
|------|--------|------|
| `ElevatorCallDuration` | **60**（秒） | `Content/GameElements/Elevator/BP_Elevator_Base.uasset` |
| 超时回调 | `RecieveReturnTimerExpired()` | 同上（继承自 `TeamElevator`） |
| 任务类型 | `MissionType = Extraction` | 同上 |

子类 Blueprint（如 `BP_Elevator_Start`）**未覆盖** `ElevatorCallDuration`，各关通常共用 **60 秒**。

反编译摘录（`analysis/rc_timer/BP_Elevator_Base.kms`）：

```kms
int ElevatorCallDuration = 60;
Enum<EMiningPodMission> MissionType = Extraction;
public override void RecieveReturnTimerExpired() { ... }
```

### 2.3 UI 显示

| 资产 | 说明 |
|------|------|
| `Content/UI/HUD_MainOnscreen/HUD_Countdown_Extraction.uasset` | 撤离倒计时 HUD |
| 事件 | `OnCountdownStarted` / `OnCountdownTimeChanged` / `OnCountdownFinished` |

### 2.4 超时触发逻辑

| 资产 | 关键符号 |
|------|----------|
| `Content/Game/GM_BXE.uasset` | `EndRunTimeAttack()` — 超时攻击模式 |
| 同上 | `EndRunNormally()` — 正常撤离 |
| 同上 | `EnemyWaveManager.TriggerWave()` / `SetAllWavesAreBlocked()` |
| 同上 | `HaveAnyPlayersLeftWithFailure()` — 判定是否有玩家未登车 |

`GM_BXE` 直接引用 `EWC_RC_EndMission_CorespawnEndless`。

### 2.5 修改指南（撤离倒计时）

| 目标 | 修改位置 |
|------|----------|
| 改撤离倒计时秒数 | `BP_Elevator_Base` → `ElevatorCallDuration`（默认 60） |
| 改 HUD 显示 | `HUD_Countdown_Extraction` |

---

## 三、End Mission 惩罚：无限刷怪

两种触发路径（Hostile Reading 到 Red **或** 电梯撤离超时）均可能进入此流程。

**路径：** `Content/Enemies/Waves/WaveControllers/InUse/EWC_RC_EndMission_CorespawnEndless.uasset`

| 属性 / 逻辑 | 说明 |
|-------------|------|
| `StartWave()` | 生成 Rift → `SpawnEnemiesFromRCPool()` |
| `OnWaveCompleted()` | 完成后再次 `StartWave()`，**无限循环** |
| `ElevatorArrivedWaveCount` | 波次计数 |
| `RetriggerableDelay` | 波次间隔控制 |
| 音乐 | `ML_GenericWave` |

| 目标 | 修改位置 |
|------|----------|
| 改无限刷怪强度/频率 | `EWC_RC_EndMission_CorespawnEndless` |

---

## 四、End Mission 惩罚：无敌抓人触手

### 4.1 难度阶段引用

**路径：** `Content/GameElements/Difficulty/BXE_StageDifficultyProgression_2Step.uasset`（Insane 点）

引用：

- `EWC_RC_EndMission_CoreTentacles`
- `Shout_RC_Omega_HostileLevelRed_Active`
- `Shout_RC_Omega_HostileLevelRed_Active_SuggestExtraction`
- `Shout_RC_Omega_HostileLevelRed_Close`

### 4.2 触手波次控制器

**路径：** `Content/Enemies/Waves/WaveControllers/InUse/EWC_RC_EndMission_CoreTentacles.uasset`

| 属性 | 说明 |
|------|------|
| `Type` | `EWaveControllerType.Tentacle` |
| `RedWaveCount` | 红警触手波次数（逻辑中从 3 起算） |
| `TenatacleCount` | 当前已存在触手数量（运行时计数器，不是目标配置） |
| 生成方式 | `SpawnEnemiesAtLocationWithCallback` + `GetSpawnPointInRange` |
| 敌人描述 | `ED_SnakingCoreTentacle` |
| 生成间隔 | Timer：约 15s / 10s / 5s（TryToSpawn 逻辑） |

### 4.3 触手敌人资产

| 资产 | 作用 |
|------|------|
| `Enemies/SnakingCoreTentacle/ED_SnakingCoreTentacle.uasset` | 敌人生成描述 |
| `Enemies/SnakingCoreTentacle/ENE_SnakingCoreTentacle_Base.uasset` | 实体（含 `GrabberComponent`） |
| `Enemies/SnakingCoreTentacle/STE_SnakingGrab.uasset` | 抓取状态效果（定身 + 持续伤害） |

`STE_SnakingGrab` 配置：`damageAmount = 5.0`，`ApplyEffectsInterval = 0.8s`。

触手走 **Tentacle 专用波次类型**，配合 `GrabberComponent`，Gameplay 上不可正常击杀，只负责抓人。

### 4.4 如何改成生成数量为 0

不要直接把 `TenatacleCount` 改成 0。该字段是运行时“当前已存在触手数”的计数器，不是目标生成数量；`TryToSpawn()` 里反而有 `TenatacleCount == 0` 时立即生成的分支。

CFG 摘要（`analysis/rc_timer/EWC_RC_EndMission_CoreTentacles.txt`）中的核心逻辑：

```text
TryToSpawn:
  Players = GetPlayerCharactersNotBurried()
  Target = Array_Length(Players) - 1
  ShouldSpawn = (TenatacleCount == 0) OR (TenatacleCount < Target)
  if ShouldSpawn:
    SpawnEnemiesAtLocationWithCallback(ED_SnakingCoreTentacle, 1, SpawnPoint, TentacleSpawnedEvent, ...)

TentacleSpawnedEvent:
  TenatacleCount += 1

TentacleDestroyed:
  TenatacleCount -= 1
```

**推荐改法：移除 Red 阶段对触手波次控制器的引用。**

修改位置：

| 模式 | 修改资产 | 修改项 |
|------|----------|--------|
| 普通任务 | `Content/GameElements/Difficulty/BXE_StageDifficultyProgression_2Step.uasset` | Insane 点里移除 / 置空 `EWC_RC_EndMission_CoreTentacles` |
| Gauntlet | `Content/GameElements/Difficulty/BXE_StageDifficultyProgression_Gauntlet.uasset` | 同样处理 Insane 点 |

这样 Hostile Level Red / 电梯超时仍可保留其他惩罚（例如无限 Corespawn），但不再启动触手控制器、计时器和 LateJoin 触手阶段通知。

**如果必须只改 `EWC_RC_EndMission_CoreTentacles` 本体：**

| 方案 | 效果 | 风险 |
|------|------|------|
| 让 `TryToSpawn()` 在进入后直接返回 / 让 `ShouldSpawn` 恒为 false | 真正不生成触手 | 需要改 Blueprint 字节码；当前 KMS 对 `ExecuteUbergraph` 反编译不完整，不适合直接用 KMS 往回编译 |
| 把 `SpawnEnemiesAtLocationWithCallback(..., Count=1, ...)` 的 `Count` 参数改为 `0` | 表面上生成数量为 0 | 仍会运行触手波次控制器、Timer、音乐/通知逻辑；还要确认 `SpawnEnemiesAtLocationWithCallback` 对 0 数量是否无副作用 |

因此，做 Mod 时最稳的是 **从 `BXE_StageDifficultyProgression_*` 断开 `EWC_RC_EndMission_CoreTentacles` 引用**，而不是修改 `TenatacleCount`。

| 目标 | 修改位置 |
|------|----------|
| 让触手生成数量为 0 | `BXE_StageDifficultyProgression_2Step` / `BXE_StageDifficultyProgression_Gauntlet` → Insane 点移除 `EWC_RC_EndMission_CoreTentacles` |
| 改触手生成逻辑/间隔 | `EWC_RC_EndMission_CoreTentacles`（`TryToSpawn` / Timer；`TenatacleCount` 是运行时计数器，不是目标数量） |
| 改 Red 阶段关联资源 | `BXE_StageDifficultyProgression_2Step`（Insane 点） |

---

## 五、相关但非计时的配置

### 5.1 任务 DNA（关卡结构，非秒数）

示例：`Content/GameElements/Missions/BXE_DNA/DNA_BXE_Linear_Normal4_Simple.uasset`

- `AmountOfStages = 4`
- `Duration` → 引用 `MD_Stages_4`（StageDuration，关卡数 UI）
- `Complexity` → `MD_Complexity_Simple`

### 5.2 阶段难度模板（敌人修正，非时间）

示例：`Content/GameElements/StageDifficulty/StageDifficulty_Generic_Stage2.uasset`

- `StageEnemyCountModifier`
- `StageEnemyDamageModifier`
- `StageResistanceModifier_*`

**不含任何时间秒数字段。**

### 5.3 难度因子（常规刷怪间隔）

示例：`Content/GameElements/Difficulty/DFC_BXE_H4_A4.uasset`

- `EnemyWaveInterval`：165–210s
- `EnemyNormalWaveInterval`：60–120s

影响 **Hostile Reading 圆环上的波次图标密度**，不是阶段边界时间，也不是撤离倒计时。

### 5.4 Gauntlet 模式

| 资产 | 说明 |
|------|------|
| `BXE_StageDifficultyProgression_Gauntlet.uasset` | Gauntlet 专用 Hostile Reading 阶段配置 |
| `BP_GameState_Gauntlet.uasset` | 引用 Gauntlet Progression |
| `GM_BXE_Gauntlet.uasset` | Gauntlet 专用 GameMode，引用 `EWC_RC_Gauntlet_GameOverTrigger` |

---

## 六、分析命令

```bash
# 从 UAssetStudio 根目录
export MAPPINGS="maps/RogueCore-5.6.1-143055+main-7f7cc36f_by_Iris.usmap"
export ASSET_ROOT="/Users/bytedance/Project/RogueCore/Content"

# --- Hostile Reading ---

# 反编译 HUD
dotnet run --project UAssetStudio.Cli -- decompile \
  "$ASSET_ROOT/UI/HUD_MainOnscreen/HostileReadings/HUD_HostileReading.uasset" \
  --mappings "$MAPPINGS" --ue-version VER_UE5_6 \
  --outdir analysis/rc_timer

# 反编译 GameState（DifficultyController 挂载）
dotnet run --project UAssetStudio.Cli -- decompile \
  "$ASSET_ROOT/Game/BP_GameState.uasset" \
  --mappings "$MAPPINGS" --ue-version VER_UE5_6 \
  --outdir analysis/rc_timer

# JSON 导出阶段 Progression（RawExport，含 BXEDifficultyPoint 二进制）
dotnet run --project UAssetStudio.Cli -- json \
  "$ASSET_ROOT/GameElements/Difficulty/BXE_StageDifficultyProgression_2Step.uasset" \
  --mappings "$MAPPINGS" --ue-version VER_UE5_6 \
  --out analysis/rc_timer/BXE_StageDifficultyProgression_2Step.json

# --- 电梯撤离倒计时 ---

dotnet run --project UAssetStudio.Cli -- decompile \
  "$ASSET_ROOT/GameElements/Elevator/BP_Elevator_Base.uasset" \
  --mappings "$MAPPINGS" --ue-version VER_UE5_6 \
  --outdir analysis/rc_timer

# --- End Mission 惩罚 ---

dotnet run --project UAssetStudio.Cli -- decompile \
  "$ASSET_ROOT/Enemies/Waves/WaveControllers/InUse/EWC_RC_EndMission_CorespawnEndless.uasset" \
  --mappings "$MAPPINGS" --ue-version VER_UE5_6 \
  --outdir analysis/rc_timer

dotnet run --project UAssetStudio.Cli -- decompile \
  "$ASSET_ROOT/Enemies/Waves/WaveControllers/InUse/EWC_RC_EndMission_CoreTentacles.uasset" \
  --mappings "$MAPPINGS" --ue-version VER_UE5_6 \
  --outdir analysis/rc_timer
```

---

## 七、关键资产索引

```
Content/
├── Game/
│   ├── BP_GameState.uasset                    # DifficultyController → Progression_2Step
│   ├── BP_GameState_Gauntlet.uasset           # Gauntlet → Progression_Gauntlet
│   └── GM_BXE.uasset                          # TriggerNewDifficulty, EndRunTimeAttack
├── GameElements/
│   ├── Difficulty/
│   │   ├── BXE_StageDifficultyProgression_2Step.uasset   # ★ Hostile Reading 阶段时间
│   │   ├── BXE_StageDifficultyProgression_Gauntlet.uasset
│   │   └── DFC_BXE_H4_A*.uasset               # 波次间隔（非阶段边界）
│   ├── StageDifficulty/
│   │   └── StageDifficulty_Generic_Stage*.uasset  # 敌人修正（非时间）
│   ├── Elevator/
│   │   └── BP_Elevator_Base.uasset            # ElevatorCallDuration = 60
│   └── Missions/
│       ├── BXE_DNA/DNA_BXE_Linear_*.uasset    # 关卡结构（非秒数）
│       └── MissionDescription/
│           ├── MD_Duration_Short.uasset       # UI 标签，非计时
│           └── BXE/MD_Stages_*.uasset         # 关卡数量
├── Enemies/
│   ├── Waves/WaveControllers/InUse/
│   │   ├── EWC_RC_EndMission_CorespawnEndless.uasset
│   │   └── EWC_RC_EndMission_CoreTentacles.uasset
│   └── SnakingCoreTentacle/
│       ├── ED_SnakingCoreTentacle.uasset
│       ├── ENE_SnakingCoreTentacle_Base.uasset
│       └── STE_SnakingGrab.uasset
└── UI/HUD_MainOnscreen/
    ├── HostileReadings/
    │   ├── HUD_HostileReading.uasset          # ★ 左上角 Hostile Reading 进度条
    │   ├── M_HostileReading.uasset
    │   └── HUD_Depth_RC.uasset
    └── HUD_Countdown_Extraction.uasset      # 电梯撤离倒计时 UI
```
