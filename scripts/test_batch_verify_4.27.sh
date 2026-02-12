#!/usr/bin/env bash

set -euo pipefail

# ==============================================================================
# UE4.27 Round-trip Batch Verification Script (Concurrent Version)
# Purpose: Batch test KMS readability refactoring round-trip with parallel processing
# Usage: ./script/test_batch_verify_4.27.sh [max_parallel_jobs]
# Default: 4 parallel jobs, adjust based on your CPU cores
# ==============================================================================

# Configuration
ASSET_DIR="/Users/bytedance/Project/UAssetStudio/scripts/original_assets"
USMAP_PATH=""  # Optional: set to a valid UE4.27 .usmap if needed
REPO_ROOT="/Users/bytedance/Project/UAssetStudio"
CLI_PROJ="$REPO_ROOT/UAssetStudio.Cli/UAssetStudio.Cli.csproj"
OUTDIR="$REPO_ROOT/script/output"

# UE Version
UE_VERSION="VER_UE4_27"

# Standalone compilation mode (generates .kms.meta for compilation without original asset)
USE_META="--meta"

# Parallel processing settings
MAX_JOBS="${1:-4}"
JOB_WAIT_TIMEOUT=300  # Max seconds to wait for a single job

# Counters for summary (use files for thread safety)
TOTAL_FILE="$OUTDIR/.total_count"
SUCCESS_FILE="$OUTDIR/.success_count"
FAILED_FILE="$OUTDIR/.failed_count"
FAILED_LIST_FILE="$OUTDIR/.failed_list"

# Colors for output (disable if not tty)
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
CYAN='\033[0;36m'
NC='\033[0m' # No Color
if [[ ! -t 1 ]]; then
  RED='' GREEN='' YELLOW='' CYAN='' NC=''
fi

echo "========================================"
echo "UE4.27 Round-trip Batch Verification"
echo "Concurrent Mode (Max $MAX_JOBS parallel)"
echo "========================================"
echo "[Info] Asset directory: $ASSET_DIR"
echo "[Info] Output directory: $OUTDIR"
echo "[Info] Parallel jobs: $MAX_JOBS"
if [[ -n "$USE_META" ]]; then
  echo "[Info] Standalone compilation mode enabled (--meta)"
fi
echo ""

# Check if asset directory exists
if [[ ! -d "$ASSET_DIR" ]]; then
  echo -e "${RED}[Error] Asset directory not found: $ASSET_DIR${NC}" >&2
  exit 1
fi

# Create output directory
mkdir -p "$OUTDIR"

# Cleanup stale lock from previous interrupted runs
rm -rf "$OUTDIR/.output_lock"

# Initialize counters
echo 0 > "$TOTAL_FILE"
echo 0 > "$SUCCESS_FILE"
echo 0 > "$FAILED_FILE"
> "$FAILED_LIST_FILE"

# Trap to kill all background jobs on ctrl-c / termination
cleanup() {
  echo ""
  echo "[Info] Interrupted, killing background jobs..."
  kill 0 2>/dev/null || true
  rm -rf "$OUTDIR/.output_lock"
  rm -f "$OUTDIR/.job_fifo"
  exit 130
}
trap cleanup SIGINT SIGTERM

# Build CLI (--no-build will be used later to avoid concurrent MSBuild lock contention)
echo "[Info] Building CLI..."
dotnet build "$CLI_PROJ" -v minimal
echo ""

# Find all .uasset files recursively
echo "[Info] Scanning for .uasset files..."
ASSET_FILES=()
while IFS= read -r file; do
  ASSET_FILES+=("$file")
done < <(find "$ASSET_DIR" -type f -name "*.uasset" 2>/dev/null | sort)

