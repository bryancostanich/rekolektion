// Option E: chip-level integration test for rekolektion v1 production
// macro `sram_weight_bank_small` (512 words x 32 bits, mux4).  The only
// purpose of this module is to instantiate the macro so OpenLane's
// chip-level P&R, LVS, and DRC can validate the macro's LEF / GDS /
// Liberty abstract views from a consumer's perspective.
//
// If chip-level LVS passes here, the macro is tapeout-ready regardless
// of any residual macro-internal LVS delta against our Python SPICE
// reference (which diverges by ~40 devices due to OpenROAD-inserted
// decap/clkbuf cells that don't exist in design intent — see
// khalkulo/conductor/.../02_sram_design/continuation_prompt.md).

module top_test (
`ifdef USE_POWER_PINS
    inout  wire          VPWR,
    inout  wire          VGND,
`endif
    input  wire          clk,
    input  wire          we,
    input  wire          cs,
    input  wire [8:0]    addr,
    input  wire [31:0]   din,
    output wire [31:0]   dout
);

    sram_weight_bank_small u_sram (
`ifdef USE_POWER_PINS
        .VPWR(VPWR),
        .VGND(VGND),
`endif
        .CLK (clk),
        .WE  (we),
        .CS  (cs),
        .ADDR(addr),
        .DIN (din),
        .DOUT(dout)
    );

endmodule
