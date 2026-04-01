// Behavioral Verilog model for sram_16x8_mux1
// 16 words x 8 bits, mux 1
// Array: 16 rows x 8 columns
// Address bits: 4
// Byte-enable bits: 1

module sram_16x8_mux1 (
    input  wire               clk,
    input  wire               rst_n,
    input  wire               we,
    input  wire               cs,
    input  wire [3:0]  addr,
    input  wire [7:0]  din,
    output reg  [7:0]  dout,
    input  wire [0:0]  ben,
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

    // ICG — latch CEN on CLK low, AND with CLK
    wire clk_gated;
    reg cen_latched;
    /* verilator lint_off LATCH */
    always_latch if (!clk) cen_latched = cen;
    /* verilator lint_on LATCH */
    assign clk_gated = clk & cen_latched;

    reg [7:0] mem [0:15];

    // Registered inputs (captured at posedge)
    reg [3:0] addr_reg;
    reg              we_reg;
    reg              cs_reg;
    reg [7:0]  din_reg;
    reg [0:0]  ben_reg;
    reg              wl_off_reg;
    reg              sleep_reg;

    // Block 1: Register inputs at posedge (blocking — OpenRAM pattern)
    /* verilator lint_off BLKSEQ */
    always @(posedge clk_gated) begin
        addr_reg = addr;
        we_reg   = we;
        cs_reg   = cs;
        din_reg  = din;
        ben_reg  = ben;
        wl_off_reg = wl_off;
        sleep_reg = sleep;
    end
    /* verilator lint_on BLKSEQ */

    // Block 2: Write at negedge
    /* verilator lint_off BLKSEQ */
    always @(negedge clk_gated) begin
        if (cs_reg && !wl_off_reg && !sleep_reg && we_reg) begin
            if (ben_reg[0]) mem[addr_reg][7:0] = din_reg[7:0];
        end
    end
    /* verilator lint_on BLKSEQ */

    // Block 3: Read at negedge (DOUT valid before next posedge)
    always @(negedge clk_gated) begin
        if (cs_reg && !wl_off_reg && !sleep_reg && !we_reg)
            dout <= mem[addr_reg];
    end

endmodule
