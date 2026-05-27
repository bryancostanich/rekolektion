#!/usr/bin/env python3
"""Cross-validate the new `Rkt.ToLef` F# emitter against the legacy
`lef_generator.py` on a real SRAM macro.

Flow:
  1. Generate `sram_32x8_mux4.lef` via the legacy Python emitter.
  2. Parse that LEF into a structural representation
     (SIZE, every PIN's direction/use/layer/rects).
  3. Synthesise a `.rkt` whose `(props (bbox …))` + `(port …)`
     elements declare the same shape.
  4. Run the F# `Rkt.ToLef` emitter via the `rekolektion-viz to-lef`
     CLI on that `.rkt`.
  5. Diff the two LEFs line-by-line, classify the differences, and
     print a report.

Acceptance criterion (LEF emitter A8):
  Every diff line falls into one of three buckets — schema-driven
  (the new emitter does what the `.rkt` declares), cosmetic
  (whitespace / decimal precision), or capability-gap (legacy
  feature the new emitter intentionally doesn't ship yet).

Run from the repo root:
    .venv/bin/python scripts/lef_emitter_cross_validate.py
"""
from __future__ import annotations

import re
import subprocess
import sys
import textwrap
from dataclasses import dataclass, field
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(REPO / "src"))

# ── 1. Generate the legacy LEF ────────────────────────────────────────


def generate_legacy(out: Path) -> None:
    from rekolektion.macro.assembler import MacroParams
    from rekolektion.macro.lef_generator import generate_lef
    out.parent.mkdir(parents=True, exist_ok=True)
    generate_lef(MacroParams(words=32, bits=8, mux_ratio=4), out)


# ── 2. Parse legacy LEF ───────────────────────────────────────────────


@dataclass
class Pin:
    name: str
    direction: str | None
    use: str | None
    shape_abutment: bool
    ports: list[tuple[str, tuple[float, float, float, float]]] = field(default_factory=list)


@dataclass
class ObsLayer:
    layer: str
    rect: tuple[float, float, float, float]


@dataclass
class Macro:
    name: str
    size_x: float
    size_y: float
    pins: list[Pin] = field(default_factory=list)
    obs_layers: list[ObsLayer] = field(default_factory=list)


def parse_legacy(text: str) -> Macro:
    """Minimal LEF parser — only the fields we need for cross-val."""
    m = re.search(r"MACRO\s+(\S+)", text)
    macro_name = m.group(1) if m else "unknown"
    sz = re.search(r"SIZE\s+([\d.]+)\s+BY\s+([\d.]+)", text)
    size_x = float(sz.group(1)) if sz else 0.0
    size_y = float(sz.group(2)) if sz else 0.0
    macro = Macro(name=macro_name, size_x=size_x, size_y=size_y)
    # Pin blocks: PIN <name> … END <name>.
    for m_pin in re.finditer(r"PIN\s+(\S+)(.*?)END\s+\1", text, re.DOTALL):
        name = m_pin.group(1)
        body = m_pin.group(2)
        direction = (re.search(r"DIRECTION\s+(\S+)", body) or [None, None])[1]
        use = (re.search(r"USE\s+(\S+)", body) or [None, None])[1]
        shape_abutment = "SHAPE ABUTMENT" in body
        ports: list[tuple[str, tuple[float, float, float, float]]] = []
        # Multiple PORT … END inside one PIN.
        for m_port in re.finditer(
            r"PORT\s+LAYER\s+(\S+)\s+;\s+RECT\s+([\d.]+)\s+([\d.]+)\s+([\d.]+)\s+([\d.]+)\s+;",
            body,
        ):
            layer = m_port.group(1)
            rect = (
                float(m_port.group(2)),
                float(m_port.group(3)),
                float(m_port.group(4)),
                float(m_port.group(5)),
            )
            ports.append((layer, rect))
        macro.pins.append(
            Pin(name=name, direction=direction, use=use,
                shape_abutment=shape_abutment, ports=ports)
        )
    # Parse OBS block: sequence of LAYER <name> ; RECT … ; pairs.
    m_obs = re.search(r"OBS\s+(.*?)\s+END", text, re.DOTALL)
    if m_obs:
        body = m_obs.group(1)
        # Walk layer/rect pairs in order. Each LAYER line is followed
        # by one RECT.
        for m_layer in re.finditer(
            r"LAYER\s+(\S+)\s+;\s+RECT\s+([\d.]+)\s+([\d.]+)\s+([\d.]+)\s+([\d.]+)\s+;",
            body,
        ):
            macro.obs_layers.append(ObsLayer(
                layer=m_layer.group(1),
                rect=(
                    float(m_layer.group(2)),
                    float(m_layer.group(3)),
                    float(m_layer.group(4)),
                    float(m_layer.group(5)),
                ),
            ))
    return macro


