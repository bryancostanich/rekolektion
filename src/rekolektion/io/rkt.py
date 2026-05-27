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


class LabelKind(str, Enum):
    """Role of a `(label …)` in the netlist.

    `NET_NAME` — the label's text names a signal or power net.
    Default for hand-authored labels and any label without an
    explicit `(kind …)` annotation. Contributes a pin to ratline /
    LabelFlood consumers on the F# side.

    `DEVICE_TERMINAL` — a FET port annotation (`D`/`G`/`S`/`B`)
    emitted by a primitive generator so Magic's `port makeall`
    sees it during LVS extraction. Net-level consumers skip these.
    Primitive generators tag them at emit time; nothing else
    should.

    `PORT_NAME` — a hand-authored sub-block's external port name
    (e.g. `A`/`B`/`Y` on a `nand2`). Reaches GDS for LVS, but
    net-level consumers skip it at the parent level so two SRefs
    of the same sub-block don't alias their port labels into one
    fake net. Sub-block authors tag explicitly via
    `kind=LabelKind.PORT_NAME` (or the `port_label(…)` sugar
    below).

    Wire format mirrors the F# DU (`LabelKind`): the string values
    of this enum are exactly what gets written / parsed in `.rkt`.
    `NET_NAME` is the implicit default — never emitted, never
    required on read.
    """

    NET_NAME = "net-name"
    DEVICE_TERMINAL = "device-terminal"
    PORT_NAME = "port-name"


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
PropValue = Union[str, int, float, "Symbol", "PropTuple"]


@dataclass(frozen=True)
class Symbol:
    """An unquoted atomic value in a property bag. Use this when the
    value should appear as a bare symbol (e.g. `(domain signal)`) rather
    than a quoted string."""

    text: str


@dataclass(frozen=True)
class PropTuple:
    """Multi-value property — emits the inner values whitespace-separated
    after the key, e.g. `(bbox -1140 -720 6432 720)`. Use when a single
    property naturally carries a fixed-arity tuple of scalars (cell
    extents, region descriptors). Each inner value follows the same
    typing rules as a scalar `PropValue` (`str`, `int`, `float`,
    `Symbol`)."""

    values: tuple[PropValue, ...]


def prop_tuple(*values: PropValue) -> PropTuple:
    """Convenience: `prop_tuple(0, 0, 100, 50)` instead of
    `PropTuple(values=(0, 0, 100, 50))`."""
    return PropTuple(values=tuple(values))


@dataclass(frozen=True)
class Property:
    """One key/value entry inside a `(props ...)` block. Value may be a
    `Symbol`, `str` (quoted), `int`, `float`, or `PropTuple` (multi-
    value)."""

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
    sub_form_comments: dict[str, list[str]] = field(default_factory=dict)


@dataclass
class Path:
    layer: Layer
    width: int
    points: list[Point]
    net: str | None = None
    cap: str | None = None
    props: list[Property] = field(default_factory=list)
    comments: list[str] = field(default_factory=list)
    sub_form_comments: dict[str, list[str]] = field(default_factory=dict)


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
    sub_form_comments: dict[str, list[str]] = field(default_factory=dict)


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
    sub_form_comments: dict[str, list[str]] = field(default_factory=dict)


@dataclass
class Label:
    layer: Layer
    text: str
    origin: Point
    cls: str | None = None
    props: list[Property] = field(default_factory=list)
    comments: list[str] = field(default_factory=list)
    sub_form_comments: dict[str, list[str]] = field(default_factory=dict)
    # `internal=True` marks the label as a viz/debug annotation that
    # should NOT be promoted to a GDS text record.  Magic's `port
    # makeall` only sees GDS text labels, so internal labels never
    # become subckt ports during LVS extraction.  Viz tools that read
    # the .rkt directly still render them.  Used for naming internal
    # nets (cs_drain_i, mag_drain_i, etc.) for traceability without
    # affecting LVS.
    internal: bool = False
    # Netlist role — see `LabelKind`. Orthogonal to `internal` (which
    # controls GDS export). A `DEVICE_TERMINAL` label still reaches
    # GDS so Magic's port extraction sees it; it just doesn't count
    # as a net at any composition level. Default `NET_NAME` matches
    # the F# reader's default-on-missing behavior.
    kind: LabelKind = LabelKind.NET_NAME


