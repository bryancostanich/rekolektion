"""Canonical `.rkt` writer — Python mirror of the F# canonical synthesizer
in `tools/viz/src/Rekolektion.Viz.Core/Rkt/Writer.fs`.

Goal: a generator-side library can build an in-memory document of cells,
ports, polygons, and comments, then emit text that the viz tool reads
without any conversion loss. Output formatting matches the F# writer:

* Two-space indent per nesting level.
* Comments emit as `; <text>` lines preceding the form they belong to.
* Floats always emit with at least one decimal (`90` becomes `90.0`).
* String literals escape `\\\\`, `\\"`, `\\n`, `\\r`, `\\t`.
* `(rot ...)` / `(mag ...)` / `(reflect ...)` skip when set to defaults
  (0.0, 1.0, False).

Importing this module:

    from rekolektion.io import rkt

    doc = rkt.Document(
        cells=[
            rkt.Cell(
                name="bitcell",
                elements=[
                    rkt.Poly(
                        layer=rkt.named("sky130", "met1"),
                        points=[(0, 0), (100, 0), (100, 50), (0, 50)],
                    ),
                ],
            ),
        ],
        top_cell="bitcell",
    )
    text = rkt.write(doc)
    open("bitcell.rkt", "w").write(text)
"""

from __future__ import annotations

from dataclasses import dataclass, field
from enum import Enum
from typing import Union


# ─── Layer references ───────────────────────────────────────────────


@dataclass(frozen=True)
class Layer:
    """Either a PDK-qualified name (`sky130:met1`) or an unknown
    `(number, datatype)` pair we don't have a name for.
    """

    kind: str  # "named" or "unknown"
    pdk: str = ""  # only for kind=="named"
    name: str = ""  # only for kind=="named"
    number: int = 0  # only for kind=="unknown"
    datatype: int = 0  # only for kind=="unknown"


def named(pdk: str, name: str) -> Layer:
    """Construct a `Named(pdk, name)` layer reference."""
    return Layer(kind="named", pdk=pdk, name=name)


def unknown(number: int, datatype: int) -> Layer:
    """Construct an `Unknown(number, datatype)` layer reference. The
    `.rkt` file emits these verbatim as `unknown:<n>/<d>`."""
    return Layer(kind="unknown", number=number, datatype=datatype)


# ─── Port flag / direction enums ────────────────────────────────────


class PortDirection(str, Enum):
    INPUT = "input"
    OUTPUT = "output"
    INOUT = "inout"
    UNSPECIFIED = "unspecified"


class PortFlag(str, Enum):
    SIGNAL = "signal"
    POWER = "power"
    GROUND = "ground"
    CLOCK = "clock"
    ANALOG = "analog"
    SCAN = "scan"


# ─── Geometry / property values ─────────────────────────────────────


@dataclass(frozen=True)
class RectShape:
    """Port shape: axis-aligned rectangle."""

    x1: int
    y1: int
    x2: int
    y2: int


@dataclass(frozen=True)
class PolyShape:
    """Port shape: closed polygon."""

    points: list[tuple[int, int]]


Point = tuple[int, int]
Shape = Union[RectShape, PolyShape]
PropValue = Union[str, int, float, "Symbol"]


@dataclass(frozen=True)
class Symbol:
    """An unquoted atomic value in a property bag. Use this when the
    value should appear as a bare symbol (e.g. `(domain signal)`) rather
    than a quoted string."""

    text: str


@dataclass(frozen=True)
class Property:
    """One key/value entry inside a `(props ...)` block. Value may be a
    `Symbol`, `str` (quoted), `int`, or `float`."""

    key: str
    value: PropValue


# ─── Element variants ───────────────────────────────────────────────


@dataclass
class Poly:
    layer: Layer
    points: list[Point]
    net: str | None = None
    props: list[Property] = field(default_factory=list)
    comments: list[str] = field(default_factory=list)


@dataclass
class Path:
    layer: Layer
    width: int
    points: list[Point]
    net: str | None = None
    cap: str | None = None
    props: list[Property] = field(default_factory=list)
    comments: list[str] = field(default_factory=list)


@dataclass
class Rect:
    layer: Layer
    x1: int
    y1: int
    x2: int
    y2: int
    net: str | None = None
    props: list[Property] = field(default_factory=list)
    comments: list[str] = field(default_factory=list)


@dataclass
class Port:
    name: str
    direction: PortDirection
    layer: Layer
    shape: Shape
    flags: list[PortFlag] = field(default_factory=list)
    net: str | None = None
    props: list[Property] = field(default_factory=list)
    comments: list[str] = field(default_factory=list)


