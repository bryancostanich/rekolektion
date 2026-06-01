(layout (version 1)
  (pdk sky130)
  (units (dbu_nm 1) (uu_um 1))
  (import "../primitives/pfet_01v8_W8p0_L2p0_core_botgate.rkt")
  (import "../primitives/pfet_01v8_W5p0_L2p0_core_botgate.rkt")
  (import "../primitives/pnp_05v5_W0p68L0p68.rkt")
  (import "../primitives/res_xhigh_po_W1p41_L14p0_nx5_core.rkt")
  (import "../primitives/nfet_01v8_W4p0_L1p0_core_topgate.rkt")
  (top bias_gen_ctat)
  (cell bias_gen_ctat
    (rect (layer sky130:nwell) -1660 -8066 4620 1990)
    (rect (layer sky130:pwell) -1722 -15402 4987 -8044)
    (sref (cell pfet_01v8_W8p0_L2p0_core_botgate) (origin 0 -3551))
    (sref (cell pfet_01v8_W5p0_L2p0_core_botgate) (origin 2960 -2010))
    (sref (cell pnp_05v5_W0p68L0p68) (origin -1476 -8253) (rot -90.0))
    (sref (cell res_xhigh_po_W1p41_L14p0_nx5_core) (origin 9824 -5335))
    (sref (cell nfet_01v8_W4p0_L1p0_core_topgate) (origin 753 -13477) (rot -90.0))
    (sref (cell nfet_01v8_W4p0_L1p0_core_topgate) (origin 3882 -10539))
    (rect (layer sky130:tap) -1480 1290 4440 1710)
    (rect (layer sky130:nsdm) -1605 1165 4565 1835)
    (rect (layer sky130:licon1) -1420 1415 -1250 1585)
    (rect (layer sky130:licon1) -1080 1415 -910 1585)
    (rect (layer sky130:licon1) -740 1415 -570 1585)
    (rect (layer sky130:licon1) -400 1415 -230 1585)
    (rect (layer sky130:licon1) -60 1415 110 1585)
    (rect (layer sky130:licon1) 280 1415 450 1585)
    (rect (layer sky130:licon1) 620 1415 790 1585)
    (rect (layer sky130:licon1) 960 1415 1130 1585)
    (rect (layer sky130:licon1) 1300 1415 1470 1585)
    (rect (layer sky130:licon1) 1640 1415 1810 1585)
    (rect (layer sky130:licon1) 1980 1415 2150 1585)
    (rect (layer sky130:licon1) 2320 1415 2490 1585)
    (rect (layer sky130:licon1) 2660 1415 2830 1585)
    (rect (layer sky130:licon1) 3000 1415 3170 1585)
    (rect (layer sky130:licon1) 3340 1415 3510 1585)
    (rect (layer sky130:licon1) 3680 1415 3850 1585)
    (rect (layer sky130:licon1) 4020 1415 4190 1585)
    (rect (layer sky130:li1) -1480 1335 4440 1665)
    (rect (layer sky130:tap) -1542 -15122 4807 -14702)
    (rect (layer sky130:psdm) -1667 -15247 4932 -14577)
    (rect (layer sky130:licon1) -1482 -14997 -1312 -14827)
    (rect (layer sky130:licon1) -1142 -14997 -972 -14827)
    (rect (layer sky130:licon1) -802 -14997 -632 -14827)
    (rect (layer sky130:licon1) -462 -14997 -292 -14827)
    (rect (layer sky130:licon1) -122 -14997 48 -14827)
    (rect (layer sky130:licon1) 218 -14997 388 -14827)
    (rect (layer sky130:licon1) 558 -14997 728 -14827)
    (rect (layer sky130:licon1) 898 -14997 1068 -14827)
    (rect (layer sky130:licon1) 1238 -14997 1408 -14827)
    (rect (layer sky130:licon1) 1578 -14997 1748 -14827)
    (rect (layer sky130:licon1) 1918 -14997 2088 -14827)
    (rect (layer sky130:licon1) 2258 -14997 2428 -14827)
    (rect (layer sky130:licon1) 2598 -14997 2768 -14827)
    (rect (layer sky130:licon1) 2938 -14997 3108 -14827)
    (rect (layer sky130:licon1) 3278 -14997 3448 -14827)
    (rect (layer sky130:licon1) 3618 -14997 3788 -14827)
    (rect (layer sky130:licon1) 3958 -14997 4128 -14827)
    (rect (layer sky130:licon1) 4298 -14997 4468 -14827)
    (rect (layer sky130:li1) -1542 -15077 4807 -14747)
    (rect (layer sky130:met1) -1660 1135 4620 1865)
    (rect (layer sky130:mcon) -1450 1365 -1280 1535)
    (rect (layer sky130:mcon) -1090 1365 -920 1535)
    (rect (layer sky130:mcon) -730 1365 -560 1535)
    (rect (layer sky130:mcon) -370 1365 -200 1535)
    (rect (layer sky130:mcon) -10 1365 160 1535)
    (rect (layer sky130:mcon) 350 1365 520 1535)
    (rect (layer sky130:mcon) 710 1365 880 1535)
    (rect (layer sky130:mcon) 1070 1365 1240 1535)
    (rect (layer sky130:mcon) 1430 1365 1600 1535)
    (rect (layer sky130:mcon) 1790 1365 1960 1535)
    (rect (layer sky130:mcon) 2150 1365 2320 1535)
    (rect (layer sky130:mcon) 2510 1365 2680 1535)
    (rect (layer sky130:mcon) 2870 1365 3040 1535)
    (rect (layer sky130:mcon) 3230 1365 3400 1535)
    (rect (layer sky130:mcon) 3590 1365 3760 1535)
    (rect (layer sky130:mcon) 3950 1365 4120 1535)
    (rect (layer sky130:met1) -1722 -15277 4987 -14547)
    (rect (layer sky130:mcon) -1512 -15047 -1342 -14877)
    (rect (layer sky130:mcon) -1152 -15047 -982 -14877)
    (rect (layer sky130:mcon) -792 -15047 -622 -14877)
    (rect (layer sky130:mcon) -432 -15047 -262 -14877)
    (rect (layer sky130:mcon) -72 -15047 98 -14877)
    (rect (layer sky130:mcon) 288 -15047 458 -14877)
    (rect (layer sky130:mcon) 648 -15047 818 -14877)
    (rect (layer sky130:mcon) 1008 -15047 1178 -14877)
    (rect (layer sky130:mcon) 1368 -15047 1538 -14877)
    (rect (layer sky130:mcon) 1728 -15047 1898 -14877)
    (rect (layer sky130:mcon) 2088 -15047 2258 -14877)
    (rect (layer sky130:mcon) 2448 -15047 2618 -14877)
    (rect (layer sky130:mcon) 2808 -15047 2978 -14877)
    (rect (layer sky130:mcon) 3168 -15047 3338 -14877)
    (rect (layer sky130:mcon) 3528 -15047 3698 -14877)
    (rect (layer sky130:mcon) 3888 -15047 4058 -14877)
    (rect (layer sky130:mcon) 4248 -15047 4418 -14877)
    (rect (layer sky130:li1) 513 -14207 1717 -14037
      (props (wire-id 1)))
    (rect (layer sky130:li1) 1547 -14997 1717 -14037
      (props (wire-id 1)))
    (rect (layer sky130:met1) 1487 -15057 1777 -14767
      (props (wire-id 1)))
    (rect (layer sky130:mcon) 1547 -14997 1717 -14827
      (props (wire-id 1)))
    (rect (layer sky130:li1) 513 -12917 2958 -12747
      (props (wire-id 2)))
    (rect (layer sky130:li1) 2788 -13562 2958 -12747
      (props (wire-id 2)))
    (rect (layer sky130:li1) 3797 -13568 3967 -8334
      (props (wire-id 3)))
    (rect (layer sky130:li1) 2788 -13568 3967 -13398
      (props (wire-id 3)))
    (rect (layer sky130:li1) 2788 -13562 2958 -13398
      (props (wire-id 3)))
    (rect (layer sky130:li1) 3152 -10779 3322 -8675
      (props (wire-id 4)))
    (rect (layer sky130:li1) 2875 -8845 3322 -8675
      (props (wire-id 4)))
    (rect (layer sky130:li1) 2875 -8845 3045 -4565
      (props (wire-id 4)))
    (rect (layer sky130:li1) 1060 -3456 1230 1573
      (props (wire-id 5)))
    (rect (layer sky130:li1) 1060 1403 1565 1573
      (props (wire-id 5)))
    (rect (layer sky130:li1) 1395 1403 1565 1585
      (props (wire-id 5)))
    (rect (layer sky130:li1) 4020 -1915 4190 1571
      (props (wire-id 6)))
    (rect (layer sky130:li1) 1395 1401 4190 1571
      (props (wire-id 6)))
    (rect (layer sky130:li1) 1395 1401 1565 1585
      (props (wire-id 6)))
    (rect (layer sky130:met1) -1305 -3531 -985 -3211
      (props (wire-id 7)))
    (rect (layer sky130:met1) 354 -10403 674 -10083
      (props (wire-id 7)))
    (rect (layer sky130:met1) -1215 -7436 -1075 -3301
      (props (wire-id 7)))
    (rect (layer sky130:met1) -1260 -7436 -1075 -7296
      (props (wire-id 7)))
    (rect (layer sky130:met1) -1260 -10323 -1120 -7296
      (props (wire-id 7)))
    (rect (layer sky130:met1) -1260 -10323 584 -10183
      (props (wire-id 7)))
    (rect (layer sky130:met1) 444 -10313 584 -10183
      (props (wire-id 7)))
    (rect (layer sky130:li1) 89 -11935 259 -11017
      (props (wire-id 8)))
    (rect (layer sky130:li1) 89 -11187 619 -11017
      (props (wire-id 8)))
    (rect (layer sky130:li1) 3797 -8504 5132 -8334
      (props (wire-id 9)))
    (rect (layer sky130:li1) 4962 -8504 5132 3350
      (props (wire-id 9)))
    (rect (layer sky130:li1) 4962 3180 13689 3350
      (props (wire-id 9)))
    (rect (layer sky130:li1) 1730 -4735 1900 -1745
      (props (wire-id 10)))
    (rect (layer sky130:li1) 1730 -4735 3045 -4565
      (props (wire-id 10)))
    (rect (layer sky130:li1) 4442 -14318 4612 -10609
      (props (wire-id 11)))
    (rect (layer sky130:li1) 1547 -14318 4612 -14148
      (props (wire-id 11)))
    (rect (layer sky130:li1) 1547 -14997 1717 -14148
      (props (wire-id 11)))
    (rect (layer sky130:met1) 5884 -14095 6204 -13775
      (props (wire-id 12)))
    (rect (layer sky130:met1) 444 -10313 2796 -10173
      (props (wire-id 12)))
    (rect (layer sky130:met1) 2656 -10313 2796 -8024
      (props (wire-id 12)))
    (rect (layer sky130:met1) 2656 -8164 6114 -8024
      (props (wire-id 12)))
    (rect (layer sky130:met1) 5974 -14005 6114 -8024
      (props (wire-id 12)))
    (rect (layer sky130:met2) -11 -12035 359 -11665
      (props (wire-id 13)))
    (rect (layer sky130:met2) 1438 -15112 1808 -14742
      (props (wire-id 13)))
    (rect (layer sky130:met2) 104 -14997 244 -11780
      (props (wire-id 13)))
    (rect (layer sky130:met2) 104 -14997 1693 -14857
      (props (wire-id 13)))
    (label (layer sky130:li1_label) (text "cgm_b") (origin 0 -7691)
      (kind port-name))
    (label (layer sky130:li1_label) (text "cgm_p_CTAT_xlat") (origin 1815 -1830)
      (kind port-name))
    (label (layer sky130:li1_label) (text "VDD") (origin 1145 -3371)
      (kind port-name))
    (label (layer sky130:li1_label) (text "GND") (origin 598 -14122)
      (kind port-name))))
