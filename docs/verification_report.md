# Phase 5: Verification Report

## Generated Macros

Three SRAM macros were generated matching V1 target configurations:

| Macro | Config | Array | Addr Bits | Dimensions (um) |
|-------|--------|-------|-----------|------------------|
| weight_32kb | 1024w x 32b, mux 8 | 128 rows x 256 cols | 10 (7 row + 3 col) | 341.77 x 234.77 |
| activation_3kb | 384w x 64b, mux 2 | 192 rows x 128 cols | 9 (8 row + 1 col) | 176.03 x 323.89 |
| test_64x8 | 64w x 8b, mux 2 | 32 rows x 16 cols | 6 (5 row + 1 col) | 29.31 x 71.09 |

Each macro includes GDS layout, behavioral Verilog model, and SPICE stub.

## Density Measurement

Target density from area budget: **290,000 bits/mm^2**

| Macro | Total Bits | Area (mm^2) | Density (bits/mm^2) | vs Target |
|-------|-----------|-------------|---------------------|-----------|
| weight_32kb | 32,768 | 0.080237 | 408,388 | 140.8% |
| activation_3kb | 24,576 | 0.057014 | 431,049 | 148.6% |
| test_64x8 | 512 | 0.002084 | 245,723 | 84.7% |

The larger macros (weight, activation) exceed the 290K target by 40-49%, which is a good
result. The small test macro (64x8) is below target at 84.7% -- this is expected because
peripheral overhead dominates at small sizes (decoder width, sense amps, etc. are a larger
fraction of total area when the bitcell array is small).

## DRC Results

Magic DRC was run on all three macros using the SKY130 process deck.

| Macro | DRC Errors | Notes |
|-------|-----------|-------|
| weight_32kb | 0 | See caveat below |
| activation_3kb | 0 | See caveat below |
| test_64x8 | 0 | See caveat below |

### DRC Caveats

The 0-error DRC result should be interpreted carefully:

1. **Foundry bitcell (sky130_fd_bd_sram__sram_sp_cell_opt1)** is a pre-validated foundry cell,
   so no DRC errors are expected within the bitcell itself.

2. **Placeholder peripheral cells** (column mux, precharge) use layer 235 (annotation layer)
   which Magic reports as "Unknown layer/datatype". These cells are geometric placeholders,
   not real transistor-level layouts, so Magic skips DRC on their contents.

3. **Foundry peripheral cells** (sense amp, write driver, NAND decoder) are from the
   sky130_fd_bd_sram library and are pre-validated.

4. **Inter-cell spacing and routing** between blocks may have DRC violations that only
   appear once proper metal routing is added between the bitcell array and peripherals.

### What Needs Fixing

- **Column mux and precharge cells**: Currently placeholders using annotation layers.
  Need real transistor-level implementations with proper SKY130 layers.
- **Inter-block routing**: No metal routing connects the bitcell array to peripherals.
  Adding word line, bit line, and power routing will likely introduce DRC issues that
  need resolution.
- **Power grid**: VDD/VSS distribution not yet implemented. Power straps across the
  macro will need DRC-clean routing on metal layers.
- **Guard rings and well taps**: Not yet placed around the macro boundary. Required
  for latchup protection and will affect final area.

## Verilog Model Validation

All generated Verilog models pass validation:
- Module names match `sram_{words}x{bits}` pattern
- Address bus widths are correct (ceil(log2(words)) bits)
- Data bus widths match configured bit width
- Synchronous read/write logic with CLK, WE, CS signals present
- Memory array declared with correct depth and width

## Comparison Against Area Budget

The area budget target of 290K bits/mm^2 is met or exceeded for the two production-sized
macros (weight and activation). Key observations:

- **Weight macro (32KB)**: 408K bits/mm^2 -- 41% above target. The 8:1 mux ratio
  reduces the number of rows, making the array more area-efficient.
- **Activation macro (3KB)**: 431K bits/mm^2 -- 49% above target. Even with a 2:1 mux,
  the moderate size keeps density high.
- **Test macro (64x8)**: 246K bits/mm^2 -- 15% below target. Expected for tiny arrays
  where peripheral overhead dominates.

These density numbers are optimistic because they include placeholder peripherals that
are smaller than real implementations would be. Once real transistor-level peripherals
are designed, actual density will likely decrease by 10-20%, which should still keep the
production macros above the 290K target.
