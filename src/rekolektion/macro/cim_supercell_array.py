"""CIM supercell array builder — F8.

Tiles CIM supercells (foundry 6T + T7 + cap, foundry-cell-validated) in
an R×C grid with the F11-validated WL bridging mechanism:
  - Strip foundry cell's internal "WL" label so per-row labels win
  - Per-row dual WL POLY strips at BOTH wl_top and wl_bot Y positions

Replaces the LR-cell-based `CIMBitcellArray`.  Same interface so
`cim_assembler.py` can swap with minimal changes.
"""
from __future__ import annotations

from pathlib import Path
from typing import Optional

import gdstk

from rekolektion.bitcell.sky130_cim_supercell import (
    CIM_SUPERCELL_VARIANTS,
    create_cim_supercell,
)
from rekolektion.bitcell.sky130_cim_tap_supercell import (
    cell_name as cim_tap_cell_name,
    create_cim_tap_supercell,
)
from rekolektion.bitcell.sky130_cim_drain_bridge import BRIDGE_H


# Foundry cell internal WL stripe Y positions, expressed in supercell-local
# coords (foundry cell is now offset by BRIDGE_H — the bridge cell sits at
# supercell-local y=[0, BRIDGE_H], foundry above it).
_FOUNDRY_WL_TOP_Y: float = BRIDGE_H + 1.385
_FOUNDRY_WL_BOT_Y: float = BRIDGE_H + 0.195
_FOUNDRY_LEF_H: float = BRIDGE_H + 1.580

# Foundry cell BL/BR M1 X positions (cell-local µm) — matched against
# bitcell_array.py's _FOUNDRY_BL_X_IN_CELL/_FOUNDRY_BR_X_IN_CELL.
_FOUNDRY_BL_X: float = 0.420
_FOUNDRY_BR_X: float = 0.780


