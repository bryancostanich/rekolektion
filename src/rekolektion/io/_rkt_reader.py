"""Internal `.rkt` reader — tokenizer + parser + schema analyzer.

Re-exported through `rekolektion.io.rkt` as `read`, `read_file`,
`load`, `Library`, `ParseError`, `SchemaError`, `ImportCycleError`.

Mirrors the F# `tools/viz/src/Rekolektion.Viz.Core/Rkt/Reader.fs`
discipline at a smaller scale: hand-written tokenizer (no regex
backtracking), recursive-descent parser building an internal
s-expression tree with source positions for error messages, then a
schema-analysis pass that populates the canonical dataclasses
declared in `rkt.py`.

The reader is kept private (underscore-prefixed) so consumers go
through the documented `rkt.read*` surface — that surface is the
stability contract.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from pathlib import Path
from typing import Iterator

# Imported lazily inside functions to avoid a circular import (this
# module is loaded by `rkt.py` while `rkt.py` is still being defined
# on first import). Read inside `_analyze_*` functions.


# ─── Errors ────────────────────────────────────────────────────────────


@dataclass
class ParseError(Exception):
    """Tokenizer or grammar failure — invalid S-expression syntax."""

    line: int
    column: int
    message: str

    def __str__(self) -> str:  # pragma: no cover - cosmetic
        return f"parse error at line {self.line}, col {self.column}: {self.message}"


@dataclass
class SchemaError(Exception):
    """Well-formed S-expression that doesn't match the `.rkt` schema."""

    form_kind: str
    expected: str
    got: str
    line: int | None = None
    column: int | None = None

    def __str__(self) -> str:  # pragma: no cover - cosmetic
        loc = (
            f" at line {self.line}, col {self.column}"
            if self.line is not None
            else ""
        )
        return (
            f"schema error in {self.form_kind}{loc}: "
            f"expected {self.expected}, got {self.got}"
        )


@dataclass
class ImportCycleError(Exception):
    """Two or more files import each other (directly or transitively)."""

    cycle: list[str]

    def __str__(self) -> str:  # pragma: no cover - cosmetic
        return "import cycle: " + " -> ".join(self.cycle)


# ─── Tokenizer ────────────────────────────────────────────────────────


_SYMBOL_EXTRA = set("_-+./:*?!$%&@<=>^~#")


def _is_symbol_char(c: str) -> bool:
    return c.isalnum() or c in _SYMBOL_EXTRA


@dataclass
class _Pos:
    line: int
    column: int


@dataclass
class _Tok:
    """One lexical token. `kind` is one of:
    'lparen', 'rparen', 'symbol', 'int', 'float', 'string', 'comment',
    'eof'."""

    kind: str
    text: str
    pos: _Pos


def _tokenize(src: str) -> Iterator[_Tok]:
    i = 0
    line = 1
    col = 1
    n = len(src)
    while i < n:
        c = src[i]
        start = _Pos(line, col)
        if c == "\n":
            i += 1
            line += 1
            col = 1
            continue
        if c.isspace():
            i += 1
            col += 1
            continue
        if c == ";":
            # Comment to end of line.
            j = i + 1
            while j < n and src[j] != "\n":
                j += 1
            yield _Tok("comment", src[i + 1 : j].lstrip(" "), start)
            col += j - i
            i = j
            continue
        if c == "(":
            yield _Tok("lparen", "(", start)
            i += 1
            col += 1
            continue
        if c == ")":
            yield _Tok("rparen", ")", start)
            i += 1
            col += 1
            continue
        if c == '"':
            # String literal — backslash escapes the next char verbatim.
            j = i + 1
            sb = []
            while j < n and src[j] != '"':
                if src[j] == "\\" and j + 1 < n:
                    sb.append(src[j + 1])
                    j += 2
                else:
                    if src[j] == "\n":
                        line += 1
                        col = 0
                    sb.append(src[j])
                    j += 1
            if j >= n:
                raise ParseError(start.line, start.column, "unterminated string")
            yield _Tok("string", "".join(sb), start)
            col += (j - i) + 1
            i = j + 1
            continue
        if _is_symbol_char(c):
            j = i + 1
            while j < n and _is_symbol_char(src[j]):
                j += 1
            text = src[i:j]
            kind = _classify_atom(text)
            yield _Tok(kind, text, start)
            col += j - i
            i = j
            continue
        raise ParseError(line, col, f"unexpected character {c!r}")
    yield _Tok("eof", "", _Pos(line, col))


