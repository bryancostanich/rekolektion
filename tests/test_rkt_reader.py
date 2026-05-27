"""Tests for the Python `.rkt` reader (`rekolektion.io.rkt.read*`).

Covers parse + analyze for every schema form, error classification
for the negative cases listed in `docs/plans/rkt_python_reader.md`,
and round-trip parity with the Python writer.
"""

from __future__ import annotations

import os
import tempfile
from pathlib import Path

import pytest

from rekolektion.io import rkt


# ─── Smoke / empty layout ─────────────────────────────────────────────


def test_empty_layout():
    src = "(layout (version 1) (pdk sky130))\n"
    doc = rkt.read(src)
    assert doc.version == 1
    assert doc.pdk == "sky130"
    assert doc.cells == []


def test_layout_units_default():
    doc = rkt.read("(layout (version 1) (pdk sky130))")
    assert doc.units.dbu_nm == 1
    assert doc.units.uu_um == 1


def test_layout_units_explicit():
    src = "(layout (version 1) (pdk sky130) (units (dbu_nm 5) (uu_um 1)))"
    doc = rkt.read(src)
    assert doc.units.dbu_nm == 5
    assert doc.units.uu_um == 1


# ─── Comments survive parse ───────────────────────────────────────────


def test_header_comments_attach():
    src = "; provenance: 2026-05-13 generator\n(layout (version 1) (pdk sky130))"
    doc = rkt.read(src)
    assert doc.header_comments == ["provenance: 2026-05-13 generator"]


def test_cell_leading_comment_attaches():
    src = (
        "(layout (version 1) (pdk sky130)\n"
        "  ; bitcell core\n"
        "  (cell bit))\n"
    )
    doc = rkt.read(src)
    assert doc.cells[0].comments == ["bitcell core"]


def test_element_leading_comment_attaches():
    src = (
        "(layout (version 1) (pdk sky130)\n"
        "  (cell c\n"
        "    ; metal-1 bitline\n"
        "    (poly (layer sky130:met1) (points (0 0) (1 0) (1 1)))))\n"
    )
    doc = rkt.read(src)
    poly = doc.cells[0].elements[0]
    assert poly.comments == ["metal-1 bitline"]


# ─── Every element variant parses ─────────────────────────────────────


def test_poly_parses():
    src = (
        "(layout (version 1) (pdk sky130)\n"
        "  (cell c (poly (layer sky130:met1) (points (0 0) (10 0) (10 5)))))\n"
    )
    doc = rkt.read(src)
    poly = doc.cells[0].elements[0]
    assert poly.layer.name == "met1"
    assert poly.points == [(0, 0), (10, 0), (10, 5)]


def test_path_parses_with_width_and_cap():
    src = (
        "(layout (version 1) (pdk sky130)\n"
        "  (cell c (path (layer sky130:li1) (width 170) (points (0 0) (500 0)) (cap round))))\n"
    )
    doc = rkt.read(src)
    p = doc.cells[0].elements[0]
    assert p.width == 170
    assert p.cap == "round"
    assert p.points == [(0, 0), (500, 0)]


def test_rect_parses():
    src = (
        "(layout (version 1) (pdk sky130)\n"
        "  (cell c (rect (layer sky130:met1) 0 0 100 50)))\n"
    )
    doc = rkt.read(src)
    r = doc.cells[0].elements[0]
    assert (r.x1, r.y1, r.x2, r.y2) == (0, 0, 100, 50)


def test_port_parses_with_flags():
    src = (
        "(layout (version 1) (pdk sky130)\n"
        "  (cell c\n"
        "    (port (name VDD) (dir inout) (layer sky130:met4) (flags power)\n"
        "          (shape (rect 0 0 100 10)))))\n"
    )
    doc = rkt.read(src)
    p = doc.cells[0].elements[0]
    assert p.name == "VDD"
    assert p.direction == rkt.PortDirection.INOUT
    assert p.flags == [rkt.PortFlag.POWER]
    assert isinstance(p.shape, rkt.RectShape)


def test_port_name_as_quoted_string_for_bus_brackets():
    src = (
        "(layout (version 1) (pdk sky130)\n"
        "  (cell c\n"
        "    (port (name \"BL[0]\") (dir input) (layer sky130:met2) (flags signal)\n"
        "          (shape (rect 0 0 10 10)))))\n"
    )
    doc = rkt.read(src)
    assert doc.cells[0].elements[0].name == "BL[0]"


