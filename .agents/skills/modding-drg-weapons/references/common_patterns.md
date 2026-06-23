# Common Mod Patterns

This document describes reusable patterns for Deep Rock Galactic weapon mods.

## Hover Invulnerability Pattern

**Concept:** Player becomes invincible while hovering in focus mode.

**Components:**
1. **Status Effect** - Provides damage resistance
2. **Upgrade Bonus** - Triggers on `NoGravityOnFocus`
3. **Overclock Package** - Combines components

**Key Values:**
```
StatChange = 9999.0  // Effectively invincible
Duration = 1.5f      // Match NoGravityOnFocusDuration
PawnAffliction = PAF_IronWill  // Reuse existing visual effects
```

## Focus Damage Boost Pattern

**Concept:** Charged shots deal increased damage.

**Upgrade Type:** `FocusDamageMultiplier`

**Key Values:**
```
Amount = 2.0f  // 200% damage on fully charged shots
// or
Amount = 1.25f // 125% damage (25% boost)
```

## Status Effect on Hit Pattern

**Concept:** Enemies receive DOT or debuff when hit by focused shots.

**Components:**
1. **Status Effect** - DOT (Damage Over Time) or debuff
2. **Weapon Property** - `FocusedHitSTE` references the status effect

**Example (Electrocution):**
```kms
// In WPN_M1000_C
Object<BlueprintGeneratedClass> FocusedHitSTE = STE_M1000_Electrocution_C;
bool RequireWeakspotForFocusedHitSTE = false;  // Apply on any hit
```

## Ammo Efficiency Pattern

**Concept:** Shots consume less ammo.

**Upgrade Type:** `AmmoCostMultiplier` or custom implementation

## Reload Speed Pattern

**Concept:** Faster reload after certain conditions (kill, weakspot hit, etc.)

**Implementation:** Usually handled in weapon blueprint event graph.

## Crafting Cost Guidelines

| Mod Type | Credits | Minerals | Approximate Total |
|----------|---------|----------|-------------------|
| Clean | 7000-8000 | Low-Medium | ~8000-10000 |
| Balanced | 8000-9000 | Medium | ~9000-11000 |
| Unstable | 8500-9500 | High | ~10000-12000 |

**Resource Types:**
- `RES_Credits` - Always required
- `RES_VEIN_Croppa` - Mined mineral
- `RES_EMBED_Jadiz` - Embedded gem
- `RES_CARVED_Umanite` - Carved resource
- `RES_CARVED_Magnite` - Carved resource
- `RES_CARVED_Bismor` - Carved resource
- `RES_EMBED_Enor` - Embedded pearl

## SaveGameID Best Practices

Each mod component needs a unique `SaveGameID` in GUID format:

```kms
Struct<Guid> SaveGameID = { SaveGameID: "{12345678-1234-1234-1234-123456789012}" };
```

**Generating GUIDs:**
- Use `uuidgen` command on macOS/Linux
- Use online GUID generators
- Use the included `generate_mod.py` script (auto-generates)

**Important:** Never reuse GUIDs from other mods or game files!

## Schematic IDs

Schematic objects in OSB need unique numeric IDs:

```kms
object SCE_M1000_MyMod_1234567890 : Schematic { ... }
```

Use any 10-digit number, or hash the mod name for consistency.
