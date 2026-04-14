"""SkyWater SKY130 design rules and layer definitions for SRAM design.

All dimensions in micrometers (μm) unless noted otherwise.

Sources:
- SkyWater SKY130 PDK DRC rules (libs.tech/magic/sky130{A,B}.tech)
- SKY130 Periphery design rules documentation
- Values validated against Magic DRC deck

NOTE: These rules are the minimum values. SRAM cells may use slightly
relaxed values for manufacturability. Rules should be validated against
the actual PDK DRC deck before tapeout.
"""

from dataclasses import dataclass
from pathlib import Path


# ---------------------------------------------------------------------------
# PDK variant selection
# ---------------------------------------------------------------------------
# sky130B adds ReRAM BEOL between M1/M2 (reram layer 201/20).
# FEOL (transistors, diff, poly, contacts, M1) is identical to sky130A.
# DRC rules for SRAM and MIM caps are unchanged.
# Only difference: via1 is thicker (0.565 vs 0.27um), shifting M2+ up 0.295um in Z.

PDK_VARIANT = "sky130B"  # "sky130A" or "sky130B"


def pdk_path(pdk_root: Path | str | None = None) -> Path:
    """Return the path to the active PDK variant directory.

    Searches standard locations if pdk_root is not provided.
    """
    if pdk_root is not None:
        p = Path(pdk_root)
        # Accept either the root or the variant dir directly
        if p.name == PDK_VARIANT:
            return p
        return p / PDK_VARIANT

    candidates = [
        Path.home() / ".volare" / PDK_VARIANT,
        Path.home() / "pdk" / PDK_VARIANT,
        Path(f"/usr/local/share/pdk/{PDK_VARIANT}"),
        Path(f"/opt/pdk/{PDK_VARIANT}"),
    ]
    for c in candidates:
        if c.exists():
            return c

    raise FileNotFoundError(
        f"PDK {PDK_VARIANT} not found. Set PDK_ROOT env var or install via volare."
    )


def magic_rcfile(pdk_root: Path | str | None = None) -> Path:
    """Return path to the Magic rcfile for the active PDK variant."""
    return pdk_path(pdk_root) / "libs.tech" / "magic" / f"{PDK_VARIANT}.magicrc"


def magic_techfile(pdk_root: Path | str | None = None) -> Path:
    """Return path to the Magic tech file for the active PDK variant."""
    return pdk_path(pdk_root) / "libs.tech" / "magic" / f"{PDK_VARIANT}.tech"


def netgen_setup(pdk_root: Path | str | None = None) -> Path:
    """Return path to the netgen setup file for the active PDK variant."""
    return pdk_path(pdk_root) / "libs.tech" / "netgen" / f"{PDK_VARIANT}_setup.tcl"


# ---------------------------------------------------------------------------
# GDS layer/datatype mapping for SKY130
# ---------------------------------------------------------------------------

@dataclass(frozen=True)
class Layer:
    """GDS layer number and datatype pair."""
    gds_layer: int
    gds_datatype: int

    @property
    def as_tuple(self) -> tuple[int, int]:
        return (self.gds_layer, self.gds_datatype)


class SKY130Layers:
    """GDS layer definitions for SKY130."""

    # Diffusion and wells
    DIFF = Layer(65, 20)        # Active diffusion
    TAP = Layer(65, 44)         # Substrate/well tap
    NWELL = Layer(64, 20)       # N-well
    DNWELL = Layer(64, 18)      # Deep N-well

    # Implants
    NSDM = Layer(93, 44)        # N+ source/drain implant marker
    PSDM = Layer(94, 20)        # P+ source/drain implant marker
    HVI = Layer(75, 20)         # High voltage implant

    # Gate
    POLY = Layer(66, 20)        # Polysilicon

    # Local interconnect
    LICON1 = Layer(66, 44)      # Contact: diff/poly to li1
    LI1 = Layer(67, 20)         # Local interconnect metal

    # Metal stack
    MCON = Layer(67, 44)        # Contact: li1 to met1
    MET1 = Layer(68, 20)        # Metal 1
    VIA = Layer(68, 44)         # Via: met1 to met2
    MET2 = Layer(69, 20)        # Metal 2
    VIA2 = Layer(69, 44)        # Via: met2 to met3
    MET3 = Layer(70, 20)        # Metal 3

    # Labels and pins
    POLY_LABEL = Layer(66, 5)
    LI1_LABEL = Layer(67, 5)
    MET1_LABEL = Layer(68, 5)
    MET2_LABEL = Layer(69, 5)
    MET1_PIN = Layer(68, 16)
    MET2_PIN = Layer(69, 16)

    # Area IDs
    COREID = Layer(81, 2)       # SRAM core cell marker — enables relaxed li1 rules

    # Boundary
    BOUNDARY = Layer(235, 4)


