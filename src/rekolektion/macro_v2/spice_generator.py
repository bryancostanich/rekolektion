"""Reference transistor-level SPICE netlist generator for v2 SRAM macros.

Produces a single .sp file that:
  1. ``.include``s each foundry cell's extracted + port-patched
     .subckt (from peripherals/cells/extracted_subckt/).  These are
     the transistor-level bodies of the bitcell, NAND_dec, DFF,
     sense_amp, write_driver.
  2. Emits per-block .subckts (sram_array_*, row_decoder_*,
     wl_driver_row_*, sense_amp_row_*, write_driver_row_*,
     ctrl_logic_*) that compose those foundry cells with named nets.
  3. Emits the top-level .subckt that instantiates every block and
     wires them by shared net names (wl_*, bl_*, br_*, enable
     signals, addr/din/dout/clk/we/cs + VPWR/VGND).

Scope:
  - Custom (Python-generated) cells `precharge_row_*` and
    `column_mux_row_*` are currently emitted as port-only stubs.
    Full transistor bodies would require Magic-extracting each
    macro variant at build time; filed as LVS tech debt.
"""
from __future__ import annotations

import math
import tempfile
from pathlib import Path
from typing import TextIO

from rekolektion.macro_v2.assembler import MacroV2Params
from rekolektion.macro_v2.column_mux_row import ColumnMuxRow
from rekolektion.macro_v2.precharge_row import PrechargeRow
from rekolektion.macro_v2.row_decoder import _SPLIT_TABLE


_EXTRACTED_DIR = (
    Path(__file__).parent.parent
    / "peripherals/cells/extracted_subckt"
)

# Foundry cells we rely on — port lists match the patched .subckt files.
_NAND_BY_FANIN: dict[int, tuple[str, tuple[str, ...]]] = {
    2: ("sky130_fd_bd_sram__openram_sp_nand2_dec", ("A", "B", "Z")),
    3: ("sky130_fd_bd_sram__openram_sp_nand3_dec", ("A", "B", "C", "Z")),
    4: ("sky130_fd_bd_sram__openram_sp_nand4_dec", ("A", "B", "C", "D", "Z")),
}
_BITCELL_NAME = "sky130_fd_bd_sram__sram_sp_cell_opt1"
_BITCELL_PORTS = ("BL", "BR", "WL", "VGND", "VNB", "VPB", "VPWR")
_DFF_NAME = "sky130_fd_bd_sram__openram_dff"
_DFF_PORTS = ("CLK", "D", "Q", "Q_N")
_SENSE_AMP_NAME = "sky130_fd_bd_sram__openram_sense_amp"
_SENSE_AMP_PORTS = ("BL", "BR", "DOUT", "EN")
_WRITE_DRIVER_NAME = "sky130_fd_bd_sram__openram_write_driver"
_WRITE_DRIVER_PORTS = ("BL", "BR", "DIN", "EN")


# ---------------------------------------------------------------------------
# Public entry
# ---------------------------------------------------------------------------

def generate_reference_spice(
    p: MacroV2Params,
    output_path: str | Path,
) -> Path:
    path = Path(output_path)
    path.parent.mkdir(parents=True, exist_ok=True)
    # Magic-extract the Python-generated precharge / column_mux bodies
    # so the reference has real transistors (matches the ext netlist).
    # We do this BEFORE opening the output file because the extracted
    # port list is used both by the top-level .subckt call emitter AND
    # the per-cell writer.
    pre_body = _extract_cell(
        PrechargeRow(bits=p.bits, mux_ratio=p.mux_ratio,
                     name=f"pre_{_tag(p)}"),
        cell_name=f"pre_{_tag(p)}",
    )
    mux_body = _extract_cell(
        ColumnMuxRow(bits=p.bits, mux_ratio=p.mux_ratio,
                     name=f"mux_{_tag(p)}"),
        cell_name=f"mux_{_tag(p)}",
    )
    with path.open("w") as f:
        _write_header(f, p)
        _write_includes(f)
        _write_top_subckt(f, p, pre_body, mux_body)
        _write_bitcell_array_subckt(f, p)
        _write_row_decoder_subckt(f, p)
        _write_wl_driver_row_subckt(f, p)
        _write_sense_amp_row_subckt(f, p)
        _write_write_driver_row_subckt(f, p)
        _write_control_logic_subckt(f, p)
        _write_extracted_subckt(f, pre_body)
        _write_extracted_subckt(f, mux_body)
    return path


