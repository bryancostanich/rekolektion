"""Tests for the Python `.rkt` writer (src/rekolektion/io/rkt.py).

These verify the canonical-text output. Where possible, examples are
round-tripped through the F# reader via `dotnet test`; here we only
check the textual contract so the test stays self-contained.
"""

from __future__ import annotations

from rekolektion.io import rkt


def test_empty_document_minimum():
    doc = rkt.Document()
    text = rkt.write(doc)
    assert text == "(layout (version 1)\n  (pdk sky130)\n  (units (dbu_nm 1) (uu_um 1)))\n"


def test_header_comments_emit_above_layout():
    doc = rkt.Document(header_comments=["provenance: tests"])
    text = rkt.write(doc)
    assert text.startswith("; provenance: tests\n(layout")


def test_cell_with_one_poly():
    doc = rkt.Document(
        cells=[
            rkt.Cell(
                name="c",
                elements=[
                    rkt.Poly(
                        layer=rkt.named("sky130", "met1"),
                        points=[(0, 0), (10, 0), (10, 5), (0, 5)],
                    ),
                ],
            ),
        ],
    )
    text = rkt.write(doc)
    assert "(cell c" in text
    assert "(layer sky130:met1)" in text
    assert "(points (0 0) (10 0) (10 5) (0 5))" in text


def test_unknown_layer_emits_unknown_prefix():
    doc = rkt.Document(
        cells=[
            rkt.Cell(
                name="c",
                elements=[
                    rkt.Poly(
                        layer=rkt.unknown(94, 20),
                        points=[(0, 0), (1, 0), (1, 1)],
                    ),
                ],
            ),
        ],
    )
    text = rkt.write(doc)
    assert "(layer unknown:94/20)" in text


def test_element_comment_emits_before_form():
    doc = rkt.Document(
        cells=[
            rkt.Cell(
                name="c",
                elements=[
                    rkt.Poly(
                        layer=rkt.named("sky130", "met1"),
                        points=[(0, 0), (1, 0), (1, 1)],
                        comments=["bitline contact"],
                    ),
                ],
            ),
        ],
    )
    text = rkt.write(doc)
    # The comment line precedes the (poly ...) form, at +1 indent.
    assert "    ; bitline contact" in text
    poly_idx = text.index("(poly")
    comment_idx = text.index("; bitline contact")
    assert comment_idx < poly_idx


def test_sref_default_orientation_omits_optional_fields():
    doc = rkt.Document(
        cells=[
            rkt.Cell(
                name="top",
                elements=[
                    rkt.SRef(cell="leaf", origin=(0, 0)),
                ],
            ),
        ],
    )
    text = rkt.write(doc)
    # Rot/mag/reflect at defaults — shouldn't appear.
    assert "(rot" not in text
    assert "(mag" not in text
    assert "(reflect" not in text


def test_sref_rotation_emits_with_decimal_point():
    doc = rkt.Document(
        cells=[
            rkt.Cell(
                name="top",
                elements=[
                    rkt.SRef(cell="leaf", origin=(0, 0), rot=90.0),
                ],
            ),
        ],
    )
    text = rkt.write(doc)
    # The reader classifies floats by lexeme; an int-shaped "90"
    # would round-trip as int and lose its float-ness.
    assert "(rot 90.0)" in text


def test_writer_emits_no_nets_block():
    # Track 06 Decision 4 = C: the (nets …) block is gone from the
    # schema. Labels with Kind = NET_NAME are the sole source of truth
    # for the net set. The writer never emits a (nets …) form.
    doc = rkt.Document(
        cells=[
            rkt.Cell(
                name="c",
                elements=[
                    rkt.Label(
                        layer=rkt.named("sky130", "met1_label"),
                        text="VDD",
                        origin=(0, 0),
                    ),
                    rkt.Label(
                        layer=rkt.named("sky130", "li1_label"),
                        text="D",
                        origin=(-395, 0),
                        kind=rkt.LabelKind.DEVICE_TERMINAL,
                    ),
                ],
            ),
        ],
    )
    text = rkt.write(doc)
    assert "(nets" not in text
    # Both labels still emit; kind annotations preserved per-label.
    assert '(label (layer sky130:met1_label) (text "VDD")' in text
    assert "(kind device-terminal)" in text


