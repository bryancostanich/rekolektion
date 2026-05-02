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


def _write_array_subckt(f: TextIO, p: CIMMacroParams) -> None:
    """Emit the cim_array intermediate subckt — 4096 supercell instances.

    Mirrors the layout's `cim_array_<variant>_<rows>x<cols>` subckt
    structure (built by `CIMSupercellArray`).  Without this intermediate
    subckt the reference is flat and netgen reports ~90 spurious net
    mismatches when it has to flatten the layout's array to compare
    against a flat reference.
    """
    super_subckt = (
        f"sky130_cim_supercell_{p.variant.lower().replace('-', '_')}"
    )
    array_name = (
        f"cim_array_{p.variant.lower().replace('-', '_')}"
        f"_{p.rows}x{p.cols}"
    )
    # Mirror the layout's array port list: VPWR / VSS (substrate net,
    # auto-named by Magic from the global VSUBS).  The supercell's VNB
    # port connects to VSS (extracted substrate name) inside the array.
    ports = (
        [f"bl_0_{c}" for c in range(p.cols)]
        + [f"br_0_{c}" for c in range(p.cols)]
        + [f"wl_0_{r}" for r in range(p.rows)]
        + [f"mwl_{r}" for r in range(p.rows)]
        + [f"mbl_{c}" for c in range(p.cols)]
        + ["VPWR", "VSS"]
    )
    f.write(f"\n* {p.rows} x {p.cols} CIM supercell array.\n")
    f.write(f".subckt {array_name}")
    for tok in ports:
        f.write(f" {tok}")
    f.write("\n")
    for r in range(p.rows):
        for c in range(p.cols):
            # Supercell port order: BL BR WL MWL MBL VPWR VGND VPB VNB.
            # VGND port → VSS net (Magic-extracted substrate name); VPB
            # port → VPWR net (NWELL body bias = supply); VNB port →
            # VSS net (PSUB body bias = ground).
            f.write(
                f"Xbc_{r}_{c} "
                f"bl_0_{c} br_0_{c} wl_0_{r} mwl_{r} mbl_{c} "
                f"VPWR VSS VPWR VSS "
                f"{super_subckt}\n"
            )
    f.write(f".ends {array_name}\n")


def _write_mwl_driver_col_subckt(f: TextIO, p: CIMMacroParams) -> None:
    """Emit cim_mwl_driver_col_<rows> intermediate subckt.

    Per-row "VPB" / "VNB" labels in the layout's
    cim_mwl_driver_row.py merge all 64 buf_2 instances' body-bias
    nets into single column-level VPB and VNB nets, which then
    propagate as ports of this subckt.
    """
    name = f"cim_mwl_driver_col_{p.rows}"
    ports = (
        [t for r in range(p.rows) for t in (f"MWL_EN[{r}]", f"mwl_{r}")]
        + ["VPWR", "VGND", "VPB", "VNB"]
    )
    f.write(f"\n* {p.rows} MWL drivers (one per row, foundry buf_2).\n")
    f.write(f".subckt {name}")
    for tok in ports:
        f.write(f" {tok}")
    f.write("\n")
    for r in range(p.rows):
        # buf_2 ports: A VGND VNB VPB VPWR X
        f.write(
            f"Xmwl_{r} MWL_EN[{r}] VGND VNB VPB VPWR mwl_{r} "
            f"sky130_fd_sc_hd__buf_2\n"
        )
    f.write(f".ends {name}\n")


def _write_mbl_precharge_row_subckt(f: TextIO, p: CIMMacroParams) -> None:
    """Emit cim_mbl_precharge_row_<cols> intermediate subckt.

    The layout's precharge row exposes per-column MBL connections as
    `MBL[c]` (uppercase, brackets) — these are the pins that connect
    out to the array's `mbl_<c>` nets at the macro top level.  Mirror
    that naming here so netgen's pin-name alignment succeeds.
    """
    name = f"cim_mbl_precharge_row_{p.cols}"
    ports = (
        ["MBL_PRE", "VREF", "VPWR"]
        + [f"MBL[{c}]" for c in range(p.cols)]
    )
    f.write(f"\n* {p.cols} MBL precharges (one per column).\n")
    f.write(f".subckt {name}")
    for tok in ports:
        f.write(f" {tok}")
    f.write("\n")
    for c in range(p.cols):
        # cim_mbl_precharge port order: MBL_PRE VREF MBL VPWR
        f.write(
            f"Xpre_{c} MBL_PRE VREF MBL[{c}] VPWR cim_mbl_precharge\n"
        )
    f.write(f".ends {name}\n")


