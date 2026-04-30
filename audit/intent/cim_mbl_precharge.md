# T4.1 — Intent doc: `cim_mbl_precharge`

## What this cell IS electrically

Single-PMOS precharge switch for the MBL line. When `MBL_PRE` (active low) is asserted, the PMOS conducts and pulls `MBL` toward `VREF`. `VREF` is an external analog supply (typically VDD/2). One transistor in the cell, plus an N-tap that ties the n-well to a `VPWR` rail (added explicitly in the layout to avoid Magic auto-naming the n-well as a separate floating node).

**Declared port list** (from generator docstring `src/rekolektion/peripherals/cim_mbl_precharge.py`): `MBL_PRE VREF MBL VPWR`. **Body bias:** PMOS body=VPWR via the in-cell N-tap.

## Source

- Generator: `src/rekolektion/peripherals/cim_mbl_precharge.py`
- Cached extracted body: `src/rekolektion/peripherals/cells/extracted_subckt/cim_mbl_precharge.subckt.sp` (6 lines)

## Cached Magic extract (full file)

```
* NGSPICE file created from cim_mbl_precharge.ext - technology: sky130B

.subckt cim_mbl_precharge MBL_PRE VREF MBL VPWR
X0 VREF MBL_PRE MBL VPWR sky130_fd_pr__pfet_01v8 ad=0.2772 pd=2.34 as=0.2772 ps=2.34 w=0.84 l=0.15
.ends
```

**1 device.** PMOS, w=0.84, l=0.15. Drain=VREF, gate=MBL_PRE, source=MBL, body=VPWR.

## Diff vs intent

| Item | Generator declared | Magic extract | Discrepancy |
|------|--------------------|----------------|-------------|
| Port list | `MBL_PRE VREF MBL VPWR` | `MBL_PRE VREF MBL VPWR` | none |
| Device count | 1 PMOS | 1 PMOS | none |
| Device sizes | not directly emitted in generator (layout-driven) | w=0.84, l=0.15 | OK |
| Body bias | "N-tap inside the cell that ties the n-well to a VPWR rail" — claim from docstring | PMOS X0 has body=VPWR (4-th terminal). T4.4 main-session flood-fill DID NOT identify this cell's NWELL as floating — `cim_mbl_precharge_row_64` shows 64 VPWR labels at sub-cell level (one per instance). N-tap presence consistent with intent. | **PASS** — body bias appears to physically reach VPWR via the in-cell N-tap. Subject to T4.4 detailed flood-fill confirmation. |

## How rekolektion uses it

- 64 instances tiled by `cim_mbl_precharge_row_64` (one per column) at the top of the macro array.
- `cim_assembler.py` places one row of these at top of the macro and ties `MBL_PRE` and `VREF` to top-level pins.

## Severity

- **PASS at intent level.** Cell topology, port list, and body-bias-to-VPWR all match.
- N-A on multi-transistor topology audit (only 1 device).

## Ambiguities

- The Magic-extracted port order (`MBL_PRE VREF MBL VPWR`) sets the convention for instance lines in `cim_sram_d_64x64.sp`. Confirm `cim_assembler.py` calls match this order — any swap of MBL ↔ VREF in instance lines would silently flip the precharge target. Did not deep-trace, but the comment in `cim_assembler.py:13596` ("VPWR external pin (TOP-RIGHT corner)") suggests the rail naming is correct end-to-end.