def test_kind_device_terminal_emits_in_label_form():
    doc = rkt.Document(
        cells=[
            rkt.Cell(
                name="c",
                elements=[
                    rkt.Label(
                        layer=rkt.named("sky130", "li1_label"),
                        text="D",
                        origin=(-395, 0),
                        kind=rkt.LabelKind.DEVICE_TERMINAL,
                    ),
                    rkt.Label(
                        layer=rkt.named("sky130", "met1_label"),
                        text="VDD",
                        origin=(100, 0),
                    ),
                ],
            ),
        ],
    )
    text = rkt.write(doc)
    # DEVICE_TERMINAL gets the annotation; NET_NAME stays implicit.
    assert "(kind device-terminal)" in text
    assert "(kind net-name)" not in text


def test_string_label_escapes_quotes_and_newlines():
    doc = rkt.Document(
        cells=[
            rkt.Cell(
                name="c",
                elements=[
                    rkt.Label(
                        layer=rkt.named("sky130", "met1"),
                        text='with "quotes" and\nnewlines',
                        origin=(0, 0),
                    ),
                ],
            ),
        ],
    )
    text = rkt.write(doc)
    assert r'\"quotes\"' in text
    assert r"\n" in text


def test_port_with_flags_and_rect_shape():
    doc = rkt.Document(
        cells=[
            rkt.Cell(
                name="c",
                elements=[
                    rkt.Port(
                        name="BL",
                        direction=rkt.PortDirection.INPUT,
                        layer=rkt.named("sky130", "met1"),
                        flags=[rkt.PortFlag.SIGNAL, rkt.PortFlag.SCAN],
                        shape=rkt.RectShape(0, 0, 10, 50),
                    ),
                ],
            ),
        ],
    )
    text = rkt.write(doc)
    assert "(port (name BL) (dir input)" in text
    assert "(flags signal scan)" in text
    assert "(shape (rect 0 0 10 50))" in text


def test_import_emits_quoted_path():
    doc = rkt.Document(imports=[rkt.Import(path="bitcell.rkt")])
    text = rkt.write(doc)
    assert '(import "bitcell.rkt")' in text


def test_props_value_kinds_round_trip_through_str():
    doc = rkt.Document(
        cells=[
            rkt.Cell(
                name="c",
                elements=[
                    rkt.Props(
                        items=[
                            rkt.Property(key="note", value="hello"),
                            rkt.Property(key="count", value=42),
                            rkt.Property(key="ratio", value=1.5),
                            rkt.Property(key="domain", value=rkt.Symbol("signal")),
                        ]
                    ),
                ],
            ),
        ],
    )
    text = rkt.write(doc)
    assert '(note "hello")' in text
    assert "(count 42)" in text
    assert "(ratio 1.5)" in text
    # Symbol values stay unquoted.
    assert "(domain signal)" in text


def test_aref_emits_rows_cols_pitches():
    doc = rkt.Document(
        cells=[
            rkt.Cell(
                name="top",
                elements=[
                    rkt.ARef(
                        cell="bit",
                        origin=(0, 0),
                        cols=64,
                        rows=1,
                        col_pitch=(10, 0),
                        row_pitch=(0, 5),
                    ),
                ],
            ),
        ],
    )
    text = rkt.write(doc)
    assert "(aref (cell bit) (origin 0 0)" in text
    assert "(cols 64) (rows 1)" in text
    assert "(col_pitch 10 0) (row_pitch 0 5)" in text


def test_top_cell_emits_top_form():
    doc = rkt.Document(
        cells=[rkt.Cell(name="c")],
        top_cell="c",
    )
    text = rkt.write(doc)
    assert "(top c)" in text
