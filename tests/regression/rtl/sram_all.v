// Behavioral Verilog model for sram_16x16_mux1
// 16 words x 16 bits, mux 1
// Array: 16 rows x 16 columns
// Address bits: 4
// Byte-enable bits: 2
// Scan chain: 24 flops (addr -> we -> cs -> din -> ben)

module sram_16x16_mux1 (
    input  wire               clk,
    input  wire               rst_n,
    input  wire               we,
    input  wire               cs,
    input  wire [3:0]  addr,
    input  wire [15:0]  din,
    output reg  [15:0]  dout,
    input  wire [1:0]  ben,
    input  wire              scan_in,
    output wire              scan_out,
    input  wire              scan_en,
    input  wire              cen,
    input  wire              sleep,
    input  wire              wl_off,
    input  wire              tm,  // physical stress mode — no behavioral effect
    inout  wire              VPWR,
    inout  wire              VGND
);

    // tm (test mode) controls physical wordline stress — no behavioral model needed
    /* verilator lint_off UNUSEDSIGNAL */
    wire _unused_tm = tm;
    /* verilator lint_on UNUSEDSIGNAL */

    // Scan chain registers (24 flops)
    reg [23:0] scan_chain;
    assign scan_out = scan_chain[23];

    // Muxed functional inputs
    wire [3:0] addr_int;
    wire              we_int;
    wire              cs_int;
    wire [15:0]  din_int;
    wire [1:0]  ben_int;

    assign addr_int = scan_en ? scan_chain[3:0] : addr;
    assign we_int   = scan_en ? scan_chain[4] : we;
    assign cs_int   = scan_en ? scan_chain[5] : cs;
    assign din_int  = scan_en ? scan_chain[21:6] : din;
    assign ben_int  = scan_en ? scan_chain[23:22] : ben;

    // Scan shift register
    always @(posedge clk) begin
        if (scan_en)
            scan_chain <= {scan_chain[22:0], scan_in};
    end

    // ICG — latch CEN on CLK low, AND with CLK
    wire clk_gated;
    reg cen_latched;
    /* verilator lint_off LATCH */
    always_latch if (!clk) cen_latched = cen;
    /* verilator lint_on LATCH */
    assign clk_gated = clk & cen_latched;

    reg [15:0] mem [0:15];

    // Registered inputs (captured at posedge)
    reg [3:0] addr_reg;
    reg              we_reg;
    reg              cs_reg;
    reg [15:0]  din_reg;
    reg [1:0]  ben_reg;
    reg              wl_off_reg;
    reg              sleep_reg;

    // Block 1: Register inputs at posedge (blocking — OpenRAM pattern)
    /* verilator lint_off BLKSEQ */
    always @(posedge clk_gated) begin
        addr_reg = addr_int;
        we_reg   = we_int;
        cs_reg   = cs_int;
        din_reg  = din_int;
        ben_reg  = ben_int;
        wl_off_reg = wl_off;
        sleep_reg = sleep;
    end
    /* verilator lint_on BLKSEQ */

    // Block 2: Write at negedge
    /* verilator lint_off BLKSEQ */
    always @(negedge clk_gated) begin
        if (cs_reg && !wl_off_reg && !sleep_reg && we_reg) begin
            if (ben_reg[0]) mem[addr_reg][7:0] = din_reg[7:0];
            if (ben_reg[1]) mem[addr_reg][15:8] = din_reg[15:8];
        end
    end
    /* verilator lint_on BLKSEQ */

    // Block 3: Read at negedge (DOUT valid before next posedge)
    always @(negedge clk_gated) begin
        if (cs_reg && !wl_off_reg && !sleep_reg && !we_reg)
            dout <= mem[addr_reg];
    end

endmodule