class CIMSupercellArray:
    """Tiled CIM supercell array with F11-style WL bridging.

    Each cell is a "supercell" wrapping the foundry 6T core + T7 + MIM cap.
    Tiling: same orientation per row (no column X-mirror — production
    macro convention), Y-mirror odd rows (for shared power rails).
    Per-row dual WL POLY strips bridge wl_top + wl_bot of every cell in
    the row.
    """

    def __init__(
        self,
        variant: str,
        rows: int,
        cols: int,
        name: Optional[str] = None,
        strap_interval: int = 8,
    ):
        if variant not in CIM_SUPERCELL_VARIANTS:
            raise ValueError(
                f"Unknown CIM variant {variant!r}. "
                f"Valid: {sorted(CIM_SUPERCELL_VARIANTS)}"
            )
        self.variant = variant
        self.rows = rows
        self.cols = cols
        # Strap insertion period.  0 disables (legacy floating-NWELL —
        # tests only).  Default 8 mirrors production's industry-standard
        # sky130 SRAM convention.  Each strap column is a CIM tap
        # supercell carrying the foundry sram_sp_wlstrap (N+/P+ taps).
        # Tap cell width matches bitcell pitch (supercell_w), so
        # `strap_aware_col_x(col, supercell_w, strap_interval, supercell_w)`
        # is the correct call signature for periphery routing.
        self.strap_interval = max(0, int(strap_interval))
        self._cfg = CIM_SUPERCELL_VARIANTS[variant]
        self.top_cell_name = (
            name
            or f"cim_array_{variant.lower().replace('-', '_')}_{rows}x{cols}"
        )

    @property
    def cell_pitch_x(self) -> float:
        return self._cfg.supercell_w

    @property
    def cell_pitch_y(self) -> float:
        return self._cfg.supercell_h

    @property
    def n_strap_cols(self) -> int:
        """Number of tap supercells inserted between bitcell columns."""
        if self.strap_interval <= 0:
            return 0
        # Strap inserted after every `strap_interval` bitcells; no trailing
        # strap (matches production BitcellArray.n_strap_cols).
        return (self.cols - 1) // self.strap_interval

    def _supercell_x(self, col: int) -> float:
        """Array X for the leftmost edge of bitcell column `col`,
        accounting for strap columns inserted to its left."""
        sw = self._cfg.supercell_w
        if self.strap_interval <= 0:
            return col * sw
        n_straps_before = col // self.strap_interval
        return col * sw + n_straps_before * sw  # tap pitch == bitcell pitch

    def _strap_x(self, strap_idx: int) -> float:
        """Array X for the leftmost edge of strap column `strap_idx`."""
        sw = self._cfg.supercell_w
        bitcells_before = (strap_idx + 1) * self.strap_interval
        return bitcells_before * sw + strap_idx * sw

    @property
    def width(self) -> float:
        return (self.cols + self.n_strap_cols) * self._cfg.supercell_w

    @property
    def height(self) -> float:
        return self.rows * self._cfg.supercell_h

    def build(self) -> gdstk.Library:
        """Build the supercell-based array library."""
        # Build the supercell once and copy its cells into the output lib
        super_lib, _ = create_cim_supercell(self.variant)
        out_lib = gdstk.Library(
            name=f"{self.top_cell_name}_lib", unit=1e-6, precision=5e-9
        )
        # Find the supercell top cell
        super_top_name = (
            f"sky130_cim_supercell_"
            f"{self.variant.lower().replace('-', '_')}"
        )
        super_top = next(c for c in super_lib.cells if c.name == super_top_name)
        # Hierarchical extraction (mirroring run_lvs_production.py): keep
        # foundry qtap's BL/BR/Q and supercell's MWL/MBL labels intact so
        # Magic's `port makeall` recursive promotes them to sub-cell ports.
        # Per-row/per-col array labels at the same physical positions then
        # bind to those ports via abutment.  Earlier flat flow stripped
        # these to prevent global merge after `top.flatten()`; that path
        # is gone.
        cell_map: dict[str, gdstk.Cell] = {}
        for c in super_lib.cells:
            copy = c.copy(c.name)
            cell_map[c.name] = copy
            out_lib.add(copy)
        super_local = cell_map[super_top_name]

        # Build the CIM tap supercell and copy its cells into the output
        # lib so we can instance them at strap-column positions.  Tap
        # carries the foundry sram_sp_wlstrap (N+/P+ taps) — same width
        # as the bitcell supercell so periphery alignment is preserved
        # under strap_aware_col_x().
        tap_local = None
        if self.strap_interval > 0 and self.n_strap_cols > 0:
            tap_lib, _ = create_cim_tap_supercell(self.variant)
            tap_top_name = cim_tap_cell_name(self.variant)
            existing = {c.name for c in out_lib.cells}
            for c in tap_lib.cells:
                if c.name in existing:
                    if c.name == tap_top_name:
                        tap_local = next(
                            x for x in out_lib.cells if x.name == c.name
                        )
                    continue
                copy = c.copy(c.name)
                out_lib.add(copy)
                if c.name == tap_top_name:
                    tap_local = copy
            if tap_local is None:
                raise RuntimeError(
                    f"Failed to import tap supercell {tap_top_name}"
                )

        # Top array cell
        top = gdstk.Cell(self.top_cell_name)

        # Place supercells in R×C grid
        # No X-mirror (production macro convention — avoids tile_array's
        # catastrophic short pattern).  Y-mirror odd rows for shared rails.
        ch = self._cfg.supercell_h
        for row in range(self.rows):
            for col in range(self.cols):
                x = self._supercell_x(col)
                y = row * ch
                if row % 2 == 0:
                    top.add(gdstk.Reference(super_local, origin=(x, y)))
                else:
                    # Y-mirror about cell midline → origin shifts up by ch
                    top.add(gdstk.Reference(
                        super_local,
                        origin=(x, y + ch),
                        x_reflection=True,
                    ))

        # Place tap supercells at every (strap_idx, row).  Same Y-mirror
        # pattern as bitcells so foundry strap M1 rails / NWELL geometry
        # abut symmetrically with adjacent bitcell rows.
        if tap_local is not None:
            for strap_idx in range(self.n_strap_cols):
                x = self._strap_x(strap_idx)
                for row in range(self.rows):
                    y = row * ch
                    if row % 2 == 0:
                        top.add(gdstk.Reference(tap_local, origin=(x, y)))
                    else:
                        top.add(gdstk.Reference(
                            tap_local,
                            origin=(x, y + ch),
                            x_reflection=True,
                        ))

        # Per-row dual WL POLY strips (F11/FT8b mechanism)
        self._add_wl_strips(top)

        # Per-row MWL POLY strip (T7 gate signal)
        self._add_mwl_strips(top)

        # Per-column BL/BR M1 strips
        self._add_bl_br_strips(top)

        # Per-column MBL M4 strips (cap top plate signal)
        self._add_mbl_strips(top)

        # Per-supercell-row NWELL bridges (issue #9 / T4.4-A — body bias).
        # Combined with the supercell's annex-NWELL extension (in
        # sky130_cim_supercell.py), every foundry NWELL fragment merges
        # into one connected plane spanning the array width.  The plane
        # then connects to the macro's peripheral cells (mwl_driver,
        # mbl_precharge, mbl_sense), which carry their own N-taps.
        self._add_nwell_row_bridges(top)

        # Per-row MET2 VPWR/VGND power rails (Step 6 of LVS port-pattern
        # plan) — bridges every supercell instance's foundry M1 power
        # rails through via1+M1 pad stacks so the array's macro-level
        # VPWR/VGND nets electrically merge with all per-instance
        # foundry rails.  CIM's row pitch (supercell_h, 3.23–4.24 µm)
        # does not match the foundry M1 rail Y stride, so production's
        # Y-mirror-shared-rail trick doesn't apply — explicit metal is
        # required.  See conductor cim_lvs_port_pattern_plan.md.
        self._add_vpwr_vgnd_m2_rails(top)

        out_lib.add(top)
        return out_lib

    def _add_nwell_row_bridges(self, top: gdstk.Cell) -> None:
        """Add thin NWELL strips at every supercell-row boundary.

        Foundry NWELL spans foundry-local y=[0, 1.58] and is extended
        through the annex via the supercell's `_NWELL_X0..X1` strip
        at supercell-local y=[1.58, supercell_h].  At every supercell
        row boundary the column-isolated NWELL needs an X-direction
        bridge to merge with adjacent columns.

        IMPORTANT: bridge cells (`sky130_cim_drain_bridge_v1`) sit at
        the bottom of every supercell, with NMOS NSDM-marked DIFF
        spanning supercell-local y=[0, 0.30] x=[0.065, 0.455].  Under
        Y-mirroring of odd rows, these bridge regions end up at the
        ROW-PAIR BOUNDARY positions in the array.  A full-width
        horizontal NWELL strip at the boundary partially covers the
        bridge DIFF — making it look like an N-tap with insufficient
        NWELL enclosure (diff/tap.10) AND, more critically, creating
        a real silicon short between the bridge BL net and VPB rail.

        Avoid this by emitting the boundary strips as PER-COLUMN
        SEGMENTS that skip the bridge NSDM x-range in every column.
        """
        NWELL = (64, 20)
        T = 0.10
        ch = self._cfg.supercell_h
        sw = self._cfg.supercell_w
        x0 = -1.0
        x1 = self.width + 0.20

        # Bridge cell NSDM x-range (in supercell-local coords).  Source:
        # sky130_cim_drain_bridge_v1 NSDM at x=[0.065, 0.455].  These
        # are the columns the boundary NWELL strips must NOT cover.
        BRIDGE_NSDM_W = 0.065   # supercell-local west edge of bridge NSDM
        BRIDGE_NSDM_E = 0.455   # supercell-local east edge

        # Vertical NWELL anchor strip running the full array height at
        # the west edge (no bridge NSDM here; west of column 0).  Width
        # ≥ 0.84 µm to satisfy nwell.1 (the previous 0.50 µm width fired
        # nwell.1 at the macro corners outside SRAM-areaid waiver).
        VERT_X0 = -1.0
        VERT_X1 = -0.16   # width 0.84 = nwell.1 min, leaves 0.16 µm gap to array col 0
        top.add(gdstk.rectangle(
            (VERT_X0, -T), (VERT_X1, self.height + T),
            layer=NWELL[0], datatype=NWELL[1],
        ))

        def _emit_full_strip(y_lo: float, y_hi: float) -> None:
            """Emit a single full-width horizontal NWELL strip."""
            top.add(gdstk.rectangle(
                (x0, y_lo), (x1, y_hi),
                layer=NWELL[0], datatype=NWELL[1],
            ))

        def _emit_segmented_strip(y_lo: float, y_hi: float) -> None:
            """Emit a horizontal NWELL strip with gaps at every BITCELL
            column's bridge NSDM x-range so the strip never overlaps
            bridge DIFF.  Used only for strips that share Y with a
            bridge cell.  Strap columns have NO bridge cell and therefore
            no NSDM gap — the NWELL strip runs through them uninterrupted,
            providing extra cross-column bridging at strap positions.
            """
            seg_x_lo = x0
            for col in range(self.cols):
                col_x_base = self._supercell_x(col)
                gap_lo = col_x_base + BRIDGE_NSDM_W
                gap_hi = col_x_base + BRIDGE_NSDM_E
                if gap_lo > seg_x_lo:
                    top.add(gdstk.rectangle(
                        (seg_x_lo, y_lo), (gap_lo, y_hi),
                        layer=NWELL[0], datatype=NWELL[1],
                    ))
                seg_x_lo = gap_hi
            if x1 > seg_x_lo:
                top.add(gdstk.rectangle(
                    (seg_x_lo, y_lo), (x1, y_hi),
                    layer=NWELL[0], datatype=NWELL[1],
                ))

        # Inter-row strip K (between row K-1 and row K) at y = K*ch ± T/2.
        # Even K strips sit at the boundary where bridge cells of adjacent
        # rows abut (row K-1 odd-mirror has bridge at TOP, row K even has
        # bridge at BOTTOM, both meeting at y=K*ch).  These MUST be
        # segmented to avoid NWELL-over-NSDM short.
        # Odd K strips sit between rows whose bridges are at OPPOSITE
        # ends (row K-1 even has bridge at bottom, row K odd-mirror has
        # bridge at top of row K = K*ch + 2.93 — far from boundary).
        # Odd K strips can be full-width — and MUST be, to provide the
        # cross-column NWELL bridging that the segmented even-K strips
        # cannot.
        for row in range(1, self.rows):
            by = row * ch
            if row % 2 == 0:
                _emit_segmented_strip(by - T / 2, by + T / 2)
            else:
                _emit_full_strip(by - T / 2, by + T / 2)
        # Bottom edge (y=0) overlaps row 0 (even) bridge → segmented.
        # Top edge (y=height) overlaps row 63 (odd-mirror) bridge → segmented.
        _emit_segmented_strip(-T, T / 2)
        _emit_segmented_strip(self.height - T / 2, self.height + T)

    def _add_wl_strips(self, top: gdstk.Cell) -> None:
        """Per-row dual WL POLY strips at wl_top and wl_bot Y.

        Stripped foundry cell's "WL" label is handled inside the supercell
        already; here we just add the parent-level spanning strips and
        per-row labels.

        Y-mirror handling: for odd rows, the supercell is Y-mirrored about
        its midline.  Foundry's WL polys (cell-local y=WL_TOP_Y, WL_BOT_Y)
        end up at array_y = row_y0 + (supercell_h - foundry_local_Y).
        """
        WL_STRIP_W = 0.18  # > poly min 0.15 to ensure overlap
        WL_W_HALF = WL_STRIP_W / 2
        x_left = -0.20
        x_right = self.width + 0.20
        ch = self._cfg.supercell_h
        # POLY layer (66, 20), POLY label (66, 5), POLY pin (66, 16)
        POLY = 66
        POLY_DT = 20
        POLY_LBL_DT = 5
        POLY_PIN_DT = 16

        for row in range(self.rows):
            row_y0 = row * ch
            if row % 2 == 0:
                wl_top_y = row_y0 + _FOUNDRY_WL_TOP_Y
                wl_bot_y = row_y0 + _FOUNDRY_WL_BOT_Y
            else:
                # Y-mirrored row: foundry polys end up at
                # row_y0 + (ch - foundry_local_Y)
                wl_top_y = row_y0 + (ch - _FOUNDRY_WL_TOP_Y)
                wl_bot_y = row_y0 + (ch - _FOUNDRY_WL_BOT_Y)
            for wl_y in (wl_bot_y, wl_top_y):
                # Spanning poly strip
                top.add(gdstk.rectangle(
                    (x_left, wl_y - WL_W_HALF),
                    (x_right, wl_y + WL_W_HALF),
                    layer=POLY, datatype=POLY_DT
                ))
                # Per-row label "wl_0_<row>"
                top.add(gdstk.Label(
                    f"wl_0_{row}", (0.0, wl_y),
                    layer=POLY, texttype=POLY_LBL_DT
                ))
                # Pin shape near label
                top.add(gdstk.rectangle(
                    (x_left, wl_y - WL_W_HALF),
                    (x_left + 0.10, wl_y + WL_W_HALF),
                    layer=POLY, datatype=POLY_PIN_DT
                ))

    def _add_vpwr_vgnd_m2_rails(self, top: gdstk.Cell) -> None:
        """Per-supercell-instance MET1 .pin labels at foundry VPWR/VGND
        rail positions, mirroring production's `_tap_block_power` pattern
        applied per-instance.

        Why: CIM's row pitch (supercell_h, 3.23–4.24 µm) does not match
        the foundry M1 rail Y stride.  Production's bitcell wrappers tile
        at row pitch = foundry strap height (2.22 µm), so adjacent rows'
        foundry M1 VPWR/VGND rails Y-abut through the wlstrap and the
        whole array's foundry M1 rails extract as one VPWR net via
        geometric overlap.  CIM has an annex region (T7+cap, 1.35–2.36 µm
        tall) BETWEEN the strap and the next bitcell row, breaking M1
        abutment.  Without an explicit merging mechanism, every supercell
        instance's foundry VPWR/VGND ports leak as instance-prefixed
        nets at the macro level (the T5.2-A LVS mismatch).

        Mechanism: at every supercell instance position, drop a met1
        `.pin` label "VPWR" at the foundry M1 VPWR rail centerline (and
        a "VGND" label at the VGND rail centerline).  Magic's ext2spice
        merges nets by name across the hierarchy, so every instance's
        foundry VPWR rail picks up the parent label "VPWR" and all
        4096 instances coalesce into one macro VPWR net.  Same for
        VGND.  Same pattern as production's `_tap_block_power(block,
        vpwr_local, vgnd_local)` — just applied per-instance instead of
        per-block (CIM has no production-style row-pitch alignment).

        Y-mirror handling: for odd rows, the foundry rails are at
        `(row+1)*ch - rail_y_local` instead of `row*ch + rail_y_local`.
        """
        M1_LBL = (68, 5)
        M1_PIN = (68, 16)

        # Foundry M1 rail Y centerlines (supercell-local).  Foundry cell
        # has VPWR M1 rail at foundry-local y=[1.495, 1.580]
        # (centerline 1.5375), VGND rail at y=[0, 0.075] (centerline
        # 0.0375).  Supercell-local Y = foundry-local Y + BRIDGE_H.
        VPWR_Y_LOCAL = BRIDGE_H + 1.5375     # 1.8375
        VGND_Y_LOCAL = BRIDGE_H + 0.0375     # 0.3375

        # Foundry M1 rail X positions (supercell-local) — match the
        # canonical port label X positions in sky130_cim_supercell.py.
        VPWR_X_LOCAL = 1.165
        VGND_X_LOCAL = 0.035

        # Pin shape size — small (0.07 half-width = 0.14 wide), inside
        # the foundry M1 rail's X/Y extent so it doesn't introduce DRC.
        PIN_HALF = 0.035

        ch = self._cfg.supercell_h

        # Collect all supercell column X positions (bitcells + taps).
        # Bitcells use sram_sp_cell_opt1; taps use sram_sp_wlstrap.  Both
        # foundry cells have VPWR/VGND M1 rails at the same Y/X positions
        # (foundry cells share the same coordinate convention).
        col_xs: list[float] = []
        for col in range(self.cols):
            col_xs.append(self._supercell_x(col))
        for strap_idx in range(self.n_strap_cols):
            col_xs.append(self._strap_x(strap_idx))

        for row in range(self.rows):
            for net, y_local, x_local in (
                ("VPWR", VPWR_Y_LOCAL, VPWR_X_LOCAL),
                ("VGND", VGND_Y_LOCAL, VGND_X_LOCAL),
            ):
                # Y-mirror: for odd rows, supercell instance origin is
                # (x, (row+1)*ch) with x_reflection=True, so a feature
                # at internal y=Y maps to array y=(row+1)*ch - Y.
                if row % 2 == 0:
                    pin_y = row * ch + y_local
                else:
                    pin_y = (row + 1) * ch - y_local

                # Per-supercell-column met1 .pin label
                for col_x in col_xs:
                    pin_x = col_x + x_local
                    top.add(gdstk.Label(
                        net, (pin_x, pin_y),
                        layer=M1_LBL[0], texttype=M1_LBL[1],
                    ))
                    top.add(gdstk.rectangle(
                        (pin_x - PIN_HALF, pin_y - PIN_HALF),
                        (pin_x + PIN_HALF, pin_y + PIN_HALF),
                        layer=M1_PIN[0], datatype=M1_PIN[1],
                    ))

    def _add_mwl_strips(self, top: gdstk.Cell) -> None:
        """Per-row MWL POLY strip — bridges T7 gates across all cells in row.

        T7 gate is a poly stripe in the annex above foundry cell.  T7's
        gate Y in supercell-local coords: foundry_h + 0.10 + overhang
        (~ 1.68 + 0.36 = 2.04).  For Y-mirrored rows, gate Y flips.
        """
        # T7 gate Y (POLY bottom) in supercell-local coords.  Tracks the
        # supercell builder's `t7_gate_y = t7_y_base + T7_DIFF_OVERHANG`
        # where t7_y_base = BRIDGE_H + 1.58 + 0.10 and T7_DIFF_OVERHANG=0.36.
        T7_GATE_Y_LOCAL = BRIDGE_H + 2.04
        STRIP_W = 0.18
        STRIP_HALF = STRIP_W / 2
        x_left = -0.20
        x_right = self.width + 0.20
        ch = self._cfg.supercell_h
        POLY = 66
        POLY_DT = 20
        POLY_LBL_DT = 5
        POLY_PIN_DT = 16

        for row in range(self.rows):
            row_y0 = row * ch
            if row % 2 == 0:
                mwl_y = row_y0 + T7_GATE_Y_LOCAL
            else:
                mwl_y = row_y0 + (ch - T7_GATE_Y_LOCAL)
            top.add(gdstk.rectangle(
                (x_left, mwl_y - STRIP_HALF),
                (x_right, mwl_y + STRIP_HALF),
                layer=POLY, datatype=POLY_DT
            ))
            top.add(gdstk.Label(
                f"mwl_{row}", (0.0, mwl_y),
                layer=POLY, texttype=POLY_LBL_DT
            ))
            top.add(gdstk.rectangle(
                (x_left, mwl_y - STRIP_HALF),
                (x_left + 0.10, mwl_y + STRIP_HALF),
                layer=POLY, datatype=POLY_PIN_DT
            ))

    def _add_bl_br_strips(self, top: gdstk.Cell) -> None:
        """Per-column BL/BR M1 strips spanning full array height."""
        STRIP_W = 0.18
        STRIP_HALF = STRIP_W / 2
        ch = self._cfg.supercell_h
        y_lo = -0.20
        y_hi = self.height + 0.20
        MET1 = 68
        MET1_DT = 20
        MET1_LBL_DT = 5
        MET1_PIN_DT = 16

        for col in range(self.cols):
            x_base = self._supercell_x(col)
            for x_local, prefix in ((_FOUNDRY_BL_X, "bl"), (_FOUNDRY_BR_X, "br")):
                strip_x = x_base + x_local
                top.add(gdstk.rectangle(
                    (strip_x - STRIP_HALF, y_lo),
                    (strip_x + STRIP_HALF, y_hi),
                    layer=MET1, datatype=MET1_DT
                ))
                top.add(gdstk.Label(
                    f"{prefix}_0_{col}", (strip_x, y_lo + 0.05),
                    layer=MET1, texttype=MET1_LBL_DT
                ))
                top.add(gdstk.rectangle(
                    (strip_x - STRIP_HALF, y_lo),
                    (strip_x + STRIP_HALF, y_lo + 0.10),
                    layer=MET1, datatype=MET1_PIN_DT
                ))

    def _add_mbl_strips(self, top: gdstk.Cell) -> None:
        """Per-column MBL M4 strips spanning full array height."""
        STRIP_W = 0.40
        STRIP_HALF = STRIP_W / 2
        y_lo = -0.20
        y_hi = self.height + 0.20
        MET4 = 71
        MET4_DT = 20
        MET4_LBL_DT = 5
        MET4_PIN_DT = 16
        # MBL position in supercell: cap center X (centered in supercell)
        mbl_x_in_super = self._cfg.supercell_w / 2

        for col in range(self.cols):
            strip_x = self._supercell_x(col) + mbl_x_in_super
            top.add(gdstk.rectangle(
                (strip_x - STRIP_HALF, y_lo),
                (strip_x + STRIP_HALF, y_hi),
                layer=MET4, datatype=MET4_DT
            ))
            top.add(gdstk.Label(
                f"mbl_{col}", (strip_x, y_lo + 0.05),
                layer=MET4, texttype=MET4_LBL_DT
            ))
            top.add(gdstk.rectangle(
                (strip_x - STRIP_HALF, y_lo),
                (strip_x + STRIP_HALF, y_lo + 0.20),
                layer=MET4, datatype=MET4_PIN_DT
            ))

    def array_cell(self, lib: gdstk.Library) -> gdstk.Cell:
        for c in lib.cells:
            if c.name == self.top_cell_name:
                return c
        return lib.cells[0]
