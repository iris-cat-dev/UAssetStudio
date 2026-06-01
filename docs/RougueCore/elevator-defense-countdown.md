# RogueCore 启动电梯倒计时定位

> 游戏资源路径：`/Users/bytedance/Project/RogueCore`  
> Mappings：`maps/RogueCore-5.6.1-143055+main-7f7cc36f_by_Iris.usmap`  
> UE 版本：`VER_UE5_6`  
> 分析产物：`UAssetStudio/analysis/elevator_countdown/`

## 结论

这次定位的是 **电梯防守 / 电梯重启阶段** 的倒计时，不是普通撤离电梯的 60 秒登车倒计时。

核心配置在：

```text
Content/GameElements/ElevatorObjectives/ElevatorDefense/EVENT_ElevatorDefense_BXE.uasset
```

关键字段：

| 字段 | 默认值 | 含义 |
|------|--------|------|
| `Duration` | `70d` | 电梯防守事件总时长，实际进度按该值推进 |
| `InitialProgress` | `0d` | 事件开始时进度 |
| `AtLocationWaveClass` | `EWC_RC_ElevatorCalled_C` | 电梯启动后触发的敌人波次 |
| `Event Start Shout` | `Shout_RC_Omega_ElevatorCalledtoNextLevel` | 事件开始语音 |

如果目标是 **缩短/延长“启动电梯”后需要守住的时间**，优先改 `EVENT_ElevatorDefense_BXE.Duration`。

## 和普通撤离倒计时的区别

| 系统 | 主要资产 | 默认时间 | 说明 |
|------|----------|----------|------|
| 电梯防守 / 重启事件 | `EVENT_ElevatorDefense_BXE` | `70s` | 本文主题，按 `Progress` 从 0 推到 1 |
| 撤离 HUD 倒计时 | `HUD_Countdown_Extraction` | 由 `FSDGameState` 广播 | UI 显示层，不负责启动计时 |
| Elite DropPod 撤离 | `BP_BXE_EliteDropPod_Escape` | `DepartureTime = 180.0f` | RogueCore DropPod 撤离版本 |
| Classic DropPod 撤离 | `BP_DropPod_Escape_Base` | `DepartureTime = 300.0f` | 旧 DropPod 撤离版本 |

已有更宽泛的任务计时分析见 `docs/RougueCore/extraction-countdown.md`。

## 运行链路

```text
玩家使用电梯防守点
  → BXE_ElevatorDefensePoint.DefenseStart()
  → DefencePointActor_Base.DefenseStart()
  → ActiveDefenceEvent = EVENT_ElevatorDefense_BXE
  → EVENT_Defense_Base.ReceiveTick()
  → Progress += DeltaSeconds / Duration * defenderBonus
  → Progress >= 1
  → DefendSucceded delegate
  → BXE_ElevatorDefensePoint.OnDefended / DefenseComplete
```

`BXE_ElevatorDefensePoint` 是电梯防守点入口，关键默认值：

```text
Content/GameElements/ElevatorObjectives/ElevatorDefense/BXE_ElevatorDefensePoint.uasset
```

| 字段 | 默认值 / 引用 | 说明 |
|------|---------------|------|
| `DefenseEvent` | `EVENT_ElevatorDefense_BXE_C` | 被启动的防守事件类 |
| `DefendPointUsable.UseDuration` | `2.0f` | 玩家按住/使用按钮的耗时，不是防守倒计时 |
| `MaxAllowedBreakdowns` | `3` | 防守过程中最多触发故障次数 |
| `MinBreakdownProgressDelta` | `0.2d` | 两次故障之间至少推进的进度差 |
| `ElevatorBreakdownTimer` | `TimerHandle` | 故障调度用 Timer |
| `BreakdownActors` | `BP_SimpleRestart_ElevatorBreakdown_C` | 故障修复交互蓝图 |

## 防守进度如何推进

进度推进逻辑在父类：

```text
Content/GameElements/Objectives/Old/DeepDive/Defense/EVENT_Defense_Base.uasset
```

关键字段：

| 字段 | 默认值 | 说明 |
|------|--------|------|
| `Duration` | `90d` | 通用默认值，子类可覆盖 |
| `InitialProgress` | `0.3000000119d` | 通用默认初始进度，子类可覆盖 |
| `Progress` | replicated double | 当前进度，`0 → 1` |
| `ProgressPaused` | replicated bool | 暂停防守进度 |
| `Defending player count` | replicated int | 当前防守圈内人数 |
| `Extra Defender Bonus` | `0.25d` | 多人防守加速系数 |

`EVENT_ElevatorDefense_BXE` 覆盖了：

```kms
double Duration = 70d;
double InitialProgress = 0d;
```

因此电梯防守事件从 0% 开始，按 70 秒基准推进。

## 故障 / 重启交互

防守过程中可能触发电梯故障，入口在：

```text
Content/GameElements/ElevatorObjectives/ElevatorDefense/BXE_ElevatorDefensePoint.uasset
```

故障 Actor：

```text
Content/GameElements/ElevatorObjectives/ElevatorDefense/BP_SimpleRestart_ElevatorBreakdown.uasset
```

关键字段：

| 字段 | 默认值 | 说明 |
|------|--------|------|
| `BaseResetTime` | `2.0f` | 单人修复基础耗时 |
| `ExtraResetTimePerPlayer` | `0.25f` | 每个额外玩家增加的修复耗时 |
| `SingleUsable.SetUseDuration(...)` | `BaseResetTime + ExtraResetTimePerPlayer * additionalPlayers` | BeginPlay 时计算 |

这个蓝图负责 **故障修复耗时**，不是启动电梯防守总时长。

## HUD 倒计时显示

撤离倒计时 UI 在：