@dataclass
class Label:
    layer: Layer
    text: str
    origin: Point
    cls: str | None = None
    props: list[Property] = field(default_factory=list)
    comments: list[str] = field(default_factory=list)


@dataclass
class SRef:
    cell: str
    origin: Point
    rot: float = 0.0
    mag: float = 1.0
    reflect: bool = False
    props: list[Property] = field(default_factory=list)
    comments: list[str] = field(default_factory=list)


@dataclass
class ARef:
    cell: str
    origin: Point
    cols: int
    rows: int
    col_pitch: Point
    row_pitch: Point
    rot: float = 0.0
    mag: float = 1.0
    reflect: bool = False
    props: list[Property] = field(default_factory=list)
    comments: list[str] = field(default_factory=list)


@dataclass
class Props:
    """Cell-level `(props ...)` element."""

    items: list[Property] = field(default_factory=list)
    comments: list[str] = field(default_factory=list)


Element = Union[Poly, Path, Rect, Port, Label, SRef, ARef, Props]


# ─── Cell / net / import / document ─────────────────────────────────


@dataclass
class Meta:
    """Provenance for a PDK-generated cell. Mirrors the F#
    `Rkt.Types.Meta` record. Only `generator` is required.

    Consumers treat the presence of `Meta` on a `Cell` as "this is
    PDK-owned" — viz refuses interior edits, tape-out ignores the
    block, and the cache uses `(generator, digest)` as the lookup
    key. See docs/io/rkt.md for the full schema.
    """

    generator: str
    params: list[Property] = field(default_factory=list)
    source: str | None = None
    generated: str | None = None
    digest: str | None = None
    comments: list[str] = field(default_factory=list)


@dataclass
class Cell:
    name: str
    elements: list[Element] = field(default_factory=list)
    comments: list[str] = field(default_factory=list)
    meta: Meta | None = None


@dataclass
class Net:
    name: str
    domain: str = "signal"
    voltage: float | None = None
    cls: str | None = None
    props: list[Property] = field(default_factory=list)
    comments: list[str] = field(default_factory=list)


@dataclass
class Units:
    dbu_nm: int = 1
    uu_um: int = 1


@dataclass
class Import:
    path: str
    comments: list[str] = field(default_factory=list)


@dataclass
class Document:
    cells: list[Cell] = field(default_factory=list)
    nets: list[Net] = field(default_factory=list)
    imports: list[Import] = field(default_factory=list)
    pdk: str = "sky130"
    version: int = 1
    units: Units = field(default_factory=Units)
    top_cell: str | None = None
    header_comments: list[str] = field(default_factory=list)


# ─── Writer ─────────────────────────────────────────────────────────


def _indent(n: int) -> str:
    return "\n" + ("  " * n)


def _comment_block(level: int, comments: list[str]) -> str:
    if not comments:
        return ""
    pad = "  " * level
    return "".join(f"\n{pad}; {c}" for c in comments)


def _leading(level: int, comments: list[str]) -> str:
    return _comment_block(level, comments) + _indent(level)


def _layer(layer: Layer) -> str:
    if layer.kind == "named":
        return f"{layer.pdk}:{layer.name}"
    return f"unknown:{layer.number}/{layer.datatype}"


def _float(v: float) -> str:
    s = repr(v)
    if "." in s or "e" in s or "E" in s or "inf" in s or "nan" in s:
        return s
    return s + ".0"


def _string(text: str) -> str:
    buf: list[str] = ['"']
    for c in text:
        if c == "\\":
            buf.append("\\\\")
        elif c == '"':
            buf.append('\\"')
        elif c == "\n":
            buf.append("\\n")
        elif c == "\r":
            buf.append("\\r")
        elif c == "\t":
            buf.append("\\t")
        else:
            buf.append(c)
    buf.append('"')
    return "".join(buf)


def _prop_value(v: PropValue) -> str:
    if isinstance(v, Symbol):
        return v.text
    if isinstance(v, bool):
        # bool is a subclass of int in Python — separate this case so
        # True/False render as symbols, not 1/0.
        return "true" if v else "false"
    if isinstance(v, int):
        return str(v)
    if isinstance(v, float):
        return _float(v)
    return _string(v)


def _prop(p: Property) -> str:
    return f"({p.key} {_prop_value(p.value)})"


def _props_form(level: int, props: list[Property]) -> str | None:
    if not props:
        return None
    lead = _indent(level)
    parts = " ".join(_prop(p) for p in props)
    return f"{lead}(props {parts})"


