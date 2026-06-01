#!/bin/bash
# Run DRC on a GDS file using Magic + SKY130
# Usage: ./scripts/run_drc.sh [--full|-f] [gds_file]
#
# By default uses the fast geometry-only rule set (whatever the
# magicrc loads). Pass --full / -f to switch to drc(full): the
# sign-off rule set including latch-up (LU.2/LU.3), implant-aware
# diff/tap.9 + licon.9, nwell.4, etc. drc(full) is slower —
# typically 2-5× on opamp-sized cells, more on full macros.

set -e

FULL_FLAG=0
ARGS=()
for arg in "$@"; do
    case "$arg" in
        --full|-f) FULL_FLAG=1 ;;
        *)         ARGS+=("$arg") ;;
    esac
done

GDS_FILE="${ARGS[0]:-output/sky130_sram_6t_bitcell.gds}"
PDK_ROOT="${PDK_ROOT:-$HOME/.volare}"
MAGIC="${MAGIC:-magic}"
DRC_STYLE_CMD=""
if [ "$FULL_FLAG" = "1" ]; then
    DRC_STYLE_CMD="drc style drc(full)"
fi

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
$DRC_STYLE_CMD
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
