#!/usr/bin/env bash

set -euo pipefail

# Paths (adjust if your environment differs)
REPO_ROOT="/Users/bytedance/Project/UAssetStudio"
CLI_PROJ="$REPO_ROOT/UAssetStudio.Cli/UAssetStudio.Cli.csproj"
OUTDIR="$REPO_ROOT/script/output"

# Explicit KMS/script and original asset
KMS_FILE="/Users/bytedance/Project/UAssetStudio/script/BP_PetComponent.kms"
ASSET_PATH="/Users/bytedance/Project/UAssetStudio/script/BP_PetComponent.uasset"

# Optional: set to a valid UE4.27 .usmap if needed
USMAP_PATH=""  # Optional: set to a valid UE4.27 .usmap if needed

# Optional: set metadata file path for standalone compilation (.kms.meta)
# META_PATH="$KMS_FILE.meta"  # Optional: defaults to KMS_FILE.meta if not set

# Optional: set explicit output file path (overrides OUTDIR)
# OUTPUT_FILE="$OUTDIR/BP_PetComponent_Modified.uasset"

# UE Version
UE_VERSION="VER_UE4_27"

echo "[Info] Output dir: $OUTDIR"
mkdir -p "$OUTDIR"

# Basic checks
if [[ ! -f "$ASSET_PATH" ]]; then
  echo "[Error] Asset not found: $ASSET_PATH" >&2
  exit 1
fi
if [[ ! -f "$KMS_FILE" ]]; then
  echo "[Error] KMS script not found: $KMS_FILE" >&2
  exit 1
fi

echo "[Info] Building CLI..."
dotnet build "$CLI_PROJ" -v minimal

# Determine output file path
if [[ -n "${OUTPUT_FILE:-}" ]]; then
  COMPILED_FILE="$OUTPUT_FILE"
  OUT_OPT="--out"
  echo "[Info] Using explicit output file: $COMPILED_FILE"
else
  COMPILED_FILE="$OUTDIR/$(basename "$ASSET_PATH" .uasset).new.uasset"
  OUT_OPT="--outdir"
fi

echo "[Info] Running compile (UE4.27)..."

# Build meta option if META_PATH is set
META_OPT=""
if [[ -n "${META_PATH:-}" ]] && [[ -f "$META_PATH" ]]; then
  echo "[Info] Using metadata: $META_PATH"
  META_OPT="--meta $META_PATH"
elif [[ -n "${META_PATH:-}" ]]; then
  echo "[Warn] Metadata file not found: $META_PATH (continue without explicit meta)"
fi

# Run compile with optional mappings
if [[ -n "$USMAP_PATH" ]] && [[ -f "$USMAP_PATH" ]]; then
  echo "[Info] Using mappings: $USMAP_PATH"
  if [[ -n "${OUTPUT_FILE:-}" ]]; then
    dotnet run --project "$CLI_PROJ" -- compile "$KMS_FILE" --asset "$ASSET_PATH" --ue-version "$UE_VERSION" --mappings "$USMAP_PATH" $META_OPT "$OUT_OPT" "$COMPILED_FILE"
  else
    dotnet run --project "$CLI_PROJ" -- compile "$KMS_FILE" --asset "$ASSET_PATH" --ue-version "$UE_VERSION" --mappings "$USMAP_PATH" $META_OPT "$OUT_OPT" "$OUTDIR"
  fi
else
  [[ -n "$USMAP_PATH" ]] && echo "[Warn] USMAP not found: $USMAP_PATH (continue without mappings)"
  if [[ -n "${OUTPUT_FILE:-}" ]]; then
    dotnet run --project "$CLI_PROJ" -- compile "$KMS_FILE" --asset "$ASSET_PATH" --ue-version "$UE_VERSION" $META_OPT "$OUT_OPT" "$COMPILED_FILE"
  else
    dotnet run --project "$CLI_PROJ" -- compile "$KMS_FILE" --asset "$ASSET_PATH" --ue-version "$UE_VERSION" $META_OPT "$OUT_OPT" "$OUTDIR"
  fi
fi

if [[ -f "$COMPILED_FILE" ]]; then
  echo "[Success] Compiled asset generated: $COMPILED_FILE"
else
  echo "[Warn] Compiled asset not found: $COMPILED_FILE" >&2
  exit 3
fi

echo "[Done] Compile test complete."
