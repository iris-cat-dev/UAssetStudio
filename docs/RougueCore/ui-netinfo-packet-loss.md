# RogueCore UI_NetInfo 丢包信息蓝图实现

> 游戏资源路径：`/Users/bytedance/Project/RogueCore`  
> Mappings：`maps/RogueCore-5.6.1-143055+main-7f7cc36f_by_Iris.usmap`  
> UE 版本：`VER_UE5_6`  
> 分析产物：`UAssetStudio/analysis/ui_netinfo/`

## 结论

`UI_NetInfo.uasset` 位于：

```text
Content/UI/Global_UI_Elements/UI_NetInfo.uasset
```

其中 `GetPktLossString` 是一个 Blueprint Pure 函数，用于把 `FSDGameInstance.GetConnectionInfo()` 返回的网络连接数组格式化成 5 列文本：

| 输出 | 含义 |
|------|------|
| `Names` | 玩家名列表 |
| `PktLossIn` | 入站丢包百分比列表 |
| `PktLossOut` | 出站丢包百分比列表 |
| `Ping` | Ping 毫秒列表 |
| `Jitter` | Jitter 毫秒列表 |
| `IsValid` | 是否存在连接信息数组 |

UI 更新发生在 `SlowTick`，当 `NetInfoLevel == 2` 且 `GetPktLossString.IsValid == true` 时显示 `PktLossBox`，否则隐藏丢包详情。

## 函数签名

在蓝图中创建一个 Pure 函数：

```text
GetPktLossString(
  out Names: Text,
  out PktLossIn: Text,
  out PktLossOut: Text,
  out Ping: Text,
  out Jitter: Text,
  out IsValid: Bool
)
```

本地变量建议：

| 变量 | 类型 | 初始值 |
|------|------|--------|
| `OutNames` | Text | 空 |
| `OutPktLossIn` | Text | `PL In` |
| `OutPktLossOut` | Text | `PL Out` |
| `OutPing` | Text | `Ping` |
| `OutJitter` | Text | `Jitter` |

## 蓝图节点流程

### 1. 读取连接信息

节点顺序：

```text
Self
  -> Get FSDGameInstance
  -> GetConnectionInfo
  -> Length
  -> Greater IntInt (> 0)
  -> Branch
```

如果 `Length <= 0`：

```text
Names = ""
PktLossIn = ""
PktLossOut = ""
Ping = ""
Jitter = ""
IsValid = false
Return
```

如果 `Length > 0`，继续遍历数组。

### 2. 初始化表头

在进入循环前设置：

```text
OutNames = ""
OutPktLossIn = "PL In"
OutPktLossOut = "PL Out"
OutPing = "Ping"
OutJitter = "Jitter"
```

### 3. 遍历 `NetworkConnectionInfo`

节点：

```text
ForEachLoop(ConnectionInfoArray)
  -> Break NetworkConnectionInfo
  -> Branch(IsValid)
```

只有元素自身的 `IsValid == true` 时才追加一行。元素无效时直接跳过，继续下一项。

### 4. 追加玩家名

玩家名来自辅助函数 `GetPlayerName(PlayerController)`。

```text
OutNames = Format Text("{0}\r\n{1}", OutNames, GetPlayerName(PlayerController))
```

蓝图中 `Format Text` 的参数名可以直接用 `0`、`1`，对应原资产里的 `FormatArgumentData.ArgumentName = "0"` / `"1"`。

### 5. 追加丢包百分比

入站丢包：

```text
PercentIn = As Percent Float(PacketLossIn)
OutPktLossIn = Format Text("{0}\r\n{1}", OutPktLossIn, PercentIn)
```

出站丢包：

```text
PercentOut = As Percent Float(PacketLossOut)
OutPktLossOut = Format Text("{0}\r\n{1}", OutPktLossOut, PercentOut)
```

原资产使用 `KismetTextLibrary.AsPercent_Float`，并开启分组显示，最小小数位为 `0`、最大小数位为 `1`。

### 6. 追加 Ping / Jitter

Ping：

```text
PingText = Conv Double To Text(Ping)
OutPing = Format Text("{0}\r\n{1}ms", OutPing, PingText)
```

Jitter：

```text
JitterText = Conv Double To Text(Jitter)
OutJitter = Format Text("{0}\r\n{1}ms", OutJitter, JitterText)
```

