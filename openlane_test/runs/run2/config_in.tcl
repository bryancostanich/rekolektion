set ::env(DESIGN_NAME) "top_test"
set ::env(VERILOG_FILES) "$::env(DESIGN_DIR)/src/top_test.v"
set ::env(VERILOG_FILES_BLACKBOX) "$::env(DESIGN_DIR)/macros/test_sram.v"

set ::env(EXTRA_LEFS) "$::env(DESIGN_DIR)/macros/test_sram.lef"
set ::env(EXTRA_GDS_FILES) "$::env(DESIGN_DIR)/macros/test_sram.gds"
set ::env(EXTRA_LIBS) "$::env(DESIGN_DIR)/macros/test_sram.lib"

set ::env(CLOCK_PORT) "clk"
set ::env(CLOCK_PERIOD) "20.0"

set ::env(FP_SIZING) "absolute"
set ::env(DIE_AREA) "0 0 200 250"
set ::env(FP_CORE_UTIL) 30

set ::env(MACRO_PLACEMENT_CFG) "$::env(DESIGN_DIR)/macro_placement.cfg"
set ::env(FP_PDN_MACRO_HOOKS) "u_sram vccd1 vssd1 VPWR VGND"

set ::env(PDK) "sky130A"
set ::env(STD_CELL_LIBRARY) "sky130_fd_sc_hd"
set ::env(STD_CELL_LIBRARY_OPT) "sky130_fd_sc_hd"
set ::env(GPL_CELL_PADDING) 0
set ::env(DPL_CELL_PADDING) 0
set ::env(RUN_VERILATOR_LINT) 0