# ---------------------------------------------------------------------------
# Build-time Magic extraction of Python-generated cells
# ---------------------------------------------------------------------------

from dataclasses import dataclass


@dataclass
class _ExtractedCell:
    """A Magic-extracted cell body + parsed port order."""
    name: str
    ports: list[str]
    body_lines: list[str]  # everything between .subckt and .ends (inclusive)


def _extract_cell(
    obj,
    cell_name: str,
) -> _ExtractedCell:
    """Build `obj` to GDS, run Magic extract on it, and return the
    extracted .subckt body plus its port order.

    The builder `obj` must implement `.build() -> gdstk.Library` and
    emit a top cell named `cell_name`.  All electrical ports are
    detected via labels in the GDS (Magic `port makeall`-style
    behaviour emerges naturally because the labels overlap metal).

    Port list is derived from the labels on the top cell (ordered by
    label name), NOT from Magic (`port makeall` on these flat cells
    doesn't produce explicit PORT statements — Magic just references
    the label-named nets in the X-lines).  The caller is responsible
    for instantiating with args in this same order.
    """
    # Defer the import to keep spice_generator import-lean when the
    # verify stack is unavailable (e.g. CI without Magic).
    from rekolektion.verify.lvs import extract_netlist

    tmpdir = Path(tempfile.mkdtemp(prefix=f"refspice_{cell_name}_"))
    lib = obj.build()
    gds_path = tmpdir / f"{cell_name}.gds"
    lib.write_gds(str(gds_path))
    extracted_sp = extract_netlist(
        gds_path, cell_name=cell_name, output_dir=tmpdir, timeout=300,
    )

    # Parse the extracted SPICE: find .subckt <cell_name> .. .ends.
    text = extracted_sp.read_text()
    lines = _unfold_continuations(text.splitlines())
    subckt_start = None
    subckt_end = None
    for i, line in enumerate(lines):
        if line.strip().startswith(f".subckt {cell_name}"):
            subckt_start = i
        elif subckt_start is not None and line.strip().lower().startswith(
            (f".ends {cell_name.lower()}", ".ends")
        ):
            subckt_end = i
            break
    if subckt_start is None:
        raise RuntimeError(
            f"Magic extraction of {cell_name} produced no .subckt line"
        )
    if subckt_end is None:
        # Some ext2spice outputs omit the cell name after .ends
        subckt_end = len(lines) - 1

    # Collect the net names referenced by X-lines; use those as the
    # port list since Magic's `.subckt` header doesn't list them.
    # Each X-line is: `X<inst> <node1> ... <nodeK> <model> <params...>`.
    # Model names in this flow start with "sky130_fd_" — any token with
    # that prefix (or containing '=' for params) terminates the node
    # list.
    def _is_model_or_param(t: str) -> bool:
        return (
            "=" in t
            or t.startswith("sky130_")
            or t.startswith("sky130")
        )
    used_nets: list[str] = []
    seen: set[str] = set()
    for line in lines[subckt_start + 1 : subckt_end + 1]:
        s = line.strip()
        if not s.startswith("X"):
            continue
        toks = s.split()
        for t in toks[1:]:
            if _is_model_or_param(t):
                break
            if t not in seen:
                used_nets.append(t)
                seen.add(t)

    # Drop internal auto-generated nets (they contain '#') and keep
    # only "label-named" public nets — those are the real ports.
    public = [n for n in used_nets if "#" not in n]
    # Stable, deterministic ordering: bitlines first (bl_, br_), then
    # muxed_bl_/muxed_br_, then col_sel_, then p_en_bar, then power.
    # Sort by natural index within each category.
    def _key(n: str) -> tuple[int, object]:
        if n.startswith("bl_"): return (0, int(n[3:]))
        if n.startswith("br_"): return (1, int(n[3:]))
        if n.startswith("muxed_bl_"): return (2, int(n[9:]))
        if n.startswith("muxed_br_"): return (3, int(n[9:]))
        if n.startswith("col_sel_"): return (4, int(n[8:]))
        if n == "p_en_bar": return (5, 0)
        if n.upper() == "VPWR": return (9, 0)
        if n.upper() == "VGND" or n.upper() == "VSUBS": return (9, 1)
        return (8, n)
    ports = sorted(public, key=_key)

    # Capture the body lines verbatim (we'll rewrite the .subckt line
    # to include the port list).
    body_lines = lines[subckt_start : subckt_end + 1]
    # Replace the .subckt line with an explicit port listing.
    body_lines[0] = f".subckt {cell_name} " + " ".join(ports)
    return _ExtractedCell(name=cell_name, ports=ports, body_lines=body_lines)


