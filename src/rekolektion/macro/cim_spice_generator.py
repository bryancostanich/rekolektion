"""Reference SPICE generator for CIM macros (supercell-based, V2).

Mirrors the v2 SRAM `spice_generator.py` pattern: emits a self-contained
SPICE deck for the CIM macro composed of:
  1. Pre-extracted .subckt definitions for the peripheral cells
     (MWL driver, MBL precharge, MBL sense), pulled from
     `peripherals/cells/extracted_subckt/`.
  2. Foundry 6T cell .subckt (with Q port added by our Q-tap modification),
     emitted from the FT7c-validated foundry extraction (8 transistors).
  3. Per-variant CIM supercell .subckt — wraps foundry 6T + T7 NMOS + MIM cap.
  4. Top-level `.subckt cim_<variant>_<rows>x<cols>` whose body
     instantiates the supercell array, MWL drivers, MBL precharges,
     MBL sense buffers and wires their ports per the placement contract.

Used as the reference netlist for LVS comparison against the GDS-extracted
netlist of the assembled CIM macro.
"""
from __future__ import annotations

from pathlib import Path
from typing import TextIO

from rekolektion.bitcell.sky130_cim_supercell import CIM_SUPERCELL_VARIANTS
from rekolektion.macro.cim_assembler import CIMMacroParams


_EXTRACTED_DIR: Path = (
    Path(__file__).parent.parent / "peripherals/cells/extracted_subckt"
)


def _read_extracted(filename: str) -> list[str]:
    """Read an extracted .subckt.sp file (pre-extracted from layout)."""
    path = _EXTRACTED_DIR / filename
    if not path.exists():
        raise FileNotFoundError(
            f"Missing extracted subckt {path}. Run "
            f"`python3 scripts/extract_cim_subckts.py` first."
        )
    return path.read_text().splitlines()


def _write_extracted(f: TextIO, lines: list[str]) -> None:
    """Emit a pre-extracted subckt body verbatim, with a separator."""
    f.write("\n")
    for ln in lines:
        f.write(ln.rstrip() + "\n")
    f.write("\n")


# Foundry 6T cell extracted topology (FT7c-validated).
# Source: peripherals/cells/extracted_subckt/sky130_fd_bd_sram__sram_sp_cell_opt1.subckt.sp
# We add a Q port to the .subckt line and substitute the storage-node auto-name
# `a_38_212#` (foundry's NMOS PD drain) with `Q` in the body so the Q port is
# actually internally connected — Magic's extraction names this node `a_38_212#`
# even with a Q label because it doesn't auto-substitute, but topology-wise the
# Q label merges with that node.  netgen graph-iso compares connectivity, not
# literal names, so the substitution is safe.
def _write_foundry_qtap(f: TextIO) -> None:
    """Emit the foundry-cell-with-Q-tap .subckt definition.

    Post-Phase-2 silicon view (matches Magic's qtap-standalone extraction):
      - Top access tx drain (was foundry-floating `a_38_292#`) is wired
        to BR inside qtap via the Phase 2 BR contact stack (LICON1+LI1
        +MCON+M1 wrapper added in the supercell builder, inside qtap).
      - Bottom access tx drain (`a_38_0#`) stays floating at qtap level;
        the external `sky130_cim_drain_bridge_v1` cell connects it to
        BL at supercell level via DIFF abutment at the cell boundary.
        Exposed as a qtap port so the supercell can bind it.
    """
    f.write(
        "\n* Foundry 6T cell with Q tap (FT7c-validated topology).\n"
        "* Storage node `Q` exposed via LI1+LICON1 on NMOS PD drain diff.\n"
        "*\n"
        "* Port list mirrors Magic's qtap-standalone extraction.  The\n"
        "* foundry cell's WL label is stripped during qtap construction\n"
        "* (so per-row wl_0_<r> labels at the array level can win Magic's\n"
        "* name resolution) — at the qtap level, the two WL POLY stripes\n"
        "* (top: a_0_262#, bot: a_0_24#) extract as separate auto-named\n"
        "* ports.  The supercell wraps qtap and ties both stripes to a\n"
        "* single WL port via supercell-level WL labels at both stripe Y\n"
        "* positions (cim_supercell.py:483-484).\n"
        "*\n"
        "* Phase 2 BR drain bridge port: li_14_0# is the LI1 stub on the\n"
        "* top access tx drain that connects to BL via the wrapper-level\n"
        "* M1 strap (in supercell), making the supercell-level binding of\n"
        "* this stub to BL the silicon-correct topology.\n"
        ".subckt sky130_fd_bd_sram__sram_sp_cell_opt1_qtap"
        " BL BR VGND VPWR VPB VNB Q li_14_0#"
        " a_38_0# a_0_262# a_0_24#\n"
    )
    # X0: NMOS access transistor (top, BR side).  Drain=BR (via Phase 2
    # in-qtap LICON1+LI1+MCON+M1 stack tying drain DIFF to BR met1 rail),
    # gate=top WL stripe (auto-named a_0_262#), source=Q (storage node).
    f.write(
        "X0 BR a_0_262# Q VNB sky130_fd_pr__nfet_01v8"
        " w=0.14 l=0.15\n"
    )
    # X1: VPWR-side parasitic pfet whose gate is the foundry's bottom
    # POLY stripe (auto-named a_0_24#).  Both stripes are tied to one
    # WL net at the supercell level via parent-level POLY straps and
    # supercell-level WL labels at both stripe Y positions.
    f.write(
        "X1 a_174_54# a_0_24# a_174_54# VPB sky130_fd_pr__pfet_01v8_hvt"
        " w=0.14 l=0.025\n"
    )
    # X2, X3: cross-coupled inverter PMOS pull-ups.
    f.write(
        "X2 a_174_212# a_16_182# a_174_134# VPB sky130_fd_pr__pfet_01v8_hvt"
        " w=0.14 l=0.15\n"
    )
    f.write(
        "X3 a_174_134# a_16_104# a_174_54# VPB sky130_fd_pr__pfet_01v8_hvt"
        " w=0.14 l=0.15\n"
    )
    # X4: WL-side dummy diode-connected pfet (substrate decap).  Gate
    # on top WL stripe (a_0_262#).
    f.write(
        "X4 a_174_212# a_0_262# a_174_212# VPB sky130_fd_pr__pfet_01v8_hvt"
        " w=0.14 l=0.025\n"
    )
    # X5: NMOS pull-down (Q side).  Q is the drain.
    f.write(
        "X5 Q a_16_182# a_0_142# VNB sky130_fd_pr__nfet_01v8"
        " w=0.21 l=0.15\n"
    )
    # X6: NMOS access transistor (bottom, BL side).  Drain=QB
    # (a_38_54#), gate=bottom WL stripe (a_0_24#), source=a_38_0#
    # (stays auto-named at qtap level — Phase 2 BL connection is
    # provided externally by the sky130_cim_drain_bridge_v1 strap cell
    # at supercell level via DIFF abutment).
    f.write(
        "X6 a_38_54# a_0_24# a_38_0# VNB sky130_fd_pr__nfet_01v8"
        " w=0.14 l=0.15\n"
    )
    # X7: NMOS pull-down (QB side).
    f.write(
        "X7 a_0_142# a_16_104# a_38_54# VNB sky130_fd_pr__nfet_01v8"
        " w=0.21 l=0.15\n"
    )
    f.write(".ends\n\n")


