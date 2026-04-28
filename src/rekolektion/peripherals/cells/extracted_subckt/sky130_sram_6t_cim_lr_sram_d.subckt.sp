* NGSPICE file created from sky130_sram_6t_cim_lr.ext - technology: sky130B

.subckt sky130_sram_6t_cim_lr BL BLB WL MWL MBL VDD VSS
X0 a_36_272# a_36_272# VSS VSUBS sky130_fd_pr__nfet_01v8 ad=0.0735 pd=0.77 as=0.0819 ps=0.81 w=0.42 l=0.15
X1 a_265_402# a_36_372# a_265_302# w_214_n52# sky130_fd_pr__pfet_01v8 ad=0.1344 pd=1.48 as=0.0735 ps=0.77 w=0.42 l=0.15
X2 a_62_616# MWL a_36_164# VSUBS sky130_fd_pr__nfet_01v8 ad=0.1386 pd=1.5 as=0.1386 ps=1.5 w=0.42 l=0.15
X3 MBL a_62_616# sky130_fd_pr__cap_mim_m3_1 l=1.45 w=1
X4 VSS a_36_164# a_62_94# VSUBS sky130_fd_pr__nfet_01v8 ad=0.0819 pd=0.81 as=0.0735 ps=0.77 w=0.42 l=0.15
X5 a_36_164# WL a_265_0# w_214_n52# sky130_fd_pr__pfet_01v8 ad=0.0735 pd=0.77 as=0.1344 ps=1.48 w=0.42 l=0.15
X6 a_265_302# a_36_272# VDD w_214_n52# sky130_fd_pr__pfet_01v8 ad=0.0735 pd=0.77 as=0.0819 ps=0.81 w=0.42 l=0.15
X7 a_62_94# WL BL VSUBS sky130_fd_pr__nfet_01v8 ad=0.0735 pd=0.77 as=0.1344 ps=1.48 w=0.42 l=0.15
X8 VDD a_36_164# a_36_164# w_214_n52# sky130_fd_pr__pfet_01v8 ad=0.0819 pd=0.81 as=0.0735 ps=0.77 w=0.42 l=0.15
X9 BLB a_36_372# a_36_272# VSUBS sky130_fd_pr__nfet_01v8 ad=0.1344 pd=1.48 as=0.0735 ps=0.77 w=0.42 l=0.15
.ends