def _classify_atom(text: str) -> str:
    # Int (with optional leading sign).
    t = text
    if t.startswith(("+", "-")):
        t = t[1:]
    if t and t.isdigit():
        return "int"
    # Float — only if a dot is present and the result parses.
    if "." in text:
        try:
            float(text)
            return "float"
        except ValueError:
            pass
    return "symbol"


# ─── Internal s-expression tree ───────────────────────────────────────


@dataclass
class _Atom:
    kind: str  # 'symbol' | 'int' | 'float' | 'string'
    text: str
    leading_comments: list[str] = field(default_factory=list)
    pos: _Pos | None = None


@dataclass
class _List:
    children: list  # list[_Atom | _List]
    leading_comments: list[str] = field(default_factory=list)
    pos: _Pos | None = None


def _parse(tokens: list[_Tok]) -> list:
    """Parse a flat token list into a list of top-level forms. Comments
    that precede a form attach as its `leading_comments`."""
    idx = 0
    pending_comments: list[str] = []
    out: list = []

    def parse_form() -> _Atom | _List:
        nonlocal idx, pending_comments
        tok = tokens[idx]
        leading = pending_comments
        pending_comments = []
        if tok.kind == "lparen":
            idx += 1
            children = []
            while True:
                if idx >= len(tokens) or tokens[idx].kind == "eof":
                    raise ParseError(tok.pos.line, tok.pos.column, "unclosed (")
                inner = tokens[idx]
                if inner.kind == "rparen":
                    idx += 1
                    break
                if inner.kind == "comment":
                    pending_comments.append(inner.text)
                    idx += 1
                    continue
                children.append(parse_form())
            return _List(children=children, leading_comments=leading, pos=tok.pos)
        if tok.kind in ("symbol", "int", "float", "string"):
            idx += 1
            return _Atom(kind=tok.kind, text=tok.text, leading_comments=leading, pos=tok.pos)
        raise ParseError(tok.pos.line, tok.pos.column, f"unexpected {tok.kind}")

    while idx < len(tokens):
        t = tokens[idx]
        if t.kind == "eof":
            break
        if t.kind == "comment":
            pending_comments.append(t.text)
            idx += 1
            continue
        if t.kind == "rparen":
            raise ParseError(t.pos.line, t.pos.column, "unexpected )")
        out.append(parse_form())
    return out


# ─── Schema analyzer helpers ──────────────────────────────────────────


def _is_list(node) -> bool:
    return isinstance(node, _List)


def _head_symbol(node: _List) -> str | None:
    if not node.children:
        return None
    head = node.children[0]
    if isinstance(head, _Atom) and head.kind == "symbol":
        return head.text
    return None


def _children_after_head(node: _List) -> list:
    return node.children[1:]


def _find_form(name: str, children: list) -> _List | None:
    for c in children:
        if _is_list(c) and _head_symbol(c) == name:
            return c
    return None


def _atom_text(node) -> str | None:
    if isinstance(node, _Atom):
        return node.text
    return None


def _atom_int(node) -> int | None:
    if isinstance(node, _Atom) and node.kind == "int":
        try:
            return int(node.text)
        except ValueError:
            return None
    return None


def _atom_float(node) -> float | None:
    if isinstance(node, _Atom) and node.kind == "float":
        try:
            return float(node.text)
        except ValueError:
            return None
    if isinstance(node, _Atom) and node.kind == "int":
        try:
            return float(node.text)
        except ValueError:
            return None
    return None


def _atom_string(node) -> str | None:
    if isinstance(node, _Atom) and node.kind == "string":
        return node.text
    return None


def _schema_error(form_kind: str, expected: str, got: str, node) -> SchemaError:
    pos = getattr(node, "pos", None)
    return SchemaError(
        form_kind=form_kind,
        expected=expected,
        got=got,
        line=pos.line if pos else None,
        column=pos.column if pos else None,
    )