def _write_supercell(f: TextIO, p: CIMMacroParams) -> None:
    """Emit the per-variant CIM supercell .subckt definition.

    Topology:
      Foundry 6T (storage)  ──── Q ──── T7 source
                                          │
                                    T7 drain ── cap_bot (MIM bottom plate)
                                          │
                                    cap top plate ── MBL
      T7 gate ── MWL
    """
    cfg = CIM_SUPERCELL_VARIANTS[p.variant]
    super_name = (
        f"sky130_cim_supercell_{p.variant.lower().replace('-', '_')}"
    )
    f.write(
        f"\n* CIM supercell ({p.variant}): foundry 6T + T7 NMOS + MIM cap.\n"
        f".subckt {super_name} BL BR WL MWL MBL VPWR VGND VPB VNB\n"
    )
    # Foundry 6T core.  Port mapping reflects post-Phase-2 silicon:
    #   - qtap's bottom-access-tx-drain port (a_38_0#) is tied to BL via
    #     the external sky130_cim_drain_bridge_v1 strap cell (Magic's
    #     hierarchical extraction inherits supercell BL via DIFF abutment
    #     at the cell boundary).
    #   - qtap's li_14_0# Phase 2 BR drain bridge stub also ties to BL
    #     via the wrapper-level M1 strap.
    #   - qtap's two WL POLY stripes (auto-named a_0_262# top, a_0_24#
    #     bot) both connect to the supercell's WL port — supercell-level
    #     WL labels at both stripe Y positions merge them.
    # Port order matches the qtap subckt: BL BR VGND VPWR VPB VNB Q
    # li_14_0# a_38_0# a_0_262# a_0_24#.
    f.write(
        "Xfoundry BL BR VGND VPWR VPB VNB Q BL BL WL WL "
        "sky130_fd_bd_sram__sram_sp_cell_opt1_qtap\n"
    )
    # T7 NMOS access transistor: gate=MWL, source=Q, drain=cap_bot, body=VNB.
    # Width 0.42 (matches access tx min for diff/tap.2), length 0.15.
    f.write(
        "XT7 cap_bot MWL Q VNB sky130_fd_pr__nfet_01v8 w=0.42 l=0.15\n"
    )
    # MIM cap: top plate=MBL, bottom plate=cap_bot.  Per-variant dimensions.
    f.write(
        f"XCC MBL cap_bot sky130_fd_pr__cap_mim_m3_1"
        f" w={cfg.cap_w} l={cfg.cap_l}\n"
    )
    f.write(f".ends {super_name}\n\n")