def _write_mbl_sense_row_subckt(f: TextIO, p: CIMMacroParams) -> None:
    """Emit cim_mbl_sense_row_<cols> intermediate subckt.

    Per-column ports `MBL[c]` and `MBL_OUT[c]` mirror the layout
    extraction's naming convention.
    """
    name = f"cim_mbl_sense_row_{p.cols}"
    ports = (
        ["VBIAS", "VPWR", "VGND"]
        + [t for c in range(p.cols)
             for t in (f"MBL[{c}]", f"MBL_OUT[{c}]")]
    )
    f.write(f"\n* {p.cols} MBL sense buffers (one per column).\n")
    f.write(f".subckt {name}")
    for tok in ports:
        f.write(f" {tok}")
    f.write("\n")
    for c in range(p.cols):
        # cim_mbl_sense port order: VBIAS MBL VSS MBL_OUT VDD
        f.write(
            f"Xsense_{c} VBIAS MBL[{c}] VGND MBL_OUT[{c}] VPWR "
            f"cim_mbl_sense\n"
        )
    f.write(f".ends {name}\n")


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
    array_subckt = (
        f"cim_array_{p.variant.lower().replace('-', '_')}"
        f"_{p.rows}x{p.cols}"
    )
    mwl_subckt = f"cim_mwl_driver_col_{p.rows}"
    pre_subckt = f"cim_mbl_precharge_row_{p.cols}"
    sense_subckt = f"cim_mbl_sense_row_{p.cols}"

    with out.open("w") as f:
        f.write(
            f"* CIM reference SPICE for {p.variant} ({p.rows}x{p.cols})\n"
            f"* Generated by rekolektion.macro.cim_spice_generator.\n"
            f"* Supercell-based architecture (foundry 6T + T7 + MIM cap).\n"
        )
        f.write(".global VDD VSS VSUBS VPWR VGND\n\n")

        # Peripheral cell .subckt definitions (extracted from layout).
        _write_extracted(f, _read_extracted("sky130_fd_sc_hd__buf_2.subckt.sp"))
        _write_extracted(f, _read_extracted("cim_mbl_precharge.subckt.sp"))
        _write_extracted(f, _read_extracted("cim_mbl_sense.subckt.sp"))
        # Foundry 6T cell + supercell wrapper.
        _write_foundry_qtap(f)
        _write_supercell(f, p)
        # Intermediate hierarchical subckts (mirror layout structure).
        _write_array_subckt(f, p)
        _write_mwl_driver_col_subckt(f, p)
        _write_mbl_precharge_row_subckt(f, p)
        _write_mbl_sense_row_subckt(f, p)

        # Top-level subckt port list — matches the macro's external pins
        # as Magic extracts them from the assembled layout.  CIM's macro
        # boundary exposes only the SoC-facing signals: control + supplies
        # + per-column MBL_OUT (sense outputs) + per-column MBL strap.
        # The internal array nets (bl_0_<c>, br_0_<c>, wl_0_<r>, mwl_<r>,
        # mbl_<c>) are wires inside the macro top — they get exposed as
        # ports of the cim_array / cim_mbl_*_row subckts but stay
        # internal at the macro top.
        ports = (
            ["MBL_PRE", "VREF", "VBIAS", "VPWR", "VGND"]
            + [f"MBL_OUT[{c}]" for c in range(p.cols)]
            + [f"MBL_{c}" for c in range(p.cols)]
        )
        f.write(f"\n.subckt {top_name}")
        for tok in ports:
            f.write(f" {tok}")
        f.write("\n")

        # ---- Bitcell array instance ----
        f.write(f"* {p.rows} x {p.cols} CIM supercell array\n")
        array_args = (
            [f"bl_0_{c}" for c in range(p.cols)]
            + [f"br_0_{c}" for c in range(p.cols)]
            + [f"wl_0_{r}" for r in range(p.rows)]
            + [f"mwl_{r}" for r in range(p.rows)]
            + [f"mbl_{c}" for c in range(p.cols)]
            + ["VPWR", "VGND"]
        )
        f.write(f"Xarr {' '.join(array_args)} {array_subckt}\n")

        # ---- MWL driver column instance ----
        f.write(f"* {p.rows} MWL drivers (one per row)\n")
        mwl_args = (
            [t for r in range(p.rows) for t in (f"MWL_EN[{r}]", f"mwl_{r}")]
            # VPB → VPWR and VNB → VGND at the macro top: the column
            # subckt exposes VPB/VNB as ports because its internal
            # buf_2 NWELLs / PSUBs are merged via parent labels into
            # column-level VPB / VNB nets.  At the macro level we tie
            # both back to the global VPWR / VGND rails.
            + ["VPWR", "VGND", "VPWR", "VGND"]
        )
        f.write(f"Xmwl_drivers {' '.join(mwl_args)} {mwl_subckt}\n")

        # ---- MBL precharge row instance ----
        f.write(f"* {p.cols} MBL precharges (one per column)\n")
        pre_args = (
            ["MBL_PRE", "VREF", "VPWR"]
            + [f"mbl_{c}" for c in range(p.cols)]
        )
        f.write(f"Xprecharge {' '.join(pre_args)} {pre_subckt}\n")

        # ---- MBL sense row instance ----
        f.write(f"* {p.cols} MBL sense buffers (one per column)\n")
        sense_args = (
            ["VBIAS", "VPWR", "VGND"]
            + [t for c in range(p.cols)
                 for t in (f"mbl_{c}", f"MBL_OUT[{c}]")]
        )
        f.write(f"Xsense {' '.join(sense_args)} {sense_subckt}\n")

        f.write(f".ends {top_name}\n")
    return out