def _points_form(level: int, points: list[Point]) -> str:
    lead = _indent(level)
    inner = " ".join(f"({x} {y})" for x, y in points)
    return f"{lead}(points {inner})"


def _net_form(level: int, net_name: str) -> str:
    return f"{_indent(level)}(net {net_name})"


# Per-element synthesizers ------------------------------------------------


def _emit_poly(level: int, poly: Poly) -> str:
    parts = [
        _leading(level, poly.comments),
        "(poly (layer ", _layer(poly.layer), ")",
        _points_form(level + 1, poly.points),
    ]
    if poly.net:
        parts.append(_net_form(level + 1, poly.net))
    pf = _props_form(level + 1, poly.props)
    if pf:
        parts.append(pf)
    parts.append(")")
    return "".join(parts)


def _emit_path(level: int, path: Path) -> str:
    parts = [
        _leading(level, path.comments),
        "(path (layer ", _layer(path.layer), ") (width ", str(path.width), ")",
        _points_form(level + 1, path.points),
    ]
    if path.cap:
        parts.append(f"{_indent(level + 1)}(cap {path.cap})")
    if path.net:
        parts.append(_net_form(level + 1, path.net))
    pf = _props_form(level + 1, path.props)
    if pf:
        parts.append(pf)
    parts.append(")")
    return "".join(parts)


def _emit_rect(level: int, rect: Rect) -> str:
    parts = [
        _leading(level, rect.comments),
        "(rect (layer ", _layer(rect.layer), ") ",
        f"{rect.x1} {rect.y1} {rect.x2} {rect.y2}",
    ]
    if rect.net:
        parts.append(_net_form(level + 1, rect.net))
    pf = _props_form(level + 1, rect.props)
    if pf:
        parts.append(pf)
    parts.append(")")
    return "".join(parts)


def _emit_port_shape(shape: Shape) -> str:
    if isinstance(shape, RectShape):
        return f" (shape (rect {shape.x1} {shape.y1} {shape.x2} {shape.y2}))"
    return " (shape (poly " + " ".join(f"({x} {y})" for x, y in shape.points) + "))"


def _emit_port(level: int, port: Port) -> str:
    parts = [
        _leading(level, port.comments),
        "(port (name ", port.name, ") (dir ", port.direction.value, ")",
        f"{_indent(level + 1)}(layer {_layer(port.layer)})",
    ]
    if port.flags:
        flag_text = " ".join(f.value for f in port.flags)
        parts.append(f"{_indent(level + 1)}(flags {flag_text})")
    parts.append(_indent(level + 1).rstrip("\n") + _emit_port_shape(port.shape).lstrip())
    # The shape line uses inner indentation; the leading-space dance
    # above keeps the form well-aligned even when no flags exist.
    if port.net:
        parts.append(_net_form(level + 1, port.net))
    pf = _props_form(level + 1, port.props)
    if pf:
        parts.append(pf)
    parts.append(")")
    return "".join(parts)


def _emit_label(level: int, label: Label) -> str:
    x, y = label.origin
    parts = [
        _leading(level, label.comments),
        "(label (layer ", _layer(label.layer), ") (text ", _string(label.text), ") ",
        f"(origin {x} {y})",
    ]
    if label.cls:
        parts.append(f"{_indent(level + 1)}(class {label.cls})")
    pf = _props_form(level + 1, label.props)
    if pf:
        parts.append(pf)
    parts.append(")")
    return "".join(parts)


def _emit_sref(level: int, sref: SRef) -> str:
    x, y = sref.origin
    parts = [
        _leading(level, sref.comments),
        f"(sref (cell {sref.cell}) (origin {x} {y})",
    ]
    if sref.rot != 0.0:
        parts.append(f" (rot {_float(sref.rot)})")
    if sref.mag != 1.0:
        parts.append(f" (mag {_float(sref.mag)})")
    if sref.reflect:
        parts.append(" (reflect true)")
    pf = _props_form(level + 1, sref.props)
    if pf:
        parts.append(pf)
    parts.append(")")
    return "".join(parts)


