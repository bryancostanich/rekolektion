def test_macro_package_imports():
    from rekolektion.macro import routing
    from rekolektion.macro import sky130_drc
    assert routing.__doc__ is not None
    assert sky130_drc.__doc__ is not None