def _sub_form_comments(children: list) -> dict:
    """Build the sub-form-comments map for an element. `children` is
    the s-exp child list inside the outer form (after the head
    symbol). For each child that's a list with a symbol head,
    the comments preceding it attach to that head-symbol key. Mirrors
    F# `subFormCommentsOf` in `Reader.fs`."""
    out: dict[str, list[str]] = {}
    for c in children:
        if not isinstance(c, _List) or not c.children:
            continue
        head = c.children[0]
        if not isinstance(head, _Atom) or head.kind != "symbol":
            continue
        if c.leading_comments:
            out[head.text] = list(c.leading_comments)
    return out


def _prop_value(node, rkt):
    """Convert an s-exp atom to a `PropValue` (str / int / float /
    Symbol)."""
    if isinstance(node, _Atom):
        if node.kind == "string":
            return node.text
        if node.kind == "int":
            return int(node.text)
        if node.kind == "float":
            return float(node.text)
        if node.kind == "symbol":
            return rkt.Symbol(text=node.text)
    raise _schema_error("prop-value", "scalar atom", str(node), node)


def _analyze_property(node: _List, rkt):
    """`(key value)` or `(key v1 v2 …)` → `Property`. Multi-value form
    becomes a `PropTuple`."""
    if not isinstance(node, _List) or not node.children:
        raise _schema_error("property", "(key value …)", str(node), node)
    head = node.children[0]
    if not isinstance(head, _Atom) or head.kind != "symbol":
        raise _schema_error("property", "key must be a symbol", str(head), node)
    key = head.text
    rest = node.children[1:]
    if len(rest) == 0:
        return rkt.Property(key=key, value=rkt.Symbol(text=""))
    if len(rest) == 1:
        return rkt.Property(key=key, value=_prop_value(rest[0], rkt))
    # Multi-value tuple. Every trailing child must be a scalar atom.
    for child in rest:
        if isinstance(child, _List):
            raise _schema_error(
                "property",
                "tuple values must be scalar atoms (no nested forms)",
                "nested list",
                child,
            )
    values = tuple(_prop_value(c, rkt) for c in rest)
    return rkt.Property(key=key, value=rkt.PropTuple(values=values))


def _analyze_props(node: _List, rkt) -> list:
    return [_analyze_property(c, rkt) for c in _children_after_head(node)
            if isinstance(c, _List)]


def _analyze_layer(node, default_pdk: str, rkt):
    """Layer atom: `pdk:name` or `unknown:<n>/<d>` or bare `name`
    (defaulting to default_pdk)."""
    text = _atom_text(node)
    if text is None:
        raise _schema_error("layer", "PDK-qualified atom", str(node), node)
    if ":" in text:
        pdk, name = text.split(":", 1)
        if pdk == "unknown":
            if "/" not in name:
                raise _schema_error(
                    "layer", "unknown:<n>/<d>", text, node)
            n_str, d_str = name.split("/", 1)
            return rkt.unknown(int(n_str), int(d_str))
        return rkt.named(pdk, name)
    # Bare name — default to document PDK.
    return rkt.named(default_pdk, text)


def _analyze_points(node: _List) -> list:
    """`(points (x y) (x y) …)` → list of (int, int) tuples."""
    if _head_symbol(node) != "points":
        raise _schema_error("points", "(points …)", str(node), node)
    out = []
    for c in _children_after_head(node):
        if not isinstance(c, _List) or len(c.children) != 2:
            raise _schema_error(
                "point", "(x y)", str(c), c)
        x = _atom_int(c.children[0])
        y = _atom_int(c.children[1])
        if x is None or y is None:
            raise _schema_error(
                "point", "two ints", str(c), c)
        out.append((x, y))
    return out


# ─── Element analyzers ────────────────────────────────────────────────


def _find_net(children: list) -> str | None:
    f = _find_form("net", children)
    if f and len(f.children) == 2:
        return _atom_text(f.children[1])
    return None


def _find_child_props(children: list, rkt) -> list:
    f = _find_form("props", children)
    if not f:
        return []
    return _analyze_props(f, rkt)


def _analyze_poly(node: _List, default_pdk: str, rkt):
    children = _children_after_head(node)
    layer_form = _find_form("layer", children)
    points_form = _find_form("points", children)
    if layer_form is None or points_form is None:
        raise _schema_error("poly", "(poly (layer …) (points …))", "missing", node)
    layer = _analyze_layer(layer_form.children[1], default_pdk, rkt)
    points = _analyze_points(points_form)
    return rkt.Poly(
        layer=layer,
        points=points,
        net=_find_net(children),
        props=_find_child_props(children, rkt),
        comments=list(node.leading_comments),
        sub_form_comments=_sub_form_comments(children),
    )


