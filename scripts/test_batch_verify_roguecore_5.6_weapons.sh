#!/usr/bin/env bash

set -euo pipefail

# ==============================================================================
# RogueCore UE5.6 WeaponsNTools round-trip batch verification
# Purpose: stress-test decompile -> compile -> verify for all asset entry files
# ==============================================================================

REPO_ROOT="${REPO_ROOT:-/Users/bytedance/Project/UAssetStudio}"
ASSET_DIR="${ASSET_DIR:-/Users/bytedance/Project/RogueCore/Content/WeaponsNTools}"
CLI_PROJ="${CLI_PROJ:-$REPO_ROOT/UAssetStudio.Cli/UAssetStudio.Cli.csproj}"
USMAP_PATH="${USMAP_PATH:-$REPO_ROOT/maps/RogueCore-5.6.1-143055+main-7f7cc36f_by_Iris.usmap}"
UE_VERSION="${UE_VERSION:-VER_UE5_6}"
OUT_ROOT="${OUT_ROOT:-$REPO_ROOT/analysis/roguecore_weapons_verify}"
MAX_JOBS="${MAX_JOBS:-4}"
SMOKE_COUNT="${SMOKE_COUNT:-5}"
ASSET_LIST_FILE="${ASSET_LIST_FILE:-}"
USE_META="${USE_META:-0}"
RESTORE="${RESTORE:-0}"
SKIP_BUILD="${SKIP_BUILD:-0}"
SMOKE_ONLY="${SMOKE_ONLY:-0}"
ASSET_TIMEOUT="${ASSET_TIMEOUT:-300}"
LOCAL_USMAP="${LOCAL_USMAP:-1}"
EXCLUDE_SK_ASSETS="${EXCLUDE_SK_ASSETS:-1}"

SCRIPT_PATH="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/$(basename "${BASH_SOURCE[0]}")"

usage() {
  cat <<'EOF'
Usage:
  scripts/test_batch_verify_roguecore_5.6_weapons.sh [jobs]
  scripts/test_batch_verify_roguecore_5.6_weapons.sh --jobs 8 --smoke-count 5

Options:
  -j, --jobs <n>          Max parallel jobs for the full run (default: 4)
  --smoke-count <n>       Number of assets to smoke test before the full run (default: 5)
  --asset-dir <path>      Override RogueCore asset directory
  --asset-list <path>     Verify only assets listed in a file; relative paths are resolved under asset-dir
  --out-root <path>       Override output root directory
  --meta                  Run verify with --meta structural verification
  --restore               Allow dotnet build to restore packages before verifying
  --skip-build            Skip the initial dotnet build step
  --smoke-only            Run only the smoke gate and write summary.json from it
  --asset-timeout <sec>   Per-asset timeout in seconds; 0 disables it (default: 300)
  --shared-usmap          Read the mappings file directly instead of per-task temp copies
  --include-sk            Include SK_ prefixed skeletal mesh assets (excluded by default)
  -h, --help              Show this help

Environment overrides:
  REPO_ROOT, ASSET_DIR, CLI_PROJ, USMAP_PATH, UE_VERSION, OUT_ROOT,
  MAX_JOBS, SMOKE_COUNT, ASSET_LIST_FILE, USE_META, RESTORE, SKIP_BUILD,
  SMOKE_ONLY, ASSET_TIMEOUT, LOCAL_USMAP, EXCLUDE_SK_ASSETS
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    -j|--jobs)
      MAX_JOBS="$2"
      shift 2
      ;;
    --smoke-count)
      SMOKE_COUNT="$2"
      shift 2
      ;;
    --asset-dir)
      ASSET_DIR="$2"
      shift 2
      ;;
    --asset-list)
      ASSET_LIST_FILE="$2"
      shift 2
      ;;
    --out-root)
      OUT_ROOT="$2"
      shift 2
      ;;
    --meta)
      USE_META=1
      shift
      ;;
    --restore)
      RESTORE=1
      shift
      ;;
    --skip-build)
      SKIP_BUILD=1
      shift
      ;;
    --smoke-only)
      SMOKE_ONLY=1
      shift
      ;;
    --asset-timeout)
      ASSET_TIMEOUT="$2"
      shift 2
      ;;
    --shared-usmap)
      LOCAL_USMAP=0
      shift
      ;;
    --include-sk)
      EXCLUDE_SK_ASSETS=0
      shift
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    ''|*[!0-9]*)
      echo "[Error] Unknown argument: $1" >&2
      usage >&2
      exit 2
      ;;
    *)
      MAX_JOBS="$1"
      shift
      ;;
  esac
