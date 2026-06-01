# RogueCore 批量给 Unlock 添加 BXEHealAction 工作流

## 目标

给一组 `BXEUnlockGeneric` 技能资产的 `Unlocks` 数组追加 `BXEHealAction` 子对象，用来复用原版 `Unlock_SkipForHealth` 的多人可重复选择判定。

当前验证过的目标目录：

```bash
/Users/bytedance/Project/RogueCore/Content/Unlocks/NonGear/InUse/Damage
```

参考资产：

```bash
/Users/bytedance/Project/RogueCore/Content/Unlocks/NonGear/InUse/Unlock_SkipForHealth.uasset
```

Mappings：

```bash
maps/RogueCore-5.6.1-143055+main-7f7cc36f_by_Iris.usmap
```

UE 版本：

```bash
VER_UE5_6
```

## 关键结论

不要只走 `compile --asset` 修改 KMS。普通编译模式只会更新原资产中已经存在的 export，不会真正新增 `BXEHealAction_0` 子对象。表现是命令显示 compiled，但再次反编译生成物时 `Unlocks` 数组仍然没有 `BXEHealAction_0`。

推荐流程是：

1. `.uasset` 转 JSON。
2. 在 JSON 层新增 `BXEHealAction` import/export。
3. 把主资产 export 的 `Unlocks` 数组追加到新 export。
4. JSON 转回 `.uasset/.uexp`。
5. 再反编译和 validate 验证。

## 目标结构

参考 `Unlock_SkipForHealth`，目标资产最后应反编译成类似：

```kms
object Unlock_BloodRush : BXEUnlockGeneric {
    Array<Object<BXELogicAction>> Unlocks = [BXELogicUnlockDataAsset_0, BXEHealAction_0];
    ...
}

object BXEHealAction_0 : BXEHealAction {
    string Name = "heal";
    float HealPercent = 0.0f;
}
```

`HealPercent = 0.0f` 用于尽量只触发多选判定，不给技能附带实际回血。

## 工作目录

```bash
OUT=output/rogue_damage_heal
TARGET_DIR="/Users/bytedance/Project/RogueCore/Content/Unlocks/NonGear/InUse/Damage"
MAP="maps/RogueCore-5.6.1-143055+main-7f7cc36f_by_Iris.usmap"
VER="VER_UE5_6"
CONTENT="/Users/bytedance/Project/RogueCore/Content"
```

## Step 1：反编译确认现状

```bash
mkdir -p "$OUT/source_kms"

for f in "$TARGET_DIR"/*.uasset; do
  dotnet run --project UAssetStudio.Cli -- decompile "$f" \
    --mappings "$MAP" \
    --ue-version "$VER" \
    --outdir "$OUT/source_kms"
done
```

检查每张卡原始 `Unlocks` 数组里是否已经有 `BXEHealAction`：

```bash
rg "BXEHealAction|Unlocks = \\[" "$OUT/source_kms"
```

## Step 2：导出 JSON

```bash
mkdir -p "$OUT/json"

for f in "$TARGET_DIR"/*.uasset; do
  name=$(basename "${f%.uasset}")
  dotnet run --project UAssetStudio.Cli -- json "$f" \
    --mappings "$MAP" \
    --ue-version "$VER" \
    --out "$OUT/json/${name}.json"
done
```

## Step 3：JSON 层补丁

补丁逻辑：

- 确保 `NameMap` 包含 `BXEHealAction`、`Default__BXEHealAction`、`BXEHealAction_0`、`HealPercent`、`Name`。
- 在 `Imports` 中追加：
  - `Default__BXEHealAction`
  - `BXEHealAction`
- 新增一个 `NormalExport`：
  - `ObjectName = BXEHealAction_0`
  - `ClassIndex = BXEHealAction` import
  - `TemplateIndex = Default__BXEHealAction` import
  - `OuterIndex = 主资产 export`
  - `Data = Name("heal") + HealPercent(0.0)`
- 主资产 `Unlocks` 数组追加 `ObjectPropertyData`，值为新 export index。
- `BXEUnlockTieredGeneric` 没有顶层 `Unlocks`，需要改 `TieredUnlocks` 中每个 tier 的 `Actions` 数组，并追加同一个 `BXEHealAction_0`。
- 更新 `DependsMap` 和 `Generations.ExportCount/NameCount`。

本次实际使用的脚本在：

```bash
output/rogue_damage_heal/patch_json.py
```

运行：

```bash
python3 "$OUT/patch_json.py"
```

输出目录：

```bash
$OUT/json_patched
```

## Step 4：JSON 转回资产

```bash
mkdir -p "$OUT/json_assets"

for json in "$OUT"/json_patched/Unlock_*.json; do
  name=$(basename "${json%.json}")
  dotnet run --project UAssetStudio.Cli -- json "$json" \
    --mappings "$MAP" \
    --ue-version "$VER" \
    --out "$OUT/json_assets/${name}.uasset"
done
```

输出会同时生成 `.uasset` 和 `.uexp`。

## Step 5：反编译验证

```bash
mkdir -p "$OUT/final_verify"

for f in "$OUT"/json_assets/Unlock_*.uasset; do
  dotnet run --project UAssetStudio.Cli -- decompile "$f" \
    --mappings "$MAP" \
    --ue-version "$VER" \
    --outdir "$OUT/final_verify"
done

rg "BXEHealAction|Unlocks = \\[" "$OUT/final_verify"
```

期望每个目标资产都出现：

```kms
Unlocks = [..., BXEHealAction_0]

object BXEHealAction_0 : BXEHealAction {
    string Name = "heal";
    float HealPercent = 0.0f;
}
```

## Step 6：结构校验

```bash
for f in "$OUT"/json_assets/Unlock_*.uasset; do
  dotnet run --project UAssetStudio.Cli -- validate "$f" \
    --mappings "$MAP" \
    --ue-version "$VER" \
    --game-content "$CONTENT"
done
```

期望全部：

```text
Validation Result: PASSED
Asset validation passed with no issues
```

## 本次产物

最终可用于打包的资产位于：

```bash
output/rogue_damage_heal/json_assets
```

包含：

```text
Unlock_BloodRush.uasset
Unlock_BloodRush.uexp
Unlock_FishInABarrel.uasset
Unlock_FishInABarrel.uexp
Unlock_GunLink_15p.uasset
Unlock_GunLink_15p.uexp
Unlock_WeaponTag_LowAmmoBigDamage.uasset
Unlock_WeaponTag_LowAmmoBigDamage.uexp
```

## 注意事项

- 资产结构验证通过不等于游戏逻辑一定生效。`BXEHealAction` 触发多人可重复选择仍是基于原版 `Unlock_SkipForHealth` 的行为推断，需要多人局实测。
- 如果希望附带真实回血，把 `HealPercent` 改为 `1.0f`；如果只想当作标记，保持 `0.0f`。
- 不要直接覆盖原始 `RogueCore/Content`，先把 `json_assets` 作为 mod 产物打包或复制到临时 mod 目录验证。