def _analyze_path(node: _List, default_pdk: str, rkt):
    children = _children_after_head(node)
    layer_form = _find_form("layer", children)
    width_form = _find_form("width", children)
    points_form = _find_form("points", children)
    if not (layer_form and width_form and points_form):
        raise _schema_error("path", "(path (layer …) (width …) (points …))", "missing", node)
    width = _atom_int(width_form.children[1])
    if width is None:
        raise _schema_error("path-width", "integer", str(width_form), width_form)
    cap_form = _find_form("cap", children)
    cap = _atom_text(cap_form.children[1]) if cap_form and len(cap_form.children) == 2 else None
    return rkt.Path(
        layer=_analyze_layer(layer_form.children[1], default_pdk, rkt),
        width=width,
        points=_analyze_points(points_form),
        cap=cap,
        net=_find_net(children),
        props=_find_child_props(children, rkt),
        comments=list(node.leading_comments),
        sub_form_comments=_sub_form_comments(children),
    )


def _analyze_rect(node: _List, default_pdk: str, rkt):
    children = _children_after_head(node)
    layer_form = _find_form("layer", children)
    if layer_form is None:
        raise _schema_error("rect", "(rect (layer …) x1 y1 x2 y2)", "no layer", node)
    layer = _analyze_layer(layer_form.children[1], default_pdk, rkt)
    # Remaining (non-form) children are the coordinates in order.
    coords = [c for c in children if isinstance(c, _Atom)]
    if len(coords) < 4:
        raise _schema_error("rect", "x1 y1 x2 y2", "fewer than 4 coords", node)
    x1, y1, x2, y2 = (_atom_int(c) for c in coords[:4])
    if None in (x1, y1, x2, y2):
        raise _schema_error("rect-coords", "four ints", "non-int", node)
    return rkt.Rect(
        layer=layer, x1=x1, y1=y1, x2=x2, y2=y2,
        net=_find_net(children),
        props=_find_child_props(children, rkt),
        comments=list(node.leading_comments),
        sub_form_comments=_sub_form_comments(children),
    )


def _analyze_port_shape(node: _List, rkt):
    head = _head_symbol(node)
    if head == "rect" and len(node.children) == 5:
        ints = [_atom_int(c) for c in node.children[1:]]
        if None in ints:
            raise _schema_error("port-shape", "four ints", "non-int", node)
        return rkt.RectShape(x1=ints[0], y1=ints[1], x2=ints[2], y2=ints[3])
    if head == "poly":
        pts = []
        for c in node.children[1:]:
            if not isinstance(c, _List) or len(c.children) != 2:
                raise _schema_error("port-poly-point", "(x y)", str(c), c)
            x = _atom_int(c.children[0])
            y = _atom_int(c.children[1])
            if x is None or y is None:
                raise _schema_error("port-poly-point", "two ints", str(c), c)
            pts.append((x, y))
        return rkt.PolyShape(points=pts)
    raise _schema_error("port-shape", "(rect …) or (poly …)", head or "?", node)


