// Minimal top-level design for OpenLane SRAM macro integration test.
// Instantiates one test_sram macro (64x8, mux 2) with simple I/O.

module top_test (
`ifdef USE_POWER_PINS
    inout  wire VPWR,
    inout  wire VGND,
`endif
    input  wire        clk,
    input  wire        rst_n,
    input  wire        we,
    input  wire        cs,
    input  wire [5:0]  addr,
    input  wire [7:0]  din,
    output wire [7:0]  dout
);

    test_sram u_sram (
`ifdef USE_POWER_PINS
        .VPWR(VPWR),
        .VGND(VGND),
`endif
        .clk(clk),
        .we(we),
        .cs(cs),
        .addr(addr),
        .din(din),
        .dout(dout)
    );

endmodule
