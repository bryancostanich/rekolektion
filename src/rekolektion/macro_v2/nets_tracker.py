"""Tracks net->polygon mappings during macro_v2 assembly and emits a JSON
sidecar (``<gds_path>.nets.json``) consumed by the F# rekolektion-viz tool.

Polygon indexes are queried via ``len(cell.polygons) - 1`` immediately after
``cell.add(rect)``, so the index always reflects the polygon's true ordinal
position in the cell -- robust even when other polygons are added without
going through the tracker (foundry cell instances, child cells, etc.).
"""
from __future__ import annotations

import json
from dataclasses import dataclass, field
from pathlib import Path
from typing import Dict, List, Literal

import gdstk

NetClass = Literal["power", "ground", "signal", "clock"]


@dataclass
class PolygonRef:
    structure: str
    layer: int
    datatype: int
    index: int


@dataclass
class NetEntry:
    name: str
    cls: NetClass
    polygons: List[PolygonRef] = field(default_factory=list)


class NetsTracker:
    """Records (structure, layer, datatype, polygon_index, net, class)
    tuples as the macro_v2 assembler draws polygons, then writes a JSON
    sidecar consumable by the F# Rekolektion.Viz tool.
    """

    def __init__(self) -> None:
        self._nets: Dict[str, NetEntry] = {}

    def record(
        self,
        *,
        cell: gdstk.Cell,
        layer: int,
        datatype: int,
        net: str,
        cls: NetClass = "signal",
    ) -> None:
        """Record the polygon most recently added to ``cell`` as belonging
        to ``net``.

        Call IMMEDIATELY after ``cell.add(rect)`` so ``len(cell.polygons)
        - 1`` points at the just-added polygon.
        """
        idx = len(cell.polygons) - 1
        if idx < 0:
            return  # nothing was added; defensive no-op
        ref = PolygonRef(
            structure=cell.name,
            layer=layer,
            datatype=datatype,
            index=idx,
        )
        if net not in self._nets:
            self._nets[net] = NetEntry(name=net, cls=cls)
        self._nets[net].polygons.append(ref)

    def write(self, gds_path: str | Path, macro_name: str) -> Path:
        """Emit ``<gds_path>.nets.json`` next to the GDS file.

        The output filename replaces the GDS extension with ``.nets.json``
        (e.g. ``foo.gds`` -> ``foo.nets.json``).
        """
        out = Path(gds_path).with_suffix(".nets.json")
        payload = {
            "version": 1,
            "macro": macro_name,
            "nets": {
                name: {
                    "class": entry.cls,
                    "polygons": [
                        {
                            "structure": p.structure,
                            "layer": p.layer,
                            "datatype": p.datatype,
                            "index": p.index,
                        }
                        for p in entry.polygons
                    ],
                }
                for name, entry in self._nets.items()
            },
        }
        out.write_text(json.dumps(payload, indent=2))
        return out