def _analyze_port(node: _List, default_pdk: str, rkt):
    children = _children_after_head(node)
    name_form = _find_form("name", children)
    dir_form = _find_form("dir", children)
    layer_form = _find_form("layer", children)
    shape_form = _find_form("shape", children)
    flags_form = _find_form("flags", children)
    if not (name_form and dir_form and layer_form and shape_form):
        raise _schema_error(
            "port",
            "(port (name …) (dir …) (layer …) (shape …))",
            "missing required sub-form",
            node,
        )
    # name accepts symbol or string.
    name_atom = name_form.children[1]
    name = _atom_string(name_atom) or _atom_text(name_atom)
    if name is None:
        raise _schema_error("port-name", "symbol or string", str(name_atom), name_atom)
    dir_text = _atom_text(dir_form.children[1])
    dir_map = {
        "input": rkt.PortDirection.INPUT,
        "output": rkt.PortDirection.OUTPUT,
        "inout": rkt.PortDirection.INOUT,
        "unspecified": rkt.PortDirection.UNSPECIFIED,
    }
    direction = dir_map.get(dir_text)
    if direction is None:
        raise _schema_error("port-dir", "input|output|inout|unspecified",
                            dir_text or "?", dir_form)
    layer = _analyze_layer(layer_form.children[1], default_pdk, rkt)
    shape = _analyze_port_shape(shape_form.children[1], rkt)
    flags = []
    if flags_form:
        flag_map = {
            "signal": rkt.PortFlag.SIGNAL,
            "power": rkt.PortFlag.POWER,
            "ground": rkt.PortFlag.GROUND,
            "clock": rkt.PortFlag.CLOCK,
            "analog": rkt.PortFlag.ANALOG,
            "scan": rkt.PortFlag.SCAN,
        }
        for c in flags_form.children[1:]:
            t = _atom_text(c)
            if t in flag_map:
                flags.append(flag_map[t])
    return rkt.Port(
        name=name,
        direction=direction,
        layer=layer,
        flags=flags,
        shape=shape,
        net=_find_net(children),
        props=_find_child_props(children, rkt),
        comments=list(node.leading_comments),
        sub_form_comments=_sub_form_comments(children),
    )


def _analyze_label(node: _List, default_pdk: str, rkt):
    children = _children_after_head(node)
    layer_form = _find_form("layer", children)
    text_form = _find_form("text", children)
    origin_form = _find_form("origin", children)
    if not (layer_form and text_form and origin_form):
        raise _schema_error(
            "label", "(label (layer …) (text …) (origin …))",
            "missing", node)
    text_atom = text_form.children[1]
    text = _atom_string(text_atom) or _atom_text(text_atom) or ""
    ox = _atom_int(origin_form.children[1])
    oy = _atom_int(origin_form.children[2]) if len(origin_form.children) > 2 else None
    if ox is None or oy is None:
        raise _schema_error("label-origin", "(origin x y) with two ints", "non-int", origin_form)
    cls_form = _find_form("class", children)
    cls = _atom_text(cls_form.children[1]) if cls_form and len(cls_form.children) > 1 else None
    kind_form = _find_form("kind", children)
    kind = rkt.LabelKind.NET_NAME
    if kind_form and len(kind_form.children) > 1:
        kind_text = _atom_text(kind_form.children[1])
        if kind_text is not None:
            try:
                kind = rkt.LabelKind(kind_text)
            except ValueError:
                raise _schema_error(
                    "label-kind",
                    f"one of {[k.value for k in rkt.LabelKind]}",
                    kind_text,
                    kind_form,
                )
    internal_form = _find_form("internal", children)
    internal = False
    if internal_form and len(internal_form.children) > 1:
        internal_text = _atom_text(internal_form.children[1])
        if internal_text in ("#t", "true"):
            internal = True
        elif internal_text in ("#f", "false"):
            internal = False
        else:
            raise _schema_error(
                "label-internal", "#t / #f / true / false",
                internal_text or "non-symbol", internal_form,
            )
    return rkt.Label(
        layer=_analyze_layer(layer_form.children[1], default_pdk, rkt),
        text=text,
        origin=(ox, oy),
        cls=cls,
        kind=kind,
        internal=internal,
        props=_find_child_props(children, rkt),
        comments=list(node.leading_comments),
        sub_form_comments=_sub_form_comments(children),
    )


def _analyze_sref(node: _List, rkt):
    children = _children_after_head(node)
    cell_form = _find_form("cell", children)
    origin_form = _find_form("origin", children)
    if not (cell_form and origin_form):
        raise _schema_error("sref", "(sref (cell …) (origin x y))", "missing", node)
    cell_name = _atom_text(cell_form.children[1]) or _atom_string(cell_form.children[1])
    ox = _atom_int(origin_form.children[1])
    oy = _atom_int(origin_form.children[2]) if len(origin_form.children) > 2 else None
    if cell_name is None or ox is None or oy is None:
        raise _schema_error("sref", "cell name + origin x y", "invalid", node)
    rot_form = _find_form("rot", children)
    mag_form = _find_form("mag", children)
    refl_form = _find_form("reflect", children)
    rot = _atom_float(rot_form.children[1]) if rot_form and len(rot_form.children) > 1 else 0.0
    mag = _atom_float(mag_form.children[1]) if mag_form and len(mag_form.children) > 1 else 1.0
    reflect = False
    if refl_form and len(refl_form.children) > 1:
        t = _atom_text(refl_form.children[1])
        reflect = t == "true"
    return rkt.SRef(
        cell=cell_name,
        origin=(ox, oy),
        rot=rot or 0.0,
        mag=mag if mag is not None else 1.0,
        reflect=reflect,
        props=_find_child_props(children, rkt),
        comments=list(node.leading_comments),
        sub_form_comments=_sub_form_comments(children),
    )