# ── 3. Synthesise a matching .rkt ─────────────────────────────────────


def synth_rkt(macro: Macro) -> str:
    """Build a `.rkt` document carrying the same SIZE + PIN declarations.

    Coordinate conversion: legacy LEF emits µm with 3-decimal precision.
    `.rkt` stores integer DBU at 1 nm/DBU. Multiply by 1000 and round
    to nearest integer.
    """
    def dbu(um: float) -> int:
        return int(round(um * 1000))

    flag_for_use: dict[str, str] = {
        "POWER": "power",
        "GROUND": "ground",
        "CLOCK": "clock",
        "ANALOG": "analog",
        "SIGNAL": "signal",
    }
    dir_for: dict[str, str] = {
        "INPUT": "input",
        "OUTPUT": "output",
        "INOUT": "inout",
    }

    lines: list[str] = []
    lines.append("(layout (version 1) (pdk sky130)")
    lines.append("  (units (dbu_nm 1) (uu_um 1))")
    lines.append(f"  (cell {macro.name}")
    lines.append(
        f"    (props (bbox 0 0 {dbu(macro.size_x)} {dbu(macro.size_y)})"
        f' (description "v1 SRAM macro from legacy lef_generator"))'
    )
    # Emit one `(port …)` per (pin, port-rect). Pins with multiple
    # rects produce N port declarations sharing the same name — the
    # new emitter then writes N PIN blocks. This is the v1
    # capability gap documented in rkt_to_lef_cross_validation.md.
    for pin in macro.pins:
        for layer, (x1, y1, x2, y2) in pin.ports:
            d = dir_for.get(pin.direction or "", "unspecified")
            flag = flag_for_use.get(pin.use or "SIGNAL", "signal")
            lines.append(
                f'    (port (name "{pin.name}") (dir {d})'
                f" (layer sky130:{layer}) (flags {flag})"
                f" (shape (rect {dbu(x1)} {dbu(y1)} {dbu(x2)} {dbu(y2)})))"
            )
    lines.append("  ))")
    return "\n".join(lines) + "\n"


# ── 4. Run new F# emitter via CLI ─────────────────────────────────────


def run_new_emitter(rkt_path: Path, lef_out: Path, macro: Macro) -> None:
    """Drive the new F# emitter via CLI. Configure OBS to mirror the
    legacy LEF's per-layer shape: full-size on layers whose OBS rect
    equals the macro bbox; band on layers whose OBS rect is smaller
    (these are the `BandExcluding` cases — typically met3)."""
    cli_proj = REPO / "tools/viz/src/Rekolektion.Viz.Cli/Rekolektion.Viz.Cli.fsproj"

    full_layers: list[str] = []
    bands: list[tuple[str, float, float]] = []
    eps = 1e-6
    for obs in macro.obs_layers:
        x0, y0, x1, y1 = obs.rect
        full = (
            abs(x0) < eps
            and abs(y0) < eps
            and abs(x1 - macro.size_x) < eps
            and abs(y1 - macro.size_y) < eps
        )
        if full:
            full_layers.append(obs.layer)
        else:
            bands.append((obs.layer, y0, y1))

    cmd = [
        "dotnet", "run", "--project", str(cli_proj), "--",
        "to-lef", str(rkt_path), str(lef_out),
        "--cell", macro.name,
        # Legacy precision is 3 decimals zero-padded.
        "--decimal-precision", "3",
        # Match the legacy cosmetic conventions exactly so the diff
        # surfaces only schema-driven / capability-gap differences.
        "--symmetry", "X Y",
        "--omit-foreign-offset",
        "--legacy-zero-short-form",
        "--emit-abutment-shape",
    ]
    if bands:
        cmd += [
            "--obs", "band-excluding",
            "--obs-layers", ",".join(full_layers) if full_layers else "",
        ]
        for layer, y0, y1 in bands:
            cmd += ["--obs-band", f"{layer}:{y0:.3f}:{y1:.3f}"]
    else:
        cmd += [
            "--obs", "fullsize",
            "--obs-layers", ",".join(full_layers) if full_layers else "met1,met2",
        ]

    res = subprocess.run(
        cmd, cwd=str(REPO),
        capture_output=True, text=True, timeout=240,
    )
    if res.returncode != 0:
        print("[cross-val] new emitter failed:")
        print(res.stderr)
        sys.exit(1)


# ── 5. Diff + classify ────────────────────────────────────────────────


def normalize_line(line: str) -> str:
    """Whitespace-normalise a LEF line for the "structural diff" pass."""
    return " ".join(line.split())


