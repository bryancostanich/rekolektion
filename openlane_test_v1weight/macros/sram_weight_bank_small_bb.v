// Blackbox Verilog stub for sram_weight_bank_small
// 512 words x 32 bits, mux 4

(* blackbox *)
module sram_weight_bank_small (
`ifdef USE_POWER_PINS
    inout  wire               VPWR,
    inout  wire               VGND,
`endif
    input  wire               CLK,
    input  wire               WE,
    input  wire               CS,
    input  wire [8:0]  ADDR,
    input  wire [31:0]  DIN,
    output wire [31:0]  DOUT
);
endmodule
