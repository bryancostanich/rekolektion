gds read /Users/bryancostanich/Git_Repos/bryan_costanich/rekolektion/output/cim_extracted_input/sky130_sram_6t_cim_lr_sram_d.gds
load sky130_sram_6t_cim_lr
select top cell
extract all

ext2spice lvs
ext2spice -o /Users/bryancostanich/Git_Repos/bryan_costanich/rekolektion/src/rekolektion/peripherals/cells/_extract_work/sky130_sram_6t_cim_lr_extracted.spice
quit -noprompt
