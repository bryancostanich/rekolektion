# T4.1 — Intent reference: production Magic-extracted cells

This doc covers the production cells whose reference SPICE bodies remain **Magic-extracted at LVS time** (not hand-written T1.1-A). For each, we record what the cell IS electrically, where the layout generator lives, and the audit handle.

For the 3 hand-written T1.1-A cells (`pre_<tag>`, `mux_<tag>`, `sram_array_<tag>`), see the dedicated docs in this directory: [`precharge_row.md`](precharge_row.md), [`column_mux_row.md`](column_mux_row.md), [`bitcell_array.md`](bitcell_array.md).

---

## `wl_driver_<tag>` (WLDriverRow)

**Electrically:** one row of word-line drivers — converts each row-decoder low-active output into a high-active WL signal that drives the array's `wl_<r>` poly stripe. Implemented as a chain of `sky130_fd_sc_hd__buf_2` + (possibly inverter) stdcells per row, depending on the variant.

- Generator: `src/rekolektion/macro/wl_driver_row.py` (WLDriverRow)
- Reference SPICE: Magic-extracted at LVS time (the body is whatever the layout produces)
- Bound by: `spice_generator.py:447-449` — `Xwl_driver dec_out_<r> ... wl_<r> ... VPWR VGND wl_driver_<tag>`
- Buf_2 usage: see `audit/hack_inventory.md` C3 — buf_2 is a logic cell flattened in LVS for OpenLane-buffer reconciliation.

**Audit handle:** topology is foundry-stdcell driven; sizing and number of buffer stages is variant-dependent. Verify by `grep` against `wl_driver_row.py` for buffer-stage count.

---

## `sa_<tag>` (SenseAmpRow)

**Electrically:** one strong-arm sense amplifier per output bit on the muxed bit-line pair. Resolves `muxed_bl_<bit>` vs `muxed_br_<bit>` differential into a CMOS-level `dout_<bit>` when `s_en` is asserted. Per-bit topology is foundry-stdcell or hand-built; the row tiles `bits` instances of the cell.

- Generator: `src/rekolektion/macro/sense_amp_row.py` (SenseAmpRow)
- Reference SPICE: Magic-extracted
- Bound by: `spice_generator.py:489-494` — `Xsa muxed_bl_* muxed_br_* s_en dout[*] VPWR VGND sa_<tag>`

**Audit handle:** verifying SA topology + sizing requires running the Magic extract and comparing against the `sense_amp_row.py` intent. Done implicitly by LVS topology match (16774=16774 net-perfect on 2026-05-03).

---

## `wd_<tag>` (WriteDriverRow)

**Electrically:** one write driver per output bit. When `w_en` is asserted, drives `muxed_bl_<bit>`/`muxed_br_<bit>` differentially to write `din_<bit>`. NMOS pull-down based with PMOS keepers.

- Generator: `src/rekolektion/macro/write_driver_row.py` (WriteDriverRow)
- Reference SPICE: Magic-extracted
- Bound by: `spice_generator.py:497-501` — `Xwd din[*] w_en muxed_bl_* muxed_br_* VPWR VGND wd_<tag>`

**Audit handle:** same as SenseAmpRow; LVS topology match validates the per-bit driver shape.

---

## `ctrl_logic_<tag>` (control_logic)

**Electrically:** the macro's central control plane — generates `clk_buf`, `we`, `cs`, `p_en_bar`, `s_en`, `w_en` from the external `clk`, `cs`, `we` inputs. Implemented as a small DFF + glue logic cluster.

- Generator: `src/rekolektion/macro/control_logic.py` (ControlLogic)
- Reference SPICE: Magic-extracted
- Bound by: top-level X-instance in `spice_generator._write_top_subckt`

**Audit handle:** the control logic is the most variable cell in the macro (per-tap timing tweaks). LVS topology match plus the `s_en`, `p_en_bar`, `w_en` signal reach to their consumers (sense, precharge, write driver respectively) is the sanity check.

---

## `row_decoder_<tag>` and predecoder

**Electrically:** the row decoder takes the high-order address bits `addr[0..rows_log2-1]` and produces `dec_out_<r>` (low-active) — one output per row. Internally a tree of `sky130_fd_bd_sram__openram_sp_nand{2,3,4}_dec` cells with predecoder layers.

- Generator: `src/rekolektion/macro/row_decoder.py` + `src/rekolektion/macro/predecoder.py`
- Reference SPICE: Magic-extracted (uses foundry NAND_dec cells)
- Bound by: `spice_generator.py:443` — `Xdecoder addr[*] dec_out_* VPWR VGND row_decoder_<tag>`

**Audit handle:** the `dec_out_<N>` overflow at the macro boundary is what `_align_ref_ports` adds 128 of in production — it's documented as a Magic over-promotion in `audit/hack_inventory.md` A2. LVS topology match (385=385 nets on the array sub-circuit) validates the decoder logic itself.

---

## Convention for adding new production cells

If a new production cell appears in `spice_generator._write_top_subckt`'s X-instances:
1. **If hand-written T1.1-A**: add a full intent doc (see `precharge_row.md` template).
2. **If Magic-extracted**: add a section to this file with the same five-bullet structure (electrically / generator / SPICE source / bound by / audit handle).

The audit-significance of an intent doc is **not** the format completeness — it's whether a reader can confirm "this is what the cell SHOULD be" against the layout reality. Hand-written cells need the full body listing because the body IS the intent. Extracted cells just need a pointer back to the generator + bound-by line.
