* NGSPICE file created from sky130_fd_bd_sram__openram_sp_nand3_dec.ext - technology: sky130B

.subckt sky130_fd_bd_sram__openram_sp_nand3_dec GND VDD C B A Z
X0 VDD C Z VDD sky130_fd_pr__pfet_01v8 ad=0.3808 pd=2.92 as=0.3192 ps=2.81 w=1.12 l=0.15
X1 GND C a_308_187# GND sky130_fd_pr__nfet_01v8 ad=0.3071 pd=2.31 as=0.0777 ps=0.95 w=0.74 l=0.15
X2 Z B VDD VDD sky130_fd_pr__pfet_01v8 ad=0.336 pd=2.84 as=0.196 ps=1.47 w=1.12 l=0.15
X3 a_308_115# A Z GND sky130_fd_pr__nfet_01v8 ad=0.0777 pd=0.95 as=0.1961 ps=2.01 w=0.74 l=0.15
X4 VDD A Z VDD sky130_fd_pr__pfet_01v8 ad=0.196 pd=1.47 as=0.3248 ps=2.82 w=1.12 l=0.15
X5 a_308_187# B a_308_115# GND sky130_fd_pr__nfet_01v8 ad=0.0777 pd=0.95 as=0.0777 ps=0.95 w=0.74 l=0.15
.ends

