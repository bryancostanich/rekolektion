# Production Features — Track Registry

| Track ID | Status | Description |
|----------|--------|-------------|
| `01_production_feature_set` | `pending` | Six production features: write enables, scan chain DFT, clock gating, power gating, wordline switchoff, burn-in test mode |
| `02_community_outreach` | `pending` | Publicize foundry SRAM cell discovery & rekolektion — blog post, Reddit, HN, FOSSi |
| `03_cim_sram_macros` | `complete` | 7T+1C CIM SRAM array macros — LVS unique / DRC clean (flat + hier) on all four variants, OpenROAD smoke route end-to-end |
| `04_sky130B_upgrade` | (see track plan) | Migration to the sky130B PDK variant |
| `05_cim_tapeout_audit` | `pending` | Pre-tapeout audit on the track 03 CIM macros: functional SPICE, bitcell margin under CIM, Calibre sign-off, antenna/density/latch-up, power integrity, independent design review |
