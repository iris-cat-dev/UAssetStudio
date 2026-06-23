# EBoltActionRifleUpgrades Reference

Complete list of upgrade types for BoltActionRifle (M1000 classic).

## Movement & Focus

| Upgrade Type | Description | Typical Values |
|--------------|-------------|----------------|
| `NoGravityOnFocus` | No gravity while in focus mode | Amount: 1.0 (boolean) |
| `FocusSpeedMultiplier` | How fast weapon charges/focuses | Amount: 1.4 (40% faster) |
| `MovementSpeedWhileFocus` | Movement speed penalty reduction | Amount: 0.5 (less penalty) |
| `CantMoveWhileFocus` | Immobilizes player during focus | Amount: 1.0 (boolean) |

## Damage

| Upgrade Type | Description | Typical Values |
|--------------|-------------|----------------|
| `FocusDamageMultiplier` | Damage multiplier on charged shots | Amount: 1.5 (50% more) |
| `MinFocusPercentageForDamageBonus` | Minimum charge for damage bonus | Amount: 0.5 (50% charge) |
| `DamageMultiplier` | General damage multiplier | Amount: 1.2 (20% more) |
| `WeakpointDamageMultiplier` | Bonus damage on weakpoint hits | Amount: 1.25 (25% more) |

## Ammo & Reload

| Upgrade Type | Description | Typical Values |
|--------------|-------------|----------------|
| `ClipSize` | Magazine capacity | Amount: +2 rounds |
| `MaxAmmo` | Total ammo reserve | Amount: +20 rounds |
| `ReloadSpeed` | Reload animation speed | Amount: 0.8 (20% faster) |
| `AmmoCostMultiplier` | Ammo consumed per shot | Amount: 0.5 (half cost) |

## Accuracy & Recoil

| Upgrade Type | Description | Typical Values |
|--------------|-------------|----------------|
| `MaxSpread` | Maximum weapon spread | Amount: 0.8 (20% tighter) |
| `SpreadPerShot` | Spread increase per shot | Amount: 0.7 (30% less) |
| `RecoilMultiplier` | Vertical recoil amount | Amount: 0.6 (40% less) |
| `ZoomSpread` | Spread while zoomed/focused | Amount: 0.5 (50% tighter) |

## Special Effects

| Upgrade Type | Description | Typical Values |
|--------------|-------------|----------------|
| `FocusSTE` | Apply status effect on focused hit | References OC_Bonus_* |
| `CanUseLaserPointer` | Enables laser pointer while equipped | Amount: 1.0 (boolean) |
| `AimedShotCost` | Ammo cost for charged shots | Amount: 2.0 (double cost) |

## Armor & Penetration

| Upgrade Type | Description | Typical Values |
|--------------|-------------|----------------|
| `ArmorBreaking` | Damage to enemy armor | Amount: 1.5 (50% more) |
| `BlowThrough` | Penetrates multiple enemies | Amount: 1.0 (boolean) |
| `Stagger` | Stun/knockback chance | Amount: 1.0 (boolean) |

## Usage in KMS

```kms
object OC_Bonus_MyMod : BoltActionRifleUpgrade {
    Enum<EBoltActionRifleUpgrades> UpgradeType = `EBoltActionRifleUpgrades::FocusDamageMultiplier`;
    float Amount = 1.5f;  // 50% increase
    Struct<Guid> SaveGameID = { SaveGameID: "{unique-guid}" };
}
```

## Combining Multiple Upgrades

Overclocks can reference multiple upgrade bonuses:

```kms
object OC_M1000_MyMod_C : OverclockUpgrade {
    Array<SoftObject> CombinedUpgrades = [
        "/Game/.../OC_Bonus_Damage.OC_Bonus_Damage",
        "/Game/.../OC_Bonus_Recoil.OC_Bonus_Recoil",
        "/Game/.../OC_Penalty_Ammo.OC_Penalty_Ammo"
    ];
}
```

Note: Penalties (negative effects) are also upgrade bonuses with negative Amount values or specific penalty types.