def port_label(
    layer: Layer,
    text: str,
    origin: Point,
    *,
    cls: str | None = None,
    props: list[Property] | None = None,
    comments: list[str] | None = None,
    internal: bool = False,
) -> Label:
    """Build a `Label` with `kind=LabelKind.PORT_NAME` set.

    Sugar for hand-authored sub-blocks: every port label gets this
    instead of `Label(...)` so it doesn't alias across SRef
    instances at the parent level (see `LabelKind.PORT_NAME` docs
    for the full story). Same arguments as `Label` minus `kind`.

    Example::

        # Inside the nand2 builder:
        elements = [
            rkt.port_label(rkt.named("sky130", "met1_label"),
                           text="A", origin=(120, 80)),
            rkt.port_label(rkt.named("sky130", "met1_label"),
                           text="B", origin=(120, 240)),
            rkt.port_label(rkt.named("sky130", "met1_label"),
                           text="Y", origin=(400, 160)),
            # … rest of geometry …
        ]
    """

    return Label(
        layer=layer,
        text=text,
        origin=origin,
        cls=cls,
        props=props if props is not None else [],
        comments=comments if comments is not None else [],
        internal=internal,
        kind=LabelKind.PORT_NAME,
    )


@dataclass
class SRef:
    cell: str
    origin: Point
    rot: float = 0.0
    mag: float = 1.0
    reflect: bool = False
    props: list[Property] = field(default_factory=list)
    comments: list[str] = field(default_factory=list)
    sub_form_comments: dict[str, list[str]] = field(default_factory=dict)


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
    sub_form_comments: dict[str, list[str]] = field(default_factory=dict)


@dataclass
class Props:
    """Cell-level `(props ...)` element."""

    items: list[Property] = field(default_factory=list)
    comments: list[str] = field(default_factory=list)
    sub_form_comments: dict[str, list[str]] = field(default_factory=dict)


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
    sub_form_comments: dict[str, list[str]] = field(default_factory=dict)


@dataclass
class Cell:
    name: str
    elements: list[Element] = field(default_factory=list)
    comments: list[str] = field(default_factory=list)
    sub_form_comments: dict[str, list[str]] = field(default_factory=dict)
    meta: Meta | None = None


@dataclass
class Units:
    dbu_nm: int = 1
    uu_um: int = 1


@dataclass
class Import:
    path: str
    comments: list[str] = field(default_factory=list)
    sub_form_comments: dict[str, list[str]] = field(default_factory=dict)


@dataclass
class Document:
    cells: list[Cell] = field(default_factory=list)
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
    if isinstance(v, PropTuple):
        # PropTuple expands inline at the `_prop` level; reaching this
        # branch means someone passed a tuple where a single scalar is
        # expected — programmer error.
        raise TypeError(
            "PropTuple cannot be rendered as a single value; "
            "use _prop which inlines tuple values after the key"
        )
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
    if isinstance(p.value, PropTuple):
        inner = " ".join(_prop_value(v) for v in p.value.values)
        return f"({p.key} {inner})"
    return f"({p.key} {_prop_value(p.value)})"


def _props_form(
    lead: str,
    props: list[Property],
    sfc: dict[str, list[str]] | None = None,
    level: int = 0,
) -> str | None:
    if not props:
        return None
    if sfc:
        # Per-property sub-form comments: prefix each `(key …)` form
        # with its comment block when present. Mirrors the F#
        # PropsEl path in `Writer.synthesizeElement`.
        rendered = []
        for p in props:
            prop_lead = _sub_form_prefix(level, sfc, p.key, " ")
            rendered.append(f"{prop_lead}{_prop(p)}")
        return f"{lead}(props{''.join(rendered)})"
    parts = " ".join(_prop(p) for p in props)
    return f"{lead}(props {parts})"


def _points_form(lead: str, points: list[Point]) -> str:
    inner = " ".join(f"({x} {y})" for x, y in points)
    return f"{lead}(points {inner})"


def _net_form(lead: str, net_name: str) -> str:
    return f"{lead}(net {net_name})"


def _sub_form_prefix(
    level: int,
    sfc: dict[str, list[str]],
    key: str,
    default_sep: str,
) -> str:
    """Return the leading-trivia string for a sub-form. With no
    `sub_form_comments[key]`, returns `default_sep` (typically `" "`
    or a leading-newline indent). With comments, forces the sub-form
    onto its own line at depth `level+1` and prefixes the comment
    block. Mirrors the F# `Writer.subFormLead`."""
    comments = sfc.get(key)
    if not comments:
        return default_sep
    # Same-line policy: when there's a comment, FORCE a newline +
    # indent so the comment renders cleanly above the sub-form.
    indent = _indent(level + 1)
    block = "".join(f"{indent}; {c}" for c in comments)
    return block + indent


# Per-element synthesizers ------------------------------------------------