def _analyze_aref(node: _List, rkt):
    children = _children_after_head(node)
    cell_form = _find_form("cell", children)
    origin_form = _find_form("origin", children)
    cols_form = _find_form("cols", children)
    rows_form = _find_form("rows", children)
    cp_form = _find_form("col_pitch", children)
    rp_form = _find_form("row_pitch", children)
    if not (cell_form and origin_form and cols_form and rows_form and cp_form and rp_form):
        raise _schema_error("aref", "required sub-forms", "missing", node)
    cell_name = _atom_text(cell_form.children[1])
    cols = _atom_int(cols_form.children[1])
    rows = _atom_int(rows_form.children[1])
    ox = _atom_int(origin_form.children[1])
    oy = _atom_int(origin_form.children[2])
    cpx = _atom_int(cp_form.children[1])
    cpy = _atom_int(cp_form.children[2])
    rpx = _atom_int(rp_form.children[1])
    rpy = _atom_int(rp_form.children[2])
    return rkt.ARef(
        cell=cell_name,
        origin=(ox, oy),
        cols=cols, rows=rows,
        col_pitch=(cpx, cpy),
        row_pitch=(rpx, rpy),
        props=_find_child_props(children, rkt),
        comments=list(node.leading_comments),
        sub_form_comments=_sub_form_comments(children),
    )


def _analyze_props_element(node: _List, rkt):
    children = _children_after_head(node)
    items = [_analyze_property(c, rkt) for c in children
             if isinstance(c, _List)]
    return rkt.Props(
        items=items,
        comments=list(node.leading_comments),
        sub_form_comments=_sub_form_comments(children),
    )


def _analyze_element(node: _List, default_pdk: str, rkt):
    head = _head_symbol(node)
    if head == "poly": return _analyze_poly(node, default_pdk, rkt)
    if head == "path": return _analyze_path(node, default_pdk, rkt)
    if head == "rect": return _analyze_rect(node, default_pdk, rkt)
    if head == "port": return _analyze_port(node, default_pdk, rkt)
    if head == "label": return _analyze_label(node, default_pdk, rkt)
    if head == "sref": return _analyze_sref(node, rkt)
    if head == "aref": return _analyze_aref(node, rkt)
    if head == "props": return _analyze_props_element(node, rkt)
    # Additive schema: unknown sub-forms inside a cell are dropped.
    return None


def _analyze_meta(node: _List, rkt):
    children = _children_after_head(node)
    gen_form = _find_form("generator", children)
    if gen_form is None or len(gen_form.children) < 2:
        raise _schema_error("meta", "(generator …)", "missing", node)
    generator = _atom_string(gen_form.children[1]) or _atom_text(gen_form.children[1]) or ""
    params: list = []
    params_form = _find_form("params", children)
    if params_form:
        params = [_analyze_property(c, rkt)
                  for c in _children_after_head(params_form)
                  if isinstance(c, _List)]
    def _opt_str(name: str) -> str | None:
        f = _find_form(name, children)
        if f and len(f.children) > 1:
            return _atom_string(f.children[1]) or _atom_text(f.children[1])
        return None
    return rkt.Meta(
        generator=generator,
        params=params,
        source=_opt_str("source"),
        generated=_opt_str("generated"),
        digest=_opt_str("digest"),
        comments=list(node.leading_comments),
        sub_form_comments=_sub_form_comments(children),
    )


