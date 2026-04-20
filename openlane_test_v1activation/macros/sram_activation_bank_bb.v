// Blackbox Verilog stub for sram_activation_bank
// 256 words x 64 bits, mux 2

(* blackbox *)
module sram_activation_bank (
`ifdef USE_POWER_PINS
    inout  wire               VPWR,
    inout  wire               VGND,
`endif
    input  wire               CLK,
    input  wire               WE,
    input  wire               CS,
    input  wire [7:0]  ADDR,
    input  wire [63:0]  DIN,
    output wire [63:0]  DOUT
);
endmodule
