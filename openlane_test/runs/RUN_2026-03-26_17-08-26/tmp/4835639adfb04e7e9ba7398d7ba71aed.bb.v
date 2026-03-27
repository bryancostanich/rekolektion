// Behavioral Verilog model for test_sram
// 64 words x 8 bits, mux 2
// Array: 32 rows x 16 columns
// Address bits: 6

module test_sram (
    input  wire               clk,
    input  wire               we,
    input  wire               cs,
    input  wire [5:0]  addr,
    input  wire [7:0]  din,
    output reg  [7:0]  dout,
    inout  wire              VPWR,
    inout  wire              VGND
);

    reg [7:0] mem [0:63];

    always @(posedge clk) begin
        if (cs) begin
            if (we)
                mem[addr] <= din;
            else
                dout <= mem[addr];
        end
    end

endmodule

