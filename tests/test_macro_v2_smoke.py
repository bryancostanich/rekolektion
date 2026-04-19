def test_macro_v2_package_imports():
    from rekolektion.macro_v2 import routing
    from rekolektion.macro_v2 import sky130_drc
    assert routing.__doc__ is not None
    assert sky130_drc.__doc__ is not None