def _emit_poly(level: int, poly: Poly) -> str:
    sfc = poly.sub_form_comments
    inner = _indent(level + 1)
    parts = [
        _leading(level, poly.comments),
        "(poly",
        _sub_form_prefix(level, sfc, "layer", " "),
        "(layer ", _layer(poly.layer), ")",
        _points_form(_sub_form_prefix(level, sfc, "points", inner), poly.points),
    ]
    if poly.net:
        parts.append(_net_form(_sub_form_prefix(level, sfc, "net", inner), poly.net))
    pf = _props_form(_sub_form_prefix(level, sfc, "props", inner), poly.props)
    if pf:
        parts.append(pf)
    parts.append(")")
    return "".join(parts)


def _emit_path(level: int, path: Path) -> str:
    sfc = path.sub_form_comments
    inner = _indent(level + 1)
    parts = [
        _leading(level, path.comments),
        "(path",
        _sub_form_prefix(level, sfc, "layer", " "),
        "(layer ", _layer(path.layer), ")",
        _sub_form_prefix(level, sfc, "width", " "),
        "(width ", str(path.width), ")",
        _points_form(_sub_form_prefix(level, sfc, "points", inner), path.points),
    ]
    if path.cap:
        parts.append(
            f"{_sub_form_prefix(level, sfc, 'cap', inner)}(cap {path.cap})")
    if path.net:
        parts.append(_net_form(_sub_form_prefix(level, sfc, "net", inner), path.net))
    pf = _props_form(_sub_form_prefix(level, sfc, "props", inner), path.props)
    if pf:
        parts.append(pf)
    parts.append(")")
    return "".join(parts)


def _emit_rect(level: int, rect: Rect) -> str:
    sfc = rect.sub_form_comments
    inner = _indent(level + 1)
    parts = [
        _leading(level, rect.comments),
        "(rect",
        _sub_form_prefix(level, sfc, "layer", " "),
        "(layer ", _layer(rect.layer), ") ",
        f"{rect.x1} {rect.y1} {rect.x2} {rect.y2}",
    ]
    if rect.net:
        parts.append(_net_form(_sub_form_prefix(level, sfc, "net", inner), rect.net))
    pf = _props_form(_sub_form_prefix(level, sfc, "props", inner), rect.props)
    if pf:
        parts.append(pf)
    parts.append(")")
    return "".join(parts)


def _emit_port_shape(lead: str, shape: Shape) -> str:
    if isinstance(shape, RectShape):
        return f"{lead}(shape (rect {shape.x1} {shape.y1} {shape.x2} {shape.y2}))"
    inner = " ".join(f"({x} {y})" for x, y in shape.points)
    return f"{lead}(shape (poly {inner}))"


def _emit_port(level: int, port: Port) -> str:
    sfc = port.sub_form_comments
    inner = _indent(level + 1)
    parts = [
        _leading(level, port.comments),
        "(port",
        _sub_form_prefix(level, sfc, "name", " "),
        "(name ", port.name, ")",
        _sub_form_prefix(level, sfc, "dir", " "),
        "(dir ", port.direction.value, ")",
        _sub_form_prefix(level, sfc, "layer", inner),
        f"(layer {_layer(port.layer)})",
    ]
    if port.flags:
        flag_text = " ".join(f.value for f in port.flags)
        parts.append(
            f"{_sub_form_prefix(level, sfc, 'flags', inner)}(flags {flag_text})")
    parts.append(_emit_port_shape(_sub_form_prefix(level, sfc, "shape", inner), port.shape))
    if port.net:
        parts.append(_net_form(_sub_form_prefix(level, sfc, "net", inner), port.net))
    pf = _props_form(_sub_form_prefix(level, sfc, "props", inner), port.props)
    if pf:
        parts.append(pf)
    parts.append(")")
    return "".join(parts)


def _emit_label(level: int, label: Label) -> str:
    sfc = label.sub_form_comments
    inner = _indent(level + 1)
    x, y = label.origin
    parts = [
        _leading(level, label.comments),
        "(label",
        _sub_form_prefix(level, sfc, "layer", " "),
        "(layer ", _layer(label.layer), ")",
        _sub_form_prefix(level, sfc, "text", " "),
        "(text ", _string(label.text), ")",
        _sub_form_prefix(level, sfc, "origin", " "),
        f"(origin {x} {y})",
    ]
    if label.cls:
        parts.append(
            f"{_sub_form_prefix(level, sfc, 'class', inner)}(class {label.cls})")
    if label.internal:
        parts.append(
            f"{_sub_form_prefix(level, sfc, 'internal', inner)}(internal #t)")
    # `(kind …)` only emitted when not the implicit default.
    if label.kind != LabelKind.NET_NAME:
        parts.append(
            f"{_sub_form_prefix(level, sfc, 'kind', inner)}(kind {label.kind.value})")
    pf = _props_form(_sub_form_prefix(level, sfc, "props", inner), label.props)
    if pf:
        parts.append(pf)
    parts.append(")")
    return "".join(parts)