done

if [[ "$USE_META" == "1" ]]; then
  VERIFY_MODE="structural"
  VERIFY_EXTRA_ARGS=(--meta)
else
  VERIFY_MODE="default"
  VERIFY_EXTRA_ARGS=()
fi

RECORDS_FILE="$OUT_ROOT/records.tsv"
SMOKE_RECORDS_FILE="$OUT_ROOT/smoke_records.tsv"
SUMMARY_FILE="$OUT_ROOT/summary.json"
FAILED_LIST_FILE="$OUT_ROOT/failed_assets.txt"
RERUN_SCRIPT="$OUT_ROOT/rerun_failed.sh"
OUTPUT_LOCK="$OUT_ROOT/.output_lock"

RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
CYAN='\033[0;36m'
NC='\033[0m'
if [[ ! -t 1 ]]; then
  RED='' GREEN='' YELLOW='' CYAN='' NC=''
fi

fail_preflight() {
  local reason="$1"
  local uexp_count="${2:-0}"

  mkdir -p "$OUT_ROOT"
  : > "$FAILED_LIST_FILE"

  if command -v python3 >/dev/null 2>&1; then
    python3 - "$SUMMARY_FILE" "$ASSET_DIR" "$USMAP_PATH" "$UE_VERSION" "$VERIFY_MODE" "$reason" "$uexp_count" <<'PY'
import json
import sys

summary_path, asset_dir, usmap_path, ue_version, mode, reason, uexp_count = sys.argv[1:]
summary = {
    "status": "preflight_failed",
    "reason": reason,
    "asset_dir": asset_dir,
    "mappings": usmap_path,
    "ue_version": ue_version,
    "mode": mode,
    "total": 0,
    "success": 0,
    "failed": 0,
    "uexp_count": int(uexp_count),
    "failures": [],
}
with open(summary_path, "w", encoding="utf-8") as f:
    json.dump(summary, f, ensure_ascii=False, indent=2)
    f.write("\n")
PY
  else
    cat > "$SUMMARY_FILE" <<EOF
{
  "status": "preflight_failed",
  "reason": "$reason",
  "total": 0,
  "success": 0,
  "failed": 0,
  "uexp_count": $uexp_count
}
EOF
  fi

  echo -e "${RED}[Error] Preflight failed: $reason${NC}" >&2
  echo "[Info] Summary written to: $SUMMARY_FILE" >&2
  exit 1
}

is_excluded_asset() {
  local asset_path="$1"
  local filename
  filename=$(basename "$asset_path")
  [[ "$EXCLUDE_SK_ASSETS" == "1" && "$filename" == SK_* ]]
}

acquire_lock() {
  while ! mkdir "$OUTPUT_LOCK" 2>/dev/null; do
    sleep 0.01
  done
}

release_lock() {
  rmdir "$OUTPUT_LOCK" 2>/dev/null || true
}

cleanup() {
  local exit_code=$?
  rm -rf "$OUTPUT_LOCK"
  if [[ $exit_code -eq 130 || $exit_code -eq 143 ]]; then
    echo ""
    echo "[Info] Interrupted, killing background jobs..."
    kill 0 2>/dev/null || true
  fi
}
trap cleanup EXIT INT TERM

has_generated_file() {
  local pattern="$1"
  local generated=false
  local file
  for file in $pattern; do
    if [[ -e "$file" ]]; then
      generated=true
      break
    fi
  done
  echo "$generated"
}