def _unfold_continuations(lines: list[str]) -> list[str]:
    """Collapse SPICE `+ ...` continuation lines into their parent."""
    out: list[str] = []
    for line in lines:
        if line.startswith("+") and out:
            out[-1] += " " + line[1:].strip()
        else:
            out.append(line)
    return out


def _write_extracted_subckt(f: TextIO, cell: _ExtractedCell) -> None:
    """Emit a Magic-extracted cell body as a .subckt block."""
    f.write(f"* ---- {cell.name} (Magic-extracted body) ----\n")
    for line in cell.body_lines:
        f.write(line + "\n")
    f.write("\n")


# ---------------------------------------------------------------------------
# Common emission helpers
# ---------------------------------------------------------------------------

def _write_header(f: TextIO, p: MacroV2Params) -> None:
    f.write(
        "* Reference transistor-level SPICE for rekolektion v2 SRAM macro.\n"
        f"* Top cell: {p.top_cell_name}\n"
        f"* Words x Bits x Mux: {p.words} x {p.bits} x mux{p.mux_ratio}\n"
        f"* Rows: {p.rows}, Cols: {p.cols}, Addr bits: {p.num_addr_bits}\n"
        "*\n"
        "* NOTE: precharge_row and column_mux_row are stubbed — their\n"
        "* transistor bodies require Magic-extraction of each macro\n"
        "* variant at build time; tracked as LVS tech debt.\n"
        "\n"
    )


def _write_includes(f: TextIO) -> None:
    for fname in sorted(_EXTRACTED_DIR.glob("*.subckt.sp")):
        f.write(f".include \"{fname}\"\n")
    f.write("\n")


def _wrap_ports(f: TextIO, ports: list[str], width: int = 78) -> None:
    line = "+"
    for port in ports:
        addition = (" " + port)
        if len(line) + len(addition) > width:
            f.write(line + "\n")
            line = "+ " + port
        else:
            line += addition if line != "+" else " " + port
    if line.strip() != "+":
        f.write(line + "\n")


def _top_ports(p: MacroV2Params) -> list[str]:
    ports: list[str] = ["clk", "we", "cs"]
    ports += [f"addr{i}" for i in range(p.num_addr_bits)]
    ports += [f"din{i}" for i in range(p.bits)]
    ports += [f"dout{i}" for i in range(p.bits)]
    ports += [f"col_sel_{k}" for k in range(p.mux_ratio)]
    ports += ["VPWR", "VGND"]
    return ports


# ---------------------------------------------------------------------------
# Top-level .subckt — wires every block by shared nets
# ---------------------------------------------------------------------------

