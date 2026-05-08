module Rekolektion.Viz.Core.Mag.LayerMap

/// Magic uses human-readable layer names; downstream renderers
/// expect SKY130 (number, datatype) keys. This module owns that
/// mapping for the SKY130A + sky130_fd_pr_reram tech file
/// vocabulary the team works in. Names not in this table fall
/// back to a synthetic (Layer=0, DataType=0) and the parser logs
/// a warning so unknown layers get rendered (in a default theme
/// color) rather than dropped silently.
///
/// Source: `$PDK_ROOT/sky130A/libs.tech/magic/sky130A.tech` plus
/// the ReRAM extensions in `sky130_fd_pr_reram`. We don't read
/// the tech file at runtime — the layers we care about for viz
/// are stable enough to hardcode.
let private table : Map<string, int * int> =
    [
        // Wells / substrate
        "nwell",          (64, 20)
        "pwell",          (64, 44)        // SKY130 has no native pwell layer; render dimly via nwell.pin
        "subscont",       (65, 44)
        "psubdiff",       (65, 20)
        "psubdiffcont",   (65, 44)
        "pwellcont",      (64, 44)

        // Diffusion
        "ndiff",          (65, 20)
        "pdiff",          (65, 20)
        "ndiffc",         (65, 44)
        "pdiffc",         (65, 44)
        "diff",           (65, 20)
        "tap",            (65, 44)

        // Transistor-channel "paint" types — Magic abstracts an
        // NMOS/PMOS gate area as a single rect with these names;
        // GDS extraction decomposes them into poly-over-diff. For
        // viz we surface the diff portion (65, 20) so the channel
        // shows up under the diffusion toggle.
        "nmos",           (65, 20)
        "pmos",           (65, 20)

        // MV (medium-voltage) variants — same physical drawing layer,
        // distinct markers in real PDK; for viz purposes alias to the
        // base diff/nwell layers so the geometry shows up.
        "mvnmos",         (65, 20)
        "mvpmos",         (65, 20)
        "mvndiff",        (65, 20)
        "mvpdiff",        (65, 20)
        "mvndiffc",       (65, 44)
        "mvpdiffc",       (65, 44)
        "mvpsubdiff",     (65, 20)
        "mvpsubdiffcont", (65, 44)
        "mvnsubdiff",     (65, 20)
        "mvnsubdiffcont", (65, 44)
        "mvnwell",        (64, 20)
        "mvpwell",        (64, 44)

        // Poly + contacts
        "poly",           (66, 20)
        "polyc",          (66, 44)
        "polycont",       (66, 44)
        "licon",          (66, 44)
        "polyres",        (66, 20)

        // Local interconnect
        "li",             (67, 20)
        "li1",            (67, 20)
        "locali",         (67, 20)
        "mcon",           (67, 44)
        "viali",          (67, 44)

        // Metal stack
        "metal1",         (68, 20)
        "met1",           (68, 20)
        "via1",           (68, 44)
        "via",            (68, 44)
        "metal2",         (69, 20)
        "met2",           (69, 20)
        "via2",           (69, 44)
        "metal3",         (70, 20)
        "met3",           (70, 20)
        "via3",           (70, 44)
        "metal4",         (71, 20)
        "met4",           (71, 20)
        "via4",           (71, 44)
        "metal5",         (72, 20)
        "met5",           (72, 20)

        // MIM cap (sits between met3 and met4)
        "capm",           (89, 44)
        "capm2",          (89, 44)
        "mimcap",         (89, 44)

        // Markers / non-physical layers
        "areaid.sc",      (81, 2)
        "comment",        (83, 44)
        // Magic-internal layers — given their own (255, *) keys
        // so the user can toggle them independently and so AutoFit
        // can exclude them from the cell's render bbox.
        "checkpaint",     (255, 0)
        "error",          (255, 1)
        "errors",         (255, 1)
        "feedback",       (255, 2)

        // ReRAM-specific (sky130_fd_pr_reram). Numbers from PDK GDS
        // layer file; if you don't see these in your install, this
        // alias still lets the parser succeed and renders in a
        // default color.
        "reram",          (201, 20)
        "urlay",          (202, 20)
        "metal6",         (143, 20)
    ]
    |> Map.ofList

/// Lookup a Magic layer name. Names are matched case-insensitively
/// because Magic accepts mixed-case in some tech files.
let tryFind (name: string) : (int * int) option =
    Map.tryFind (name.ToLowerInvariant()) table

/// Same as tryFind but returns a synthetic key (0, 0) and lets the
/// caller log if the lookup misses.
let lookupOrUnknown (name: string) : (int * int) * bool =
    match tryFind name with
    | Some k -> k, true
    | None -> (0, 0), false
