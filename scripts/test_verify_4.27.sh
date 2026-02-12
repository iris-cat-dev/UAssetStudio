#!/usr/bin/env bash

set -euo pipefail

# Paths (adjust if your environment differs)
ASSET_PATH="/Users/bytedance/Project/UAssetStudio/scripts/original_assets/BoltActionRifle/Overclocks/OSB_M1000.uasset"
USMAP_PATH=""  # Optional: set to a valid UE4.27 .usmap if needed
REPO_ROOT="/Users/bytedance/Project/UAssetStudio"
CLI_PROJ="$REPO_ROOT/UAssetStudio.Cli/UAssetStudio.Cli.csproj"
OUTDIR="$REPO_ROOT/script/output"
USE_META="--meta"

# UE Version
UE_VERSION="VER_UE4_27"

echo "[Info] Output dir: $OUTDIR"
mkdir -p "$OUTDIR"

# Basic checks
if [[ ! -f "$ASSET_PATH" ]]; then
  echo "[Error] Asset not found: $ASSET_PATH" >&2
  exit 1
fi

echo "[Info] Building CLI..."
dotnet build "$CLI_PROJ" -v minimal

FILENAME=$(basename "$ASSET_PATH")
KMS_FILE="$OUTDIR/${FILENAME%.*}.kms"
NEW_FILE="$OUTDIR/${FILENAME%.*}.new.uasset"

echo "[Info] Running verify (UE4.27): decompile -> compile -> link -> write"
if [[ -n "$USE_META" ]]; then
  echo "[Info] Standalone compilation mode enabled (--meta)"
fi
if [[ -n "$USMAP_PATH" ]] && [[ -f "$USMAP_PATH" ]]; then
  echo "[Info] Using mappings: $USMAP_PATH"
  dotnet run --project "$CLI_PROJ" -- verify "$ASSET_PATH" --ue-version "$UE_VERSION" --mappings "$USMAP_PATH" --outdir "$OUTDIR" $USE_META
else
  [[ -n "$USMAP_PATH" ]] && echo "[Warn] USMAP not found: $USMAP_PATH (continue without mappings)"
  dotnet run --project "$CLI_PROJ" -- verify "$ASSET_PATH" --ue-version "$UE_VERSION" --outdir "$OUTDIR" $USE_META
fi

if [[ -f "$KMS_FILE" ]]; then
  echo "[Success] KMS generated: $KMS_FILE"
else
  echo "[Warn] KMS not found: $KMS_FILE" >&2
  exit 2
fi

if [[ -f "$NEW_FILE" ]]; then
  echo "[Success] New asset generated: $NEW_FILE"
else
  echo "[Warn] New asset not found: $NEW_FILE" >&2
  exit 3
fi

echo "[Done] Verify test complete."
