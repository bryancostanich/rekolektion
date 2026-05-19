#!/bin/bash
# Compare viz's in-process DRC against Magic on a .rkt file.
# Used for regression testing as viz's DRC rule set evolves.
#
# Output is two sorted lists of "<rule-id> ×<count>" — one from
# viz, one from Magic. Diff them by eye; matching rules + similar
# counts = viz is converging on Magic. Common discrepancies:
#   * Magic reports per-tile, viz reports per-cluster (Magic
#     count often higher).
#   * Magic uses netlist for same-net well/implant relaxations;
#     viz doesn't (viz over-reports nwell.2a, psdm.2 etc).
#   * Some Magic rules have implant-aware scoping viz can't
#     fully replicate (false positives on rules viz tracks but
#     Magic considers context-OK).
#
# Usage:
#   tools/viz/scripts/drc_golden_diff.sh path/to/file.rkt
#
# Requires: dotnet, Magic + SKY130 PDK at $PDK_ROOT, the viz
# Core DLL already built (`dotnet build` in tools/viz first).
set -e
RKT="$1"
[ -z "$RKT" ] && { echo "usage: $0 path/to/file.rkt"; exit 1; }
[ ! -f "$RKT" ] && { echo "no such file: $RKT"; exit 1; }

NAME=$(basename "$RKT" .rkt)
GDS="/tmp/$NAME.gds"
VIZ_OUT="/tmp/$NAME.viz.txt"
MAGIC_RAW="/tmp/$NAME.magic.raw"
MAGIC_OUT="/tmp/$NAME.magic.txt"
REPO=/Users/bryancostanich/git_repos/bryan_costanich/rekolektion

echo "=== $NAME ==="
cd "$REPO"
dotnet run --project tools/viz/src/Rekolektion.Viz.Cli -- to-gds "$RKT" "$GDS" 2>&1 | tail -1

dotnet fsi /tmp/run_drc.fsx "$RKT" 2>&1 \
  | awk '/^=== FULL FLAT/{inblock=1; next} inblock && /^ {2}[a-z]/{print $1, $2} inblock && /^$/{inblock=0}' \
  | sort > "$VIZ_OUT"

export PATH="$HOME/.local/bin:$PATH"
export PDK_ROOT="$HOME/.volare"
magic -dnull -noconsole -rcfile "$PDK_ROOT/sky130A/libs.tech/magic/sky130A.magicrc" > "$MAGIC_RAW" 2>&1 <<EOF
gds read $GDS
load $NAME
select top cell
drc catchup
drc check
foreach {msg boxes} [drc listall why] {
    set n [llength \$boxes]
    puts "RULE: \$msg | x\$n"
}
quit -noprompt
EOF

# Parse Magic's "RULE: text (id) | xN" into "id ×N"
grep '^RULE:' "$MAGIC_RAW" | sed -E 's/.*\(([^)]+)\) \| x([0-9]+).*/\1 ×\2/' | sort > "$MAGIC_OUT"

echo ""
echo "--- viz counts ---"
cat "$VIZ_OUT"
[ ! -s "$VIZ_OUT" ] && echo "(none)"
echo "--- Magic counts ---"
cat "$MAGIC_OUT"
[ ! -s "$MAGIC_OUT" ] && echo "(none)"
