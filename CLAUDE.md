# Rekolektion — SRAM Generator for SKY130

## After Every Bitcell Change

Run ALL THREE steps after modifying `src/rekolektion/bitcell/sky130_6t_lr.py`:

```bash
# 1. Generate GDS
python3 -c "from rekolektion.bitcell.sky130_6t_lr import generate_bitcell; generate_bitcell('output/sky130_6t_lr.gds')"

# 2. Render per-layer PNGs (READ these to verify visually)
python3 scripts/render_cell.py output/sky130_6t_lr.gds output/renders/lr/

# 3. Generate 3D files (GLB + STL)
python3 scripts/gds_to_stl.py output/sky130_6t_lr.gds output/3d_lr/

# 4. DRC check via Magic
export PATH="$HOME/.local/bin:$PATH"
export PDK_ROOT="$HOME/.volare"
magic -dnull -noconsole -rcfile "$PDK_ROOT/sky130A/libs.tech/magic/sky130A.magicrc" <<'EOF'
gds read output/sky130_6t_lr.gds
load sky130_sram_6t_bitcell_lr
select top cell
drc catchup
drc check
set result [drc listall why]
set total 0
foreach {msg boxes} $result { set n [llength $boxes]; incr total $n; puts "($n) $msg" }
if {$total == 0} { puts "*** DRC CLEAN ***" }
puts "=== TOTAL: $total ==="
quit -noprompt
EOF
```

## Output Locations

| Output | Path |
|--------|------|
| LR bitcell GDS | `output/sky130_6t_lr.gds` |
| LR per-layer PNGs | `output/renders/lr/` |
| LR 3D GLB/STL | `output/3d_lr/` |
| Foundry per-layer PNGs | `output/renders/foundry/` |
| Foundry 3D | `output/3d_foundry/` |
| TB (original) GDS | `output/sky130_sram_6t_bitcell.gds` |
| TB 3D | `output/3d/` |

## Key Files

- `src/rekolektion/bitcell/sky130_6t_lr.py` — active custom bitcell (LR topology)
- `src/rekolektion/tech/sky130.py` — design rules and layer definitions
- `scripts/render_cell.py` — GDS to per-layer PNG renderer
- `scripts/gds_to_stl.py` — GDS to 3D GLB/STL converter

## Planning

Use conductor at `/Users/bryancostanich/Git_Repos/bryan_costanich/khalkulo/conductor/` for planning. SRAM track: `projects/v1_tapeout/tracks/02_sram_design/plan.md`
