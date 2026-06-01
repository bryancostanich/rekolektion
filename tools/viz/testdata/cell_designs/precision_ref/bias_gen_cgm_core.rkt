(layout (version 1)
  (pdk sky130)
  (units (dbu_nm 1) (uu_um 1))
  (import "../primitives/pfet_01v8_W2p5_L2p0_core_botgate.rkt")
  (import "../primitives/pfet_01v8_W0p42_L20p0_core_botgate.rkt")
  (import "../primitives/nfet_01v8_W1p0_L2p0_core_topgate.rkt")
  (import "../primitives/nfet_01v8_W4p0_L2p0_core_topgate.rkt")
  (import "../primitives/res_xhigh_po_W1p41_L12p0_core.rkt")
  (top bias_gen_cgm_core)
  (cell bias_gen_cgm_core
    (rect (layer sky130:nwell) -1757 -8660 23104 -1372)
    (rect (layer sky130:pwell) -1788 -12722 6199 -8692)
    (sref (cell pfet_01v8_W2p5_L2p0_core_botgate) (origin 173 -7000) (rot 90.0))
    (sref (cell pfet_01v8_W2p5_L2p0_core_botgate) (origin 173 -4040) (rot 90.0))
    (sref (cell pfet_01v8_W0p42_L20p0_core_botgate) (origin 12444 -6847))
    (sref (cell pfet_01v8_W0p42_L20p0_core_botgate) (origin 12444 -5427) (rot 180.0))
    (sref (cell pfet_01v8_W0p42_L20p0_core_botgate) (origin 12444 -4337))
    (sref (cell pfet_01v8_W0p42_L20p0_core_botgate) (origin 12444 -2917) (rot 180.0))
    (sref (cell nfet_01v8_W1p0_L2p0_core_topgate) (origin 4594 -9716))
    (sref (cell nfet_01v8_W4p0_L2p0_core_topgate) (origin 687 -10297) (rot -90.0))
    (sref (cell res_xhigh_po_W1p41_L12p0_core) (origin 14615 -9797) (rot 90.0))
    (rect (layer sky130:tap) -1577 -2072 22924 -1652)
    (rect (layer sky130:nsdm) -1702 -2197 23049 -1527)
    (rect (layer sky130:licon1) -1517 -1947 -1347 -1777)
    (rect (layer sky130:licon1) -1177 -1947 -1007 -1777)
    (rect (layer sky130:licon1) -837 -1947 -667 -1777)
    (rect (layer sky130:licon1) -497 -1947 -327 -1777)
    (rect (layer sky130:licon1) -157 -1947 13 -1777)
    (rect (layer sky130:licon1) 183 -1947 353 -1777)
    (rect (layer sky130:licon1) 523 -1947 693 -1777)
    (rect (layer sky130:licon1) 863 -1947 1033 -1777)
    (rect (layer sky130:licon1) 1203 -1947 1373 -1777)
    (rect (layer sky130:licon1) 1543 -1947 1713 -1777)
    (rect (layer sky130:licon1) 1883 -1947 2053 -1777)
    (rect (layer sky130:licon1) 2223 -1947 2393 -1777)
    (rect (layer sky130:licon1) 2563 -1947 2733 -1777)
    (rect (layer sky130:licon1) 2903 -1947 3073 -1777)
    (rect (layer sky130:licon1) 3243 -1947 3413 -1777)
    (rect (layer sky130:licon1) 3583 -1947 3753 -1777)
    (rect (layer sky130:licon1) 3923 -1947 4093 -1777)
    (rect (layer sky130:licon1) 4263 -1947 4433 -1777)
    (rect (layer sky130:licon1) 4603 -1947 4773 -1777)
    (rect (layer sky130:licon1) 4943 -1947 5113 -1777)
    (rect (layer sky130:licon1) 5283 -1947 5453 -1777)
    (rect (layer sky130:licon1) 5623 -1947 5793 -1777)
    (rect (layer sky130:licon1) 5963 -1947 6133 -1777)
    (rect (layer sky130:licon1) 6303 -1947 6473 -1777)
    (rect (layer sky130:licon1) 6643 -1947 6813 -1777)
    (rect (layer sky130:licon1) 6983 -1947 7153 -1777)
    (rect (layer sky130:licon1) 7323 -1947 7493 -1777)
    (rect (layer sky130:licon1) 7663 -1947 7833 -1777)
    (rect (layer sky130:licon1) 8003 -1947 8173 -1777)
    (rect (layer sky130:licon1) 8343 -1947 8513 -1777)
    (rect (layer sky130:licon1) 8683 -1947 8853 -1777)
    (rect (layer sky130:licon1) 9023 -1947 9193 -1777)
    (rect (layer sky130:licon1) 9363 -1947 9533 -1777)
    (rect (layer sky130:licon1) 9703 -1947 9873 -1777)
    (rect (layer sky130:licon1) 10043 -1947 10213 -1777)
    (rect (layer sky130:licon1) 10383 -1947 10553 -1777)
    (rect (layer sky130:licon1) 10723 -1947 10893 -1777)
    (rect (layer sky130:licon1) 11063 -1947 11233 -1777)
    (rect (layer sky130:licon1) 11403 -1947 11573 -1777)
    (rect (layer sky130:licon1) 11743 -1947 11913 -1777)
    (rect (layer sky130:licon1) 12083 -1947 12253 -1777)
    (rect (layer sky130:licon1) 12423 -1947 12593 -1777)
    (rect (layer sky130:licon1) 12763 -1947 12933 -1777)
    (rect (layer sky130:licon1) 13103 -1947 13273 -1777)
    (rect (layer sky130:licon1) 13443 -1947 13613 -1777)
    (rect (layer sky130:licon1) 13783 -1947 13953 -1777)
    (rect (layer sky130:licon1) 14123 -1947 14293 -1777)
    (rect (layer sky130:licon1) 14463 -1947 14633 -1777)
    (rect (layer sky130:licon1) 14803 -1947 14973 -1777)
    (rect (layer sky130:licon1) 15143 -1947 15313 -1777)
    (rect (layer sky130:licon1) 15483 -1947 15653 -1777)
    (rect (layer sky130:licon1) 15823 -1947 15993 -1777)
    (rect (layer sky130:licon1) 16163 -1947 16333 -1777)
    (rect (layer sky130:licon1) 16503 -1947 16673 -1777)
    (rect (layer sky130:licon1) 16843 -1947 17013 -1777)
    (rect (layer sky130:licon1) 17183 -1947 17353 -1777)
    (rect (layer sky130:licon1) 17523 -1947 17693 -1777)
    (rect (layer sky130:licon1) 17863 -1947 18033 -1777)
    (rect (layer sky130:licon1) 18203 -1947 18373 -1777)
    (rect (layer sky130:licon1) 18543 -1947 18713 -1777)
    (rect (layer sky130:licon1) 18883 -1947 19053 -1777)
    (rect (layer sky130:licon1) 19223 -1947 19393 -1777)
    (rect (layer sky130:licon1) 19563 -1947 19733 -1777)
    (rect (layer sky130:licon1) 19903 -1947 20073 -1777)
    (rect (layer sky130:licon1) 20243 -1947 20413 -1777)
    (rect (layer sky130:licon1) 20583 -1947 20753 -1777)
    (rect (layer sky130:licon1) 20923 -1947 21093 -1777)
    (rect (layer sky130:licon1) 21263 -1947 21433 -1777)
    (rect (layer sky130:licon1) 21603 -1947 21773 -1777)
    (rect (layer sky130:licon1) 21943 -1947 22113 -1777)
    (rect (layer sky130:licon1) 22283 -1947 22453 -1777)
    (rect (layer sky130:licon1) 22623 -1947 22793 -1777)
    (rect (layer sky130:li1) -1577 -2027 22924 -1697)
    (rect (layer sky130:tap) -1608 -12442 6019 -12022)
    (rect (layer sky130:psdm) -1733 -12567 6144 -11897)
    (rect (layer sky130:licon1) -1548 -12317 -1378 -12147)
    (rect (layer sky130:licon1) -1208 -12317 -1038 -12147)
    (rect (layer sky130:licon1) -868 -12317 -698 -12147)
    (rect (layer sky130:licon1) -528 -12317 -358 -12147)
    (rect (layer sky130:licon1) -188 -12317 -18 -12147)
    (rect (layer sky130:licon1) 152 -12317 322 -12147)
    (rect (layer sky130:licon1) 492 -12317 662 -12147)
    (rect (layer sky130:licon1) 832 -12317 1002 -12147)
    (rect (layer sky130:licon1) 1172 -12317 1342 -12147)
    (rect (layer sky130:licon1) 1512 -12317 1682 -12147)
    (rect (layer sky130:licon1) 1852 -12317 2022 -12147)
    (rect (layer sky130:licon1) 2192 -12317 2362 -12147)
    (rect (layer sky130:licon1) 2532 -12317 2702 -12147)
    (rect (layer sky130:licon1) 2872 -12317 3042 -12147)
    (rect (layer sky130:licon1) 3212 -12317 3382 -12147)
    (rect (layer sky130:licon1) 3552 -12317 3722 -12147)
    (rect (layer sky130:licon1) 3892 -12317 4062 -12147)
    (rect (layer sky130:licon1) 4232 -12317 4402 -12147)
    (rect (layer sky130:licon1) 4572 -12317 4742 -12147)
    (rect (layer sky130:licon1) 4912 -12317 5082 -12147)
    (rect (layer sky130:licon1) 5252 -12317 5422 -12147)
    (rect (layer sky130:licon1) 5592 -12317 5762 -12147)
    (rect (layer sky130:li1) -1608 -12397 6019 -12067)
    (rect (layer sky130:met1) -1788 -2227 23104 -1497)
    (rect (layer sky130:mcon) -1547 -1997 -1377 -1827)
    (rect (layer sky130:mcon) -1187 -1997 -1017 -1827)
    (rect (layer sky130:mcon) -827 -1997 -657 -1827)
    (rect (layer sky130:mcon) -467 -1997 -297 -1827)
    (rect (layer sky130:mcon) -107 -1997 63 -1827)
    (rect (layer sky130:mcon) 253 -1997 423 -1827)
    (rect (layer sky130:mcon) 613 -1997 783 -1827)
    (rect (layer sky130:mcon) 973 -1997 1143 -1827)
    (rect (layer sky130:mcon) 1333 -1997 1503 -1827)
    (rect (layer sky130:mcon) 1693 -1997 1863 -1827)
    (rect (layer sky130:mcon) 2053 -1997 2223 -1827)
    (rect (layer sky130:mcon) 2413 -1997 2583 -1827)
    (rect (layer sky130:mcon) 2773 -1997 2943 -1827)
    (rect (layer sky130:mcon) 3133 -1997 3303 -1827)
    (rect (layer sky130:mcon) 3493 -1997 3663 -1827)
    (rect (layer sky130:mcon) 3853 -1997 4023 -1827)
    (rect (layer sky130:mcon) 4213 -1997 4383 -1827)
    (rect (layer sky130:mcon) 4573 -1997 4743 -1827)
    (rect (layer sky130:mcon) 4933 -1997 5103 -1827)
    (rect (layer sky130:mcon) 5293 -1997 5463 -1827)
    (rect (layer sky130:mcon) 5653 -1997 5823 -1827)
    (rect (layer sky130:mcon) 6013 -1997 6183 -1827)
    (rect (layer sky130:mcon) 6373 -1997 6543 -1827)
    (rect (layer sky130:mcon) 6733 -1997 6903 -1827)
    (rect (layer sky130:mcon) 7093 -1997 7263 -1827)
    (rect (layer sky130:mcon) 7453 -1997 7623 -1827)
    (rect (layer sky130:mcon) 7813 -1997 7983 -1827)
    (rect (layer sky130:mcon) 8173 -1997 8343 -1827)
    (rect (layer sky130:mcon) 8533 -1997 8703 -1827)
    (rect (layer sky130:mcon) 8893 -1997 9063 -1827)
    (rect (layer sky130:mcon) 9253 -1997 9423 -1827)
    (rect (layer sky130:mcon) 9613 -1997 9783 -1827)
    (rect (layer sky130:mcon) 9973 -1997 10143 -1827)
    (rect (layer sky130:mcon) 10333 -1997 10503 -1827)
    (rect (layer sky130:mcon) 10693 -1997 10863 -1827)
    (rect (layer sky130:mcon) 11053 -1997 11223 -1827)
    (rect (layer sky130:mcon) 11413 -1997 11583 -1827)
    (rect (layer sky130:mcon) 11773 -1997 11943 -1827)
    (rect (layer sky130:mcon) 12133 -1997 12303 -1827)
    (rect (layer sky130:mcon) 12493 -1997 12663 -1827)
    (rect (layer sky130:mcon) 12853 -1997 13023 -1827)
    (rect (layer sky130:mcon) 13213 -1997 13383 -1827)
    (rect (layer sky130:mcon) 13573 -1997 13743 -1827)
    (rect (layer sky130:mcon) 13933 -1997 14103 -1827)
    (rect (layer sky130:mcon) 14293 -1997 14463 -1827)
    (rect (layer sky130:mcon) 14653 -1997 14823 -1827)
    (rect (layer sky130:mcon) 15013 -1997 15183 -1827)
    (rect (layer sky130:mcon) 15373 -1997 15543 -1827)
    (rect (layer sky130:mcon) 15733 -1997 15903 -1827)
    (rect (layer sky130:mcon) 16093 -1997 16263 -1827)
    (rect (layer sky130:mcon) 16453 -1997 16623 -1827)
    (rect (layer sky130:mcon) 16813 -1997 16983 -1827)
    (rect (layer sky130:mcon) 17173 -1997 17343 -1827)
    (rect (layer sky130:mcon) 17533 -1997 17703 -1827)
    (rect (layer sky130:mcon) 17893 -1997 18063 -1827)
    (rect (layer sky130:mcon) 18253 -1997 18423 -1827)
    (rect (layer sky130:mcon) 18613 -1997 18783 -1827)
    (rect (layer sky130:mcon) 18973 -1997 19143 -1827)
    (rect (layer sky130:mcon) 19333 -1997 19503 -1827)
    (rect (layer sky130:mcon) 19693 -1997 19863 -1827)
    (rect (layer sky130:mcon) 20053 -1997 20223 -1827)
    (rect (layer sky130:mcon) 20413 -1997 20583 -1827)
    (rect (layer sky130:mcon) 20773 -1997 20943 -1827)
    (rect (layer sky130:mcon) 21133 -1997 21303 -1827)
    (rect (layer sky130:mcon) 21493 -1997 21663 -1827)
    (rect (layer sky130:mcon) 21853 -1997 22023 -1827)
    (rect (layer sky130:mcon) 22213 -1997 22383 -1827)
    (rect (layer sky130:mcon) 22573 -1997 22743 -1827)
    (rect (layer sky130:met1) -1788 -12597 23104 -11867)
    (rect (layer sky130:mcon) -1578 -12367 -1408 -12197)
    (rect (layer sky130:mcon) -1218 -12367 -1048 -12197)
    (rect (layer sky130:mcon) -858 -12367 -688 -12197)
    (rect (layer sky130:mcon) -498 -12367 -328 -12197)
    (rect (layer sky130:mcon) -138 -12367 32 -12197)
    (rect (layer sky130:mcon) 222 -12367 392 -12197)
    (rect (layer sky130:mcon) 582 -12367 752 -12197)
    (rect (layer sky130:mcon) 942 -12367 1112 -12197)
    (rect (layer sky130:mcon) 1302 -12367 1472 -12197)
    (rect (layer sky130:mcon) 1662 -12367 1832 -12197)
    (rect (layer sky130:mcon) 2022 -12367 2192 -12197)
    (rect (layer sky130:mcon) 2382 -12367 2552 -12197)
    (rect (layer sky130:mcon) 2742 -12367 2912 -12197)
    (rect (layer sky130:mcon) 3102 -12367 3272 -12197)
    (rect (layer sky130:mcon) 3462 -12367 3632 -12197)
    (rect (layer sky130:mcon) 3822 -12367 3992 -12197)
    (rect (layer sky130:mcon) 4182 -12367 4352 -12197)
    (rect (layer sky130:mcon) 4542 -12367 4712 -12197)
    (rect (layer sky130:mcon) 4902 -12367 5072 -12197)
    (rect (layer sky130:mcon) 5262 -12367 5432 -12197)
    (rect (layer sky130:mcon) 5622 -12367 5792 -12197)
    (rect (layer sky130:li1) 12294 -5077 12594 -4687)
    (rect (layer sky130:li1) 2214 -4431 2384 -4072
      (props (wire-id 1)))
    (rect (layer sky130:li1) 2104 -4431 2384 -4261
      (props (wire-id 1)))
    (rect (layer sky130:li1) 2104 -5503 2274 -4261
      (props (wire-id 1)))
    (rect (layer sky130:li1) 2104 -5503 2384 -5333
      (props (wire-id 1)))
    (rect (layer sky130:li1) 2214 -5692 2384 -5333
      (props (wire-id 1)))
    (rect (layer sky130:li1) 22504 -6752 22674 -5522
      (props (wire-id 2)))
    (rect (layer sky130:li1) 22504 -4242 22674 -3012
      (props (wire-id 3)))
    (rect (layer sky130:li1) 2722 -9181 4679 -9011
      (props (wire-id 4)))
    (rect (layer sky130:li1) 2722 -10382 2892 -9011
      (props (wire-id 4)))
    (rect (layer sky130:li1) 3364 -9956 3534 -9011
      (props (wire-id 5)))
    (rect (layer sky130:li1) 3364 -9181 4679 -9011
      (props (wire-id 5)))
    (rect (layer sky130:li1) 447 -11637 617 -11357
      (props (wire-id 6)))
    (rect (layer sky130:li1) 447 -11637 7700 -11467
      (props (wire-id 6)))
    (rect (layer sky130:li1) 7530 -11637 7700 -9712
      (props (wire-id 6)))
    (rect (layer sky130:li1) -92 -9237 78 -8060
      (props (wire-id 7)))
    (rect (layer sky130:li1) -92 -9237 617 -9067
      (props (wire-id 7)))
    (rect (layer sky130:li1) -92 -8230 1648 -8060
      (props (wire-id 8)))
    (rect (layer sky130:li1) 1478 -8230 1648 -6915
      (props (wire-id 8)))
    (rect (layer sky130:li1) 2214 -6941 2384 -6582
      (props (wire-id 9)))
    (rect (layer sky130:li1) 2104 -6941 2384 -6771
      (props (wire-id 9)))
    (rect (layer sky130:li1) 2104 -7623 2274 -6771
      (props (wire-id 9)))
    (rect (layer sky130:li1) 2104 -7623 2892 -7453
      (props (wire-id 9)))
    (rect (layer sky130:li1) 2722 -10382 2892 -7453
      (props (wire-id 9)))
    (rect (layer sky130:li1) 1478 -7085 1648 -3955
      (props (wire-id 10)))
    (rect (layer sky130:li1) -92 -2980 78 -1798
      (props (wire-id 11)))
    (rect (layer sky130:li1) -92 -1968 10743 -1798
      (props (wire-id 11)))
    (rect (layer sky130:li1) 10573 -1968 10743 -1777
      (props (wire-id 11)))
    (rect (layer sky130:met1) 10513 -2007 10803 -1717
      (props (wire-id 11)))
    (rect (layer sky130:mcon) 10573 -1947 10743 -1777
      (props (wire-id 11)))
    (rect (layer sky130:li1) 2214 -3182 2384 -2822
      (props (wire-id 12)))
    (rect (layer sky130:li1) 2104 -2992 2384 -2822
      (props (wire-id 12)))
    (rect (layer sky130:li1) 2104 -2992 2274 -1810
      (props (wire-id 12)))
    (rect (layer sky130:li1) 2104 -1980 10743 -1810
      (props (wire-id 12)))
    (rect (layer sky130:li1) 10573 -1980 10743 -1777
      (props (wire-id 12)))
    (rect (layer sky130:met1) 10513 -2007 10803 -1717
      (props (wire-id 12)))
    (rect (layer sky130:mcon) 10573 -1947 10743 -1777
      (props (wire-id 12)))
    (rect (layer sky130:li1) -92 -5940 78 -5441
      (props (wire-id 13)))
    (rect (layer sky130:li1) -1627 -5611 78 -5441
      (props (wire-id 13)))
    (rect (layer sky130:li1) -1627 -5611 -1457 -1786
      (props (wire-id 13)))
    (rect (layer sky130:li1) -1627 -1956 10743 -1786
      (props (wire-id 13)))
    (rect (layer sky130:li1) 10573 -1956 10743 -1777
      (props (wire-id 13)))
    (rect (layer sky130:met1) 10513 -2007 10803 -1717
      (props (wire-id 13)))
    (rect (layer sky130:mcon) 10573 -1947 10743 -1777
      (props (wire-id 13)))
    (rect (layer sky130:met1) -167 -5345 153 -5025
      (props (wire-id 14)))
    (rect (layer sky130:met1) 3289 -10031 3609 -9711
      (props (wire-id 14)))
    (rect (layer sky130:met1) -77 -5599 63 -5115
      (props (wire-id 14)))
    (rect (layer sky130:met1) -77 -5599 2019 -5459
      (props (wire-id 14)))
    (rect (layer sky130:met1) 1879 -8142 2019 -5459
      (props (wire-id 14)))
    (rect (layer sky130:met1) 1879 -8142 3519 -8002
      (props (wire-id 14)))
    (rect (layer sky130:met1) 3379 -9941 3519 -8002
      (props (wire-id 14)))
    (rect (layer sky130:mcon) -92 -5270 78 -5100
      (props (wire-id 14)))
    (rect (layer sky130:mcon) 3364 -9956 3534 -9786
      (props (wire-id 14)))
    (rect (layer sky130:li1) 21530 -12317 21700 -9712
      (props (wire-id 15)))
    (rect (layer sky130:li1) 10573 -12317 21700 -12147
      (props (wire-id 15)))
    (rect (layer sky130:met1) 10513 -12377 10803 -12087
      (props (wire-id 15)))
    (rect (layer sky130:mcon) 10573 -12317 10743 -12147
      (props (wire-id 15)))
    (rect (layer sky130:li1) 12359 -12317 12529 -7112
      (props (wire-id 16)))
    (rect (layer sky130:li1) 10573 -12317 12529 -12147
      (props (wire-id 16)))
    (rect (layer sky130:met1) 10513 -12377 10803 -12087
      (props (wire-id 16)))
    (rect (layer sky130:mcon) 10573 -12317 10743 -12147
      (props (wire-id 16)))
    (rect (layer sky130:met1) 5579 -10031 5899 -9711
      (props (wire-id 17)))
    (rect (layer sky130:met1) 10498 -12392 10818 -12072
      (props (wire-id 17)))
    (rect (layer sky130:met1) 5669 -12302 5809 -9801
      (props (wire-id 17)))
    (rect (layer sky130:met1) 5669 -12302 10728 -12162
      (props (wire-id 17)))
    (rect (layer sky130:mcon) 5654 -9956 5824 -9786
      (props (wire-id 17)))
    (rect (layer sky130:met1) 12284 -2727 12604 -2407
      (props (wire-id 18)))
    (rect (layer sky130:met1) 12284 -7357 12604 -7037
      (props (wire-id 18)))
    (rect (layer sky130:met1) 12374 -2637 23033 -2497
      (props (wire-id 18)))
    (rect (layer sky130:met1) 22893 -2690 23033 -2497
      (props (wire-id 18)))
    (rect (layer sky130:met1) 22866 -2690 23033 -2550
      (props (wire-id 18)))
    (rect (layer sky130:met1) 22866 -7267 23006 -2550
      (props (wire-id 18)))
    (rect (layer sky130:met1) 12374 -7267 23006 -7127
      (props (wire-id 18)))
    (rect (layer sky130:mcon) 12359 -2652 12529 -2482
      (props (wire-id 18)))
    (rect (layer sky130:mcon) 12359 -7282 12529 -7112
      (props (wire-id 18)))
    (rect (layer sky130:met1) 12284 -4845 12604 -4525
      (props (wire-id 19)))
    (rect (layer sky130:met1) 12284 -5237 12604 -4917
      (props (wire-id 19)))
    (rect (layer sky130:met1) 12374 -5147 12514 -4615
      (props (wire-id 19)))
    (rect (layer sky130:mcon) 12359 -4770 12529 -4600
      (props (wire-id 19)))
    (rect (layer sky130:mcon) 12359 -5162 12529 -4992
      (props (wire-id 19)))
    (rect (layer sky130:met1) 12284 -4845 12604 -4525
      (props (wire-id 20)))
    (rect (layer sky130:met1) 12284 -7357 12604 -7037
      (props (wire-id 20)))
    (rect (layer sky130:met1) 12374 -4755 22998 -4615
      (props (wire-id 20)))
    (rect (layer sky130:met1) 22858 -4773 22998 -4615
      (props (wire-id 20)))
    (rect (layer sky130:met1) 22858 -4773 23010 -4633
      (props (wire-id 20)))
    (rect (layer sky130:met1) 22870 -7267 23010 -4633
      (props (wire-id 20)))
    (rect (layer sky130:met1) 12374 -7267 23010 -7127
      (props (wire-id 20)))
    (rect (layer sky130:mcon) 12359 -4770 12529 -4600
      (props (wire-id 20)))
    (rect (layer sky130:mcon) 12359 -7282 12529 -7112
      (props (wire-id 20)))
    (label (layer sky130:li1_label) (text "cgm_a") (origin -7 -5185)
      (kind port-name))
    (label (layer sky130:li1_label) (text "cgm_b") (origin -7 -8145)
      (kind port-name))
    (label (layer sky130:li1_label) (text "VDD") (origin -7 -2895)
      (kind port-name))
    (label (layer sky130:li1_label) (text "GND") (origin 5739 -9871)
      (kind port-name))))
