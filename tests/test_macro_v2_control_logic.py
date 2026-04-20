import gdstk
import pytest

from rekolektion.macro_v2.control_logic import ControlLogic


def test_control_logic_default_uses_replica():
    cl = ControlLogic()
    assert cl.use_replica is True


def test_control_logic_can_disable_replica():
    cl = ControlLogic(use_replica=False)
    assert cl.use_replica is False


def test_control_logic_default_name_reflects_replica():
    assert ControlLogic().top_cell_name == "ctrl_logic_rbl"
    assert ControlLogic(use_replica=False).top_cell_name == "ctrl_logic_delay"


def test_control_logic_builds_with_dff_and_nand_refs():
    cl = ControlLogic()
    lib = cl.build()
    top = next(c for c in lib.cells if c.name == cl.top_cell_name)
    dff_refs = [r for r in top.references if "dff" in r.cell.name]
    nand_refs = [r for r in top.references if "nand" in r.cell.name]
    assert len(dff_refs) >= 4, "expected at least 4 DFFs (clk_buf, p_en_bar, s_en, w_en)"
    assert len(nand_refs) >= 2, "expected at least 2 NAND2 combinational gates"


def test_control_logic_delay_chain_variant_builds():
    cl = ControlLogic(use_replica=False)
    lib = cl.build()
    top = next(c for c in lib.cells if c.name == cl.top_cell_name)
    assert len(top.references) > 0


@pytest.mark.magic
@pytest.mark.parametrize("use_replica", [True, False])
def test_control_logic_drc_clean(tmp_path, use_replica):
    from rekolektion.verify.drc import run_drc
    cell_name = f"ctrl_{'rbl' if use_replica else 'delay'}"
    cl = ControlLogic(use_replica=use_replica, name=cell_name)
    lib = cl.build()
    gds = tmp_path / f"{cell_name}.gds"
    lib.write_gds(str(gds))
    result = run_drc(gds, cell_name=cell_name, output_dir=tmp_path)
    assert result.clean, f"use_replica={use_replica}: {result.errors}"
