* NGSPICE file created from cim_mwl_driver.ext - technology: sky130B

.subckt cim_mwl_driver MWL_EN MWL VSS VDD
X0 a_60_230# MWL a_60_96# VSUBS sky130_fd_pr__nfet_01v8 ad=0.1386 pd=1.5 as=0.1092 ps=0.94 w=0.42 l=0.15
X1 a_60_96# MWL_EN a_60_0# VSUBS sky130_fd_pr__nfet_01v8 ad=0.1092 pd=0.94 as=0.1386 ps=1.5 w=0.42 l=0.15
X2 a_248_96# MWL_EN a_248_0# w_212_n56# sky130_fd_pr__pfet_01v8 ad=0.2184 pd=1.36 as=0.2772 ps=2.34 w=0.84 l=0.15
X3 a_248_230# MWL a_248_96# w_212_n56# sky130_fd_pr__pfet_01v8 ad=0.2772 pd=2.34 as=0.2184 ps=1.36 w=0.84 l=0.15
.ends

