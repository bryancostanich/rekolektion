#!/bin/bash
# Verify SRAM array DRC by generating a test array, flattening it in Magic,
# and running DRC.
#
# Usage: ./scripts/verify_array_drc.sh [rows] [cols]
#
# Requires: Python 3 with rekolektion installed, Magic, SKY130 PDK

set -e

ROWS="${1:-4}"
COLS="${2:-4}"
PDK_ROOT="${PDK_ROOT:-$HOME/.volare}"
MAGIC="${MAGIC:-magic}"
OUTPUT_DIR="output"
GDS_FILE="$OUTPUT_DIR/verify_array_${ROWS}x${COLS}.gds"
ARRAY_NAME="sram_array_${ROWS}x${COLS}"

MAGICRC="$PDK_ROOT/sky130A/libs.tech/magic/sky130A.magicrc"

if [ ! -f "$MAGICRC" ]; then
    echo "Error: SKY130 magicrc not found: $MAGICRC"
    echo "Set PDK_ROOT to point to your PDK installation."
    exit 1
fi

echo "====================================================="
echo "  SRAM Array DRC Verification"
echo "====================================================="
echo "  Array size: ${ROWS} rows x ${COLS} cols"
echo "  PDK:        $PDK_ROOT/sky130A"
echo ""

# Step 1: Generate the test array
echo "[1/3] Generating ${ROWS}x${COLS} array GDS..."
python3 -c "
from rekolektion.bitcell.foundry_sp import load_foundry_sp_bitcell
from rekolektion.array.tiler import tile_array
bc = load_foundry_sp_bitcell()
tile_array(bc, ${ROWS}, ${COLS}, '${GDS_FILE}')
print('  -> Written to ${GDS_FILE}')
"

# Step 2: Flatten and run DRC in Magic
echo "[2/3] Flattening and running DRC in Magic..."
echo ""

RESULT=$($MAGIC -dnull -noconsole -rcfile "$MAGICRC" <<EOF
gds read $GDS_FILE
flatten $ARRAY_NAME
load $ARRAY_NAME
select top cell
drc catchup
drc check

# Count and categorize errors
set result [drc listall why]
set total 0
set boundary_total 0
set internal_total 0

# Known inter-cell boundary error types
set boundary_types [list "Metal2 spacing" "Metal1 spacing" "N-well spacing"]

foreach {msg boxes} \$result {
    set n [llength \$boxes]
    incr total \$n

    set is_boundary 0
    foreach btype \$boundary_types {
        if {[string match "*\$btype*" \$msg]} {
            set is_boundary 1
            incr boundary_total \$n
            break
        }
    }
    if {!\$is_boundary} {
        incr internal_total \$n
    }

    puts "(\$n) \$msg"
}

puts ""
puts "====================================================="
puts "  DRC Summary: $ARRAY_NAME"
puts "====================================================="
puts "  Total DRC errors:           \$total"
puts "  Inter-cell boundary errors: \$boundary_total"
puts "  Internal cell errors:       \$internal_total"
puts ""
if {\$boundary_total == 0} {
    puts "  PASS: No inter-cell boundary DRC errors"
} else {
    puts "  FAIL: \$boundary_total inter-cell boundary errors remain"
}
puts "====================================================="
quit -noprompt
EOF
)

echo "$RESULT" | grep -v "^Magic\|^Starting\|^Using\|^Processing\|^Switching\|^Sourcing\|^2 Magic\|^Input style\|^The following\|^Scaled\|^Loading\|^Warning\|^Library\|^Reading\|^already\|^CIF\|^    ubm"

echo ""
echo "[3/3] Done."