def _analyze_cell(node: _List, default_pdk: str, rkt):
    children = _children_after_head(node)
    if not children:
        raise _schema_error("cell", "(cell <name> …)", "empty", node)
    name = _atom_text(children[0]) or _atom_string(children[0])
    if name is None:
        raise _schema_error("cell", "cell name", str(children[0]), children[0])
    meta_form = _find_form("meta", children[1:])
    meta = _analyze_meta(meta_form, rkt) if meta_form else None
    elements = []
    for c in children[1:]:
        if not isinstance(c, _List):
            continue
        if _head_symbol(c) == "meta":
            continue
        el = _analyze_element(c, default_pdk, rkt)
        if el is not None:
            elements.append(el)
    return rkt.Cell(
        name=name,
        elements=elements,
        comments=list(node.leading_comments),
        sub_form_comments=_sub_form_comments(children),
        meta=meta,
    )


def _analyze_document(root: _List, rkt):
    if _head_symbol(root) != "layout":
        raise _schema_error("document", "(layout …)", _head_symbol(root) or "?", root)
    children = _children_after_head(root)
    version_form = _find_form("version", children)
    version = _atom_int(version_form.children[1]) if version_form else 1
    if version != 1:
        raise _schema_error("layout-version", "1", str(version), version_form or root)
    pdk_form = _find_form("pdk", children)
    pdk = _atom_text(pdk_form.children[1]) if pdk_form else "sky130"
    units_form = _find_form("units", children)
    dbu_nm = 1
    uu_um = 1
    if units_form:
        for c in _children_after_head(units_form):
            if not isinstance(c, _List) or len(c.children) != 2:
                continue
            head = _head_symbol(c)
            v = _atom_int(c.children[1]) or 1
            if head == "dbu_nm":
                dbu_nm = v
            elif head == "uu_um":
                uu_um = v
    top_form = _find_form("top", children)
    top_cell = _atom_text(top_form.children[1]) if top_form else None
    imports = []
    for c in children:
        if isinstance(c, _List) and _head_symbol(c) == "import":
            if len(c.children) >= 2:
                p = _atom_string(c.children[1]) or _atom_text(c.children[1])
                if p:
                    imports.append(rkt.Import(
                        path=p, comments=list(c.leading_comments)))
    cells = [_analyze_cell(c, pdk, rkt) for c in children
             if isinstance(c, _List) and _head_symbol(c) == "cell"]
    return rkt.Document(
        cells=cells,
        imports=imports,
        pdk=pdk,
        version=version,
        units=rkt.Units(dbu_nm=dbu_nm, uu_um=uu_um),
        top_cell=top_cell,
        header_comments=list(root.leading_comments),
    )


# ─── Public read / load ───────────────────────────────────────────────


def read(text: str):
    """Parse a single `.rkt` source string into a `Document`."""
    from rekolektion.io import rkt  # late import to dodge circular dep
    tokens = list(_tokenize(text))
    forms = _parse(tokens)
    if not forms:
        raise ParseError(1, 1, "empty input")
    if len(forms) > 1:
        raise ParseError(1, 1, "expected single (layout …) form")
    root = forms[0]
    if not isinstance(root, _List):
        raise ParseError(1, 1, "top-level form must be (layout …)")
    return _analyze_document(root, rkt)


def read_file(path: str | Path):
    return read(Path(path).read_text())


@dataclass
class Library:
    documents: dict
    cell_index: dict
    top_cell: str | None = None


def load(path: str | Path) -> Library:
    """Multi-file load — walks `(import …)` references with cycle
    detection. Paths resolve relative to the importing file.
    """
    root_abs = Path(path).resolve()
    documents: dict = {}
    cell_index: dict = {}
    visiting: list[str] = []

    def visit(p: Path):
        key = str(p.resolve())
        # Cycle check FIRST: a file currently on the visiting stack
        # is also already in `documents` (we add before recursing),
        # so the documents-membership check has to come second.
        if key in visiting:
            raise ImportCycleError(cycle=visiting + [key])
        if key in documents:
            return
        visiting.append(key)
        doc = read_file(p)
        documents[key] = doc
        for c in doc.cells:
            if c.name not in cell_index:
                cell_index[c.name] = key
        # Recurse into imports relative to this file.
        base = p.parent
        for imp in doc.imports:
            ip = Path(imp.path)
            target = ip if ip.is_absolute() else (base / ip)
            visit(target)
        visiting.pop()

    visit(root_abs)
    root_doc = documents[str(root_abs)]
    return Library(
        documents=documents,
        cell_index=cell_index,
        top_cell=root_doc.top_cell,
    )