def test_sref_parses():
    src = (
        "(layout (version 1) (pdk sky130)\n"
        "  (cell parent (sref (cell child) (origin 100 200))))\n"
    )
    doc = rkt.read(src)
    s = doc.cells[0].elements[0]
    assert s.cell == "child"
    assert s.origin == (100, 200)


def test_aref_parses():
    src = (
        "(layout (version 1) (pdk sky130)\n"
        "  (cell parent\n"
        "    (aref (cell child) (origin 0 0)\n"
        "          (cols 64) (rows 1) (col_pitch 10 0) (row_pitch 0 5))))\n"
    )
    doc = rkt.read(src)
    a = doc.cells[0].elements[0]
    assert a.cols == 64
    assert a.col_pitch == (10, 0)


def test_props_with_scalar_and_tuple():
    src = (
        "(layout (version 1) (pdk sky130)\n"
        "  (cell c\n"
        "    (props (bbox 0 0 100 50) (description \"hi\"))))\n"
    )
    doc = rkt.read(src)
    props = doc.cells[0].elements[0]
    bbox = next(p for p in props.items if p.key == "bbox")
    desc = next(p for p in props.items if p.key == "description")
    assert isinstance(bbox.value, rkt.PropTuple)
    assert bbox.value.values == (0, 0, 100, 50)
    assert desc.value == "hi"


# ─── Layer references ─────────────────────────────────────────────────


def test_named_layer_parses():
    src = "(layout (version 1) (pdk sky130) (cell c (rect (layer sky130:met1) 0 0 1 1)))"
    doc = rkt.read(src)
    layer = doc.cells[0].elements[0].layer
    assert layer.pdk == "sky130"
    assert layer.name == "met1"


def test_unknown_layer_parses_with_n_d():
    src = "(layout (version 1) (pdk sky130) (cell c (rect (layer unknown:94/20) 0 0 1 1)))"
    doc = rkt.read(src)
    layer = doc.cells[0].elements[0].layer
    assert layer.kind == "unknown"
    assert (layer.number, layer.datatype) == (94, 20)


# ─── Round-trip ───────────────────────────────────────────────────────


def test_round_trip_simple():
    original = rkt.Document(
        cells=[
            rkt.Cell(name="bit", elements=[
                rkt.Poly(
                    layer=rkt.named("sky130", "met1"),
                    points=[(0, 0), (100, 0), (100, 50), (0, 50)],
                    net="BL",
                ),
            ]),
        ],
        top_cell="bit",
    )
    text = rkt.write(original)
    reread = rkt.read(text)
    assert reread.top_cell == "bit"
    assert reread.cells[0].name == "bit"
    assert reread.cells[0].elements[0].net == "BL"


def test_round_trip_with_bbox_proptuple():
    original = rkt.Document(
        cells=[
            rkt.Cell(name="c", elements=[
                rkt.Props(items=[
                    rkt.Property(key="bbox", value=rkt.prop_tuple(-1140, -720, 6430, 720)),
                    rkt.Property(key="description", value="hi"),
                ]),
            ]),
        ],
    )
    text = rkt.write(original)
    reread = rkt.read(text)
    bbox = next(p for p in reread.cells[0].elements[0].items if p.key == "bbox")
    assert isinstance(bbox.value, rkt.PropTuple)
    assert bbox.value.values == (-1140, -720, 6430, 720)


def test_round_trip_with_comments():
    src = (
        "; file header\n"
        "(layout (version 1) (pdk sky130)\n"
        "  ; cell header\n"
        "  (cell c\n"
        "    ; element\n"
        "    (poly (layer sky130:met1) (points (0 0) (1 0) (1 1)))))\n"
    )
    doc = rkt.read(src)
    text2 = rkt.write(doc)
    doc2 = rkt.read(text2)
    assert doc2.header_comments == ["file header"]
    assert doc2.cells[0].comments == ["cell header"]
    assert doc2.cells[0].elements[0].comments == ["element"]


# ─── Errors ───────────────────────────────────────────────────────────


def test_unterminated_string_raises_parse_error():
    with pytest.raises(rkt.ParseError) as info:
        rkt.read('(layout "oops')
    assert "unterminated" in info.value.message


def test_unexpected_close_paren_raises_parse_error():
    with pytest.raises(rkt.ParseError):
        rkt.read("(layout))")


def test_wrong_version_raises_schema_error():
    with pytest.raises(rkt.SchemaError) as info:
        rkt.read("(layout (version 99) (pdk sky130))")
    assert info.value.form_kind == "layout-version"


