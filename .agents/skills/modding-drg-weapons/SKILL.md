---
name: modding-drg-weapons
description: Use this skill when creating Deep Rock Galactic weapon overclock mods using UAssetStudio. This includes generating status effects, upgrade bonuses, overclock packages, and modifying the overclock bank (OSB). Also use when compiling .kms scripts to .uasset files, analyzing original game assets, or creating complete weapon mod workflows with hover-invulnerability, damage boosts, or custom status effects.
---

# Modding DRG Weapons

## Overview

This skill automates the creation of Deep Rock Galactic weapon overclock mods using UAssetStudio. It generates KMS script templates, compiles them to game-ready .uasset files, and organizes outputs according to the game's directory structure.

## Quick Start

### Prerequisites

- UAssetStudio CLI built and ready (`dotnet build`)
- Original game .uasset files extracted to `original_assets/BoltActionRifle/`
- UE4.27 version assets (Deep Rock Galactic uses this version)

### Creating Your First Mod

**Example:** Create a hover-invulnerability overclock for M1000

```bash
# 1. Generate mod template
./scripts/generate_mod.py --name "HoverclockInvulnerable" --weapon M1000 --type "hover-invuln"

# 2. Compile all components
./scripts/compile_mod.sh

# 3. Install to game (see output for paths)
```

## Workflow

### Step 1: Analyze Original Assets

Before creating a mod, examine the original game files:

```bash
# List available overclocks for a weapon
ls original_assets/BoltActionRifle/Overclocks/OC_*.uasset

# Check structure of an existing overclock
dotnet run --project UAssetStudio.Cli -- decompile OC_M1000_Hoverclock_C.uasset --outdir ./analysis
```

### Step 2: Generate Mod Templates

Use the generation script to create KMS templates:

```bash
./scripts/generate_mod.py \
  --name "MyOverclock" \
  --weapon M1000 \
  --type "custom" \
  --effect "damage-boost" \
  --duration 5.0
```

Generated files:
- `STE_MyOverclock.kms` - Status effect definition
- `OC_Bonus_MyOverclock.kms` - Upgrade bonus
- `OC_M1000_MyOverclock_C.kms` - Overclock package
- `OSB_M1000_Modified.kms` - Modified overclock bank

### Step 3: Customize KMS Files

Edit the generated .kms files to adjust:
- Stat values (damage, duration, multipliers)
- Status effect types
- Crafting costs
- Description texts

### Step 4: Compile

```bash
./scripts/compile_mod.sh
```

Output structure:
```
output/
├── STE_MyOverclock.uasset
├── Overclocks/
│   ├── OC_M1000_MyOverclock_C.uasset
│   ├── OSB_M1000.uasset
│   └── OC_BonusesAndPenalties/
│       └── OC_Bonus_MyOverclock.uasset
```

### Step 5: Install

Copy compiled files to game directory:

```bash
GAME_DIR="/path/to/DeepRockGalactic/Content"

# Status effect
cp output/STE_*.uasset "$GAME_DIR/WeaponsNTools/BoltActionRifle/"
cp output/STE_*.uexp "$GAME_DIR/WeaponsNTools/BoltActionRifle/"

# Overclocks (replace original OSB_M1000!)
cp output/Overclocks/*.uasset "$GAME_DIR/WeaponsNTools/BoltActionRifle/Overclocks/"
cp output/Overclocks/*.uexp "$GAME_DIR/WeaponsNTools/BoltActionRifle/Overclocks/"

# Bonuses
cp output/Overclocks/OC_BonusesAndPenalties/* "$GAME_DIR/WeaponsNTools/BoltActionRifle/Overclocks/OC_BonusesAndPenalties/"
```

## Common Mod Types

### Hover Invulnerability
- **Effect:** No gravity + invincible during focus mode
- **Key Property:** `NoGravityOnFocus` + high damage resistance
- **Duration:** Matches `NoGravityOnFocusDuration` (default 1.5s)

### Focus Damage Boost
- **Effect:** Increased damage on charged shots
- **Key Property:** `FocusDamageMultiplier`
- **Example:** OC_M1000_FocusDamage_U

### Status Effect on Hit
- **Effect:** Apply DOT or debuff to enemies
- **Key Property:** `FocusedHitSTE` (Status Effect on focused hit)
- **Example:** OC_M1000_FocusElectrocute_U

## Key File Reference

| File | Purpose | Location in Game |
|------|---------|------------------|
| `STE_*.uasset` | Status effect definitions | `WeaponsNTools/BoltActionRifle/` |
| `OC_*_C.uasset` | Overclock packages | `WeaponsNTools/BoltActionRifle/Overclocks/` |
| `OSB_M1000.uasset` | Overclock bank (weapon's overclock list) | `WeaponsNTools/BoltActionRifle/Overclocks/` |
| `OC_Bonus_*.uasset` | Upgrade bonuses | `WeaponsNTools/BoltActionRifle/Overclocks/OC_BonusesAndPenalties/` |
| `WPN_M1000.uasset` | Weapon blueprint | `WeaponsNTools/BoltActionRifle/` |

## Important Properties

### Status Effects (STE)
```kms
float Duration = 1.5f;
Object<PawnAffliction> PawnAffliction = PAF_IronWill;  // Visual/sound effects
Array<Object<StatChangeStatusEffectItem>> StatusEffects = [...];  // Stat modifiers
```

### Upgrade Bonuses (OC_Bonus)
```kms
Enum<EBoltActionRifleUpgrades> UpgradeType = `EBoltActionRifleUpgrades::NoGravityOnFocus`;
float Amount = 1.0f;
Struct<Guid> SaveGameID = { SaveGameID: "{unique-guid}" };  // Must be unique!
```

### Overclock Packages (OC_*_C)
```kms
Object<SchematicCategory> Category = SCAT_OC_Clean;  // Clean/Balanced/Unstable
Array<SoftObject> CombinedUpgrades = [...];  // References to bonuses
Object CraftingCost = { RES_Credits: 8500, ... };
```

## Troubleshooting

### Compilation fails
- Check original asset exists in `original_assets/`
- Verify UE version matches (VER_UE4_27 for DRG)
- Check KMS syntax with `./scripts/compile_mod.sh --syntax-check`

### Mod doesn't appear in game
- Verify OSB_M1000.uasset was replaced (contains overclock list)
- Check file paths match game's directory structure
- Ensure .uexp files are copied alongside .uasset

### Status effect not applying
- Verify `PawnAffliction` references a valid affliction
- Check `StatChangeStatusEffectItem` has correct `Stat` and `StatChange` values
- Ensure `Duration` is set correctly

## Resources

### scripts/
- `generate_mod.py` - Generate KMS templates for new mods
- `compile_mod.sh` - Compile all KMS files to .uasset
- `analyze_asset.sh` - Decompile and analyze original game assets

### references/
- `kms_syntax.md` - KMS language syntax reference
- `drg_asset_structure.md` - Deep Rock Galactic asset organization
- `common_patterns.md` - Reusable mod patterns and examples
- `upgrade_types.md` - All EBoltActionRifleUpgrades values

### assets/
- `templates/` - Starter KMS templates for common mod types
  - `status_effect.kms.template`
  - `upgrade_bonus.kms.template`
  - `overclock.kms.template`
  - `overclock_bank.kms.template`