if [[ ${#ASSET_FILES[@]} -eq 0 ]]; then
  echo -e "${YELLOW}[Warn] No .uasset files found in $ASSET_DIR${NC}" >&2
  exit 0
fi

echo "[Info] Found ${#ASSET_FILES[@]} asset(s) to verify"
echo "[Info] Starting parallel processing..."
echo ""

# Lock directory for synchronized output (mkdir is atomic on all platforms)
OUTPUT_LOCK="$OUTDIR/.output_lock"

# Acquire lock function (cross-platform)
acquire_lock() {
  while ! mkdir "$OUTPUT_LOCK" 2>/dev/null; do
    sleep 0.01
  done
}

# Release lock function
release_lock() {
  rmdir "$OUTPUT_LOCK" 2>/dev/null || true
}

# Function to process a single asset
process_asset() {
  local ASSET_PATH="$1"
  local INDEX="$2"
  local TOTAL="$3"
  local FILENAME
  FILENAME=$(basename "$ASSET_PATH")
  local REL_PATH="${ASSET_PATH#$ASSET_DIR/}"
  local LOG_FILE="$OUTDIR/${FILENAME%.*}.log"

  # Run verify command (--no-build to avoid concurrent MSBuild lock contention)
  local RESULT=0
  if [[ -n "$USMAP_PATH" ]] && [[ -f "$USMAP_PATH" ]]; then
    dotnet run --no-build --project "$CLI_PROJ" -- verify "$ASSET_PATH" \
      --ue-version "$UE_VERSION" \
      --mappings "$USMAP_PATH" \
      --outdir "$OUTDIR" \
      $USE_META \
      > "$LOG_FILE" 2>&1 || RESULT=$?
  else
    dotnet run --no-build --project "$CLI_PROJ" -- verify "$ASSET_PATH" \
      --ue-version "$UE_VERSION" \
      --outdir "$OUTDIR" \
      $USE_META \
      > "$LOG_FILE" 2>&1 || RESULT=$?
  fi

  # Atomic output with mkdir lock
  acquire_lock

  printf "[%3d/%3d] Processing: %-60s" "$INDEX" "$TOTAL" "$REL_PATH"

  # Check result: try UTF-8 first (macOS/Linux), then UTF-16LE (Windows)
  # Use "Verified" without colon to match both "Verified:" and "Verified (structural):"
  local VERIFIED=false
  if grep -q "Verified" "$LOG_FILE" 2>/dev/null; then
    VERIFIED=true
  elif iconv -f UTF-16LE -t UTF-8 "$LOG_FILE" 2>/dev/null | grep -q "Verified"; then
    VERIFIED=true
  fi

  if [[ "$VERIFIED" == "true" ]]; then
    printf "${GREEN}[OK]${NC}\n"
    # Atomic increment success counter
    local current
    current=$(cat "$SUCCESS_FILE")
    echo $((current + 1)) > "$SUCCESS_FILE"
  else
    printf "${RED}[FAIL]${NC} (exit: $RESULT)\n"
    # Atomic increment failed counter
    local current
    current=$(cat "$FAILED_FILE")
    echo $((current + 1)) > "$FAILED_FILE"
    echo "$REL_PATH" >> "$FAILED_LIST_FILE"
  fi

  # Increment total counter (inside lock for thread safety)
  local current_total
  current_total=$(cat "$TOTAL_FILE")
  echo $((current_total + 1)) > "$TOTAL_FILE"

  release_lock
}

# FIFO-based semaphore for parallel job control
# Use a persistent file descriptor to avoid FIFO open/close race conditions
JOB_FIFO="$OUTDIR/.job_fifo"
rm -f "$JOB_FIFO"
mkfifo "$JOB_FIFO"
exec 3<>"$JOB_FIFO"  # Open FIFO read-write on fd 3 (keeps it alive)
rm -f "$JOB_FIFO"     # Unlink file; fd 3 keeps working

# Pre-fill with MAX_JOBS tokens
for ((i = 0; i < MAX_JOBS; i++)); do
  echo "token" >&3
done

CURRENT_INDEX=0
TOTAL_COUNT=${#ASSET_FILES[@]}

for ASSET_PATH in "${ASSET_FILES[@]}"; do
  CURRENT_INDEX=$((CURRENT_INDEX + 1))

  # Block until a token is available (a previous job completed)
  read -r <&3

  # Run in background; return token to fd 3 when done
  (
    process_asset "$ASSET_PATH" "$CURRENT_INDEX" "$TOTAL_COUNT"
    echo "token" >&3
  ) &
done

# Wait for all remaining jobs
echo ""
echo "[Info] Waiting for all jobs to complete..."
wait

# Close fd 3
exec 3>&-

# Read final counters
TOTAL=$(cat "$TOTAL_FILE")
SUCCESS=$(cat "$SUCCESS_FILE")
FAILED=$(cat "$FAILED_FILE")

# Generate summary
echo ""
echo "========================================"
echo "Summary"
echo "========================================"
echo "Total:   $TOTAL"
echo -e "Success: ${GREEN}$SUCCESS${NC}"
echo -e "Failed:  ${RED}$FAILED${NC}"

if [[ $FAILED -gt 0 ]]; then
  echo ""
  echo "Failed assets:"
  while read -r path; do
    echo "  - $path"
  done < "$FAILED_LIST_FILE"
fi

# Cleanup counter files
rm -f "$TOTAL_FILE" "$SUCCESS_FILE" "$FAILED_FILE" "$FAILED_LIST_FILE"
rm -rf "$OUTPUT_LOCK"

if [[ $FAILED -eq 0 ]]; then
  echo ""
  echo -e "${GREEN}All verifications passed!${NC}"
  exit 0
else
  exit 1
fi