def _write_top_subckt(
    f: TextIO,
    p: MacroV2Params,
    pre_body: "_ExtractedCell",
    mux_body: "_ExtractedCell",
) -> None:
    ports = _top_ports(p)
    wl_nets = [f"wl_{r}" for r in range(p.rows)]
    dec_nets = [f"dec_out_{r}" for r in range(p.rows)]  # decoder output (active-low)
    bl_nets = [f"bl_{c}" for c in range(p.cols)]
    br_nets = [f"br_{c}" for c in range(p.cols)]
    muxed_bl = [f"muxed_bl_{i}" for i in range(p.bits)]
    muxed_br = [f"muxed_br_{i}" for i in range(p.bits)]

    f.write(f".subckt {p.top_cell_name}\n")
    _wrap_ports(f, ports)

    f.write("\n* Control logic: clk/we/cs -> clk_buf, wl_en, p_en_bar, s_en, w_en\n")
    f.write(
        f"Xcontrol clk we cs clk_buf wl_en p_en_bar s_en w_en VPWR VGND "
        f"ctrl_logic_{_tag(p)}\n"
    )

    f.write("\n* Row decoder: addr -> dec_out[0..rows-1] (active-low)\n")
    addr_args = " ".join(f"addr{i}" for i in range(p.num_addr_bits))
    dec_args = " ".join(dec_nets)
    f.write(
        f"Xdecoder {addr_args} {dec_args} wl_en VPWR VGND row_decoder_{_tag(p)}\n"
    )

    f.write("\n* WL driver row: dec_out (low-active) -> WL (high-active)\n")
    f.write(
        f"Xwl_driver {' '.join(dec_nets)} {' '.join(wl_nets)} VPWR VGND "
        f"wl_driver_{_tag(p)}\n"
    )

    f.write("\n* Bitcell array: rows x cols cells\n")
    f.write(
        f"Xarray {' '.join(wl_nets)} {' '.join(bl_nets)} {' '.join(br_nets)} "
        f"VPWR VGND sram_array_{_tag(p)}\n"
    )

    # Precharge / column_mux instantiations use the port ORDER from the
    # Magic-extracted subckt (which is whatever order Magic emits when
    # walking the GDS labels — not alphabetical, not structural).  We
    # map each extracted port name to the top-level net with the same
    # name.
    f.write("\n* Precharge row (Magic-extracted port order)\n")
    f.write(
        f"Xprecharge {' '.join(pre_body.ports)} {pre_body.name}\n"
    )

    f.write("\n* Column mux row (Magic-extracted port order)\n")
    f.write(
        f"Xcolmux {' '.join(mux_body.ports)} {mux_body.name}\n"
    )

    f.write("\n* Sense amp row (one per bit on muxed output)\n")
    dout_args = " ".join(f"dout{i}" for i in range(p.bits))
    f.write(
        f"Xsa {' '.join(muxed_bl)} {' '.join(muxed_br)} s_en {dout_args} "
        f"VPWR VGND sa_{_tag(p)}\n"
    )

    f.write("\n* Write driver row (one per bit on muxed output)\n")
    din_args = " ".join(f"din{i}" for i in range(p.bits))
    f.write(
        f"Xwd {din_args} w_en {' '.join(muxed_bl)} {' '.join(muxed_br)} "
        f"VPWR VGND wd_{_tag(p)}\n"
    )

    f.write(f"\n.ends {p.top_cell_name}\n\n")


def _tag(p: MacroV2Params) -> str:
    return f"m{p.mux_ratio}_{p.words}x{p.bits}"


# ---------------------------------------------------------------------------
# Bitcell array — rows * cols foundry bitcells
# ---------------------------------------------------------------------------