def test_bad_port_dir_raises_schema_error():
    src = (
        "(layout (version 1) (pdk sky130)\n"
        "  (cell c (port (name X) (dir wibble) (layer sky130:met3) (flags signal)\n"
        "                (shape (rect 0 0 10 10)))))\n"
    )
    with pytest.raises(rkt.SchemaError) as info:
        rkt.read(src)
    assert info.value.form_kind == "port-dir"


# ─── Import resolution + Library ──────────────────────────────────────


def test_library_load_resolves_imports():
    with tempfile.TemporaryDirectory() as d:
        d = Path(d)
        primitives = d / "primitives"
        primitives.mkdir()
        prim_path = primitives / "shared.rkt"
        prim_path.write_text("(layout (version 1) (pdk sky130) (cell shared))")
        macro = d / "macro.rkt"
        macro.write_text(
            "(layout (version 1) (pdk sky130)\n"
            '  (import "primitives/shared.rkt")\n'
            "  (cell parent (sref (cell shared) (origin 0 0))))\n"
        )
        lib = rkt.load(macro)
        # Both files documented.
        assert len(lib.documents) == 2
        assert "shared" in lib.cell_index
        assert "parent" in lib.cell_index
        # Shared cell sourced from primitives file.
        assert lib.cell_index["shared"].endswith("shared.rkt")


def test_library_load_detects_cycles():
    with tempfile.TemporaryDirectory() as d:
        d = Path(d)
        a = d / "a.rkt"
        b = d / "b.rkt"
        a.write_text(
            "(layout (version 1) (pdk sky130) (import \"b.rkt\") (cell aOnly))"
        )
        b.write_text(
            "(layout (version 1) (pdk sky130) (import \"a.rkt\") (cell bOnly))"
        )
        with pytest.raises(rkt.ImportCycleError):
            rkt.load(a)


# ─── Cross-language parity (Python writer ↔ Python reader) ────────────


def test_python_writer_python_reader_round_trip_bbox():
    doc = rkt.Document(
        cells=[
            rkt.Cell(name="c", elements=[
                rkt.Props(items=[rkt.Property(
                    key="bbox", value=rkt.prop_tuple(0, 0, 100, 50)
                )]),
            ]),
        ],
    )
    text = rkt.write(doc)
    doc2 = rkt.read(text)
    # Re-write should be byte-identical (writer is canonical).
    assert rkt.write(doc2) == text


# ─── Sub-form comments (in-element) ───────────────────────────────────


def test_sub_form_comments_attach_on_poly():
    src = (
        "(layout (version 1) (pdk sky130)\n"
        "  (cell c\n"
        "    (poly\n"
        "      ; before (layer)\n"
        "      (layer sky130:met1)\n"
        "      ; before (points)\n"
        "      (points (0 0) (1 0) (1 1)))))\n"
    )
    doc = rkt.read(src)
    poly = doc.cells[0].elements[0]
    assert poly.sub_form_comments.get("layer") == ["before (layer)"]
    assert poly.sub_form_comments.get("points") == ["before (points)"]


def test_sub_form_comments_round_trip_through_writer():
    src = (
        "(layout (version 1) (pdk sky130)\n"
        "  (cell c\n"
        "    (rect\n"
        "      ; before (layer)\n"
        "      (layer sky130:met1) 0 0 100 50\n"
        "      ; before (net)\n"
        "      (net BL))))\n"
    )
    doc = rkt.read(src)
    text = rkt.write(doc)
    assert "; before (layer)" in text
    assert "; before (net)" in text
    # Re-read recovers the same map.
    doc2 = rkt.read(text)
    r = doc2.cells[0].elements[0]
    assert r.sub_form_comments.get("layer") == ["before (layer)"]
    assert r.sub_form_comments.get("net") == ["before (net)"]


def test_sub_form_comments_on_props_key():
    src = (
        "(layout (version 1) (pdk sky130)\n"
        "  (cell c\n"
        "    (props\n"
        "      ; before bbox\n"
        "      (bbox 0 0 100 50)\n"
        "      ; before description\n"
        "      (description \"hi\"))))\n"
    )
    doc = rkt.read(src)
    props = doc.cells[0].elements[0]
    assert props.sub_form_comments.get("bbox") == ["before bbox"]
    assert props.sub_form_comments.get("description") == ["before description"]
    text = rkt.write(doc)
    assert "; before bbox" in text
    assert "; before description" in text
