#!/usr/bin/env bash

set -euo pipefail

# Paths
REPO_ROOT="/Users/bytedance/Project/UAssetStudio"
CLI_PROJ="$REPO_ROOT/UAssetStudio.Cli/UAssetStudio.Cli.csproj"
MOD_DIR="$REPO_ROOT/script/Mod_HoverclockInvulnerable"
SOURCE_DIR="$MOD_DIR/original_assets/BoltActionRifle"
OUTDIR="$MOD_DIR/output"

# UE Version
UE_VERSION="VER_UE4_27"

echo "========================================"
echo "Hoverclock Invulnerable Mod Compiler"
echo "========================================"
echo ""

# Create output directory with subdirectories
mkdir -p "$OUTDIR/Overclocks/OC_BonusesAndPenalties"
mkdir -p "$OUTDIR/Overclocks"

echo "[Info] Building CLI..."
dotnet build "$CLI_PROJ" -v minimal
echo ""

# Define original assets
ORIGINAL_OC_BONUS="$SOURCE_DIR/Overclocks/OC_BonusesAndPenalties/OC_Bonus_Hoverclock.uasset"
ORIGINAL_OC="$SOURCE_DIR/Overclocks/OC_M1000_Hoverclock_C.uasset"
ORIGINAL_STE="$SOURCE_DIR/STE_M1000_Electrocution.uasset"
ORIGINAL_OSB="$SOURCE_DIR/Overclocks/OSB_M1000.uasset"
ORIGINAL_WPN="$SOURCE_DIR/WPN_M1000.uasset"

echo "[Info] Checking for original game assets..."
echo "  - OC_Bonus_Hoverclock: $([ -f "$ORIGINAL_OC_BONUS" ] && echo 'FOUND' || echo 'NOT FOUND')"
echo "  - OC_M1000_Hoverclock_C: $([ -f "$ORIGINAL_OC" ] && echo 'FOUND' || echo 'NOT FOUND')"
echo "  - STE_M1000_Electrocution: $([ -f "$ORIGINAL_STE" ] && echo 'FOUND' || echo 'NOT FOUND')"
echo "  - OSB_M1000: $([ -f "$ORIGINAL_OSB" ] && echo 'FOUND' || echo 'NOT FOUND')"
echo "  - WPN_M1000: $([ -f "$ORIGINAL_WPN" ] && echo 'FOUND' || echo 'NOT FOUND')"
echo ""

# Function to compile a KMS file
compile_asset() {
    local kms_file="$1"
    local original_asset="$2"
    local output_path="$3"
    local output_name="$4"

    if [[ ! -f "$kms_file" ]]; then
        echo "[Error] KMS file not found: $kms_file"
        return 1
    fi

    if [[ ! -f "$original_asset" ]]; then
        echo "[Error] Original asset not found: $original_asset"
        return 1
    fi

    echo "[Info] Compiling: $(basename "$kms_file")"
    echo "       Using base: $(basename "$original_asset")"

    # Compile to temp directory first
    local temp_outdir="$OUTDIR/temp_$$"
    mkdir -p "$temp_outdir"

    dotnet run --project "$CLI_PROJ" -- compile "$kms_file" \
        --asset "$original_asset" \
        --ue-version "$UE_VERSION" \
        --outdir "$temp_outdir"

    # Compiler uses KMS filename (without extension) for output
    local kms_basename=$(basename "$kms_file" .kms)
    local compiled_uasset="$temp_outdir/${kms_basename}.new.uasset"
    local compiled_uexp="$temp_outdir/${kms_basename}.new.uexp"
    local final_uasset="$output_path/${output_name}.uasset"
    local final_uexp="$output_path/${output_name}.uexp"

    if [[ -f "$compiled_uasset" ]]; then
        mv "$compiled_uasset" "$final_uasset"
        if [[ -f "$compiled_uexp" ]]; then
            mv "$compiled_uexp" "$final_uexp"
        fi
        rm -rf "$temp_outdir"
        echo "[Success] Compiled: $final_uasset"
        ls -lh "$final_uasset"
        if [[ -f "$final_uexp" ]]; then
            ls -lh "$final_uexp"
        fi
        return 0
    else
        rm -rf "$temp_outdir"
        echo "[Error] Compilation failed!"
        return 1
    fi
}

# Track success
SUCCESS_COUNT=0
TOTAL_COUNT=0

# Compile Status Effect
if [[ -f "$ORIGINAL_STE" ]]; then
    echo "[Step 1/4] Compiling Status Effect..."
    ((TOTAL_COUNT++))
    if compile_asset "$MOD_DIR/STE_HoverclockInvulnerable.kms" "$ORIGINAL_STE" "$OUTDIR" "STE_HoverclockInvulnerable"; then
        ((SUCCESS_COUNT++))
    fi
    echo ""
fi

# Compile Upgrade Bonus
if [[ -f "$ORIGINAL_OC_BONUS" ]]; then
    echo "[Step 2/4] Compiling Upgrade Bonus..."
    ((TOTAL_COUNT++))
    if compile_asset "$MOD_DIR/OC_Bonus_Hoverclock_Invulnerable.kms" "$ORIGINAL_OC_BONUS" "$OUTDIR/Overclocks/OC_BonusesAndPenalties" "OC_Bonus_Hoverclock_Invulnerable"; then
        ((SUCCESS_COUNT++))
    fi
    echo ""
fi

# Compile Overclock
if [[ -f "$ORIGINAL_OC" ]]; then
    echo "[Step 3/4] Compiling Overclock..."
    ((TOTAL_COUNT++))
    if compile_asset "$MOD_DIR/OC_M1000_Hoverclock_Invulnerable_C.kms" "$ORIGINAL_OC" "$OUTDIR/Overclocks" "OC_M1000_Hoverclock_Invulnerable_C"; then
        ((SUCCESS_COUNT++))
    fi
    echo ""
fi

# Compile Overclock Bank
if [[ -f "$ORIGINAL_OSB" ]]; then
    echo "[Step 4/4] Compiling Overclock Bank (OSB_M1000)..."
    ((TOTAL_COUNT++))
    if compile_asset "$MOD_DIR/OSB_M1000_Modified.kms" "$ORIGINAL_OSB" "$OUTDIR/Overclocks" "OSB_M1000"; then
        ((SUCCESS_COUNT++))
    fi
    echo ""
fi

echo "========================================"
echo "Compilation Complete"
echo "========================================"
echo ""
echo "Results: $SUCCESS_COUNT / $TOTAL_COUNT files compiled successfully"
echo ""
echo "Output directory structure:"
echo "$OUTDIR"
tree -L 3 "$OUTDIR" 2>/dev/null || find "$OUTDIR" -type f -name "*.uasset" -o -name "*.uexp" | sort
echo ""

if [[ $SUCCESS_COUNT -eq $TOTAL_COUNT ]]; then
    echo "[SUCCESS] All mod files compiled successfully!"
    echo ""
    echo "Installation:"
    echo "  Copy the output/BoltActionRifle folder to your game directory"
    echo "  and replace the original files (backup recommended)"
    echo ""
    echo "Files to install:"
    find "$OUTDIR" -type f \( -name "*.uasset" -o -name "*.uexp" \) | while read f; do
        echo "    - $(basename $(dirname $f))/$(basename $f)"
    done
    exit 0
else
    echo "[WARNING] Some files failed to compile. Check errors above."
    exit 1
fi
