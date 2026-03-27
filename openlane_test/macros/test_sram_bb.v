// Blackbox stub for test_sram (OpenSTA/synthesis use only)
(* blackbox *)
module test_sram (
    input  wire        clk,
    input  wire        we,
    input  wire        cs,
    input  wire [5:0]  addr,
    input  wire [7:0]  din,
    output wire [7:0]  dout,
    inout  wire        VPWR,
    inout  wire        VGND
);
endmodule
