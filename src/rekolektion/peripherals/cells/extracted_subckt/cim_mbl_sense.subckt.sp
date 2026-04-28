* NGSPICE file created from cim_mbl_sense.ext - technology: sky130B

.subckt cim_mbl_sense VBIAS MBL VSS MBL_OUT VDD
X0 VDD MBL MBL_OUT VSS sky130_fd_pr__nfet_01v8 ad=0.33 pd=2.66 as=0.26 ps=1.52 w=1 l=0.15
X1 MBL_OUT VBIAS VSS VSS sky130_fd_pr__nfet_01v8 ad=0.26 pd=1.52 as=0.33 ps=2.66 w=1 l=0.15
.ends