def _emit_aref(level: int, aref: ARef) -> str:
    x, y = aref.origin
    cx, cy = aref.col_pitch
    rx, ry = aref.row_pitch
    parts = [
        _leading(level, aref.comments),
        f"(aref (cell {aref.cell}) (origin {x} {y})",
        f"{_indent(level + 1)}(cols {aref.cols}) (rows {aref.rows})",
        f"{_indent(level + 1)}(col_pitch {cx} {cy}) (row_pitch {rx} {ry})",
    ]
    if aref.rot != 0.0:
        parts.append(f"{_indent(level + 1)}(rot {_float(aref.rot)})")
    if aref.mag != 1.0:
        parts.append(f" (mag {_float(aref.mag)})")
    if aref.reflect:
        parts.append(" (reflect true)")
    pf = _props_form(level + 1, aref.props)
    if pf:
        parts.append(pf)
    parts.append(")")
    return "".join(parts)


def _emit_props_el(level: int, props: Props) -> str:
    parts = [_leading(level, props.comments), "(props"]
    for p in props.items:
        parts.append(" ")
        parts.append(_prop(p))
    parts.append(")")
    return "".join(parts)


def _emit_element(level: int, el: Element) -> str:
    if isinstance(el, Poly):
        return _emit_poly(level, el)
    if isinstance(el, Path):
        return _emit_path(level, el)
    if isinstance(el, Rect):
        return _emit_rect(level, el)
    if isinstance(el, Port):
        return _emit_port(level, el)
    if isinstance(el, Label):
        return _emit_label(level, el)
    if isinstance(el, SRef):
        return _emit_sref(level, el)
    if isinstance(el, ARef):
        return _emit_aref(level, el)
    if isinstance(el, Props):
        return _emit_props_el(level, el)
    raise TypeError(f"unknown element variant: {type(el).__name__}")


def _emit_meta(level: int, meta: Meta) -> str:
    """Emit a `(meta ...)` block as the first child of a cell.
    Always emits `(params ...)` even when empty so consumers can
    distinguish "no params" from "schema malformed."
    """

    inner = _indent(level + 1)
    parts = [
        _leading(level, meta.comments),
        "(meta",
        f"{inner}(generator {_string(meta.generator)})",
    ]
    params_body = "".join(f" {_prop(p)}" for p in meta.params)
    parts.append(f"{inner}(params{params_body})")
    if meta.source is not None:
        parts.append(f"{inner}(source {_string(meta.source)})")
    if meta.generated is not None:
        parts.append(f"{inner}(generated {_string(meta.generated)})")
    if meta.digest is not None:
        parts.append(f"{inner}(digest {_string(meta.digest)})")
    parts.append(")")
    return "".join(parts)


def _emit_cell(level: int, cell: Cell) -> str:
    parts = [
        _leading(level, cell.comments),
        f"(cell {cell.name}",
    ]
    if cell.meta is not None:
        parts.append(_emit_meta(level + 1, cell.meta))
    for el in cell.elements:
        parts.append(_emit_element(level + 1, el))
    parts.append(")")
    return "".join(parts)


def _emit_net(level: int, net: Net) -> str:
    parts = [
        _leading(level, net.comments),
        f"(net {net.name} (domain {net.domain})",
    ]
    if net.voltage is not None:
        parts.append(f" (voltage {_float(net.voltage)})")
    if net.cls:
        parts.append(f" (class {net.cls})")
    for p in net.props:
        parts.append(" ")
        parts.append(_prop(p))
    parts.append(")")
    return "".join(parts)


def _emit_nets_block(level: int, nets: list[Net]) -> str | None:
    if not nets:
        return None
    lead = _indent(level)
    body = "".join(_emit_net(level + 1, n) for n in nets)
    return f"{lead}(nets{body})"


def _emit_import(level: int, imp: Import) -> str:
    return f"{_leading(level, imp.comments)}(import {_string(imp.path)})"


def write(doc: Document) -> str:
    """Produce the canonical `.rkt` text for `doc`.

    Output ends with a single trailing newline. Round-trips through the
    F# reader byte-for-byte when fed the same document.
    """

    parts: list[str] = []
    if doc.header_comments:
        for c in doc.header_comments:
            parts.append(f"; {c}\n")
    parts.append("(layout")
    parts.append(f" (version {doc.version})")
    parts.append(f"{_indent(1)}(pdk {doc.pdk})")
    parts.append(
        f"{_indent(1)}(units (dbu_nm {doc.units.dbu_nm}) (uu_um {doc.units.uu_um}))"
    )
    for imp in doc.imports:
        parts.append(_emit_import(1, imp))
    if doc.top_cell is not None:
        parts.append(f"{_indent(1)}(top {doc.top_cell})")
    nb = _emit_nets_block(1, doc.nets)
    if nb:
        parts.append(nb)
    for cell in doc.cells:
        parts.append(_emit_cell(1, cell))
    parts.append(")\n")
    return "".join(parts)
