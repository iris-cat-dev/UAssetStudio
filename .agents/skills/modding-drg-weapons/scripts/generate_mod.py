#!/usr/bin/env python3
"""
Generate KMS template files for Deep Rock Galactic weapon overclock mods.

Usage:
    ./generate_mod.py --name "HoverclockInvulnerable" --weapon M1000 --type hover-invuln
    ./generate_mod.py --name "FocusDamageBoost" --weapon M1000 --type damage-boost --amount 2.0
"""

import argparse
import uuid
import os
from datetime import datetime


def generate_guid():
    """Generate a unique GUID for SaveGameID."""
    return str(uuid.uuid4())


def generate_status_effect(name, duration=1.5, stat_change=9999.0):
    """Generate STE (Status Effect) KMS template."""
    return f'''[Import("/Script/FSD")]
public class StatChangeStatusEffectItem {{}}

[Import("/Script/FSD")]
public StatusEffect Default__StatusEffect;

[Import("/Script/FSD")]
public class PawnStat {{}}

[Import("/Script/FSD")]
public class PawnAffliction {{}}

[Import("/Script/Engine")]
public class BlueprintGeneratedClass {{}}

[Import("/Script/CoreUObject")]
public class Object {{}}


[Import("/Game/GameElements/PawnStats/PST_DamageResistance")]
public PawnStat PST_DamageResistance;

[Import("/Game/GameElements/PawnAffliction/PAF_IronWill")]
public PawnAffliction PAF_{name};


object StatChangeStatusEffectItem_0 : StatChangeStatusEffectItem {{
    Object<PawnStat> Stat = PST_DamageResistance;
    float StatChange = {stat_change}f;
    bool AffectedByResistances = false;
}}

[Parsed, ReplicationDataIsSetUp, EditInlineNew, CompiledFromBlueprint, HasInstancedReference]
class STE_{name}_C : StatusEffect {{
    Object<PawnAffliction> PawnAffliction = PAF_{name};
    Array<Object<StatChangeStatusEffectItem>> StatusEffects = [StatChangeStatusEffectItem_0];
    float Duration = {duration}f;
}}
'''


def generate_upgrade_bonus(name, upgrade_type="NoGravityOnFocus", amount=1.0):
    """Generate OC_Bonus KMS template."""
    guid = generate_guid()
    return f'''[Import("/Script/FSD")]
public BoltActionRifleUpgrade Default__BoltActionRifleUpgrade;

[Import("/Script/FSD")]
public class BoltActionRifleUpgrade {{}}

[Import("/Script/FSD")]
public class StatusEffect {{}}

[Import("/Script/Engine")]
public class BlueprintGeneratedClass {{}}


[Import("/Game/WeaponsNTools/BoltActionRifle/STE_{name}")]
public class STE_{name}_C {{}}


object OC_Bonus_{name} : BoltActionRifleUpgrade {{
    Enum<EBoltActionRifleUpgrades> UpgradeType = `EBoltActionRifleUpgrades::{upgrade_type}`;
    float Amount = {amount}f;

    // Additional mod-specific properties
    bool GrantInvulnerability = true;
    float InvulnerabilityDuration = 1.5f;
    Object<BlueprintGeneratedClass> InvulnerabilityStatusEffect = STE_{name}_C;

    Struct<Guid> SaveGameID = {{ SaveGameID: "{{{guid}}}" }};
}}
'''


def generate_overclock(name, weapon, category="SCAT_OC_Clean"):
    """Generate OC_*_C KMS template."""
    guid = generate_guid()
    schematic_id = hash(name) % 10000000000

    category_map = {
        "clean": "SCAT_OC_Clean",
        "balanced": "SCAT_OC_Balanced",
        "unstable": "SCAT_OC_Unstable"
    }
    category_val = category_map.get(category.lower(), category)

    return f'''[Import("/Script/FSD")]
public class CombinedUpgrade {{}}

[Import("/Script/FSD")]
public class OverclockUpgrade {{}}

[Import("/Script/FSD")]
public OverclockUpgrade Default__OverclockUpgrade;

[Import("/Script/FSD")]
public class ItemUpgradeCategory {{}}

[Import("/Script/FSD")]
public class SchematicCategory {{}}


[Import("/Game/WeaponsNTools/_UpgradeCategories/UPC_{weapon}")]
public ItemUpgradeCategory UPC_{weapon};

[Import("/Game/GameElements/Schematics/Categories/{category_val}")]
public SchematicCategory {category_val};

[Import("/Game/WeaponsNTools/BoltActionRifle/Overclocks/OC_BonusesAndPenalties/OC_Bonus_{name}")]
public BoltActionRifleUpgrade OC_Bonus_{name};


object OC_{weapon}_{name}_C : OverclockUpgrade {{
    Object<SchematicCategory> SchematicCategory = {category_val};
    Array<SoftObject> CombinedUpgrades = ["/Game/WeaponsNTools/BoltActionRifle/Overclocks/OC_BonusesAndPenalties/OC_Bonus_{name}.OC_Bonus_{name}"];
    Text Name = "{name}";
    Text Description = "Custom overclock with special effects";
    Object<ItemUpgradeCategory> Category = UPC_{weapon};
    Array<Struct<ItemUpgradeStatText>> StatTexts = [
        {{ StatText: "Special Effect", IsAdventageous: true }}
    ];
    Struct<Guid> SaveGameID = {{ SaveGameID: "{{{guid}}}" }};
    Object<Class> NativeClass = CombinedUpgrade;
}}
'''


