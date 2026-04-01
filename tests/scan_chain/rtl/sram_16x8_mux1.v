// Behavioral Verilog model for sram_16x8_mux1
// 16 words x 8 bits, mux 1
// Array: 16 rows x 8 columns
// Address bits: 4
// Scan chain: 14 flops (addr -> we -> cs -> din)

module sram_16x8_mux1 (
    input  wire               clk,
    input  wire               rst_n,
    input  wire               we,
    input  wire               cs,
    input  wire [3:0]  addr,
    input  wire [7:0]  din,
    output reg  [7:0]  dout,
    input  wire              scan_in,
    output wire              scan_out,
    input  wire              scan_en,
    inout  wire              VPWR,
    inout  wire              VGND
);

    // Scan chain registers (14 flops)
    reg [13:0] scan_chain;
    assign scan_out = scan_chain[13];

    // Muxed functional inputs
    wire [3:0] addr_int;
    wire              we_int;
    wire              cs_int;
    wire [7:0]  din_int;

    assign addr_int = scan_en ? scan_chain[3:0] : addr;
    assign we_int   = scan_en ? scan_chain[4] : we;
    assign cs_int   = scan_en ? scan_chain[5] : cs;
    assign din_int  = scan_en ? scan_chain[13:6] : din;

    // Scan shift register
    always @(posedge clk) begin
        if (scan_en)
            scan_chain <= {scan_chain[12:0], scan_in};
    end

    reg [7:0] mem [0:15];

    // Registered inputs (captured at posedge)
    reg [3:0] addr_reg;
    reg              we_reg;
    reg              cs_reg;
    reg [7:0]  din_reg;

    // Block 1: Register inputs at posedge (blocking — OpenRAM pattern)
    /* verilator lint_off BLKSEQ */
    always @(posedge clk) begin
        addr_reg = addr_int;
        we_reg   = we_int;
        cs_reg   = cs_int;
        din_reg  = din_int;
    end
    /* verilator lint_on BLKSEQ */

    // Block 2: Write at negedge
    /* verilator lint_off BLKSEQ */
    always @(negedge clk) begin
        if (cs_reg && we_reg) begin
            mem[addr_reg] = din_reg;
        end
    end
    /* verilator lint_on BLKSEQ */

    // Block 3: Read at negedge (DOUT valid before next posedge)
    always @(negedge clk) begin
        if (cs_reg && !we_reg)
            dout <= mem[addr_reg];
    end

endmodule
