#!/bin/bash
# Run DRC on a GDS file using Magic + SKY130
# Usage: ./scripts/run_drc.sh [gds_file]

set -e

GDS_FILE="${1:-output/sky130_sram_6t_bitcell.gds}"
PDK_ROOT="${PDK_ROOT:-$HOME/.volare}"
MAGIC="${MAGIC:-magic}"

if [ ! -f "$GDS_FILE" ]; then
    echo "Error: GDS file not found: $GDS_FILE"
    exit 1
fi

TECHFILE="$PDK_ROOT/sky130A/libs.tech/magic/sky130A.tech"
MAGICRC="$PDK_ROOT/sky130A/libs.tech/magic/sky130A.magicrc"

if [ ! -f "$TECHFILE" ]; then
    echo "Error: SKY130 tech file not found: $TECHFILE"
    echo "Set PDK_ROOT to point to your PDK installation."
    exit 1
fi

GDS_FULL=$(cd "$(dirname "$GDS_FILE")" && pwd)/$(basename "$GDS_FILE")

echo "Running DRC on: $GDS_FILE"
echo "Using PDK at: $PDK_ROOT/sky130A"
echo ""

$MAGIC -dnull -noconsole -rcfile "$MAGICRC" <<EOF
gds read $GDS_FULL
set topcell [lindex [cellname list top] 0]
puts "Top cell: \$topcell"
load \$topcell
select top cell
drc catchup
drc check
set count [drc count total]
puts ""
puts "============================================"
puts "DRC Results: \$topcell"
puts "============================================"
puts "Total DRC errors: \$count"
puts ""
if {\$count > 0} {
    puts "DRC Error Details:"
    puts "--------------------------------------------"
    set why_dict [drc listall why]
    foreach {msg boxes} \$why_dict {
        puts "\nViolation: \$msg"
        set box_count 0
        foreach box \$boxes {
            puts "  at: \$box"
            incr box_count
            if {\$box_count > 30} {
                puts "  ... (truncated)"
                break
            }
        }
    }
} else {
    puts "*** DRC CLEAN ***"
}
puts ""
puts "============================================"
quit -noprompt
EOF
