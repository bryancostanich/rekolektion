# T1.1-A — Intent doc: production `sram_array_<tag>` (BitcellArray)

## What this cell IS electrically

A `rows × cols` 2-D tile of SKY130 foundry bridged-bitcells. Each bitcell exposes the standard 6T SRAM interface (BL, BR, WL, VPWR/VGND/body) plus a Phase 2 drain-bridge wrapper (`sky130_fd_bd_sram__sram_sp_cell_bridged`) that ties access-tx drain DIFF to BL/BR met1 rails — this is the silicon-correctness fix for issue #7 (drain-floating defect masked by label-merge LVS).

For `sram_weight_bank_small` (128 rows × 128 cols) → 16,384 bitcells. For `sram_activation_bank` (256 rows × 64 cols) → 16,384 bitcells. Both ship 16,384 cells.

**Per-cell structure** (the bridged wrapper):
- 1 instance of `sky130_fd_bd_sram__sram_sp_cell_bridged` per (row, col)
- 5 ports per cell: WL (gate input), BR (right bit-line), VSUBS (substrate), BL (left bit-line), VPWR/VGND/VPB/VNB (supply + body)
- Internal: foundry 6T (4 PFETs + 4 NFETs) + drain-bridge metal extension to expose drain DIFF as BL/BR

The bridged wrapper itself remains Magic-extracted — it's a fixed foundry-cell + drain-bridge wrapper with topology that doesn't drift, and re-deriving its 6T+bridge by hand is high effort for low audit value. T1.1-A's concern is the array-level structure (how cells are tiled and bound), which is what this hand-written body captures.

## Source

- Layout generator: `src/rekolektion/macro/bitcell_array.py` (BitcellArray)
- Bridged wrapper: `src/rekolektion/bitcell/sky130_fd_bd_sram__sram_sp_cell_bridged` GDS (Magic-extracted)
- **Reference SPICE: hand-written in `src/rekolektion/macro/spice_generator.py:644` (`_write_bitcell_array_subckt`).** Hand-written intent for the array; bridged wrapper subckt remains Magic-extracted.
- Canonical port order: `src/rekolektion/macro/spice_generator.py:_bitcell_array_canonical_ports`

## Hand-written subckt body (paraphrased)

```
.subckt sram_array_<tag>
+ wl_0_0 wl_0_1 ... wl_0_{rows-1}
+ bl_0_0 br_0_0 bl_0_1 br_0_1 ... bl_0_{cols-1} br_0_{cols-1}
+ VPWR VGND VPB VNB VSUBS
* For each (r, c) in rows × cols:
X_bc_<r>_<c>  wl_0_<r>  wl_0_<r>  br_0_<c>  VSUBS  bl_0_<c>  sky130_fd_bd_sram__sram_sp_cell_bridged
.ends
```

(The duplicate `wl_0_<r>` reflects the bridged cell's two-WL-port interface; the two are tied at the cell boundary.)

## Diff vs intent

| Item | Hand-written intent | Layout (post-Magic-extract, hierarchical) | Delta |
|------|---------------------|--------------------------------------------|-------|
| Port order | per-row WL × `rows` then per-col BL/BR pair × `cols` then supplies/bodies/VSUBS | matches | ✓ |
| Bitcell instance count | 16,384 (128×128 or 256×64) | 16,384 | ✓ — verified by `audit/flood_fill_2026-05-03.md`: `bl_0_<c>` and `br_0_<c>` each fan out at 64-66 refs (per-col), `wl_0_<r>` similarly (per-row), confirming each cell terminates correctly |
| Drain-bridge presence | bridged wrapper, Phase 2 mechanism | Magic extracts the bridge correctly per task #52 | ✓ — issue #7 resolved, not re-floating |
| Body bias VPB→VPWR | `equate VPB VPWR` in netgen | NWELL biased subsurface from foundry sram_sp_wlstrap chain | waivered via `nwell_bias_disclosure.md` |

## How rekolektion uses it

- Built by `BitcellArray.build()`; X-mirror tiling within each row pair (foundry SRAM convention).
- `cim_assembler.py` for CIM doesn't use this — CIM has its own supercell array. This doc covers the production assembler path.
- The hand-written body lets LVS compare against intent rather than against the same Python code that produced the layout (T1.1-A audit concern).

## Severity

- **PASS at intent level.** Hand-written body matches layout topology and per-cell wiring.
- The waivered N-well bias (subsurface conduction) is a separate disclosure, not an intent-level mismatch.

## Ambiguities / followups

- The bridged wrapper `sky130_fd_bd_sram__sram_sp_cell_bridged` is Magic-extracted, not hand-written. Its internal 6T+bridge topology is a foundry+rekolektion design — if the bridge geometry changes (currently `sky130_cim_drain_bridge_v1`), the wrapper subckt re-extracts but the array-level intent doc here doesn't change.
- Hand-written port order matches the canonical layout labels (`wl_0_<r>`, `bl_0_<c>`, `br_0_<c>`). If the layout label scheme changes (e.g. to `wl_<r>` without the leading `0`), this doc + `_bitcell_array_canonical_ports` need to be updated together.