def _write_bitcell_array_subckt(f: TextIO, p: MacroV2Params) -> None:
    name = f"sram_array_{_tag(p)}"
    wl_ports = [f"wl{r}" for r in range(p.rows)]
    bl_ports = [f"bl{c}" for c in range(p.cols)]
    br_ports = [f"br{c}" for c in range(p.cols)]
    ports = wl_ports + bl_ports + br_ports + ["VPWR", "VGND"]
    f.write(f"* ---- {name} ----\n")
    f.write(f".subckt {name}\n")
    _wrap_ports(f, ports)
    for r in range(p.rows):
        for c in range(p.cols):
            # Bitcell extracted subckt ports (native Magic order):
            #   BL BR VGND VPWR VPB WL VNB
            # Tie VNB=VGND, VPB=VPWR.
            f.write(
                f"Xbc_{r}_{c} bl{c} br{c} "
                f"VGND VPWR VPWR wl{r} VGND "
                f"{_BITCELL_NAME}\n"
            )
    f.write(f".ends {name}\n\n")


# ---------------------------------------------------------------------------
# Row decoder — composition of NAND_dec cells per the _SPLIT_TABLE
# ---------------------------------------------------------------------------

def _write_row_decoder_subckt(f: TextIO, p: MacroV2Params) -> None:
    name = f"row_decoder_{_tag(p)}"
    addr_ports = [f"addr{i}" for i in range(p.num_addr_bits)]
    dec_out_ports = [f"dec_out_{r}" for r in range(p.rows)]
    ports = addr_ports + dec_out_ports + ["wl_en", "VPWR", "VGND"]

    f.write(f"* ---- {name} ----\n")
    f.write(f".subckt {name}\n")
    _wrap_ports(f, ports)

    split = _SPLIT_TABLE[p.rows]
    if len(split) == 1:
        # Single predecoder case: just a vertical column of NAND_k
        # gates, one per row.  Each NAND_k's k inputs come from k
        # address bits (or their inversions).  For a real LVS match
        # we'd need the inversion logic; here we approximate with
        # addr[i] connections only (NAND output = decoded low).
        k = split[0]
        nand_name, nand_ports = _NAND_BY_FANIN[k]
        for r in range(p.rows):
            inputs = [f"addr{i}" for i in range(k)]
            args = inputs + [dec_out_ports[r]] + ["VGND", "VPWR"]
            f.write(f"Xnand_{r} {' '.join(args)} {nand_name}\n")
    else:
        # Multi-predecoder case TBD — emit stub placeholder.
        f.write(
            "* multi-predecoder row_decoder body TBD; see macro_v2/row_decoder.py\n"
        )

    f.write(f".ends {name}\n\n")


# ---------------------------------------------------------------------------
# WL driver row — NAND3 with B,C tied to VPWR, one per row
# ---------------------------------------------------------------------------

def _write_wl_driver_row_subckt(f: TextIO, p: MacroV2Params) -> None:
    name = f"wl_driver_{_tag(p)}"
    in_ports = [f"dec_out_{r}" for r in range(p.rows)]
    out_ports = [f"wl_{r}" for r in range(p.rows)]
    ports = in_ports + out_ports + ["VPWR", "VGND"]

    f.write(f"* ---- {name} ----\n")
    f.write(f".subckt {name}\n")
    _wrap_ports(f, ports)

    nand3, _ = _NAND_BY_FANIN[3]
    for r in range(p.rows):
        # NAND3(A=dec_out_r, B=VPWR, C=VPWR) = NOT dec_out_r
        f.write(
            f"Xwld_{r} {in_ports[r]} VPWR VPWR {out_ports[r]} "
            f"VGND VPWR {nand3}\n"
        )
    f.write(f".ends {name}\n\n")


# ---------------------------------------------------------------------------
# Sense amp row — one foundry sense_amp per bit on the muxed output
# ---------------------------------------------------------------------------