原资产中 `Ping` / `Jitter` 先转成 Text，再通过格式字符串拼上 `ms`。

### 7. 循环结束后返回

`ForEachLoop.Completed` 后：

```text
Names = OutNames
PktLossIn = OutPktLossIn
PktLossOut = OutPktLossOut
Ping = OutPing
Jitter = OutJitter
IsValid = true
Return
```

注意：这里的 `IsValid = true` 只表示连接信息数组长度大于 0。即使数组里所有元素的 `NetworkConnectionInfo.IsValid` 都是 false，函数仍会返回 true，只是输出内容基本只剩表头。

## GetPlayerName 辅助函数

`GetPlayerName` 的蓝图实现：

```text
Input:
  PlayerController: FSDPlayerController

Output:
  Name: String

Flow:
  IsValid(PlayerController)
    false -> Name = "<unknown>"
    true:
      IsValid(PlayerController.PlayerState)
        false -> Name = "<unknown>"
        true  -> Name = PlayerController.PlayerState.GetPlayerName()
```

这个函数返回 `String`，在 `GetPktLossString` 中再用 `Conv String To Text` 转成 Text。

## SlowTick 中的使用方式

`SlowTick` 调用 `ExecuteUbergraph_UI_NetInfo`，核心逻辑是：

```text
GetPktLossString(...)
if NetInfoLevel == 2 && IsValid:
  PktLossBox.SetVisibility(Visible)
  TextBlock_PktLoss_PlayerName.SetText(Names)
  TextBlock_PktLossIn.SetText(PktLossIn)
  TextBlock_PktLossOut.SetText(PktLossOut)
  TextBlock_Ping.SetText(Ping)
  TextBlock_Jitter.SetText(Jitter)
else:
  PktLossBox.SetVisibility(Hidden/Collapsed)
```

蓝图纯函数会在每个使用点重新求值。原资产里 `GetPktLossString` 在 `SlowTick` 中被重复调用多次：一次用于判断 `IsValid`，之后分别给 5 个 TextBlock 取输出值。如果手写蓝图并想减少重复查询，可以把函数改成非 Pure，或者先把输出保存到本地变量后再设置 TextBlock。

## 定时与显示级别

`Construct` 中会绑定设置变化事件：

```text
FSDGameUserSettings.OnShowNetInfoLevelChanged
  -> OnNetInfoLevelChanged(NewValue)
  -> NetInfoLevel = NewValue
```

同时读取当前设置：

```text
FSDGameUserSettings.GetShowNetInfoLevel()
  -> OnNetInfoLevelChanged(CurrentValue)
```

当 `NetInfoLevel` 非 0 时，会设置循环定时器：

```text
Set Timer by Delegate
  Delegate = SlowTick
  Time = 1.01
  Looping = true
```

显示级别大致为：

| `NetInfoLevel` | 行为 |
|----------------|------|
| `0` | 隐藏整个网络信息 UI |
| `1` | 显示基础网络信息，如 Ping、In/Out KB/s |
| `2` | 额外显示 `PktLossBox` 丢包明细 |

## 还原用伪代码

```csharp
var infos = GetFSDGameInstance(Self).GetConnectionInfo();

if (infos.Length <= 0)
{
    Names = "";
    PktLossIn = "";
    PktLossOut = "";
    Ping = "";
    Jitter = "";
    IsValid = false;
    return;
}

var outNames = "";
var outPktLossIn = "PL In";
var outPktLossOut = "PL Out";
var outPing = "Ping";
var outJitter = "Jitter";

foreach (var info in infos)
{
    if (!info.IsValid)
    {
        continue;
    }

    outNames = Format("{0}\r\n{1}", outNames, GetPlayerName(info.PlayerController));
    outPktLossIn = Format("{0}\r\n{1}", outPktLossIn, AsPercent(info.PacketLossIn));
    outPktLossOut = Format("{0}\r\n{1}", outPktLossOut, AsPercent(info.PacketLossOut));
    outPing = Format("{0}\r\n{1}ms", outPing, ToText(info.Ping));
    outJitter = Format("{0}\r\n{1}ms", outJitter, ToText(info.Jitter));
}

Names = outNames;
PktLossIn = outPktLossIn;
PktLossOut = outPktLossOut;
Ping = outPing;
Jitter = outJitter;
IsValid = true;
```