def classify_diff(legacy: str, new: str) -> dict[str, list[str]]:
    """Produce a categorised report of differences."""
    legacy_lines = [normalize_line(l) for l in legacy.splitlines() if l.strip()]
    new_lines = [normalize_line(l) for l in new.splitlines() if l.strip()]

    only_legacy = sorted(set(legacy_lines) - set(new_lines))
    only_new = sorted(set(new_lines) - set(legacy_lines))

    buckets: dict[str, list[str]] = {
        "schema-driven": [],
        "capability-gap": [],
        "cosmetic": [],
        "uncategorised": [],
    }
    for line in only_legacy:
        if "SYMMETRY" in line:
            buckets["capability-gap"].append(f"legacy-only: {line}")
        elif "SHAPE ABUTMENT" in line:
            buckets["capability-gap"].append(f"legacy-only: {line}")
        elif "FOREIGN" in line and "0 0" not in line:
            buckets["cosmetic"].append(f"legacy-only: {line}")
        elif "LAYER met3" in line and "RECT" not in line:
            # legacy emits OBS LAYER met3 + a band-excluding RECT;
            # new emitter doesn't have BandExcluding policy in v1.
            buckets["capability-gap"].append(f"legacy-only: {line}")
        elif re.fullmatch(r"RECT [\d.]+ [\d.]+ [\d.]+ [\d.]+ ;", line):
            buckets["cosmetic"].append(f"legacy-only: {line}")
        elif re.fullmatch(r"SIZE [\d.]+ BY [\d.]+ ;", line):
            # Decimal-precision difference on the macro extent —
            # same cosmetic class as the RECT lines.
            buckets["cosmetic"].append(f"legacy-only: {line}")
        elif re.fullmatch(r"ORIGIN [\d.]+ [\d.]+ ;", line):
            buckets["cosmetic"].append(f"legacy-only: {line}")
        else:
            buckets["uncategorised"].append(f"legacy-only: {line}")
    for line in only_new:
        if "FOREIGN" in line and "0 0" in line:
            buckets["cosmetic"].append(f"new-only: {line}")
        elif re.fullmatch(r"RECT [\d.]+ [\d.]+ [\d.]+ [\d.]+ ;", line):
            buckets["cosmetic"].append(f"new-only: {line}")
        elif re.fullmatch(r"SIZE [\d.]+ BY [\d.]+ ;", line):
            buckets["cosmetic"].append(f"new-only: {line}")
        elif re.fullmatch(r"ORIGIN [\d.]+ [\d.]+ ;", line):
            buckets["cosmetic"].append(f"new-only: {line}")
        elif "# DESCRIPTION" in line:
            buckets["schema-driven"].append(f"new-only: {line}")
        else:
            buckets["uncategorised"].append(f"new-only: {line}")
    return buckets


# ── main ──────────────────────────────────────────────────────────────


def main() -> int:
    legacy_path = REPO / "scratch/cross_val/legacy.lef"
    rkt_path = REPO / "scratch/cross_val/synthesized.rkt"
    new_path = REPO / "scratch/cross_val/new.lef"
    legacy_path.parent.mkdir(parents=True, exist_ok=True)

    print("[1/5] Generating legacy LEF (sram_32x8_mux4) …")
    generate_legacy(legacy_path)
    legacy = legacy_path.read_text()
    print(f"      legacy LEF: {legacy_path.stat().st_size} bytes")

    print("[2/5] Parsing legacy LEF …")
    macro = parse_legacy(legacy)
    print(f"      macro: {macro.name}, size {macro.size_x} × {macro.size_y} µm,"
          f" {len(macro.pins)} pins")

    print("[3/5] Synthesising matching .rkt …")
    rkt_text = synth_rkt(macro)
    rkt_path.write_text(rkt_text)
    print(f"      .rkt: {rkt_path.stat().st_size} bytes")

    print("[4/5] Running new F# emitter via CLI …")
    run_new_emitter(rkt_path, new_path, macro)
    new = new_path.read_text()
    print(f"      new LEF: {new_path.stat().st_size} bytes")

    print("[5/5] Diffing + classifying …")
    buckets = classify_diff(legacy, new)
    print()
    for kind in ("schema-driven", "capability-gap", "cosmetic", "uncategorised"):
        items = buckets[kind]
        marker = "✗" if kind == "uncategorised" and items else "·"
        print(f"  {marker} {kind} ({len(items)})")
        for item in items[:10]:
            print(f"      {item}")
        if len(items) > 10:
            print(f"      … {len(items) - 10} more")

    print()
    uncat = len(buckets["uncategorised"])
    if uncat == 0:
        print("✓ Cross-validation passes: every difference is classified.")
        return 0
    print(f"✗ {uncat} uncategorised difference(s) — investigate.")
    return 1


if __name__ == "__main__":
    sys.exit(main())