def _emit_sref(level: int, sref: SRef) -> str:
    sfc = sref.sub_form_comments
    inner = _indent(level + 1)
    x, y = sref.origin
    parts = [
        _leading(level, sref.comments),
        "(sref",
        _sub_form_prefix(level, sfc, "cell", " "),
        f"(cell {sref.cell})",
        _sub_form_prefix(level, sfc, "origin", " "),
        f"(origin {x} {y})",
    ]
    if sref.rot != 0.0:
        parts.append(
            f"{_sub_form_prefix(level, sfc, 'rot', ' ')}(rot {_float(sref.rot)})")
    if sref.mag != 1.0:
        parts.append(
            f"{_sub_form_prefix(level, sfc, 'mag', ' ')}(mag {_float(sref.mag)})")
    if sref.reflect:
        parts.append(
            f"{_sub_form_prefix(level, sfc, 'reflect', ' ')}(reflect true)")
    pf = _props_form(_sub_form_prefix(level, sfc, "props", inner), sref.props)
    if pf:
        parts.append(pf)
    parts.append(")")
    return "".join(parts)


def _emit_aref(level: int, aref: ARef) -> str:
    sfc = aref.sub_form_comments
    inner = _indent(level + 1)
    x, y = aref.origin
    cx, cy = aref.col_pitch
    rx, ry = aref.row_pitch
    parts = [
        _leading(level, aref.comments),
        "(aref",
        _sub_form_prefix(level, sfc, "cell", " "),
        f"(cell {aref.cell})",
        _sub_form_prefix(level, sfc, "origin", " "),
        f"(origin {x} {y})",
        _sub_form_prefix(level, sfc, "cols", inner),
        f"(cols {aref.cols})",
        _sub_form_prefix(level, sfc, "rows", " "),
        f"(rows {aref.rows})",
        _sub_form_prefix(level, sfc, "col_pitch", inner),
        f"(col_pitch {cx} {cy})",
        _sub_form_prefix(level, sfc, "row_pitch", " "),
        f"(row_pitch {rx} {ry})",
    ]
    if aref.rot != 0.0:
        parts.append(
            f"{_sub_form_prefix(level, sfc, 'rot', inner)}(rot {_float(aref.rot)})")
    if aref.mag != 1.0:
        parts.append(
            f"{_sub_form_prefix(level, sfc, 'mag', ' ')}(mag {_float(aref.mag)})")
    if aref.reflect:
        parts.append(
            f"{_sub_form_prefix(level, sfc, 'reflect', ' ')}(reflect true)")
    pf = _props_form(_sub_form_prefix(level, sfc, "props", inner), aref.props)
    if pf:
        parts.append(pf)
    parts.append(")")
    return "".join(parts)


def _emit_props_el(level: int, props: Props) -> str:
    sfc = props.sub_form_comments
    parts = [_leading(level, props.comments), "(props"]
    if sfc:
        # Per-property sub-form comments (e.g. `; before bbox`).
        for p in props.items:
            parts.append(_sub_form_prefix(level, sfc, p.key, " "))
            parts.append(_prop(p))
    else:
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
    # No `(nets …)` block. Labels with `Kind = NET_NAME` are the
    # source of truth for the net set; downstream consumers walk
    # labels directly. See spec.md "Source of truth for nets" and
    # track 06 Decision 4 = C in plan.md.
    for cell in doc.cells:
        parts.append(_emit_cell(1, cell))
    parts.append(")\n")
    return "".join(parts)


# ─── Reader (re-exports from _rkt_reader) ──────────────────────────────

from rekolektion.io._rkt_reader import (  # noqa: E402
    ImportCycleError,
    Library,
    ParseError,
    SchemaError,
    load,
    read,
    read_file,
)

__all__ = [
    # Reader surface
    "read",
    "read_file",
    "load",
    "Library",
    "ParseError",
    "SchemaError",
    "ImportCycleError",
    # Writer surface (existing)
    "write",
    "Document",
    "Cell",
    "Meta",
    "Units",
    "Import",
    "Layer",
    "named",
    "unknown",
    "Property",
    "PropTuple",
    "prop_tuple",
    "Symbol",
    "PortDirection",
    "PortFlag",
    "LabelKind",
    "RectShape",
    "PolyShape",
    "Poly",
    "Path",
    "Rect",
    "Port",
    "Label",
    "SRef",
    "ARef",
    "Props",
    "port_label",
]
