// Option E: chip-level integration test for rekolektion v1 production
// macro `sram_activation_bank` (256 words x 64 bits, mux=2).

module top_test (
`ifdef USE_POWER_PINS
    inout  wire          VPWR,
    inout  wire          VGND,
`endif
    input  wire          clk,
    input  wire          we,
    input  wire          cs,
    input  wire [7:0]    addr,
    input  wire [63:0]   din,
    output wire [63:0]   dout
);

    sram_activation_bank u_sram (
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
