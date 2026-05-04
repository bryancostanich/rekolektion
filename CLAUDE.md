# Rekolektion — SRAM Generator for SKY130

## Known traps — read before "fixing" these

### Magic ext2spice port-promotion through hierarchy is broken

If you see "extracted SPICE is missing top-level ports that the LEF/refspice declares" — e.g. `MWL_EN[r]` missing in CIM, `addr[i]` / `clk` / `cs` / `we` / `dec_out_*` missing in production — **do not add labels at the macro top to fix it.** The prior session already tried that and it does not work.

Concrete evidence:
- Commit `b09c441` (F12 attempt): "Magic's hierarchical port-promotion refuses to merge a child cell's interior addr[i] rail with the parent's feeder, **regardless of .pin shape placement**."
- Commit `a97f56f`: F12 reverted because flat extraction hides issue #7 (per-cell drain floats).
- Tasks `#36`, `#44`, `#64`, `#103`, `#105` all touch this.
- Verified 2026-05-03: `cim_mwl_driver_col_64` has 64 correct `MWL_EN[r]` labels on layer 67/5; standalone extract finds them as ports; macro-top extract drops them.

The `_align_ref_ports` aligner in `scripts/run_lvs_cim.py:54` and `scripts/run_lvs_production.py:129` papers over the gap by stripping un-promoted ports from the reference SPICE. It is a documented Magic-tooling workaround. The silicon is electrically correct — verify via flood-fill (task #110), not via end-to-end LVS port match.

**Before any "obvious" label-fix attempt: read `b09c441`, `a97f56f`, `audit/hack_inventory.md` entry A1, and tasks #64 / #110.**



## After Every Bitcell Change

Run ALL steps after modifying `src/rekolektion/bitcell/sky130_6t_lr.py`:

```bash
# 1. Generate GDS
python3 -c "from rekolektion.bitcell.sky130_6t_lr import generate_bitcell; generate_bitcell('output/sky130_6t_lr.gds')"

# 2. Render per-layer PNGs (F# tool — READ these to verify visually)
cd ~/Git_Repos/bryan_costanich/rekolektion
dotnet run --project tools/viz/src/Rekolektion.Viz.Cli -- render output/sky130_6t_lr.gds output/renders/lr/

# 3. Generate 3D files — GLB + STL + in-situ GLB (F# tool)
dotnet run --project tools/viz/src/Rekolektion.Viz.Cli -- mesh output/sky130_6t_lr.gds output/3d_lr/

# 3a. Open the live desktop viewer (interactive 2D + 3D)
dotnet run --project tools/viz/src/Rekolektion.Viz.Cli -- app

# 3b. Headless one-shot render (no GUI; for CI / agents)
dotnet run --project tools/viz/src/Rekolektion.Viz.Cli -- viz-render \
    --gds output/sky130_6t_lr.gds --output output/renders/lr_smoke.png

# 4. DRC check via Magic
cd ~/Git_Repos/bryan_costanich/rekolektion
export PATH="$HOME/.local/bin:$PATH"
export PDK_ROOT="$HOME/.volare"
magic -dnull -noconsole -rcfile "$PDK_ROOT/sky130B/libs.tech/magic/sky130B.magicrc" <<'EOF'
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

## Chip Visualization (F# tool at `khalkulo/tools/viz/`)

Chip-level visualizations live in the khalkulo repo at `docs/viz/`.

```bash
cd ~/Git_Repos/bryan_costanich/khalkulo/tools/viz

# Static block diagram SVG
dotnet run -- svg ~/Git_Repos/bryan_costanich/khalkulo/docs/viz/chip_dataflow.svg

# Animated dataflow SVG (standalone CSS animation)
dotnet run -- animate ~/Git_Repos/bryan_costanich/khalkulo/docs/viz/chip_dataflow_animated.svg

# Interactive HTML viewer (single self-contained file)
dotnet run -- web ~/Git_Repos/bryan_costanich/khalkulo/docs/viz/index.html
```

## Output Locations

| Output | Path |
|--------|------|
| LR bitcell GDS | `output/sky130_6t_lr.gds` |
| LR per-layer PNGs | `output/renders/lr/` |
| LR 3D GLB/STL | `output/3d_lr/` |
| Foundry per-layer PNGs | `output/renders/foundry/` |
| Foundry 3D | `output/3d_foundry/` |

## CIM Macro Commands

```bash
# Generate all 4 CIM cell variants (GDS + SPICE)
python3 -c "from rekolektion.bitcell.sky130_6t_lr_cim import generate_cim_variants; generate_cim_variants()"

# Assemble all 4 CIM macros (GDS + LEF + Liberty + blackbox Verilog)
python3 -c "from rekolektion.macro.cim_assembler import generate_all_cim_macros; generate_all_cim_macros()"

# DRC a CIM cell variant
magic -dnull -noconsole -rcfile "$PDK_ROOT/sky130B/libs.tech/magic/sky130B.magicrc" <<'EOF'
gds read output/cim_variants/sky130_6t_cim_lr_sram_a.gds
load sky130_sram_6t_cim_lr
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

## Key Files

- `src/rekolektion/bitcell/sky130_6t_lr.py` — active custom bitcell (LR topology)
- `src/rekolektion/bitcell/sky130_6t_lr_cim.py` — 7T+1C CIM cell generator + variants
- `src/rekolektion/tech/sky130.py` — design rules, layer defs, PDK variant config
- `src/rekolektion/macro/cim_assembler.py` — CIM macro assembler
- `tools/viz/` — F# 5-project solution (.NET 10): Core (GDS/sidecar), Render (Skia + 3D mesh), App (Avalonia desktop), Cli (read/render/mesh/app/viz-render), Mcp (stdio JSON-RPC server with 7 agent tools)
- `scripts/render_cell.py` — GDS to per-layer PNG renderer (Python, legacy)
- `scripts/gds_to_stl.py` — GDS to 3D GLB/STL converter (Python, legacy)

## Planning

Use conductor at `/Users/bryancostanich/Git_Repos/bryan_costanich/khalkulo/conductor/` for planning. SRAM track: `projects/v1_tapeout/tracks/02_sram_design/plan.md`