def generate_cim_reference_spice(
    p: CIMMacroParams,
    output_path: str | Path,
    *,
    top_subckt_name: str | None = None,
) -> Path:
    """Generate the reference SPICE deck for a CIM macro.

    Parameters
    ----------
    p : CIMMacroParams
        Variant + dimensions.
    output_path : path
        Where to write the .sp file.
    top_subckt_name : str, optional
        Override the top-level subckt name (default ``p.top_cell_name``).
    """
    out = Path(output_path)
    out.parent.mkdir(parents=True, exist_ok=True)

    top_name = top_subckt_name or p.top_cell_name
    super_subckt = (
        f"sky130_cim_supercell_{p.variant.lower().replace('-', '_')}"
    )
    mwl_driver_subckt = "sky130_fd_sc_hd__buf_2"

    with out.open("w") as f:
        f.write(
            f"* CIM reference SPICE for {p.variant} ({p.rows}x{p.cols})\n"
            f"* Generated by rekolektion.macro.cim_spice_generator.\n"
            f"* Supercell-based architecture (foundry 6T + T7 + MIM cap).\n"
        )
        f.write(".global VDD VSS VSUBS VPWR VGND\n\n")

        # Peripheral cell .subckt definitions (extracted from layout).
        _write_extracted(f, _read_extracted(f"{mwl_driver_subckt}.subckt.sp"))
        _write_extracted(f, _read_extracted("cim_mbl_precharge.subckt.sp"))
        _write_extracted(f, _read_extracted("cim_mbl_sense.subckt.sp"))
        # Foundry 6T cell + supercell wrapper.
        _write_foundry_qtap(f)
        _write_supercell(f, p)

        # Top-level subckt port list — matches the macro's external pins
        # as drawn by `cim_assembler.assemble_cim` and the array's per-row /
        # per-col labels (`bl_0_<c>`, `br_0_<c>`, `wl_0_<r>`, `mwl_<r>`,
        # `mbl_<c>`).  Magic's flat extraction promotes any labeled net
        # touching the top-cell boundary into a port.
        ports = (
            [f"MWL_EN[{r}]" for r in range(p.rows)]
            + ["MBL_PRE", "VREF", "VBIAS"]
            + [f"MBL_OUT[{c}]" for c in range(p.cols)]
            + [f"bl_0_{c}" for c in range(p.cols)]
            + [f"br_0_{c}" for c in range(p.cols)]
            + [f"wl_0_{r}" for r in range(p.rows)]
            + [f"mwl_{r}" for r in range(p.rows)]
            + [f"mbl_{c}" for c in range(p.cols)]
            + ["VPWR", "VGND"]
        )
        f.write(f".subckt {top_name}")
        for tok in ports:
            f.write(f" {tok}")
        f.write("\n")

        # ---- Bitcell array instances (one Xbc per [row][col]) ----
        # Each supercell connects:
        #   bl_0_<c>  — column BL          (foundry's BL bitline)
        #   br_0_<c>  — column BR          (foundry's BR bitline)
        #   wl_0_<r>  — row WL             (foundry's WL gate)
        #   mwl_<r>   — row MWL            (T7 gate)
        #   mbl_<c>   — column MBL         (cap top plate)
        #   VPWR / VGND / VPB / VNB        — global supplies + body bias
        f.write(f"* {p.rows} x {p.cols} CIM supercell array\n")
        for r in range(p.rows):
            for c in range(p.cols):
                f.write(
                    f"Xbc_{r}_{c} "
                    f"bl_0_{c} br_0_{c} wl_0_{r} mwl_{r} mbl_{c} "
                    f"VPWR VGND VPWR VGND "
                    f"{super_subckt}\n"
                )
        f.write("\n")

        # ---- MWL drivers (one per row) ----
        # Drives mwl_<r> from MWL_EN[r] input.  Foundry buf_2 ports:
        # A (input), VGND, VNB (p-substrate body bias = VGND), VPB
        # (n-well body bias = VPWR), VPWR, X (output).
        f.write(f"* {p.rows} MWL drivers (foundry buf_2)\n")
        for r in range(p.rows):
            f.write(
                f"Xmwl_{r} MWL_EN[{r}] VGND VGND VPWR VPWR mwl_{r} "
                f"{mwl_driver_subckt}\n"
            )
        f.write("\n")

        # ---- MBL precharges (one per column) ----
        f.write(f"* {p.cols} MBL precharges\n")
        for c in range(p.cols):
            # cim_mbl_precharge port order: MBL_PRE VREF MBL VPWR
            f.write(
                f"Xpre_{c} MBL_PRE VREF mbl_{c} VPWR cim_mbl_precharge\n"
            )
        f.write("\n")

        # ---- MBL sense buffers (one per column) ----
        f.write(f"* {p.cols} MBL sense buffers\n")
        for c in range(p.cols):
            # cim_mbl_sense port order: VBIAS MBL VSS MBL_OUT VDD
            f.write(
                f"Xsense_{c} VBIAS mbl_{c} VGND MBL_OUT[{c}] VPWR "
                f"cim_mbl_sense\n"
            )
        f.write("\n")

        f.write(f".ends {top_name}\n")
    return out