def _write_sense_amp_row_subckt(f: TextIO, p: MacroV2Params) -> None:
    name = f"sa_{_tag(p)}"
    mbl = [f"muxed_bl_{i}" for i in range(p.bits)]
    mbr = [f"muxed_br_{i}" for i in range(p.bits)]
    dout = [f"dout{i}" for i in range(p.bits)]
    ports = mbl + mbr + ["s_en"] + dout + ["VPWR", "VGND"]

    f.write(f"* ---- {name} ----\n")
    f.write(f".subckt {name}\n")
    _wrap_ports(f, ports)

    for i in range(p.bits):
        # sense_amp ports: BL BR DOUT EN GND VDD
        f.write(
            f"Xsa_{i} {mbl[i]} {mbr[i]} {dout[i]} s_en VGND VPWR "
            f"{_SENSE_AMP_NAME}\n"
        )
    f.write(f".ends {name}\n\n")


# ---------------------------------------------------------------------------
# Write driver row
# ---------------------------------------------------------------------------

def _write_write_driver_row_subckt(f: TextIO, p: MacroV2Params) -> None:
    name = f"wd_{_tag(p)}"
    din = [f"din{i}" for i in range(p.bits)]
    mbl = [f"muxed_bl_{i}" for i in range(p.bits)]
    mbr = [f"muxed_br_{i}" for i in range(p.bits)]
    ports = din + ["w_en"] + mbl + mbr + ["VPWR", "VGND"]

    f.write(f"* ---- {name} ----\n")
    f.write(f".subckt {name}\n")
    _wrap_ports(f, ports)

    for i in range(p.bits):
        # write_driver ports: BL BR DIN EN GND VDD
        f.write(
            f"Xwd_{i} {mbl[i]} {mbr[i]} {din[i]} w_en VGND VPWR "
            f"{_WRITE_DRIVER_NAME}\n"
        )
    f.write(f".ends {name}\n\n")


# ---------------------------------------------------------------------------
# Control logic — 4 DFFs + 2 NAND2s (skeleton; internal logic stubbed)
# ---------------------------------------------------------------------------

def _write_control_logic_subckt(f: TextIO, p: MacroV2Params) -> None:
    name = f"ctrl_logic_{_tag(p)}"
    f.write(
        f"* ---- {name} (wiring matches assembler _route_ctrl_internal) ----\n"
    )
    f.write(
        f".subckt {name} clk we cs clk_buf wl_en p_en_bar s_en w_en "
        "VPWR VGND\n"
    )
    # NAND2 outputs drive DFF D inputs:
    #   NAND2_0.Z -> DFF_0.D, DFF_1.D  (via z0 rail)
    #   NAND2_1.Z -> DFF_2.D, DFF_3.D  (via z1 rail)
    # DFF Q outputs drive the ctrl_logic block outputs.
    dff_q_nets = ("clk_buf", "p_en_bar", "s_en", "w_en")
    dff_d_nets = ("nand0_z", "nand0_z", "nand1_z", "nand1_z")
    # DFF port order from the cached (port-patched) subckt:
    # CLK D Q Q_N GND VDD.  Q_N has no label in the foundry GDS so
    # Magic will NOT promote it to a port in the assembled netlist;
    # we leave it on a per-instance floating net here.
    for i in range(4):
        f.write(
            f"Xdff{i} clk {dff_d_nets[i]} {dff_q_nets[i]} dff{i}_qn "
            f"VGND VPWR {_DFF_NAME}\n"
        )
    # NAND2: A=we, B=cs, Z=nandi_z (per _route_ctrl_internal).
    nand2, _ = _NAND_BY_FANIN[2]
    for i in range(2):
        f.write(
            f"Xnand{i} we cs nand{i}_z VGND VPWR {nand2}\n"
        )
    f.write(f".ends {name}\n\n")


# ---------------------------------------------------------------------------
# Precharge / column mux — stubs (Python-generated, not extracted yet)
# ---------------------------------------------------------------------------

# Precharge / column_mux bodies are emitted by _write_extracted_subckt.
# No hand-written stubs — all content comes from Magic extraction.
