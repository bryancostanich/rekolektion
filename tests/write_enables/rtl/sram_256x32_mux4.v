// Behavioral Verilog model for sram_256x32_mux4
// 256 words x 32 bits, mux 4
// Array: 64 rows x 128 columns
// Address bits: 8
// Byte-enable bits: 4

module sram_256x32_mux4 (
    input  wire               clk,
    input  wire               rst_n,
    input  wire               we,
    input  wire               cs,
    input  wire [7:0]  addr,
    input  wire [31:0]  din,
    output reg  [31:0]  dout,
    input  wire [3:0]  ben,
    inout  wire              VPWR,
    inout  wire              VGND
);

    reg [31:0] mem [0:255];

    // Registered inputs (captured at posedge)
    reg [7:0] addr_reg;
    reg              we_reg;
    reg              cs_reg;
    reg [31:0]  din_reg;
    reg [3:0]  ben_reg;

    // Block 1: Register inputs at posedge (blocking — OpenRAM pattern)
    /* verilator lint_off BLKSEQ */
    always @(posedge clk) begin
        addr_reg = addr;
        we_reg   = we;
        cs_reg   = cs;
        din_reg  = din;
        ben_reg  = ben;
    end
    /* verilator lint_on BLKSEQ */

    // Block 2: Write at negedge
    /* verilator lint_off BLKSEQ */
    always @(negedge clk) begin
        if (cs_reg && we_reg) begin
            if (ben_reg[0]) mem[addr_reg][7:0] = din_reg[7:0];
            if (ben_reg[1]) mem[addr_reg][15:8] = din_reg[15:8];
            if (ben_reg[2]) mem[addr_reg][23:16] = din_reg[23:16];
            if (ben_reg[3]) mem[addr_reg][31:24] = din_reg[31:24];
        end
    end
    /* verilator lint_on BLKSEQ */

    // Block 3: Read at negedge (DOUT valid before next posedge)
    always @(negedge clk) begin
        if (cs_reg && !we_reg)
            dout <= mem[addr_reg];
    end

endmodule
