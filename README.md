# PCD — Printed Circuit Database

**The drawing is the board.**

PCD is a managed-ObjectARX AutoCAD 2027 add-in that reads the active drawing's database — symbol
tables, records, and model-space entities — and renders its structure as a 3D scene styled like a
circuit board, in a clear area beside the source geometry. It's a fun, visual way to see what's
inside a drawing: ownership relationships become copper traces, reference relationships become thin
hairlines. It's a representation of the database, not a real or manufacturable PCB.

## What you're seeing

- **Chips** — each symbol table is an IC (sized by how many records it holds); each record is a
  smaller chip on that table. The **DATABASE die** is the root object every table hangs off, and
  `*Model_Space` — the container that owns every drawable entity — is rendered as a second large die.
- **Packages** — each model-space entity becomes a piece of hardware whose family is chosen from its
  DXF type (resistor, capacitor, IC, connector, and so on).
- **Copper traces** — **ownership** relationships (entity → `*Model_Space`, record → its table,
  table → the DATABASE), class-colored and dominant.
- **Hairlines** — **reference** relationships (an entity's layer, linetype, text style, block,
  dimension style, or registered application), thinner and dimmer so ownership stays prominent.
- **Pods** — the block of text on each part is its real `entget` data: the actual DXF group codes
  and values for that entity or record.
- **Green binary plume** — the column of 0/1s rising from a part is the raw IEEE-754 64-bit encoding
  of that part's numeric values: the literal bits of its numbers.
- **Red katakana plume** — a taller, denser plume of katakana instead of binary marks an **ACIS 3D
  solid** (a genuine 3D object in the source drawing).

PCD **never saves** the drawing and touches only the `PCD-*` layer namespace (`PCD-NET` copper,
`PCD-PLUME` binary, `PCD-KATA` katakana, and so on), so you can freeze or delete its output without
affecting your drawing. Re-running the `PCD` command replaces the previous render rather than
stacking a new one.

## Requirements

- AutoCAD 2027 (internal series R26.0)
- .NET 10 SDK (the host runtime; target framework `net10.0-windows`)
- Windows x64

The projects reference the managed ObjectARX assemblies from
`C:\Program Files\Autodesk\AutoCAD 2027\` (`acmgd`, `acdbmgd`, `accoremgd`, `acdbmgdbrep`). Adjust
the `HintPath` entries in `core/PCD.Core.csproj` and `loader/PCD.Loader.csproj` if AutoCAD is
installed elsewhere.

## Compatibility

As built, PCD runs on **AutoCAD 2027 only**. This is a hard constraint of two binding requirements
measured from the build, not a conservative default:

| PCD requires | Value | AutoCAD 2027 provides |
|---|---|---|
| .NET runtime | .NET 10 (`net10.0-windows`) | .NET 10 (`acdbmgd.dll` targets `.NETCoreApp,Version=v10.0`) |
| ObjectARX managed API | AssemblyVersion `26.0.0.0` | AssemblyVersion `26.0.0.0` (FileVersion `26.0.60`) |

The manifest also gates loading to `SeriesMin/Max = R26.0`.

- **AutoCAD 2025 / 2026 cannot load PCD.** They host .NET 8, and the CLR refuses to load a .NET 10
  assembly on a .NET 8 host — the framework mismatch blocks it before any API binding is even
  attempted. Their ObjectARX AssemblyVersion (25.x) is a second, independent mismatch.
- **AutoCAD 2028+ is untested.** It would load only if that release keeps *both* .NET 10 and
  ObjectARX AssemblyVersion `26.0.0.0`. Autodesk normally bumps the managed AssemblyVersion each
  major release, which breaks the binding, so forward compatibility cannot be assumed — verify per
  release.

**To verify on any AutoCAD:** install the bundle, run `PCDPING` (loads the assembly and reports the
resolved `PCD.Core.dll` path — it errors immediately if the runtime or binding does not resolve),
then `PCD`.

Supporting more versions means multi-targeting the projects (per-version `.NET` TFM and ObjectARX
`HintPath`s) and shipping one `<ComponentEntry>` per version in the manifest — each compiled against
that release's ObjectARX reference assemblies.

## Build and install

```powershell
# Build the plugin bundle (both DLLs into bundle\PCD.bundle\Contents\)
powershell -ExecutionPolicy Bypass -File tools\build_bundle.ps1

