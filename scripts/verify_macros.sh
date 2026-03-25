#!/usr/bin/env bash
# Run Magic DRC on each generated V1 macro.
#
# Prerequisites:
#   - Magic installed and on PATH
#   - SKY130 PDK installed via volare
#
# Usage:
#   ./scripts/verify_macros.sh

set -euo pipefail

export PATH="$HOME/.local/bin:$PATH"
export PDK_ROOT="$HOME/.volare"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_DIR="$(dirname "$SCRIPT_DIR")"
MACRO_DIR="$PROJECT_DIR/output/macros"
DRC_DIR="$PROJECT_DIR/output/drc"

mkdir -p "$DRC_DIR"

# Locate SKY130A magicrc
MAGICRC=""
for candidate in \
    "$PDK_ROOT/sky130A/libs.tech/magic/sky130A.magicrc" \
    "$HOME/pdk/sky130A/libs.tech/magic/sky130A.magicrc" \
    "/usr/local/share/pdk/sky130A/libs.tech/magic/sky130A.magicrc"; do
    if [ -f "$candidate" ]; then
        MAGICRC="$candidate"
        break
    fi
done

if [ -z "$MAGICRC" ]; then
    echo "WARNING: Could not find sky130A.magicrc. DRC results may be incomplete."
fi

MACROS=("weight_32kb" "activation_3kb" "test_64x8")

echo "============================================================"
echo "SRAM Macro DRC Verification"
echo "============================================================"
echo ""

TOTAL_ERRORS=0
SUMMARY=""

for macro_name in "${MACROS[@]}"; do
    GDS_FILE="$MACRO_DIR/${macro_name}.gds"
    RESULT_DIR="$DRC_DIR/${macro_name}"
    mkdir -p "$RESULT_DIR"

    if [ ! -f "$GDS_FILE" ]; then
        echo "SKIP: $GDS_FILE not found (run generate_v1_macros.py first)"
        SUMMARY="${SUMMARY}${macro_name}: SKIPPED (GDS not found)\n"
        continue
    fi

    echo "Running DRC on ${macro_name}..."
    echo "  GDS: $GDS_FILE"

    # Write TCL script for Magic DRC
    TCL_FILE="$RESULT_DIR/run_drc.tcl"
    LOG_FILE="$RESULT_DIR/drc_results.log"

    cat > "$TCL_FILE" <<TCLEOF
# DRC script for ${macro_name}
gds read $GDS_FILE
set top [lindex [cellname list top] 0]
puts "Checking cell: \$top"
load \$top
select top cell
drc catchup
set count [drc count]
puts "DRC_ERROR_COUNT: \$count"

set f [open $LOG_FILE w]
puts \$f "DRC Results for ${macro_name}"
puts \$f "GDS: $GDS_FILE"
puts \$f "==============================="
set why_list [drc listall why]
foreach {msg box_list} \$why_list {
    puts \$f ""
    puts \$f "Violation: \$msg"
    foreach box \$box_list {
        puts \$f "  at: \$box"
    }
}
puts \$f ""
puts \$f "==============================="
puts \$f "Total DRC errors: \$count"
close \$f

quit -noprompt
TCLEOF

    # Run Magic
    MAGIC_CMD="magic -dnull -noconsole"
    if [ -n "$MAGICRC" ]; then
        MAGIC_CMD="$MAGIC_CMD -rcfile $MAGICRC"
    fi

    MAGIC_OUTPUT=$($MAGIC_CMD "$TCL_FILE" 2>&1) || true

    # Save raw output for debugging
    echo "$MAGIC_OUTPUT" > "$RESULT_DIR/magic_stdout.log"

    # Extract error count — try DRC_ERROR_COUNT first, fallback to Magic output
    ERROR_COUNT=$(echo "$MAGIC_OUTPUT" | grep "DRC_ERROR_COUNT:" | awk -F: '{print $2}' | tr -d ' ')
    if [ -z "$ERROR_COUNT" ]; then
        # Fallback: parse "Total DRC errors found: N" from Magic stdout
        ERROR_COUNT=$(echo "$MAGIC_OUTPUT" | grep "Total DRC errors found:" | awk -F: '{print $2}' | tr -d ' ')
    fi
    if [ -z "$ERROR_COUNT" ]; then
        ERROR_COUNT="unknown"
        echo "  WARNING: Could not parse DRC error count from Magic output"
    fi

    echo "  DRC errors: $ERROR_COUNT"
    echo "  Log: $LOG_FILE"
    echo ""

    if [ "$ERROR_COUNT" != "unknown" ]; then
        TOTAL_ERRORS=$((TOTAL_ERRORS + ERROR_COUNT))
    fi
    SUMMARY="${SUMMARY}${macro_name}: ${ERROR_COUNT} DRC errors\n"
done

echo "============================================================"
echo "DRC Summary"
echo "============================================================"
echo -e "$SUMMARY"
echo "Total DRC errors across all macros: $TOTAL_ERRORS"
echo ""
echo "NOTE: DRC errors are expected at this stage. The macros use"
echo "placeholder geometry for peripheral cells. Fixing DRC"
echo "violations requires proper cell-level layout."
echo "============================================================"