```text
Content/UI/HUD_MainOnscreen/HUD_Countdown_Extraction.uasset
```

它只是显示层：

```text
Construct()
  → 绑定 FSDGameState.OnCountdownStarted
  → 绑定 FSDGameState.OnCountdownTimeChanged
  → 绑定 FSDGameState.OnCountdownFinished

OnCountdownTimeChanged(SecondsLeft)
  → FormatTime(SecondsLeft)
  → DATA_Time.SetText("{mm} : {ss}")
```

如果要改屏幕上 `mm:ss` 的格式，改 `HUD_Countdown_Extraction.FormatTime()`；如果要改真实秒数，不在这个 UI 里改。

## 修改指南

| 目标 | 修改位置 |
|------|----------|
| 改电梯防守 / 启动事件持续时间 | `EVENT_ElevatorDefense_BXE.Duration` |
| 改电梯防守初始进度 | `EVENT_ElevatorDefense_BXE.InitialProgress` |
| 改玩家按按钮启动耗时 | `BXE_ElevatorDefensePoint.DefendPointUsable.UseDuration` |
| 改故障出现次数 | `BXE_ElevatorDefensePoint.MaxAllowedBreakdowns` |
| 改故障出现频率门槛 | `BXE_ElevatorDefensePoint.MinBreakdownProgressDelta` |
| 改故障修复耗时 | `BP_SimpleRestart_ElevatorBreakdown.BaseResetTime` / `ExtraResetTimePerPlayer` |
| 改撤离 HUD 时间格式 | `HUD_Countdown_Extraction.FormatTime()` |
| 改 Elite DropPod 撤离时间 | `BP_BXE_EliteDropPod_Escape.DepartureTime` |

## 分析命令

```bash
# 从 UAssetStudio 根目录
export MAPPINGS="maps/RogueCore-5.6.1-143055+main-7f7cc36f_by_Iris.usmap"
export ASSET_ROOT="/Users/bytedance/Project/RogueCore/Content"
export OUT="analysis/elevator_countdown"

mkdir -p "$OUT"

# 电梯防守点入口
dotnet run --project UAssetStudio.Cli -- decompile \
  "$ASSET_ROOT/GameElements/ElevatorObjectives/ElevatorDefense/BXE_ElevatorDefensePoint.uasset" \
  --mappings "$MAPPINGS" --ue-version VER_UE5_6 \
  --outdir "$OUT" --meta

dotnet run --project UAssetStudio.Cli -- cfg \
  "$ASSET_ROOT/GameElements/ElevatorObjectives/ElevatorDefense/BXE_ElevatorDefensePoint.uasset" \
  --mappings "$MAPPINGS" --ue-version VER_UE5_6 \
  --outdir "$OUT"

# 电梯防守事件
dotnet run --project UAssetStudio.Cli -- decompile \
  "$ASSET_ROOT/GameElements/ElevatorObjectives/ElevatorDefense/EVENT_ElevatorDefense_BXE.uasset" \
  --mappings "$MAPPINGS" --ue-version VER_UE5_6 \
  --outdir "$OUT" --meta

# 通用防守事件父类
dotnet run --project UAssetStudio.Cli -- decompile \
  "$ASSET_ROOT/GameElements/Objectives/Old/DeepDive/Defense/EVENT_Defense_Base.uasset" \
  --mappings "$MAPPINGS" --ue-version VER_UE5_6 \
  --outdir "$OUT" --meta

dotnet run --project UAssetStudio.Cli -- cfg \
  "$ASSET_ROOT/GameElements/Objectives/Old/DeepDive/Defense/EVENT_Defense_Base.uasset" \
  --mappings "$MAPPINGS" --ue-version VER_UE5_6 \
  --outdir "$OUT"

# 故障修复蓝图
dotnet run --project UAssetStudio.Cli -- decompile \
  "$ASSET_ROOT/GameElements/ElevatorObjectives/ElevatorDefense/BP_SimpleRestart_ElevatorBreakdown.uasset" \
  --mappings "$MAPPINGS" --ue-version VER_UE5_6 \
  --outdir "$OUT" --meta

# HUD 倒计时显示层
dotnet run --project UAssetStudio.Cli -- decompile \
  "$ASSET_ROOT/UI/HUD_MainOnscreen/HUD_Countdown_Extraction.uasset" \
  --mappings "$MAPPINGS" --ue-version VER_UE5_6 \
  --outdir "$OUT" --meta
```

## 关键资产索引

```text
Content/
├── GameElements/
│   ├── ElevatorObjectives/
│   │   └── ElevatorDefense/
│   │       ├── BXE_ElevatorDefensePoint.uasset          # 电梯防守点入口
│   │       ├── EVENT_ElevatorDefense_BXE.uasset         # ★ Duration = 70
│   │       ├── BP_SimpleRestart_ElevatorBreakdown.uasset # 故障修复交互
│   │       └── BP_ElevatorBreakdown_Base.uasset
│   └── Objectives/
│       └── Old/DeepDive/Defense/
│           ├── DefencePointActor_Base.uasset            # 防守点父类
│           └── EVENT_Defense_Base.uasset                # ★ Progress/Duration 通用逻辑
├── Enemies/
│   └── Waves/WaveControllers/InUse/
│       └── EWC_RC_ElevatorCalled.uasset                 # 电梯事件波次
├── GameElements/DropPod/
│   └── BP_BXE_EliteDropPod_Escape.uasset                # DepartureTime = 180
├── LevelElements/Droppod/
│   └── BP_DropPod_Escape_Base.uasset                    # DepartureTime = 300
└── UI/HUD_MainOnscreen/
    └── HUD_Countdown_Extraction.uasset                  # mm:ss 显示层
```