# Install into AutoCAD's per-user trusted plugin location, then restart AutoCAD
powershell -ExecutionPolicy Bypass -File tools\install_bundle.ps1        # self-contained copy
powershell -ExecutionPolicy Bypass -File tools\install_bundle.ps1 -Dev   # junction to the repo build
powershell -ExecutionPolicy Bypass -File tools\install_bundle.ps1 -Uninstall
```

The bundle installs to `%APPDATA%\Autodesk\ApplicationPlugins\PCD.bundle\`. AutoCAD trusts that
location (no SECURELOAD prompt) and demand-loads the add-in on the first `PCD*` command — no
`NETLOAD`.

## Commands

| Command | Action |
|---|---|
| `PCD` | Read the active drawing's database and render the board beside the source geometry. Idempotent. |
| `PCDVIEW` | Frame the board: SW isometric, zoom extents, realistic visual style. |
| `PCDPING` | Report the loaded add-in version and the resolved `PCD.Core.dll` path. |
| `PCDTEMPLATE` | Build a coverage drawing that exercises every package and relationship class PCD renders (see below). Never saves. |
| `PCDTEMPLATERESET` | Erase **all** model-space entities (confirmation required) so `PCDTEMPLATE` can build from a clean slate. |

### Coverage template

`PCDTEMPLATE` constructs the minimum drawing that lights up every feature PCD renders — one of every
entity type it maps to a package, spread across five layers, four linetypes, two text styles, and the
other symbol tables, so all seven relationship classes appear on the board. It is the reference
input for verifying a render. Entity counts per family are chosen so consecutive handles cover each
`(abs handle) % N` package sub-variety. Build a template, run `PCD`, then `SAVEAS` to keep it.

## Performance

Building the board is compute-intensive and runs on AutoCAD's main thread, so **AutoCAD will appear
unresponsive while `PCD` runs — it is busy, not frozen.** Give it time: a small drawing renders in
seconds, a large one can take several minutes. The cost scales with the number of source entities
and relationships — PCD reads up to a few hundred parts and then generates the traces, vias, pads,
and the per-part data "rain", which is commonly tens of thousands of objects (a modest template
render produces ~20,000). Let it finish; the command returns and prints a summary when done.
Re-running `PCD` replaces the previous board rather than adding to it.

## Diagnostics

`PCD` and `PCDTEMPLATE` write small diagnostic files (routing statistics, a coverage report). By
default these go to `%TEMP%\PCD\`. Override the directory with the `PCD_OUT` environment variable.

Setting `PCD_DSN` (to any value) additionally exports a Specctra DSN of the routing problem for study
in an external autorouter; it is off by default so normal renders do not build it.

## Architecture

The geometry and routing live in `PCD.Core.dll`; `PCD.Loader.dll` is a thin entry assembly that
hot-loads Core into a collectible `AssemblyLoadContext` per command, so Core can be rebuilt without
restarting AutoCAD. Both ship together in the bundle.

- `src/Demo.cs` — database read, part/edge model, placement, the grid router, and rendering.
- `src/Pcb.cs` — geometry primitives (extrusions, traces, text, materials).
- `src/Template.cs` — the coverage-template builder.
- `src/Loader.cs` — command entry points and the hot-load mechanism.
- `src/Paths.cs` — diagnostic output-path resolution.

Routing can be verified geometrically: `tools/netdump.lsp` dumps every copper trace (with its
elevation and width) from the active drawing, and `tools/cross_check.py` reports same-layer
crossings from that dump. See each tool's header for usage.

## License

PCD is licensed under the **GNU General Public License v3.0** — see [LICENSE](LICENSE).
Copyright © 2026 ParadoxEthos.

The GPL requires anyone who redistributes PCD or a derivative work to preserve this copyright notice
and to state the changes they made, so authorship of the original work is retained in every copy and
fork. Every source file carries the notice header.