classify_failure() {
  local log_file="$1"
  local kms_generated="$2"
  local new_generated="$3"
  local exit_code="$4"

  if [[ "$exit_code" == "124" ]] || grep -Eiq 'timed out after' "$log_file" 2>/dev/null; then
    echo "timeout_failed"
  elif grep -Eiq 'FileNotFound|Could not find file|does not exist|mappings|usmap|Failed to load|Package has unversioned properties|UnauthorizedAccess|Operation not permitted' "$log_file" 2>/dev/null; then
    echo "load_failed"
  elif [[ "$kms_generated" != "true" ]]; then
    echo "decompile_failed"
  elif ! grep -q 'Compiled script:' "$log_file" 2>/dev/null; then
    echo "compile_failed"
  elif [[ "$new_generated" != "true" ]]; then
    echo "link_write_failed"
  elif grep -Eiq 'Assert|mismatch|comparison|Verify|binary|JSON' "$log_file" 2>/dev/null; then
    echo "verification_failed"
  else
    echo "unknown_failed"
  fi
}

process_asset() {
  local asset_path="$1"
  local phase="$2"
  local index="$3"
  local total="$4"
  local records_file="$5"

  local rel_path="${asset_path#$ASSET_DIR/}"
  local rel_no_ext="${rel_path%.*}"
  local asset_outdir
  if [[ "$phase" == "smoke" ]]; then
    asset_outdir="$OUT_ROOT/_smoke/$rel_no_ext"
  else
    asset_outdir="$OUT_ROOT/$rel_no_ext"
  fi

  local log_file="$asset_outdir/verify.log"
  local map_path="$USMAP_PATH"
  local has_uexp=false
  if [[ -f "${asset_path%.*}.uexp" ]]; then
    has_uexp=true
  fi

  mkdir -p "$asset_outdir"
  if [[ "$LOCAL_USMAP" == "1" ]]; then
    map_path="$asset_outdir/.mappings.usmap"
    if ! cp "$USMAP_PATH" "$map_path" 2> "$log_file"; then
      local status="failed"
      local stage="load_failed"
      local result=1
      local kms_generated=false
      local new_generated=false
      acquire_lock
      printf "[%s %3d/%3d] %-80s ${RED}[FAIL]${NC} %s exit=%s\n" "$phase" "$index" "$total" "$rel_path" "$stage" "$result"
      printf "%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\n" \
        "$phase" "$status" "$stage" "$result" "$has_uexp" "$kms_generated" "$new_generated" "$rel_path" "$asset_outdir" \
        >> "$records_file"
      release_lock
      return
    fi
  fi

  local result=0
  (
    cd "$asset_outdir"
    cmd=(dotnet run --no-build --project "$CLI_PROJ" -- verify "$asset_path" \
      --ue-version "$UE_VERSION" \
      --mappings "$map_path" \
      --outdir "$asset_outdir")
    if [[ ${#VERIFY_EXTRA_ARGS[@]} -gt 0 ]]; then
      cmd+=("${VERIFY_EXTRA_ARGS[@]}")
    fi
    "${cmd[@]}"
  ) > "$log_file" 2>&1 &
  local verify_pid=$!
  local elapsed=0
  while kill -0 "$verify_pid" 2>/dev/null; do
    if [[ "$ASSET_TIMEOUT" -gt 0 && "$elapsed" -ge "$ASSET_TIMEOUT" ]]; then
      {
        echo ""
        echo "[Error] Asset verification timed out after ${ASSET_TIMEOUT}s"
      } >> "$log_file"
      pkill -TERM -P "$verify_pid" 2>/dev/null || true
      kill "$verify_pid" 2>/dev/null || true
      sleep 2
      pkill -KILL -P "$verify_pid" 2>/dev/null || true
      kill -9 "$verify_pid" 2>/dev/null || true
      wait "$verify_pid" 2>/dev/null || true
      result=124
      break
    fi
    sleep 1
    elapsed=$((elapsed + 1))
  done
  if [[ "$result" -ne 124 ]]; then
    wait "$verify_pid" 2>/dev/null || result=$?
  fi
  if [[ "$LOCAL_USMAP" == "1" ]]; then
    rm -f "$map_path"
  fi

  local verified=false
  if grep -Eq 'Verified:|Verified \(structural\):' "$log_file" 2>/dev/null; then
    verified=true
  elif iconv -f UTF-16LE -t UTF-8 "$log_file" 2>/dev/null | grep -Eq 'Verified:|Verified \(structural\):'; then
    verified=true
  fi

  local kms_generated
  local new_generated
  kms_generated=$(has_generated_file "$asset_outdir/*.kms")
  new_generated=$(has_generated_file "$asset_outdir/*.new.uasset")

  local status="failed"
  local stage="none"
  if [[ "$verified" == "true" ]]; then
    status="success"
  else
    stage=$(classify_failure "$log_file" "$kms_generated" "$new_generated" "$result")
  fi

  acquire_lock
  if [[ "$status" == "success" ]]; then
    printf "[%s %3d/%3d] %-80s ${GREEN}[OK]${NC}\n" "$phase" "$index" "$total" "$rel_path"
  else
    printf "[%s %3d/%3d] %-80s ${RED}[FAIL]${NC} %s exit=%s\n" "$phase" "$index" "$total" "$rel_path" "$stage" "$result"
  fi
  printf "%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\n" \
    "$phase" "$status" "$stage" "$result" "$has_uexp" "$kms_generated" "$new_generated" "$rel_path" "$asset_outdir" \
    >> "$records_file"
  release_lock
}

write_summary() {
  local records_file="$1"

  python3 - "$records_file" "$SUMMARY_FILE" "$FAILED_LIST_FILE" "$ASSET_DIR" "$USMAP_PATH" "$UE_VERSION" "$VERIFY_MODE" "$OUT_ROOT" <<'PY'
import json
import os
import sys
from collections import Counter

records_path, summary_path, failed_list_path, asset_dir, usmap_path, ue_version, mode, out_root = sys.argv[1:]

records = []
if os.path.exists(records_path):
    with open(records_path, "r", encoding="utf-8") as f:
        for line in f:
            line = line.rstrip("\n")
            if not line:
                continue
            phase, status, stage, exit_code, has_uexp, kms_generated, new_generated, rel_path, outdir = line.split("\t", 8)
            record = {
                "phase": phase,
                "status": status,
                "stage": stage,
                "exit_code": int(exit_code),
                "has_uexp": has_uexp == "true",
                "kms_generated": kms_generated == "true",
                "new_uasset_generated": new_generated == "true",
                "asset": rel_path,
                "outdir": outdir,
            }
            if status != "success":
                log_path = os.path.join(outdir, "verify.log")
                record["log"] = log_path
                if os.path.exists(log_path):
                    with open(log_path, "r", encoding="utf-8", errors="replace") as log:
                        lines = log.readlines()
                    record["log_tail"] = "".join(lines[-40:])
            records.append(record)

success = sum(1 for r in records if r["status"] == "success")
failed_records = [r for r in records if r["status"] != "success"]

by_ext = Counter(os.path.splitext(r["asset"])[1] or "<none>" for r in records)
by_top_dir = Counter((r["asset"].split("/", 1)[0] if "/" in r["asset"] else ".") for r in records)
by_stage = Counter(r["stage"] for r in failed_records)

summary = {
    "status": "passed" if not failed_records else "failed",
    "asset_dir": asset_dir,
    "mappings": usmap_path,
    "ue_version": ue_version,
    "mode": mode,
    "out_root": out_root,
    "total": len(records),
    "success": success,
    "failed": len(failed_records),
    "by_extension": dict(sorted(by_ext.items())),
    "by_top_directory": dict(sorted(by_top_dir.items())),
    "by_failure_stage": dict(sorted(by_stage.items())),
    "failures": failed_records,
}

with open(summary_path, "w", encoding="utf-8") as f:
    json.dump(summary, f, ensure_ascii=False, indent=2)
    f.write("\n")

with open(failed_list_path, "w", encoding="utf-8") as f:
    for record in failed_records:
        f.write(record["asset"] + "\n")
PY
}

write_rerun_script() {
  local failed_count="$1"
  if [[ "$failed_count" -eq 0 ]]; then
    rm -f "$RERUN_SCRIPT"
    return
  fi

  cat > "$RERUN_SCRIPT" <<EOF
#!/usr/bin/env bash
set -euo pipefail

OUT_ROOT="\${OUT_ROOT:-$OUT_ROOT/rerun_failed_\$(date +%Y%m%d_%H%M%S)}" \\
ASSET_LIST_FILE="$FAILED_LIST_FILE" \\
"$SCRIPT_PATH" "\$@"
EOF
  chmod +x "$RERUN_SCRIPT"
}

echo "========================================"
echo "RogueCore UE5.6 WeaponsNTools Verification"
echo "========================================"
echo "[Info] Asset directory: $ASSET_DIR"
echo "[Info] Mappings:        $USMAP_PATH"
echo "[Info] Output root:     $OUT_ROOT"
echo "[Info] Verify mode:     $VERIFY_MODE"
echo "[Info] Dotnet restore:  $RESTORE"
echo "[Info] Skip build:      $SKIP_BUILD"
echo "[Info] Smoke only:      $SMOKE_ONLY"
echo "[Info] Asset timeout:   $ASSET_TIMEOUT"
echo "[Info] Local usmap:     $LOCAL_USMAP"
echo "[Info] Exclude SK_:     $EXCLUDE_SK_ASSETS"
echo "[Info] Parallel jobs:   $MAX_JOBS"
echo ""

if [[ ! -d "$ASSET_DIR" ]]; then
  fail_preflight "asset directory not found: $ASSET_DIR"
fi

if [[ ! -f "$USMAP_PATH" ]]; then
  fail_preflight "mappings file not found: $USMAP_PATH"
fi

if [[ ! -f "$CLI_PROJ" ]]; then
  fail_preflight "CLI project not found: $CLI_PROJ"
fi

if ! command -v dotnet >/dev/null 2>&1; then
  fail_preflight "dotnet is not available on PATH"
fi

if ! command -v python3 >/dev/null 2>&1; then
  fail_preflight "python3 is required to generate summary.json"
fi

mkdir -p "$OUT_ROOT"
rm -rf "$OUTPUT_LOCK"
: > "$RECORDS_FILE"
: > "$SMOKE_RECORDS_FILE"
: > "$FAILED_LIST_FILE"

ASSET_FILES=()
EXCLUDED_COUNT=0
if [[ -n "$ASSET_LIST_FILE" ]]; then
  if [[ ! -f "$ASSET_LIST_FILE" ]]; then
    fail_preflight "asset list not found: $ASSET_LIST_FILE"
  fi
  while IFS= read -r entry || [[ -n "$entry" ]]; do
    [[ -z "$entry" ]] && continue
    [[ "$entry" == \#* ]] && continue
    if [[ "$entry" = /* ]]; then
      asset_path="$entry"
    else
      asset_path="$ASSET_DIR/$entry"
    fi
    if [[ -f "$asset_path" ]]; then
      case "$asset_path" in
        *.uasset|*.umap)
          if is_excluded_asset "$asset_path"; then
            EXCLUDED_COUNT=$((EXCLUDED_COUNT + 1))
          else
            ASSET_FILES+=("$asset_path")
          fi
          ;;
      esac
    else
      echo "[Warn] Listed asset does not exist: $entry" >&2
    fi
  done < "$ASSET_LIST_FILE"
else
  while IFS= read -r file; do
    if is_excluded_asset "$file"; then
      EXCLUDED_COUNT=$((EXCLUDED_COUNT + 1))
    else
      ASSET_FILES+=("$file")
    fi
  done < <(find "$ASSET_DIR" -type f \( -name "*.uasset" -o -name "*.umap" \) 2>/dev/null | LC_ALL=C sort)
fi

UEXP_COUNT=$(find "$ASSET_DIR" -type f -name "*.uexp" 2>/dev/null | wc -l | tr -d ' ')

if [[ ${#ASSET_FILES[@]} -eq 0 ]]; then
  fail_preflight "no .uasset or .umap files found under asset directory; found $UEXP_COUNT .uexp sidecar files" "$UEXP_COUNT"
fi

echo "[Info] Found ${#ASSET_FILES[@]} .uasset/.umap asset(s)"
if [[ "$EXCLUDED_COUNT" -gt 0 ]]; then
  echo "[Info] Excluded $EXCLUDED_COUNT SK_ prefixed model asset(s)"
fi
echo "[Info] Found $UEXP_COUNT .uexp sidecar file(s)"
echo ""

if [[ "$SKIP_BUILD" == "1" ]]; then
  echo "[Info] Skipping CLI build"
else
  echo "[Info] Building CLI..."
  BUILD_ARGS=("$CLI_PROJ" -v minimal)
  if [[ "$RESTORE" != "1" ]]; then
    BUILD_ARGS+=(--no-restore)
  fi
  dotnet build "${BUILD_ARGS[@]}"
fi
echo ""

SMOKE_TOTAL="$SMOKE_COUNT"
if [[ "$SMOKE_TOTAL" -gt "${#ASSET_FILES[@]}" ]]; then
  SMOKE_TOTAL="${#ASSET_FILES[@]}"
fi

if [[ "$SMOKE_TOTAL" -gt 0 ]]; then
  echo "[Info] Smoke testing $SMOKE_TOTAL asset(s)..."
  for ((i = 0; i < SMOKE_TOTAL; i++)); do
    process_asset "${ASSET_FILES[$i]}" "smoke" "$((i + 1))" "$SMOKE_TOTAL" "$SMOKE_RECORDS_FILE"
  done
  echo ""
fi

if [[ "$SMOKE_ONLY" == "1" ]]; then
  write_summary "$SMOKE_RECORDS_FILE"

  FAILED_COUNT=$(python3 - "$SUMMARY_FILE" <<'PY'
import json
import sys
with open(sys.argv[1], "r", encoding="utf-8") as f:
    print(json.load(f)["failed"])
PY
)
  SUCCESS_COUNT=$(python3 - "$SUMMARY_FILE" <<'PY'
import json
import sys
with open(sys.argv[1], "r", encoding="utf-8") as f:
    print(json.load(f)["success"])
PY
)
  TOTAL_COUNT=$(python3 - "$SUMMARY_FILE" <<'PY'
import json
import sys
with open(sys.argv[1], "r", encoding="utf-8") as f:
    print(json.load(f)["total"])
PY
)

  write_rerun_script "$FAILED_COUNT"

  echo "========================================"
  echo "Smoke Summary"
  echo "========================================"
  echo "Total:   $TOTAL_COUNT"
  echo -e "Success: ${GREEN}$SUCCESS_COUNT${NC}"
  echo -e "Failed:  ${RED}$FAILED_COUNT${NC}"
  echo "[Info] Summary:       $SUMMARY_FILE"
  echo "[Info] Failed assets: $FAILED_LIST_FILE"
  if [[ "$FAILED_COUNT" -gt 0 ]]; then
    echo "[Info] Rerun script:  $RERUN_SCRIPT"
    exit 1
  fi
  exit 0
fi

echo "[Info] Starting full verification..."
JOB_FIFO="$OUT_ROOT/.job_fifo"
rm -f "$JOB_FIFO"
mkfifo "$JOB_FIFO"
exec 3<>"$JOB_FIFO"
rm -f "$JOB_FIFO"

for ((i = 0; i < MAX_JOBS; i++)); do
  echo "token" >&3
done

TOTAL_COUNT=${#ASSET_FILES[@]}
for ((i = 0; i < TOTAL_COUNT; i++)); do
  read -r <&3
  (
    process_asset "${ASSET_FILES[$i]}" "full" "$((i + 1))" "$TOTAL_COUNT" "$RECORDS_FILE"
    echo "token" >&3
  ) &
done

wait
exec 3>&-
echo ""

write_summary "$RECORDS_FILE"

FAILED_COUNT=$(python3 - "$SUMMARY_FILE" <<'PY'
import json
import sys
with open(sys.argv[1], "r", encoding="utf-8") as f:
    print(json.load(f)["failed"])
PY
)
SUCCESS_COUNT=$(python3 - "$SUMMARY_FILE" <<'PY'
import json
import sys
with open(sys.argv[1], "r", encoding="utf-8") as f:
    print(json.load(f)["success"])
PY
)
TOTAL_COUNT=$(python3 - "$SUMMARY_FILE" <<'PY'
import json
import sys
with open(sys.argv[1], "r", encoding="utf-8") as f:
    print(json.load(f)["total"])
PY
)

write_rerun_script "$FAILED_COUNT"

echo "========================================"
echo "Summary"
echo "========================================"
echo "Total:   $TOTAL_COUNT"
echo -e "Success: ${GREEN}$SUCCESS_COUNT${NC}"
echo -e "Failed:  ${RED}$FAILED_COUNT${NC}"
echo "[Info] Summary:       $SUMMARY_FILE"
echo "[Info] Failed assets: $FAILED_LIST_FILE"
if [[ "$FAILED_COUNT" -gt 0 ]]; then
  echo "[Info] Rerun script:  $RERUN_SCRIPT"
  exit 1
fi

echo -e "${GREEN}All verifications passed!${NC}"
