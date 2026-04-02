// Blackbox Verilog stub for sram_1024x32_mux8
// 1024 words x 32 bits, mux 8

(* blackbox *)
module sram_1024x32_mux8 (
    input  wire               clk,
    input  wire               we,
    input  wire               cs,
    input  wire [9:0]  addr,
    input  wire [31:0]  din,
    output wire [31:0]  dout,
    input  wire [3:0]  ben,
    input  wire              scan_in,
    output wire              scan_out,
    input  wire              scan_en,
    input  wire              cen,
    input  wire              sleep,
    input  wire              wl_off,
    input  wire              tm,
    inout  wire              VPWR,
    inout  wire              VGND
);
endmodule