def generate_overclock_bank(name, weapon):
    """Generate OSB_* KMS template with new overclock entry."""
    schematic_id = abs(hash(name)) % 10000000000

    return f'''// This is a simplified template. You need to merge this with the original OSB_{weapon}.kms
// Add this import and entry to the existing OSB file:

[Import("/Game/WeaponsNTools/BoltActionRifle/Overclocks/OC_{weapon}_{name}_C")]
public OverclockUpgrade OC_{weapon}_{name}_C;

// Add to object OSB_{weapon} Overclocks map:
// OC_{weapon}_{name}_C: SCE_{weapon}_{name}_{schematic_id}

// Add new schematic object:
object SCE_{weapon}_{name}_{schematic_id} : Schematic {{
    Object<SchematicCategory> Category = SCAT_OC_Clean;
    Object<SchematicPricingTier> PricingTier = Overclocks;
    Object<SchematicRarity> Rarity = SCR_Overclocks;
    Object<PlayerCharacterID> UsedByCharacter = ScoutID;
    Object<OverclockShematicItem> Item = OverclockShematicItem_{name};
    Object CraftingCost = {{ RES_Credits: 8500, RES_VEIN_Croppa: 150, RES_EMBED_Jadiz: 100, RES_CARVED_Bismor: 120 }};
    bool CostIsLocked = true;
    Struct<Guid> SaveGameID = {{ SaveGameID: "{{{generate_guid()}}}" }};
}}

object OverclockShematicItem_{name} : OverclockShematicItem {{
    Object<ItemID> OwningItem = ID_{weapon};
    Object<OverclockUpgrade> Overclock = OC_{weapon}_{name}_C;
}}
'''


def main():
    parser = argparse.ArgumentParser(description="Generate KMS templates for DRG weapon mods")
    parser.add_argument("--name", required=True, help="Mod name (e.g., HoverclockInvulnerable)")
    parser.add_argument("--weapon", default="M1000", help="Weapon code (e.g., M1000, PGL, C4)")
    parser.add_argument("--type", default="custom",
                       choices=["hover-invuln", "damage-boost", "status-effect", "custom"],
                       help="Type of mod")
    parser.add_argument("--duration", type=float, default=1.5, help="Effect duration in seconds")
    parser.add_argument("--amount", type=float, default=1.0, help="Effect amount/multiplier")
    parser.add_argument("--category", default="clean", choices=["clean", "balanced", "unstable"],
                       help="Overclock category")
    parser.add_argument("--outdir", default=".", help="Output directory")

    args = parser.parse_args()

    # Create output directory
    os.makedirs(args.outdir, exist_ok=True)

    # Determine upgrade type based on mod type
    upgrade_type_map = {
        "hover-invuln": "NoGravityOnFocus",
        "damage-boost": "FocusDamageMultiplier",
        "status-effect": "FocusSTE",
        "custom": "NoGravityOnFocus"
    }
    upgrade_type = upgrade_type_map[args.type]

    # Generate files
    files_generated = []

    # 1. Status Effect
    stat_change = 9999.0 if args.type == "hover-invuln" else args.amount
    ste_content = generate_status_effect(args.name, args.duration, stat_change)
    ste_path = os.path.join(args.outdir, f"STE_{args.name}.kms")
    with open(ste_path, 'w') as f:
        f.write(ste_content)
    files_generated.append(ste_path)

    # 2. Upgrade Bonus
    bonus_content = generate_upgrade_bonus(args.name, upgrade_type, args.amount)
    bonus_path = os.path.join(args.outdir, f"OC_Bonus_{args.name}.kms")
    with open(bonus_path, 'w') as f:
        f.write(bonus_content)
    files_generated.append(bonus_path)

    # 3. Overclock Package
    oc_content = generate_overclock(args.name, args.weapon, args.category)
    oc_path = os.path.join(args.outdir, f"OC_{args.weapon}_{args.name}_C.kms")
    with open(oc_path, 'w') as f:
        f.write(oc_content)
    files_generated.append(oc_path)

    # 4. Overclock Bank additions (reference file)
    osb_content = generate_overclock_bank(args.name, args.weapon)
    osb_path = os.path.join(args.outdir, f"OSB_{args.weapon}_Additions.kms")
    with open(osb_path, 'w') as f:
        f.write(osb_content)
    files_generated.append(osb_path)

    # Print summary
    print(f"✅ Generated {len(files_generated)} KMS template files:")
    for f in files_generated:
        print(f"   - {f}")
    print(f"\n📝 Next steps:")
    print(f"   1. Edit the .kms files to customize effects and stats")
    print(f"   2. Merge OSB_{args.weapon}_Additions.kms into the original OSB_{args.weapon}.kms")
    print(f"   3. Run compile_mod.sh to build the mod")


if __name__ == "__main__":
    main()