LAYERS = SKY130Layers()


# ---------------------------------------------------------------------------
# Design rules
# ---------------------------------------------------------------------------

@dataclass(frozen=True)
class MinWidthSpacing:
    """Minimum width and spacing for a layer."""
    min_width: float
    min_spacing: float
    min_enclosure: float = 0.0  # Enclosure by parent layer, if applicable


@dataclass(frozen=True)
class ContactRules:
    """Rules for a contact/via layer."""
    size: float             # Contact/via width (square)
    spacing: float          # Min spacing between contacts
    enclosure_below: float  # Enclosure by layer below
    enclosure_above: float  # Enclosure by layer above


class SKY130Rules:
    """SKY130 design rules relevant to SRAM design.

    All values in micrometers. These represent the minimum DRC-clean values
    from the SKY130 PDK. Some rules are context-dependent (e.g., differ for
    isolated vs. dense geometries); we use the most conservative value.
    """

    # --- Diffusion (active area) ---
    DIFF_MIN_WIDTH = 0.15
    DIFF_MIN_SPACING = 0.27       # Diff to diff (same type)
    DIFF_MIN_ENCLOSURE_BY_NWELL = 0.18  # Min diff enclosure by nwell (PMOS side)

    # --- Tap (substrate/well contacts) ---
    TAP_MIN_WIDTH = 0.26
    TAP_MIN_SPACING = 0.27

    # --- N-well ---
    NWELL_MIN_WIDTH = 0.84
    NWELL_MIN_SPACING = 1.27      # Nwell to nwell (different potential)
    NWELL_TO_NWELL_SAME = 0.60    # Nwell to nwell (same potential, abutting ok)
    NWELL_ENCLOSURE_OF_PSDM = 0.18  # Nwell must enclose psdm by this

    # --- Implants ---
    NSDM_MIN_WIDTH = 0.38
    NSDM_MIN_SPACING = 0.38
    NSDM_ENCLOSURE_OF_DIFF = 0.125  # NSDM must enclose diff by this
    PSDM_MIN_WIDTH = 0.38
    PSDM_MIN_SPACING = 0.38
    PSDM_ENCLOSURE_OF_DIFF = 0.125  # PSDM must enclose diff by this

    # --- Polysilicon ---
    POLY_MIN_WIDTH = 0.15
    POLY_MIN_SPACING = 0.21       # Poly to poly
    POLY_MIN_EXTENSION_PAST_DIFF = 0.13  # Poly must extend past diff edge
    POLY_ENDCAP = 0.13            # Same as extension past diff
    DIFF_EXTENSION_PAST_POLY = 0.25  # Diff must extend past poly (source/drain)
                                     # Practical min with contact landing:
                                     # licon(0.17) + diff_encl(0.04×2) ≈ 0.25
    POLY_TO_DIFF_SPACING = 0.075  # Poly edge to diff (non-gate, same net ok)
    GATE_MIN_LENGTH = 0.15        # Same as poly min width (L_min)

    # --- Transistor sizing ---
    # These are the minimum gate dimensions
    NMOS_MIN_WIDTH = 0.42         # Min NMOS channel width (W)
    NMOS_MIN_LENGTH = 0.15        # Min NMOS channel length (L)
    PMOS_MIN_WIDTH = 0.42         # Min PMOS channel width (W) — same as NMOS on SKY130
    PMOS_MIN_LENGTH = 0.15        # Min PMOS channel length (L)

    # --- LICON1 (contact from diff/poly to li1) ---
    LICON_SIZE = 0.17             # Square contact size
    LICON_SPACING = 0.17          # Min spacing between licons
    LICON_DIFF_ENCLOSURE = 0.04   # Diff enclosure of licon (one direction)
    LICON_DIFF_ENCLOSURE_OTHER = 0.06  # Diff enclosure of licon (other direction)
    LICON_POLY_ENCLOSURE = 0.05   # Poly enclosure of licon (on poly contact)
    LICON_POLY_ENCLOSURE_OTHER = 0.08  # Poly enclosure of licon (other dir)
    LI1_ENCLOSURE_OF_LICON = 0.08  # LI1 enclosure of licon

    # --- LI1 (local interconnect) ---
    LI1_MIN_WIDTH = 0.17
    LI1_MIN_SPACING = 0.17

    # --- MCON (contact from li1 to met1) ---
    MCON_SIZE = 0.17
    MCON_SPACING = 0.19
    LI1_ENCLOSURE_OF_MCON = 0.0   # LI1 enclosure of mcon (can be zero if li1 is wider)
    MET1_ENCLOSURE_OF_MCON = 0.03  # Met1 enclosure of mcon (one direction)
    MET1_ENCLOSURE_OF_MCON_OTHER = 0.06  # Met1 enclosure of mcon (other dir)

    # --- Metal 1 ---
    MET1_MIN_WIDTH = 0.14
    MET1_MIN_SPACING = 0.14

    # --- VIA (met1 to met2) ---
    VIA_SIZE = 0.15
    VIA_SPACING = 0.17
    MET1_ENCLOSURE_OF_VIA = 0.055
    MET2_ENCLOSURE_OF_VIA = 0.055

    # --- Metal 2 ---
    MET2_MIN_WIDTH = 0.14
    MET2_MIN_SPACING = 0.14

    # --- Derived pitches (useful for cell sizing) ---
    @property
    def poly_pitch(self) -> float:
        """Minimum center-to-center poly pitch."""
        return self.POLY_MIN_WIDTH + self.POLY_MIN_SPACING  # 0.36 μm

    @property
    def li1_pitch(self) -> float:
        """Minimum center-to-center li1 pitch."""
        return self.LI1_MIN_WIDTH + self.LI1_MIN_SPACING  # 0.34 μm

    @property
    def met1_pitch(self) -> float:
        """Minimum center-to-center met1 pitch."""
        return self.MET1_MIN_WIDTH + self.MET1_MIN_SPACING  # 0.28 μm (but typically 0.34-0.46)

    @property
    def met2_pitch(self) -> float:
        """Minimum center-to-center met2 pitch."""
        return self.MET2_MIN_WIDTH + self.MET2_MIN_SPACING  # 0.28 μm

    @property
    def licon_pitch(self) -> float:
        """Minimum center-to-center licon pitch."""
        return self.LICON_SIZE + self.LICON_SPACING  # 0.34 μm

    @property
    def mcon_pitch(self) -> float:
        """Minimum center-to-center mcon pitch."""
        return self.MCON_SIZE + self.MCON_SPACING  # 0.36 μm

    # --- Supply voltage ---
    VDD_NOMINAL = 1.8  # Volts
    VDD_MIN = 1.62     # -10%
    VDD_MAX = 1.98     # +10%


RULES = SKY130Rules()


# ---------------------------------------------------------------------------
# SPICE model references
# ---------------------------------------------------------------------------

# Paths are relative to the PDK installation ($PDK_ROOT/sky130A)
SPICE_MODELS = {
    "tt": "libs.tech/ngspice/sky130.lib.spice tt",
    "ss": "libs.tech/ngspice/sky130.lib.spice ss",
    "ff": "libs.tech/ngspice/sky130.lib.spice ff",
    "sf": "libs.tech/ngspice/sky130.lib.spice sf",
    "fs": "libs.tech/ngspice/sky130.lib.spice fs",
}

NMOS_MODEL = "sky130_fd_pr__nfet_01v8"
PMOS_MODEL = "sky130_fd_pr__pfet_01v8"
