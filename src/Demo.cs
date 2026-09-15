using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using AcColor = Autodesk.AutoCAD.Colors.Color;

namespace PCD
{
    /// <summary>
    /// PCD core (v0.8). Full-depth database render, matching NXCYBER's coverage: the DATABASE die,
    /// all nine symbol tables (chips sized by importance = record count), every record and the NOD,
    /// and all model-space entities — each with its real entget pod and its real 64-bit rain — plus
    /// class-colored copper traces for the real relationships (record->table & entity->owner
    /// ownership, entity->layer-record, block/linetype/style references). Empty drawing -> demo.
    /// </summary>
    public static class Demo
    {
        // ---- palette -------------------------------------------------------------
        private static readonly AcColor Board     = AcColor.FromRgb(30, 120, 66);     // solder-mask green
        private static readonly AcColor Copper    = AcColor.FromRgb(200, 128, 52);    // copper trace
        private static readonly AcColor Silk      = AcColor.FromRgb(238, 240, 244);
        private static readonly AcColor Data      = AcColor.FromRgb(206, 212, 220);
        private static readonly AcColor IcBody    = AcColor.FromRgb(40, 40, 48);
        private static readonly AcColor RecBody   = AcColor.FromRgb(54, 54, 64);
        private static readonly AcColor DieBody   = AcColor.FromRgb(58, 46, 30);
        private static readonly AcColor GpuSub    = AcColor.FromRgb(28, 32, 60);     // GPU substrate (blue-violet)
        private static readonly AcColor GpuDie    = AcColor.FromRgb(22, 24, 32);     // bare GPU silicon
        private static readonly AcColor CopTop    = AcColor.FromRgb(212, 175, 55);
        private static readonly AcColor CopBot    = AcColor.FromRgb(150, 90, 40);
        private static readonly AcColor Via       = AcColor.FromRgb(228, 198, 92);
        private static readonly AcColor Pad       = AcColor.FromRgb(212, 175, 55);
        private static readonly AcColor Resistor  = AcColor.FromRgb(210, 140, 40);
        private static readonly AcColor Capacitor = AcColor.FromRgb(40, 180, 210);
        private static readonly AcColor Inductor  = AcColor.FromRgb(80, 200, 120);
        private static readonly AcColor Plume     = AcColor.FromRgb(40, 255, 90);
        private static readonly AcColor Terminal  = AcColor.FromRgb(200, 202, 210);
        private static readonly AcColor CapRim    = AcColor.FromRgb(150, 120, 50);
        private static readonly AcColor Mlcc      = AcColor.FromRgb(184, 152, 106);  // tiny ceramic cap body
        private static readonly AcColor Pin1      = AcColor.FromRgb(14, 14, 18);
        private static readonly AcColor Generic   = AcColor.FromRgb(150, 120, 170);
        private static readonly AcColor OpaqueBk  = AcColor.FromRgb(8, 8, 10);        // ACIS black-box
        private static readonly AcColor KataRed   = AcColor.FromRgb(232, 66, 66);     // opaque katakana
        // ---- extended package palette ----
        private static readonly AcColor DiodeBk   = AcColor.FromRgb(26, 26, 32);      // diode/transistor molded body
        private static readonly AcColor CanSilver = AcColor.FromRgb(198, 200, 205);   // metal can (crystal, TO, osc)
        private static readonly AcColor RelayBlue = AcColor.FromRgb(38, 66, 150);     // relay / large module case
        private static readonly AcColor PotBody   = AcColor.FromRgb(28, 56, 140);     // potentiometer body
        private static readonly AcColor Plastic   = AcColor.FromRgb(24, 24, 28);      // connector plastic base
        private static readonly AcColor ShieldTin = AcColor.FromRgb(176, 180, 188);   // EMI shield can
        private static readonly AcColor HeatAl    = AcColor.FromRgb(150, 154, 160);   // anodised heatsink
        private static readonly AcColor Glass     = AcColor.FromRgb(22, 30, 38);      // display glass
        private static readonly AcColor FuseGl    = AcColor.FromRgb(198, 186, 150);   // fuse cartridge glass
        private static readonly AcColor[] LedLens = {                                 // LED lens tints (picked by handle)
            AcColor.FromRgb(235, 60, 55), AcColor.FromRgb(70, 220, 90),
            AcColor.FromRgb(70, 130, 255), AcColor.FromRgb(255, 176, 40), AcColor.FromRgb(240, 240, 245) };

        private const string LBoard = "PCD-BOARD", LSilk = "PCD-SILK", LData = "PCD-DATA";
        private const string LIc = "PCD-IC", LPart = "PCD-PART", LVia = "PCD-VIA";
        private const string LPad = "PCD-PAD", LPlume = "PCD-PLUME", LNet = "PCD-NET", LKata = "PCD-KATA";

        // full-width katakana pool (== NXCYBER's), for the opaque-ACIS static plume
        private const string KataPool =
            "アイウエオカキクケコサシスセソ" +
            "タチツテトナニヌネノハヒフヘホ" +
            "マミムメモヤユヨラリルレロワヲン";

        private const double BT = 1.6;
        private const double CharW = 1.1, Pitch = 1.45, Margin = 0.85;
        private const double BitH = 0.7, BitStep = 0.8, ColStep = 1.1;
        private const double KataColStep = 1.15, KataStep = 1.2;   // ACIS katakana: spaced, not squished
        // Ceiling on parts (each part can spawn a 64-bit rain plume, so this bounds
        // render time on pathological drawings). Sized to fully cover a normal drawing;
        // tables+records are added before entities, so a low cap starves the entity read.
        private const int MaxParts = 400;
        // ReserveLayers marks a trace's OWN cells with CoreMark (hard block for other nets) and its 4
        // neighbours with +1 (soft cost only). Keeping the two apart matters: with a plain +3/+1 scheme,
        // three unrelated buffers summing to 3 read as a core and hard-blocked cells nobody occupied,
        // which closed corridors early and starved later nets (measured: 118 of 450 edges routed).
        private const int CoreMark = 50;
        // Per-cell penalty for top copper crossing refdes lettering. Large enough that a via down
        // to an inner plane and back (viaCost 55 each way) always beats crossing a multi-cell
        // label, small enough to stay finite so a walled-in net still routes instead of dropping.
        private const int SilkCost = 260;
        // Entity-band hardware (the drawing's entities: passives, LEDs, connectors, ACIS boxes) is drawn
        // at this fraction of its nominal package size, so it reads as the small discrete parts a real
        // board carries relative to its ICs. Chips (tables/records/CPU) are not scaled.
        private const double EntityScale = 0.62;
        // Pin-row pitch on a destination chip: one pad per ownership connection, this far apart (>= cs*sqrt2).
        private const double PinRowPitch = 1.0;

        private static readonly Vector3d Up = new Vector3d(0, 0, 1);
        private static readonly Vector3d FaceY = new Vector3d(0, 1, 0);
        private static ObjectId _mono, _kata;

        // ---- rows / parts / edges ------------------------------------------------
        private sealed class Row
        {
            public int Code; public string Name, Disp; public double[] Reals;
            public Row(int c, string n, string d, double[] r) { Code = c; Name = n; Disp = d; Reals = r; }
        }
        private static readonly double[] None = new double[0];
        private static Row Txt(int c, string n, string v) => new Row(c, n, v ?? "", None);
        private static Row Num(int c, string n, int v) => new Row(c, n, v.ToString(), None);
        private static Row Rl(int c, string n, double v) => new Row(c, n, F4(v), new[] { v });
        private static Row Pt(int c, string n, double x, double y, double z)
        {
            string d = Math.Abs(z) < 1e-9 ? "(" + F2(x) + " " + F2(y) + ")"
                                          : "(" + F2(x) + " " + F2(y) + " " + F2(z) + ")";
            return new Row(c, n, d, new[] { x, y, z });
        }
        private static string F4(double v) => v.ToString("0.0000");
        private static string F2(double v) => v.ToString("0.##");
        private static string Bits64(double x)
        {
            long b = BitConverter.DoubleToInt64Bits(x);
            var sb = new StringBuilder(64);
            for (int i = 63; i >= 0; i--) sb.Append((char)('0' + (int)((b >> i) & 1L)));
            return sb.ToString();
        }

        // ---- deterministic PRNG (stable across re-runs; per-part seeded) ----------
        private static uint _rng = 1;
        private static void Seed(uint s) { _rng = s == 0 ? 1u : s; }
        private static uint Rnd() { _rng = _rng * 1664525u + 1013904223u; return _rng; }
        private static double R01() => (Rnd() >> 8) / 16777216.0;
        private static double RR(double a, double bb) => a + R01() * (bb - a);

        /// <summary>Give each part organic size/orientation/package variety (deterministic by index).</summary>
        private static void Vary(List<Part> parts)
        {
            foreach (var p in parts)
            {
                var (w, d, z, col) = PartFoot(p);
                Seed((uint)(p.Idx * 2654435761u + 17u));
                bool chip = p.Kind == Kind.Die || p.Kind == Kind.Table || p.Kind == Kind.Nod
                         || p.Kind == Kind.Record || p.Kind == Kind.Gpu;
                if (chip)
                {
                    // organic scatter: every chip gets size variety and a random 0/90 orientation
                    p.Vw = w * RR(0.88, 1.18); p.Vd = d * RR(0.88, 1.22); p.Vz = z * RR(0.8, 1.6);
                    p.Variant = (int)(Rnd() % 3);      // SOIC / QFP / DIP lead style
                    if (p.Kind != Kind.Die && p.Kind != Kind.Gpu && R01() < 0.45) { double t = p.Vw; p.Vw = p.Vd; p.Vd = t; p.Rot = 1; }
                }
                else if (p.Kind == Kind.Opaque)
                {
                    // merged ACIS chip: keep the bbox-derived size (relative to the object), light jitter only
                    p.Vw = w * EntityScale * RR(0.97, 1.03); p.Vd = d * EntityScale * RR(0.97, 1.03); p.Vz = z * EntityScale;
                    p.Variant = (int)(Rnd() % 3);
                }
                else
                {
                    // DATA-DRIVEN size: the entity's real magnitude (radius/length/height) sets the
                    // package scale -- a big circle is a big cap, a short line a small resistor.
                    double s = p.Mag > 0 ? Math.Min(2.0, 0.72 + 0.55 * Math.Log10(1 + p.Mag)) : RR(0.8, 1.2);
                    double es = s * EntityScale;   // magnitude scale x the global entity shrink
                    p.Vw = w * es * RR(0.9, 1.12); p.Vd = d * es * RR(0.9, 1.12); p.Vz = z * es * RR(0.75, 1.3);
                    p.Variant = (int)(Rnd() % 3);      // package sub-shape
                    if (R01() < 0.55) { double t = p.Vw; p.Vw = p.Vd; p.Vd = t; p.Rot = 1; }
                }
                p.Col = Jit(col, 18);
            }
        }

        private static int Clamp(int v) => v < 0 ? 0 : (v > 255 ? 255 : v);
        private static AcColor Jit(AcColor c, int amt) => AcColor.FromRgb(
            (byte)Clamp(c.Red + (int)RR(-amt, amt)),
            (byte)Clamp(c.Green + (int)RR(-amt, amt)),
            (byte)Clamp(c.Blue + (int)RR(-amt, amt)));

        private enum Kind { Die, Table, Record, Nod, Resistor, Capacitor, Inductor, TextPart, Array, Socket, TestPad, Opaque, Generic,
            Diode, Led, Transistor, Crystal, Connector, Relay, Dip, Pot, Gpu,
            // types that used to collapse to Generic, now packaged
            Shield, Sensor, Coil, Antenna, Memory, Fuse, Heatsink, Ribbon, Display, Rail }
        private enum ECls { Own, Layer, Block, Ltype, Style, Dim, App, Bad }
        private sealed class Part
        {
            public int Idx; public string RefDes, Title; public Kind Kind; public Row[] Rows;
            public double W, D, TopZ, Cx, Cy; public AcColor Col; public int Rec;
            public double Mag;    // real-geometry magnitude (radius/length/height) -> discrete size
            public string Band;   // "table" | "record" | "entity"
            public int TableIdx = -1, LayerRec = -1, OwnerIdx = -1;   // OwnerIdx = true DWG owner (Model Space for entities)
            public double Vw, Vd, Vz; public int Variant, Rot;   // per-part variety
        }
        private sealed class Edge { public int A, B; public ECls Cls; }

        // ---- nets are colored BY TABLE: every relationship belongs to one symbol table's net ----
        private static readonly AcColor[] NetPalette = {
            AcColor.FromRgb(0, 225, 255),  AcColor.FromRgb(255, 70, 210), AcColor.FromRgb(255, 150, 0),
            AcColor.FromRgb(255, 225, 0),  AcColor.FromRgb(120, 255, 70), AcColor.FromRgb(90, 130, 255),
            AcColor.FromRgb(255, 80, 80),  AcColor.FromRgb(190, 100, 255), AcColor.FromRgb(0, 255, 190),
            AcColor.FromRgb(255, 200, 120),
        };
        private static readonly Dictionary<int, AcColor> _tblCol = new Dictionary<int, AcColor>();
        /// <summary>The net color of a part = the color of the symbol table it belongs to.</summary>
        private static AcColor TableColor(List<Part> parts, int idx)
        {
            var p = parts[idx];
            int t = (p.Kind == Kind.Table || p.Kind == Kind.Nod) ? idx
                  : (p.Kind == Kind.Record || p.Kind == Kind.Gpu) ? p.TableIdx
                  : p.LayerRec >= 0 ? parts[p.LayerRec].TableIdx : -1;
            return t >= 0 && _tblCol.TryGetValue(t, out var c) ? c : Copper;
        }
        private static AcColor NetCol(List<Part> parts, Edge e)
        {
            Part B = parts[e.B];   // the table side of the relationship owns the net
            int pick = (B.Kind == Kind.Table || B.Kind == Kind.Nod || B.Kind == Kind.Record
                     || B.Kind == Kind.Gpu) ? e.B : e.A;
            return TableColor(parts, pick);
        }
        /// <summary>Dim a color toward black by factor f (0..1) — reference hairlines vs ownership copper.</summary>
        private static AcColor Dim(AcColor c, double f) =>
            AcColor.FromRgb((byte)(c.Red * f), (byte)(c.Green * f), (byte)(c.Blue * f));

        // legacy copper shades (used only by the empty-drawing demo)
        private static AcColor EdgeColor(ECls c) => c switch
        {
            ECls.Own   => AcColor.FromRgb(206, 132, 54),
            ECls.Layer => AcColor.FromRgb(196, 124, 50),
            _          => AcColor.FromRgb(188, 118, 48),
        };

        public static void Build(Database db, Transaction tr)
        {
            var b = new Pcb(db, tr);
            Setup(b);
            var parts = new List<Part>();
            var edges = new List<Edge>();
            bool any = ReadDatabase(db, tr, parts, edges);
            // build the board CLEAR of the source drawing, never on top of it
            if (_srcAny) b.Origin = new Vector3d(Math.Max(0, _srcMaxX) + 15, 0, 0);
            if (!any) { Synthetic(b); return; }
            Vary(parts);
            SizeByPins(parts, edges);   // a chip grows to hold one pad per connection (a 113-pin hub IS a big chip)
            Layout(parts);
            BuildFromGraph(b, parts, edges);
        }

        // ======================================================================
        //  READ — full depth
        // ======================================================================
        private static double _srcMaxX; private static bool _srcAny;   // source-drawing extents
        // source entity positions: the board OUTLINE is derived from their convex hull
        private static readonly List<(double x, double y)> _srcPts = new List<(double, double)>();
        private static readonly List<(double x, double y, double r)> _holes = new List<(double, double, double)>();   // mounting holes

        private static bool ReadDatabase(Database db, Transaction tr, List<Part> parts, List<Edge> edges)
        {
            _srcMaxX = -1e9; _srcAny = false; _srcPts.Clear();
            var layerMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var blockMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var ltMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var styMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var dimMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var appMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            int Add(Part p) { p.Idx = parts.Count; parts.Add(p); return p.Idx; }

            // --- DATABASE die ---
            int nod = 0;
            try { var d0 = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForRead);
                  foreach (var _ in d0) nod++; } catch { }
            string name = "(unsaved)";
            try { if (!string.IsNullOrEmpty(db.Filename)) name = Path.GetFileName(db.Filename); } catch { }
            int die = Add(new Part { Kind = Kind.Die, Band = "table", RefDes = "CPU", Col = DieBody,
                W = 42, D = 38, TopZ = 4.4, Rows = new[] {
                    Txt(0, "root object", "DATABASE"),
                    Txt(5, "dwg name", name),
                    Txt(1, "detail", "one per DWG; 330 chains end here"),
                    Txt(2, "owns", "9 symbol tables + NOD (" + nod + " keys)"),
                    Txt(3, "owner", "none (root)"),
                }, Title = "DATABASE :: " + name });

            // --- 9 symbol tables (ordered by importance), each -> a chip + record parts ---
            int lBlock = AddTable(db, tr, db.BlockTableId, "BLOCK", parts, edges, die, Add, blockMap, tr2 => BlockRows(tr2));
            int lLayer = AddTable(db, tr, db.LayerTableId, "LAYER", parts, edges, die, Add, layerMap, tr2 => LayerRows(tr2));
            int lLt    = AddTable(db, tr, db.LinetypeTableId, "LTYPE", parts, edges, die, Add, ltMap, tr2 => LtypeRows(tr2));
            int lSty   = AddTable(db, tr, db.TextStyleTableId, "STYLE", parts, edges, die, Add, styMap, tr2 => StyleRows(tr2));
            AddTable(db, tr, db.DimStyleTableId, "DIMSTYLE", parts, edges, die, Add, dimMap, tr2 => DimRows(tr2));
            AddTable(db, tr, db.RegAppTableId, "APPID", parts, edges, die, Add, appMap, tr2 => NameOnly(tr2));
            AddTable(db, tr, db.ViewportTableId, "VPORT", parts, edges, die, Add, null, tr2 => NameOnly(tr2));
            AddTable(db, tr, db.ViewTableId, "VIEW", parts, edges, die, Add, null, tr2 => NameOnly(tr2));
            AddTable(db, tr, db.UcsTableId, "UCS", parts, edges, die, Add, null, tr2 => NameOnly(tr2));

            // --- Named Object Dictionary ---
            if (parts.Count < MaxParts)
            {
                int nodP = Add(new Part { Kind = Kind.Nod, Band = "table", RefDes = "NOD", Col = IcBody,
                    W = 18 + Math.Min(nod, 12), D = 12, TopZ = 3.0, Rec = nod, Rows = new[] {
                        Txt(0, "class", "AcDbDictionary"), Num(90, "keys", nod),
                        Txt(2, "role", "named-object root; holds groups, layouts, etc."),
                    }, Title = "NOD :: named object dict" });
                edges.Add(new Edge { A = nodP, B = die, Cls = ECls.Own });
            }

            // --- model-space entities ---
            // Every model-space entity is OWNED by the *Model_Space block record (a chip added under
            // the BLOCK table). Layer/linetype/style are references, not owners. Route ownership.
            int msIdx = blockMap.TryGetValue("*Model_Space", out var _ms) ? _ms : die;
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
            int r = 0, c = 0, l = 0, t = 0, j = 0, tp = 0, g = 0, dd = 0, q = 0, y = 0, kk = 0, u = 0, rv = 0;
            int mp = 0, mk = 0, ee = 0, ff = 0, hs = 0, ds = 0, ww = 0;
            // ACIS b-reps are collected, then MERGED by touching bounding box into one chip
            // per physical part (a sheet-metal part is one chip, not 30 region boxes).
            var acis = new List<AcisBox>();
            foreach (ObjectId id in ms)
            {
                if (parts.Count >= MaxParts) break;
                var e = tr.GetObject(id, OpenMode.ForRead) as Entity;
                if (e == null || e.Layer.StartsWith("PCD-")) continue;
                try { var ex = e.GeometricExtents;
                      if (ex.MaxPoint.X > _srcMaxX) _srcMaxX = ex.MaxPoint.X; _srcAny = true;
                      _srcPts.Add(((ex.MinPoint.X + ex.MaxPoint.X) / 2, (ex.MinPoint.Y + ex.MaxPoint.Y) / 2)); } catch { }
                string ty = e.GetRXClass().DxfName;
                var rows = new List<Row> {
                    Txt(0, "entity type", ty), Txt(5, "handle", e.Handle.ToString()),
                    Num(62, "color ACI", e.ColorIndex), Rl(48, "lt scale", e.LinetypeScale),
                };
                Kind k; double mag = 0;
                // Matched by DXF NAME rather than by managed type: these classes are not all exposed
                // the same way across host versions, and the name is what the database actually stores.
                // Each still emits the full Own + Layer + Ltype relationship set; this only decides the
                // package it wears, so no relationship depends on the match succeeding.
                Kind? byName = ty switch
                {
                    "HATCH"       => Kind.Shield,     // a fill pattern -> a perforated EMI shield can
                    "DIMENSION"   => Kind.Sensor,     // a measurement -> a sensor package with a port
                    "SPLINE"      => Kind.Coil,       // a wound curve -> an air-core wound inductor
                    "LEADER"      => Kind.Antenna,    // points somewhere -> a chip antenna + whip
                    "MULTILEADER" => Kind.Antenna,
                    "ACAD_TABLE"  => Kind.Memory,     // rows and columns -> a memory edge card
                    "ATTDEF"      => Kind.Fuse,       // a named slot -> a cartridge fuse in clips
                    "WIPEOUT"     => Kind.Heatsink,   // masks what is under it -> a finned heatsink
                    "MLINE"       => Kind.Ribbon,     // parallel conductors -> an IDC ribbon header
                    "3DFACE"      => Kind.Display,    // a flat plate -> a glass display module
                    "XLINE"       => Kind.Rail,       // unbounded -> a power bus bar on standoffs
                    "RAY"         => Kind.Rail,
                    _             => (Kind?)null,
                };
                // sub-variety WITHIN a DXF family is picked by the entity's own handle (reproducible,
                // data-derived): one DXF type -> a family of realistic packages, so the board reads varied.
                long hh = 0; try { hh = e.Handle.Value; } catch { } hh = Math.Abs(hh);
                if (byName.HasValue)
                {
                    k = byName.Value;
                    try { var xb = e.GeometricExtents;
                          rows.Add(Pt(10, "min pt", xb.MinPoint.X, xb.MinPoint.Y, xb.MinPoint.Z));
                          rows.Add(Pt(11, "max pt", xb.MaxPoint.X, xb.MaxPoint.Y, xb.MaxPoint.Z));
                          mag = xb.MaxPoint.DistanceTo(xb.MinPoint); } catch { }
                }
                else switch (e)
                {
                    case Line ln:
                        rows.Add(Pt(10, "start pt", ln.StartPoint.X, ln.StartPoint.Y, ln.StartPoint.Z));
                        rows.Add(Pt(11, "end pt", ln.EndPoint.X, ln.EndPoint.Y, ln.EndPoint.Z));
                        rows.Add(Pt(210, "normal", ln.Normal.X, ln.Normal.Y, ln.Normal.Z));
                        mag = ln.Length; k = (hh % 3 == 0) ? Kind.Diode : Kind.Resistor; break;
                    case Arc a:
                        rows.Add(Rl(40, "radius", a.Radius)); rows.Add(Rl(50, "start ang", Deg(a.StartAngle)));
                        rows.Add(Rl(51, "end ang", Deg(a.EndAngle)));
                        rows.Add(Pt(10, "center", a.Center.X, a.Center.Y, a.Center.Z));
                        mag = a.Radius; k = (hh % 3 == 0) ? Kind.Crystal : Kind.Inductor; break;
                    case Circle ci:
                        rows.Add(Rl(40, "radius", ci.Radius));
                        rows.Add(Pt(10, "center", ci.Center.X, ci.Center.Y, ci.Center.Z));
                        rows.Add(Pt(210, "normal", ci.Normal.X, ci.Normal.Y, ci.Normal.Z));
                        mag = ci.Radius; k = (hh % 4) switch { 0 => Kind.Led, 1 => Kind.Transistor, 2 => Kind.Pot, _ => Kind.Capacitor }; break;
                    case Ellipse el:
                        rows.Add(Rl(40, "major", el.MajorRadius)); rows.Add(Rl(41, "ratio", el.RadiusRatio));
                        rows.Add(Pt(10, "center", el.Center.X, el.Center.Y, el.Center.Z));
                        mag = el.MajorRadius; k = (hh % 2 == 0) ? Kind.Crystal : Kind.Capacitor; break;
                    case DBText dt:
                        rows.Add(Rl(40, "height", dt.Height));
                        rows.Add(Pt(10, "position", dt.Position.X, dt.Position.Y, dt.Position.Z));
                        rows.Add(Rl(50, "rotation", Deg(dt.Rotation))); rows.Add(Txt(1, "text value", dt.TextString));
                        mag = dt.Height * 3; k = Kind.TextPart; break;
                    case MText mt:
                        rows.Add(Rl(40, "height", mt.TextHeight));
                        rows.Add(Pt(10, "position", mt.Location.X, mt.Location.Y, mt.Location.Z));
                        rows.Add(Txt(1, "text value", mt.Contents));
                        mag = mt.TextHeight * 3; k = Kind.TextPart; break;
                    case Polyline pl:
                        rows.Add(Num(90, "vertex count", pl.NumberOfVertices)); rows.Add(Rl(38, "elevation", pl.Elevation));
                        rows.Add(Num(70, "flags", pl.Closed ? 1 : 0));
                        try { mag = pl.Length; } catch { } k = pl.Closed ? ((hh % 2 == 0) ? Kind.Dip : Kind.Connector) : Kind.Array; break;
                    case BlockReference br:
                        rows.Add(Txt(2, "name", br.Name)); rows.Add(Pt(10, "insert pt", br.Position.X, br.Position.Y, br.Position.Z));
                        rows.Add(Rl(50, "rotation", Deg(br.Rotation)));
                        k = (hh % 3) switch { 0 => Kind.Relay, 1 => Kind.Connector, _ => Kind.Socket }; break;
                    case DBPoint dp:
                        rows.Add(Pt(10, "position", dp.Position.X, dp.Position.Y, dp.Position.Z)); k = Kind.TestPad; break;
                    case Solid3d _:
                    case Region _:
                    case Autodesk.AutoCAD.DatabaseServices.Surface _:
                    case Body _:
                        // defer: collect its bbox, merge with touching solids after the loop
                        try { var xe = e.GeometricExtents;
                              acis.Add(new AcisBox { X0 = xe.MinPoint.X, Y0 = xe.MinPoint.Y, Z0 = xe.MinPoint.Z,
                                                     X1 = xe.MaxPoint.X, Y1 = xe.MaxPoint.Y, Z1 = xe.MaxPoint.Z,
                                                     Layer = e.Layer, Type = ty }); }
                        catch { acis.Add(new AcisBox { X1 = 1, Y1 = 1, Z1 = 1, Layer = e.Layer, Type = ty }); }
                        continue;
                    default: k = Kind.Generic; break;
                }
                int idx = Add(new Part { Kind = k, Band = "entity", Mag = mag, Rows = rows.ToArray(),
                    Title = ty + " :: " + e.Handle,
                    RefDes = k switch { Kind.Resistor => "R" + (++r), Kind.Capacitor => "C" + (++c),
                        Kind.Inductor => "L" + (++l), Kind.TextPart => "T" + (++t), Kind.Socket => "J" + (++j),
                        Kind.TestPad => "TP" + (++tp), Kind.Array => "RN" + (++t), Kind.Opaque => "M" + (++g),
                        Kind.Diode => "D" + (++dd), Kind.Led => "D" + (++dd), Kind.Transistor => "Q" + (++q),
                        Kind.Crystal => "Y" + (++y), Kind.Connector => "J" + (++j), Kind.Relay => "K" + (++kk),
                        Kind.Dip => "U" + (++u), Kind.Pot => "RV" + (++rv),
                        Kind.Shield => "MP" + (++mp), Kind.Sensor => "MK" + (++mk),
                        Kind.Coil => "L" + (++l), Kind.Antenna => "E" + (++ee),
                        Kind.Memory => "U" + (++u), Kind.Fuse => "F" + (++ff),
                        Kind.Heatsink => "HS" + (++hs), Kind.Ribbon => "J" + (++j),
                        Kind.Display => "DS" + (++ds), Kind.Rail => "W" + (++ww),
                        _ => "X" + (++g) } });

                // OWNERSHIP edge (the routed copper): the entity belongs to *Model_Space.
                edges.Add(new Edge { A = idx, B = msIdx, Cls = ECls.Own }); parts[idx].OwnerIdx = msIdx;
                // REFERENCES (layer/linetype/style/block): the entity also POINTS TO the layer it is on,
                // its linetype, its style. These are routed too, but as THIN DIM hairlines (see the router)
                // so the ownership copper stays dominant. Layer also drives the color group.
                if (layerMap.TryGetValue(e.Layer, out int lyr))
                { parts[idx].LayerRec = lyr; edges.Add(new Edge { A = idx, B = lyr, Cls = ECls.Layer }); }
                try { string ltn = e.Linetype; if (!string.IsNullOrEmpty(ltn) && ltMap.TryGetValue(ltn, out int lti))
                      edges.Add(new Edge { A = idx, B = lti, Cls = ECls.Ltype }); } catch { }
                if (e is DBText dtt) TryStyle(tr, dtt.TextStyleId, styMap, idx, edges);
                if (e is MText mtt) TryStyle(tr, mtt.TextStyleId, styMap, idx, edges);
                if (e is BlockReference bref && blockMap.TryGetValue(bref.Name, out int bi))
                    edges.Add(new Edge { A = idx, B = bi, Cls = ECls.Block });
                // A dimension REFERENCES the DIMSTYLE it is drawn with. Before this, ECls.Dim was
                // declared and never emitted, so the DIMSTYLE chip sat on the board with records but
                // no traffic -- two of nine symbol tables were decorative.
                if (e is Dimension dmn)
                { try { if (dimMap.TryGetValue(dmn.DimensionStyleName, out int dsi))
                            edges.Add(new Edge { A = idx, B = dsi, Cls = ECls.Dim }); } catch { } }
                // Xdata REFERENCES the APPID that registered it: group 1001 opens each app's block,
                // so one edge per distinct registered application on this entity.
                try { var xd = e.XData;
                      if (xd != null)
                      { var seenApp = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (TypedValue tv in xd)
                            if (tv.TypeCode == (short)DxfCode.ExtendedDataRegAppName && tv.Value != null)
                            { string an = tv.Value.ToString();
                              if (seenApp.Add(an) && appMap.TryGetValue(an, out int ai))
                                  edges.Add(new Edge { A = idx, B = ai, Cls = ECls.App }); } } } catch { }
            }

            AddMergedAcis(acis, parts, edges, layerMap, msIdx, Add, ref g);
            return parts.Count > 1;
        }

        private sealed class AcisBox { public double X0, Y0, Z0, X1 = 1, Y1 = 1, Z1 = 1; public string Layer, Type; }

        /// <summary>Union ACIS b-reps whose XY bounding boxes touch/overlap, then emit ONE opaque
        /// chip per cluster, sized RELATIVE to the physical object (bigger part -> bigger chip).</summary>
        private static void AddMergedAcis(List<AcisBox> acis, List<Part> parts, List<Edge> edges,
                                          Dictionary<string, int> layerMap, int msIdx, Func<Part, int> Add, ref int g)
        {
            int n = acis.Count; if (n == 0) return;
            var par = new int[n]; for (int i = 0; i < n; i++) par[i] = i;
            int Find(int x) { while (par[x] != x) { par[x] = par[par[x]]; x = par[x]; } return x; }
            for (int i = 0; i < n; i++)
                for (int jj = i + 1; jj < n; jj++)
                {
                    AcisBox A = acis[i], B = acis[jj];
                    double tol = 0.05 * Math.Max(Math.Max(A.X1 - A.X0, A.Y1 - A.Y0), Math.Max(B.X1 - B.X0, B.Y1 - B.Y0));
                    bool ov = !(A.X1 < B.X0 - tol || B.X1 < A.X0 - tol || A.Y1 < B.Y0 - tol || B.Y1 < A.Y0 - tol);
                    if (ov) par[Find(i)] = Find(jj);
                }
            var groups = new Dictionary<int, List<int>>();
            for (int i = 0; i < n; i++) { int rt = Find(i); if (!groups.TryGetValue(rt, out var lst)) { lst = new List<int>(); groups[rt] = lst; } lst.Add(i); }

            // measure every cluster first, so sizes can be scaled RELATIVE to the largest
            var boxes = new List<(double w, double d, double h, string layer, int cnt, string types)>();
            double maxDim = 1e-9;
            foreach (var grp in groups.Values)
            {
                double x0 = 1e18, y0 = 1e18, z0 = 1e18, x1 = -1e18, y1 = -1e18, z1 = -1e18;
                var lys = new Dictionary<string, int>(); var tys = new SortedSet<string>();
                foreach (int i in grp)
                {
                    AcisBox A = acis[i];
                    x0 = Math.Min(x0, A.X0); y0 = Math.Min(y0, A.Y0); z0 = Math.Min(z0, A.Z0);
                    x1 = Math.Max(x1, A.X1); y1 = Math.Max(y1, A.Y1); z1 = Math.Max(z1, A.Z1);
                    lys[A.Layer] = lys.TryGetValue(A.Layer, out int cc) ? cc + 1 : 1; tys.Add(A.Type);
                }
                double w = x1 - x0, d = y1 - y0, h = z1 - z0;
                string ly = null; int best = -1; foreach (var kv in lys) if (kv.Value > best) { best = kv.Value; ly = kv.Key; }
                maxDim = Math.Max(maxDim, Math.Max(w, d));
                boxes.Add((w, d, h, ly, grp.Count, string.Join("+", tys)));
            }
            double scale = 30.0 / maxDim;
            foreach (var bx in boxes)
            {
                double cw = Math.Max(11, Math.Min(40, bx.w * scale));
                double cd = Math.Max(9, Math.Min(34, bx.d * scale));
                double cz = Math.Max(2.6, Math.Min(6.5, bx.h * scale * 0.4));
                var rows = new[] {
                    Txt(0, "entity type", bx.types), Num(90, "pieces merged", bx.cnt),
                    Txt(1, "acis", "b-rep -- opaque to LISP"),
                    Rl(40, "bbox W", bx.w), Rl(41, "bbox D", bx.d), Rl(43, "bbox H", bx.h),
                    Txt(3, "note", "one chip per physical solid; static below") };
                int idx = Add(new Part { Kind = Kind.Opaque, Band = "entity", W = cw, D = cd, TopZ = cz,
                    Rows = rows, Title = "ACIS solid :: " + bx.cnt + " pcs", RefDes = "M" + (++g) });
                edges.Add(new Edge { A = idx, B = msIdx, Cls = ECls.Own }); parts[idx].OwnerIdx = msIdx;   // owned by *Model_Space
                if (bx.layer != null && layerMap.TryGetValue(bx.layer, out int lyr))
                { parts[idx].LayerRec = lyr; edges.Add(new Edge { A = idx, B = lyr, Cls = ECls.Layer }); }   // + layer reference (thin dim)
            }
        }

        private static void TryStyle(Transaction tr, ObjectId styId, Dictionary<string, int> styMap, int idx, List<Edge> edges)
        {
            try { var s = (SymbolTableRecord)tr.GetObject(styId, OpenMode.ForRead);
                  if (styMap.TryGetValue(s.Name, out int si)) edges.Add(new Edge { A = idx, B = si, Cls = ECls.Style }); } catch { }
        }

        // Create a table chip (sized by record count) + its record parts + ownership edges.
        private static int AddTable(Database db, Transaction tr, ObjectId tableId, string tname,
            List<Part> parts, List<Edge> edges, int die, Func<Part, int> add,
            Dictionary<string, int> map, Func<SymbolTableRecord, Row[]> detail)
        {
            var st = (SymbolTable)tr.GetObject(tableId, OpenMode.ForRead);
            var recIds = new List<ObjectId>();
            foreach (ObjectId id in st) recIds.Add(id);
            int rc = recIds.Count;
            var table = new Part { Kind = Kind.Table, Band = "table", RefDes = tname, Col = IcBody, Rec = rc,
                W = 15 + Math.Min(rc, 16) * 1.6, D = 11 + Math.Min(rc, 16) * 0.45, TopZ = 3.2,
                Rows = new[] { Txt(0, "class", "SymbolTable"), Txt(2, "name", tname + " table"),
                               Num(90, "records", rc), Txt(3, "owner", "the DATABASE") },
                Title = tname + " TABLE (" + rc + ")" };
            int ti = add(table);
            edges.Add(new Edge { A = ti, B = die, Cls = ECls.Own });

            foreach (ObjectId id in recIds)
            {
                if (parts.Count >= MaxParts) break;
                SymbolTableRecord rec;
                try { rec = (SymbolTableRecord)tr.GetObject(id, OpenMode.ForRead); } catch { continue; }
                if (rec.Name != null && rec.Name.StartsWith("PCD-")) continue;
                if (tname == "BLOCK" && rec.Name != null && rec.Name.StartsWith("*")
                    && !rec.Name.Equals("*Model_Space", StringComparison.OrdinalIgnoreCase)
                    && !rec.Name.Equals("*Paper_Space", StringComparison.OrdinalIgnoreCase)) continue;
                Row[] rows;
                try { rows = detail(rec); } catch { rows = new[] { Txt(2, "name", rec.Name), Txt(5, "handle", rec.Handle.ToString()) }; }
                // *Model_Space is the GPU. It OWNS every drawable entity, so it carries the widest
                // ownership bus on the board (one pad per entity) -- the electrical signature of the
                // second big die. It stays a BLOCK record: its own ownership edge still runs to the
                // BLOCK table below, so the database mapping is unchanged; only the package differs.
                bool isMs = tname == "BLOCK" && rec.Name != null
                            && rec.Name.Equals("*Model_Space", StringComparison.OrdinalIgnoreCase);
                var rp = isMs
                    ? new Part { Kind = Kind.Gpu, Band = "record", TableIdx = ti, Col = GpuSub,
                        W = 46, D = 34, TopZ = 4.0, RefDes = "GPU", Rows = rows,
                        Title = tname + " :: " + Short(rec.Name) }
                    : new Part { Kind = Kind.Record, Band = "record", TableIdx = ti, Col = RecBody,
                        W = 15, D = 8, TopZ = 2.6, RefDes = "", Rows = rows, Title = tname + " :: " + Short(rec.Name) };
                int ri = add(rp);
                edges.Add(new Edge { A = ri, B = ti, Cls = ECls.Own });
                if (map != null && rec.Name != null && !map.ContainsKey(rec.Name)) map[rec.Name] = ri;
            }
            return ti;
        }

        private static string Short(string s) => string.IsNullOrEmpty(s) ? "?" : (s.Length > 16 ? s.Substring(0, 15) + "~" : s);

        // ---- per-table record detail (nxcRecDet parity) --------------------------
        private static Row[] LayerRows(SymbolTableRecord r)
        {
            var l = (LayerTableRecord)r; string lt = "Continuous"; int flags = (l.IsFrozen ? 1 : 0) | (l.IsLocked ? 4 : 0);
            return new[] { Txt(2, "name", l.Name), Rl(62, "color ACI", l.Color.ColorIndex), Txt(6, "ltype", lt),
                Rl(70, "flags", flags), Rl(370, "lineweight", (int)l.LineWeight), Txt(5, "handle", l.Handle.ToString()) };
        }
        private static Row[] LtypeRows(SymbolTableRecord r)
        {
            var lt = (LinetypeTableRecord)r;
            return new[] { Txt(2, "name", lt.Name), Num(73, "dashes", lt.NumDashes), Rl(40, "pat length", lt.PatternLength),
                Txt(3, "descr", Short(lt.Comments)), Txt(5, "handle", lt.Handle.ToString()) };
        }
        private static Row[] StyleRows(SymbolTableRecord r)
        {
            var s = (TextStyleTableRecord)r;
            return new[] { Txt(2, "name", s.Name), Txt(3, "font", Short(s.FileName)), Rl(40, "height", s.TextSize),
                Rl(41, "width", s.XScale), Rl(50, "oblique", Deg(s.ObliquingAngle)), Txt(5, "handle", s.Handle.ToString()) };
        }
        private static Row[] DimRows(SymbolTableRecord r)
        {
            var d = (DimStyleTableRecord)r;
            return new[] { Txt(2, "name", d.Name), Rl(140, "text ht", d.Dimtxt), Rl(40, "scale", d.Dimscale),
                Txt(5, "handle", d.Handle.ToString()) };
        }
        private static Row[] BlockRows(SymbolTableRecord r)
        {
            var btr = (BlockTableRecord)r; int n = 0; foreach (var _ in btr) n++;
            return new[] { Txt(2, "name", btr.Name), Num(90, "entities", n),
                Txt(70, "kind", btr.IsLayout ? "layout" : (btr.IsAnonymous ? "anon" : "block")),
                Txt(5, "handle", btr.Handle.ToString()) };
        }
        private static Row[] NameOnly(SymbolTableRecord r) =>
            new[] { Txt(2, "name", r.Name), Txt(5, "handle", r.Handle.ToString()) };

        private static double Deg(double rad) => rad * 180.0 / Math.PI;

        // ======================================================================
        //  LAYOUT — FUNCTIONAL SECTIONS (hierarchy the way a real board shows it):
        //  the CPU (DATABASE) sits alone at the left; every symbol table gets a
        //  silkscreen-bordered SECTION holding its record BANK; the LAYER section
        //  nests each layer's entities under that layer's record. Ownership runs
        //  as BUSES: a fan-out off the table chip's bottom edge, one spine per
        //  column, one stub per part. Cross-section references go to the router.
        // ======================================================================
        // ======================================================================
        //  LAYOUT — ORGANIC SCATTER around a central hub (the director's reference boards):
        //  the CPU is fixed at the origin; every other part is seeded by its place in the
        //  ownership tree (angle inherited from its parent with jitter, radius loosely by
        //  depth) and then RELAXED: rectangles push apart, children are drawn toward their
        //  parent by a spring, nothing may sit on the hub. No zones, labels, grids or buses —
        //  hierarchy is implied by proximity and by the radiating nets, as on a real board.
        // ======================================================================
        private static void Layout(List<Part> parts)
        {
            int n = parts.Count, cpu = -1;
            var parent = new int[n]; var depth = new int[n]; var tables = new List<int>();
            for (int i = 0; i < n; i++)
            {
                var p = parts[i]; parent[i] = -1;
                switch (p.Kind)
                {
                    case Kind.Die: cpu = i; depth[i] = 0; break;
                    case Kind.Table: case Kind.Nod: tables.Add(i); depth[i] = 1; break;
                    case Kind.Record: case Kind.Gpu: parent[i] = p.TableIdx; depth[i] = 2; break;
                    default: parent[i] = p.OwnerIdx >= 0 ? p.OwnerIdx : p.LayerRec; depth[i] = 3; break;
                }
            }
            foreach (int t in tables) parent[t] = cpu;
            for (int i = 0; i < n; i++) if (parent[i] < 0 && i != cpu) parent[i] = cpu;   // orphans hang off the hub
            _tblCol.Clear();
            for (int i = 0; i < tables.Count; i++) _tblCol[tables[i]] = NetPalette[i % NetPalette.Length];
            if (cpu >= 0) { parts[cpu].Cx = 0; parts[cpu].Cy = 0; }

            // seed: angle inherited down the tree (with jitter), radius loosely by depth
            var ang = new double[n]; var done = new bool[n];
            if (cpu >= 0) done[cpu] = true;
            for (int k = 0; k < tables.Count; k++)
            { Seed((uint)(tables[k] * 7919u + 3u)); ang[tables[k]] = 2 * Math.PI * (k + RR(-0.2, 0.2)) / tables.Count; done[tables[k]] = true; }
            for (int pass = 0; pass < 4; pass++)
                for (int i = 0; i < n; i++)
                    if (!done[i] && parent[i] >= 0 && done[parent[i]])
                    {
                        Seed((uint)(i * 2654435761u + 11u));
                        double spread = depth[i] == 2 ? 0.55 : 0.45;
                        ang[i] = ang[parent[i]] + RR(-spread, spread); done[i] = true;
                    }
            double[] baseR = { 0, 46, 80, 110 };   // compacted (~0.73x): same tree seeding, tighter rings
            for (int i = 0; i < n; i++)
            {
                if (i == cpu) continue;
                Seed((uint)(i * 40503u + 5u));
                double r = baseR[Math.Min(3, depth[i])] * RR(0.7, 1.35);
                parts[i].Cx = Math.Cos(ang[i]) * r; parts[i].Cy = Math.Sin(ang[i]) * r;
            }

            // relax: spring toward parent + push rectangles apart + keep clear of the hub
            const double clear = 6.5;    // extra leeway between parts (room for routing; ~3.8 router cells)
            void PushApart(double gap)
            {
                for (int i = 0; i < n; i++)
                    for (int j = i + 1; j < n; j++)
                    {
                        var a = parts[i]; var c = parts[j];
                        double hw = a.Vw / 2 + c.Vw / 2 + gap, hd = a.Vd / 2 + c.Vd / 2 + gap;
                        double dx = c.Cx - a.Cx, dy = c.Cy - a.Cy;
                        double ox = hw - Math.Abs(dx), oy = hd - Math.Abs(dy);
                        if (ox <= 0 || oy <= 0) continue;
                        bool fa = i == cpu, fc = j == cpu;
                        if (ox < oy) { double s = (dx >= 0 ? 1 : -1) * ox * 0.5; if (!fa) a.Cx -= s * (fc ? 2 : 1); if (!fc) c.Cx += s * (fa ? 2 : 1); }
                        else         { double s = (dy >= 0 ? 1 : -1) * oy * 0.5; if (!fa) a.Cy -= s * (fc ? 2 : 1); if (!fc) c.Cy += s * (fa ? 2 : 1); }
                    }
            }
            for (int it = 0; it < 420; it++)
            {
                PushApart(clear);
                for (int i = 0; i < n; i++)                       // spring toward the parent
                {
                    if (i == cpu || parent[i] < 0) continue;
                    var a = parts[i]; var pp = parts[parent[i]];
                    double dx = pp.Cx - a.Cx, dy = pp.Cy - a.Cy, d = Math.Sqrt(dx * dx + dy * dy) + 1e-9;
                    double want = (depth[i] == 1 ? 48 : depth[i] == 2 ? 38 : 28) + (a.Vw + pp.Vw) * 0.3;   // compacted springs
                    double f = (d - want) * 0.06;
                    a.Cx += dx / d * f; a.Cy += dy / d * f;
                }
                if (cpu >= 0)                                     // hub keep-out
                {
                    var h = parts[cpu];
                    for (int i = 0; i < n; i++)
                    {
                        if (i == cpu) continue;
                        var a = parts[i];
                        double dx = a.Cx - h.Cx, dy = a.Cy - h.Cy, d = Math.Sqrt(dx * dx + dy * dy) + 1e-9;
                        double min = Math.Max(h.Vw, h.Vd) / 2 + Math.Max(a.Vw, a.Vd) / 2 + 14;   // compacted hub keep-out
                        if (d < min) { a.Cx += dx / d * (min - d); a.Cy += dy / d * (min - d); }
                    }
                }
            }
            for (int it = 0; it < 160; it++) PushApart(clear * 0.7);   // settle: no spring -> guarantees no overlaps
        }

        private sealed class Sect { public double Cx, Cy, W, D; public string Label; }
        private sealed class SecDef
        {
            public string Label; public double W, D; public AcColor Col = Copper;   // the table's net color
            public List<(Part p, double dx, double dy)> Put = new List<(Part, double, double)>();
            public List<List<double[]>> Bus = new List<List<double[]>>();
            public List<double[]> Pads = new List<double[]>();
        }
        private static readonly List<Sect> Sections = new List<Sect>();
        private static readonly List<List<double[]>> Buses = new List<List<double[]>>();
        private static readonly List<AcColor> BusCols = new List<AcColor>();      // parallel to Buses
        private static readonly List<double[]> BusPads = new List<double[]>();
        private static readonly List<AcColor> BusPadCols = new List<AcColor>();   // parallel to BusPads

        private const double SPad = 4.5;   // section inner margin

        private static void LayoutZones(List<Part> parts)   // LEGACY functional-zone layout (unused)
        {
            Sections.Clear(); Buses.Clear(); BusCols.Clear(); BusPads.Clear(); BusPadCols.Clear();
            int cpu = -1, layerTbl = -1;
            var tableRecs = new Dictionary<int, List<Part>>();
            var layerEnts = new Dictionary<int, List<Part>>();
            var otherEnts = new List<Part>();
            var tables = new List<Part>();
            foreach (var p in parts)
            {
                switch (p.Kind)
                {
                    case Kind.Die: cpu = p.Idx; break;
                    case Kind.Table:
                    case Kind.Nod:
                        tables.Add(p);
                        if (p.Title.StartsWith("LAYER")) layerTbl = p.Idx;
                        break;
                    case Kind.Record:
                    case Kind.Gpu:
                        if (!tableRecs.TryGetValue(p.TableIdx, out var lr)) { lr = new List<Part>(); tableRecs[p.TableIdx] = lr; }
                        lr.Add(p);
                        break;
                    default:
                        if (p.LayerRec >= 0) { if (!layerEnts.TryGetValue(p.LayerRec, out var le)) { le = new List<Part>(); layerEnts[p.LayerRec] = le; } le.Add(p); }
                        else otherEnts.Add(p);
                        break;
                }
            }

            // one net color per symbol table (in importance order)
            _tblCol.Clear();
            for (int i = 0; i < tables.Count; i++) _tblCol[tables[i].Idx] = NetPalette[i % NetPalette.Length];

            var defs = new List<SecDef>();
            foreach (var tbl in tables)
            {
                var recs = tableRecs.TryGetValue(tbl.Idx, out var rr) ? rr : new List<Part>();
                var d = tbl.Idx == layerTbl ? SectionForLayer(tbl, recs, layerEnts) : SectionForTable(tbl, recs);
                d.Col = _tblCol[tbl.Idx];
                defs.Add(d);
            }
            if (otherEnts.Count > 0) defs.Add(SectionForMisc(otherEnts));

            // tile the sections in rows; aim for a ~4:3 block
            const double secGap = 9.0;
            double area = 0, wmax = 0;
            foreach (var s in defs) { area += (s.W + secGap) * (s.D + secGap); wmax = Math.Max(wmax, s.W); }
            double targetW = Math.Max(wmax, Math.Sqrt(area) * 1.25);

            // shelf-pack the sections, flowing AROUND a reserved hole (the CPU lives in the
            // middle of the board like a real hub, not parked at an edge)
            (double w, double d) Tile(bool dry, double tw, double hx0, double hx1, double hy0, double hy1)
            {
                double ox = 0, oy = 0, rowD = 0, maxX = 0, totD = 0; bool hole = hx1 > hx0;
                foreach (var s in defs)
                {
                    for (int guard = 0; guard < 4; guard++)
                    {
                        bool inHole = hole && oy > hy0 && (oy - s.D) < hy1 && ox < hx1 && (ox + s.W) > hx0;
                        if (inHole) ox = hx1 + secGap;                                   // jump past the CPU
                        if (ox > 0 && ox + s.W > tw) { ox = 0; oy -= rowD + secGap; rowD = 0; continue; }
                        break;
                    }
                    if (!dry) Materialize(s, ox, oy);
                    ox += s.W + secGap; rowD = Math.Max(rowD, s.D);
                    maxX = Math.Max(maxX, ox - secGap); totD = Math.Max(totD, -oy + rowD);
                }
                return (maxX, totD);
            }
            if (cpu >= 0)
            {
                var c = parts[cpu];
                var (w1, d1) = Tile(true, targetW, 0, 0, 0, 0);                  // pass 1: block size
                double m = 13, cx = w1 * 0.44, cy = -d1 * 0.52;                  // centralized, off-center
                double hx0 = cx - c.Vw / 2 - m, hx1 = cx + c.Vw / 2 + m, hy0 = cy - c.Vd / 2 - m, hy1 = cy + c.Vd / 2 + m;
                Tile(false, targetW + (hx1 - hx0) * 0.55, hx0, hx1, hy0, hy1);   // pass 2: around the hole
                c.Cx = cx; c.Cy = cy;
            }
            else Tile(false, targetW, 0, 0, 0, 0);
        }

        private static void Materialize(SecDef s, double ox, double oy)
        {
            foreach (var (p, dx, dy) in s.Put) { p.Cx = ox + dx; p.Cy = oy + dy; }
            foreach (var wp in s.Bus)
            {
                var t = new List<double[]>(wp.Count);
                foreach (var q in wp) t.Add(new[] { ox + q[0], oy + q[1] });
                Buses.Add(t); BusCols.Add(s.Col);
            }
            foreach (var q in s.Pads) { BusPads.Add(new[] { ox + q[0], oy + q[1] }); BusPadCols.Add(s.Col); }
            Sections.Add(new Sect { Cx = ox + s.W / 2, Cy = oy - s.D / 2, W = s.W, D = s.D, Label = s.Label });
        }

        /// <summary>A table section: table chip on top, records in a neat grid BANK,
        /// one bus spine per column fanning out of the table's bottom edge.</summary>
        private static SecDef SectionForTable(Part tbl, List<Part> recs)
        {
            const double lane = 3.4, colGap = 3.0, rowGap = 4.5;
            var s = new SecDef { Label = tbl.RefDes };
            int n = recs.Count;
            if (n == 0)
            {
                s.W = tbl.Vw + 2 * SPad; s.D = tbl.Vd + 2 * SPad;
                s.Put.Add((tbl, s.W / 2, -(SPad + tbl.Vd / 2)));
                return s;
            }
            int ncols = Math.Min(4, (int)Math.Ceiling(Math.Sqrt(n)));
            int nrows = (n + ncols - 1) / ncols;
            double recW = 0, recD = 0;
            foreach (var r in recs) { recW = Math.Max(recW, r.Vw); recD = Math.Max(recD, r.Vd); }
            double cellW = lane + recW + colGap, rowP = recD + rowGap;
            double bankW = ncols * cellW - colGap;
            double innerW = Math.Max(tbl.Vw, bankW);
            double chan = 2.4 + ncols * 0.9;                        // escape-routing channel
            s.W = innerW + 2 * SPad;
            s.D = SPad + tbl.Vd + chan + nrows * rowP - rowGap + SPad;
            double tcx = s.W / 2, tby = -(SPad + tbl.Vd);
            s.Put.Add((tbl, tcx, -(SPad + tbl.Vd / 2)));
            double bankLeft = SPad + (innerW - bankW) / 2, yBank = tby - chan;
            for (int c = 0; c < ncols; c++)
            {
                double colLeft = bankLeft + c * cellW, spine = colLeft + 1.2, left = colLeft + lane;
                double padX = tcx - tbl.Vw / 2 + (c + 0.5) * tbl.Vw / ncols;
                double chanY = tby - 1.0 - c * 0.9;
                double lastCy = 0; bool any = false;
                for (int i = c; i < n; i += ncols)
                {
                    var r = recs[i]; int row = i / ncols;
                    double cy = yBank - row * rowP - r.Vd / 2;
                    s.Put.Add((r, left + r.Vw / 2, cy));
                    s.Bus.Add(new List<double[]> { new[] { spine, cy }, new[] { left, cy } });
                    s.Pads.Add(new[] { left + 0.1, cy });
                    lastCy = cy; any = true;
                }
                if (!any) continue;
                s.Bus.Add(new List<double[]> { new[] { padX, tby }, new[] { padX, chanY },
                                               new[] { spine, chanY }, new[] { spine, lastCy } });
                s.Pads.Add(new[] { padX, tby });
            }
            return s;
        }

        /// <summary>The LAYER section: each layer is a CELL = its record heading a compact GRID
        /// BANK of that layer's entities (bounded height, wraps into sub-columns); the cells are
        /// shelf-packed into rows and fed by a left backbone + per-shelf trunk bus.</summary>
        private static SecDef SectionForLayer(Part tbl, List<Part> recs, Dictionary<int, List<Part>> layerEnts)
        {
            const double lane = 3.6, colGap = 3.6, shelfGap = 6.0;
            const double eLane = 2.4, eSpine = 1.1, eColGap = 2.2, eRowGap = 3.4, eChan = 2.8;
            var s = new SecDef { Label = tbl.RefDes };
            int n = recs.Count;
            if (n == 0)
            {
                s.W = tbl.Vw + 2 * SPad; s.D = tbl.Vd + 2 * SPad;
                s.Put.Add((tbl, s.W / 2, -(SPad + tbl.Vd / 2)));
                return s;
            }
            // measure each layer CELL: record head + entity grid (ec sub-columns)
            var colW = new double[n]; var colH = new double[n]; var colEnts = new List<Part>[n];
            var ec = new int[n]; var eCellW = new double[n]; var eRowP = new double[n]; var eDmax = new double[n];
            double totW = 0, maxH = 0;
            for (int i = 0; i < n; i++)
            {
                var ents = layerEnts.TryGetValue(recs[i].Idx, out var ee) ? ee : new List<Part>();
                colEnts[i] = ents;
                double recW = recs[i].Vw, recD = recs[i].Vd;
                if (ents.Count == 0) { ec[i] = 0; colW[i] = lane + recW + colGap; colH[i] = recD; }
                else
                {
                    int m = ents.Count;
                    ec[i] = Math.Min(4, (int)Math.Ceiling(Math.Sqrt(m)));
                    double ew = 0, ed = 0; foreach (var e in ents) { ew = Math.Max(ew, e.Vw); ed = Math.Max(ed, e.Vd); }
                    eDmax[i] = ed; eCellW[i] = eLane + ew + eColGap; eRowP[i] = ed + eRowGap;
                    int erows = (m + ec[i] - 1) / ec[i];
                    double gridW = ec[i] * eCellW[i];
                    colW[i] = Math.Max(lane + recW + colGap, gridW + colGap);
                    colH[i] = recD + eChan + erows * eRowP[i];
                }
                totW += colW[i]; maxH = Math.Max(maxH, colH[i]);
            }
            // shelf-pack the cells toward a square-ish bank
            double targetW = Math.Max(tbl.Vw + 8, Math.Sqrt(totW * maxH) * 1.1);
            var shelves = new List<List<int>>(); var curSh = new List<int>(); double cw = 0;
            for (int i = 0; i < n; i++)
            { if (curSh.Count > 0 && cw + colW[i] > targetW) { shelves.Add(curSh); curSh = new List<int>(); cw = 0; } curSh.Add(i); cw += colW[i]; }
            if (curSh.Count > 0) shelves.Add(curSh);
            double bankW = 0, bankH = 0; var shelfH = new double[shelves.Count];
            for (int sI = 0; sI < shelves.Count; sI++)
            { double w = 0, h = 0; foreach (int i in shelves[sI]) { w += colW[i]; h = Math.Max(h, colH[i]); } bankW = Math.Max(bankW, w); shelfH[sI] = h; bankH += h + (sI > 0 ? shelfGap : 0); }

            double leftPad = SPad + 2.0, chan = 3.2;
            double innerW = Math.Max(tbl.Vw, bankW);
            s.W = leftPad + innerW + SPad;
            s.D = SPad + tbl.Vd + chan + bankH + SPad;
            double tcx = s.W / 2, tby = -(SPad + tbl.Vd);
            s.Put.Add((tbl, tcx, -(SPad + tbl.Vd / 2)));

            double backboneX = SPad * 0.6, bankTop = tby - chan;
            s.Bus.Add(new List<double[]> { new[] { tcx, tby }, new[] { tcx, tby - 1.4 }, new[] { backboneX, tby - 1.4 } });
            s.Pads.Add(new[] { tcx, tby });

            double yTop = bankTop, lastTrunkY = tby - 1.4;
            for (int sI = 0; sI < shelves.Count; sI++)
            {
                double trunkY = yTop + 1.4;
                double shelfRight = leftPad; foreach (int i in shelves[sI]) shelfRight += colW[i];
                s.Bus.Add(new List<double[]> { new[] { backboneX, trunkY }, new[] { shelfRight, trunkY } });
                lastTrunkY = trunkY;
                double xx = leftPad;
                foreach (int i in shelves[sI])
                {
                    var r = recs[i]; var ents = colEnts[i];
                    double colLeft = xx, recSpine = colLeft + 1.1, recLeft = colLeft + lane;
                    double recW = r.Vw, recD = r.Vd, rcy = yTop - recD / 2, rbot = yTop - recD;
                    s.Put.Add((r, recLeft + recW / 2, rcy));                       // layer record heads the cell
                    s.Bus.Add(new List<double[]> { new[] { recSpine, trunkY }, new[] { recSpine, rcy }, new[] { recLeft, rcy } });
                    s.Pads.Add(new[] { recLeft + 0.1, rcy });
                    if (ents.Count > 0)
                    {
                        int m = ents.Count, eec = ec[i]; double ecw = eCellW[i], erp = eRowP[i], ed = eDmax[i];
                        double gridTop = yTop - recD - eChan, feedY = rbot - 1.4, recFeedX = recLeft + 1.5;
                        double firstSp = colLeft + eSpine, lastSp = colLeft + eSpine + (eec - 1) * ecw;
                        s.Bus.Add(new List<double[]> { new[] { recFeedX, rbot }, new[] { recFeedX, feedY } });
                        s.Bus.Add(new List<double[]> { new[] { Math.Min(recFeedX, firstSp), feedY }, new[] { Math.Max(recFeedX, lastSp), feedY } });
                        for (int sc = 0; sc < eec; sc++)
                        {
                            double scSpine = colLeft + eSpine + sc * ecw, entLeft = colLeft + sc * ecw + eLane;
                            double lastEy = feedY; bool any = false;
                            for (int k = sc; k < m; k += eec)
                            {
                                var e = ents[k]; int rr = k / eec;
                                double ecy = gridTop - rr * erp - ed / 2;
                                s.Put.Add((e, entLeft + e.Vw / 2, ecy));
                                s.Bus.Add(new List<double[]> { new[] { scSpine, ecy }, new[] { entLeft, ecy } });
                                s.Pads.Add(new[] { entLeft + 0.1, ecy });
                                lastEy = ecy; any = true;
                            }
                            if (any) s.Bus.Add(new List<double[]> { new[] { scSpine, feedY }, new[] { scSpine, lastEy } });
                        }
                    }
                    xx += colW[i];
                }
                yTop -= shelfH[sI] + shelfGap;
            }
            s.Bus.Add(new List<double[]> { new[] { backboneX, tby - 1.4 }, new[] { backboneX, lastTrunkY } });
            return s;
        }

        /// <summary>Entities on no known layer: a plain silkscreen zone, gridded, unbused.</summary>
        private static SecDef SectionForMisc(List<Part> ents)
        {
            const double colGap = 3.4, rowGap = 4.6;
            var s = new SecDef { Label = "MISC" };
            int n = ents.Count, ncols = Math.Min(5, (int)Math.Ceiling(Math.Sqrt(n)));
            double cw = 0, cd = 0;
            foreach (var e in ents) { cw = Math.Max(cw, e.Vw); cd = Math.Max(cd, e.Vd); }
            double cellW = cw + colGap, rowP = cd + rowGap;
            int nrows = (n + ncols - 1) / ncols;
            s.W = ncols * cellW - colGap + 2 * SPad;
            s.D = nrows * rowP - rowGap + 2 * SPad;
            for (int i = 0; i < n; i++)
            {
                int c = i % ncols, rw = i / ncols;
                s.Put.Add((ents[i], SPad + c * cellW + ents[i].Vw / 2, -(SPad + rw * rowP + ents[i].Vd / 2)));
            }
            return s;
        }

        // ======================================================================
        //  BUILD from the laid-out graph
        // ======================================================================
        private static void BuildFromGraph(Pcb b, List<Part> parts, List<Edge> edges)
        {
            double minX = 1e9, maxX = -1e9, minY = 1e9, maxY = -1e9;
            foreach (var p in parts)
            {
                minX = Math.Min(minX, p.Cx - p.Vw / 2); maxX = Math.Max(maxX, p.Cx + p.Vw / 2);
                minY = Math.Min(minY, p.Cy - p.Vd / 2 - 4); maxY = Math.Max(maxY, p.Cy + p.Vd / 2 + 4);
            }
            double pad = 17;   // margin ring (compacted)
            double boardW = (maxX - minX) + 2 * pad;

            // PART DATA now prints ON each part's own body (PlacePart -> Pod), including
            // entities, so the board no longer reserves a bottom strip for a legend table.
            double extra = 0;

            double shiftX = pad - minX, shiftY = pad + extra - minY;
            foreach (var p in parts) { p.Cx += shiftX; p.Cy += shiftY; }
            double boardH = (maxY - minY) + 2 * pad + extra;

            // ---- OUTLINE driven by the SOURCE DRAWING: its entities' convex hull, fitted to the
            //      board and grown until the content core sits inside ----
            double coreX0 = pad - 2, coreX1 = pad + (maxX - minX) + 2;
            double coreY0 = pad - 2, coreY1 = pad + extra + (maxY - minY) + 2;
            var poly = BoardOutline(boardW, boardH, coreX0, coreY0, coreX1, coreY1);
            BuildBoardPoly(b, poly, boardW, boardH, coreX0, coreY0, coreX1, coreY1);

            foreach (var p in parts) PlacePart(b, p);

            // NETS are collected first and drawn after crossing resolution (vias + bottom hops)
            var nets = new List<Net>();

            if (Paths.DsnEnabled) try { WriteDsn(parts, edges, poly, Paths.Out("pcd.dsn")); } catch { }   // Freerouting study dump (opt-in via PCD_DSN)

            // ---- grid MAZE ROUTER: every trace stays on TOP and routes AROUND chip footprints ----
            const double cs = 0.6;   // routing grid: fine enough for the compacted layout's 3 giant reference nets (measured: 1.2 starved them, 0.8 left 58 dropped)
            int gw = (int)(boardW / cs) + 3, gh = (int)(boardH / cs) + 3;
            var blk = new bool[gw, gh];
            foreach (var p in parts)   // EVERY part is hardware: nothing routes under it on the TOP layer
            {
                double m = KeepOut(p);   // the COURTYARD: body + silkscreen ring (encloses corner pads and leads)
                int x0 = Cl((int)((p.Cx - p.Vw / 2 - m) / cs), 0, gw - 1), x1 = Cl((int)((p.Cx + p.Vw / 2 + m) / cs), 0, gw - 1);
                int y0 = Cl((int)((p.Cy - p.Vd / 2 - m) / cs), 0, gh - 1), y1 = Cl((int)((p.Cy + p.Vd / 2 + m) / cs), 0, gh - 1);
                for (int gx = x0; gx <= x1; gx++) for (int gy = y0; gy <= y1; gy++) blk[gx, gy] = true;
            }
            for (int gx = 0; gx < gw; gx++) for (int gy = 0; gy < gh; gy++)          // nothing routes off the board
                if (!PointInPoly(poly, (gx + 0.5) * cs, (gy + 0.5) * cs)) blk[gx, gy] = true;
            var used = new int[gw, gh];   // occupancy: traces avoid cells already used -> parallel lanes
            // Refdes silkscreen keep-out, kept in its OWN grid rather than folded into `used`.
            // `used` encodes two things already -- the hard-block test (>= CoreMark) and the +1
            // per-neighbour buffers -- so any label cost large enough to actually deflect a trace
            // would wrap past CoreMark once a buffer landed on the same cell and wall it off by
            // accident. A separate grid has no ceiling and no interaction with either.
            // It applies to LAYER 0 ONLY: silkscreen is printed on the top face, so inner and
            // bottom copper run beneath the lettering without ever obscuring it. That is also why
            // this can be expensive without dropping nets -- the router dives instead of failing,
            // which is what hard-blocking labels (measured at 82 lost reference pins) could not do.
            var silk = new int[gw, gh];
            foreach (var p in parts)
            {
                var lb = LabelBox(p); if (lb == null) continue;
                int lx0 = Cl((int)((lb[0] - cs) / cs), 0, gw - 1), lx1 = Cl((int)((lb[2] + cs) / cs), 0, gw - 1);
                int ly0 = Cl((int)((lb[1] - cs) / cs), 0, gh - 1), ly1 = Cl((int)((lb[3] + cs) / cs), 0, gh - 1);
                for (int gx = lx0; gx <= lx1; gx++) for (int gy = ly0; gy <= ly1; gy++) silk[gx, gy] = SilkCost;
            }

            // ---- every relationship is a routed net. Nets INTO the same chip merge into one tree
            //      (first source routes to the chip, later sources join the nearest point of that
            //      net). Nets to the CPU are never merged: one spoke per table, radiating from the
            //      hub as a parallel BUNDLE whose width is that table's record count. ----
            (int x, int y) Cell(Point3d p) => (Cl((int)(p.X / cs), 0, gw - 1), Cl((int)(p.Y / cs), 0, gh - 1));
            void Reserve(List<(int, int)> path, int wide)
            {
                foreach (var (ux, uy) in path)
                    for (int ox = -wide; ox <= wide; ox++) for (int oy = -wide; oy <= wide; oy++)
                    {
                        int x = ux + ox, y = uy + oy; if (x < 0 || y < 0 || x >= gw || y >= gh) continue;
                        used[x, y] += (ox == 0 && oy == 0) ? 3 : 1;
                    }
            }
            List<double[]> Corners(Point3d from, List<(int, int)> path, double[] end)
            {
                var pts = new List<double[]> { new[] { from.X, from.Y } };
                for (int i = 1; i < path.Count - 1; i++)
                {
                    var (px, py) = path[i - 1]; var (mx, my) = path[i]; var (nx, ny) = path[i + 1];
                    if ((mx - px) != (nx - mx) || (my - py) != (ny - my))       // keep only corners
                        pts.Add(new[] { mx * cs + cs / 2, my * cs + cs / 2 });
                }
                pts.Add(end);
                return pts;
            }
            // ---- OCTILINEARIZE: collapse a wandering grid path into the fewest STRAIGHT + single-45deg
            //      doglegs that stay clear of HARDWARE (crossing other traces is fine -> vias handle it) ----
            bool SegClear(double ax, double ay, double bx, double by)
            {
                double len = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
                int steps = Math.Max(1, (int)(len / (cs * 0.5)));
                for (int s = 1; s < steps; s++)   // endpoints are pads -> skip
                {
                    double t = (double)s / steps, x = ax + (bx - ax) * t, y = ay + (by - ay) * t;
                    if (blk[Cl((int)(x / cs), 0, gw - 1), Cl((int)(y / cs), 0, gh - 1)]) return false;
                }
                return true;
            }
            double[] Dogleg(double[] A, double[] B)   // corner of a clear straight+45 route A->B, or null
            {
                double dx = B[0] - A[0], dy = B[1] - A[1];
                double sx = Math.Sign(dx), sy = Math.Sign(dy), mm = Math.Min(Math.Abs(dx), Math.Abs(dy));
                double[] cd = { A[0] + sx * mm, A[1] + sy * mm };   // 45deg first, then straight
                double[] cst = { B[0] - sx * mm, B[1] - sy * mm };  // straight first, then 45deg
                if (SegClear(A[0], A[1], cd[0], cd[1]) && SegClear(cd[0], cd[1], B[0], B[1])) return cd;
                if (SegClear(A[0], A[1], cst[0], cst[1]) && SegClear(cst[0], cst[1], B[0], B[1])) return cst;
                return null;
            }
            List<double[]> Clean(List<double[]> pts)
            {
                if (pts.Count <= 2) { var d = Dogleg(pts[0], pts[pts.Count - 1]); if (pts.Count == 2 && d != null) return new List<double[]> { pts[0], d, pts[1] }; return pts; }
                var outp = new List<double[]> { pts[0] }; int anchor = 0;
                while (anchor < pts.Count - 1)
                {
                    int best = -1; double[] corner = null;
                    for (int j = pts.Count - 1; j > anchor; j--) { var c = Dogleg(pts[anchor], pts[j]); if (c != null) { best = j; corner = c; break; } }
                    if (best < 0) { anchor++; outp.Add(pts[anchor]); continue; }
                    bool degenerate = (Math.Abs(corner[0] - pts[anchor][0]) < 0.05 && Math.Abs(corner[1] - pts[anchor][1]) < 0.05)
                                   || (Math.Abs(corner[0] - pts[best][0]) < 0.05 && Math.Abs(corner[1] - pts[best][1]) < 0.05);
                    if (!degenerate) outp.Add(corner);
                    outp.Add(pts[best]); anchor = best;
                }
                return outp;
            }

            // VIA LEGALITY: a via may never sit under hardware (chip footprint) or on/near a solder
            // joint (pads ring each chip), and must stay off the board edge.
            var viaOk = new bool[gw, gh];
            for (int i = 0; i < gw; i++) for (int j = 0; j < gh; j++)
            {
                double x = (i + 0.5) * cs, y = (j + 0.5) * cs;
                viaOk[i, j] = PointInPoly(poly, x, y) && DistToPolyEdge(poly, x, y) >= 3.0;
            }
            foreach (var p in parts)
            {
                double hw = p.Vw / 2 + 2.6, hd = p.Vd / 2 + 2.6;   // footprint + pad-ring keep-out
                int x0 = Cl((int)((p.Cx - hw) / cs), 0, gw - 1), x1 = Cl((int)((p.Cx + hw) / cs), 0, gw - 1);
                int y0 = Cl((int)((p.Cy - hd) / cs), 0, gh - 1), y1 = Cl((int)((p.Cy + hd) / cs), 0, gh - 1);
                for (int i = x0; i <= x1; i++) for (int j = y0; j <= y1; j++) viaOk[i, j] = false;
            }
            var blkBot = new bool[gw, gh];   // bottom layer: only the board edge blocks (runs under chips)
            for (int gx = 0; gx < gw; gx++) for (int gy = 0; gy < gh; gy++)
                if (!PointInPoly(poly, (gx + 0.5) * cs, (gy + 0.5) * cs)) blkBot[gx, gy] = true;
            var usedBot = new int[gw, gh]; var usedMid = new int[gw, gh];
            foreach (var (hx, hy, hr) in _holes)   // no copper (trace or via) over a mounting hole
            {
                double rr = hr + 1.5;
                int x0 = Cl((int)((hx - rr) / cs), 0, gw - 1), x1 = Cl((int)((hx + rr) / cs), 0, gw - 1);
                int y0 = Cl((int)((hy - rr) / cs), 0, gh - 1), y1 = Cl((int)((hy + rr) / cs), 0, gh - 1);
                for (int i = x0; i <= x1; i++) for (int j = y0; j <= y1; j++)
                { double dx = (i + 0.5) * cs - hx, dy = (j + 0.5) * cs - hy; if (dx * dx + dy * dy <= rr * rr) { blk[i, j] = true; blkBot[i, j] = true; viaOk[i, j] = false; } }
            }
            bool ViaLegal(double x, double y)
            { int i = Cl((int)(x / cs), 0, gw - 1), j = Cl((int)(y / cs), 0, gh - 1); return viaOk[i, j]; }
            double[] SnapVia(double x, double y, int rmax = 5)   // nearest legal via cell within rmax rings, or null
            {
                int ci = Cl((int)(x / cs), 0, gw - 1), cj = Cl((int)(y / cs), 0, gh - 1);
                if (viaOk[ci, cj]) return new[] { (ci + 0.5) * cs, (cj + 0.5) * cs };
                for (int r = 1; r <= rmax; r++)
                    for (int di = -r; di <= r; di++) for (int dj = -r; dj <= r; dj++)
                    {
                        if (Math.Abs(di) != r && Math.Abs(dj) != r) continue;   // ring only
                        int ni = ci + di, nj = cj + dj;
                        if (ni < 0 || nj < 0 || ni >= gw || nj >= gh) continue;
                        if (viaOk[ni, nj]) return new[] { (ni + 0.5) * cs, (nj + 0.5) * cs };
                    }
                return null;
            }
            // bottom-layer jumper (fallback) with LEGAL off-pad vias; every segment octilinear.
            // Three measured defects lived here and produced every residual same-layer crossing:
            //   (a) the inner run is DRAWN at Zof(2) but was straightened against usedMid (layer 1's
            //       occupancy) -- blkL shares blkBot across layers 1/2, usedL does NOT share a grid,
            //       so the check consulted the wrong plane and jumpers cut through bottom A* copper;
            //   (b) a jumper never reserved its own cells (it emits geometry, not an A* cell path),
            //       so jumper N+1 was blind to jumper N -- jumper x jumper crossings;
            //   (c) CleanG's 2-point branch returns the RAW straight line when no dogleg is clear,
            //       so a jumper with no legal route was drawn straight through whatever was there.
            // Fixed by checking the drawn plane, letting the run pick the inner plane that is
            // actually free, reserving what it lays down, and validating the result before use.
            int jumpMid = 0, jumpDirty = 0;
            void BottomJumper(double[] paC, double[] pbC, AcColor col, double w,
                              Func<int, int, bool> bTop, Func<int, int, bool> bMid, Func<int, int, bool> bBot)
            {
                var va = SnapVia(paC[0], paC[1], 12); var vb = SnapVia(pbC[0], pbC[1], 12);
                if (va == null || vb == null)
                {   // nowhere legal to via near a pad -> the WHOLE jumper runs on the deepest inner plane (allowed
                    // under hardware) with its vias at the pad cells. NEVER an unrouted top-layer L across the board.
                    DropVia(b, paC[0], paC[1], col, 0, 2); DropVia(b, pbC[0], pbC[1], col, 0, 2);
                    var lp = new List<double[]> { paC, new[] { pbC[0], paC[1] }, pbC };
                    nets.Add(new Net { Wp = lp, Z = Zof(2), Col = col, Top = false, W = w });
                    ReserveWorld(lp, usedBot, cs, gw, gh);
                    return;
                }
                // pick the inner plane that is actually free: bottom first, then middle. Layers 1 and 2
                // are separate copper, so this roughly doubles the jumper capacity instead of piling
                // every fallback onto layer 2.
                int jl = 2; var run = CleanG(new List<double[]> { va, vb }, bBot, cs, gw, gh);
                if (!PathClearG(run, bBot, cs, gw, gh))
                {
                    var alt = CleanG(new List<double[]> { va, vb }, bMid, cs, gw, gh);
                    if (PathClearG(alt, bMid, cs, gw, gh)) { jl = 1; run = alt; jumpMid++; }
                    else jumpDirty++;   // no legal inner route at all -> last resort, counted not hidden
                }
                DropVia(b, va[0], va[1], col, 0, jl); DropVia(b, vb[0], vb[1], col, 0, jl);
                // stubs and the inner run are straightened against hardware AND other nets' cells (no crossings)
                var s1 = CleanG(new List<double[]> { paC, va }, bTop, cs, gw, gh);
                var s2 = CleanG(new List<double[]> { vb, pbC }, bTop, cs, gw, gh);
                nets.Add(new Net { Wp = s1,  Z = ZTop,     Col = col, Top = true,  W = w });
                nets.Add(new Net { Wp = run, Z = Zof(jl),  Col = col, Top = false, W = w });
                nets.Add(new Net { Wp = s2,  Z = ZTop,     Col = col, Top = true,  W = w });
                ReserveWorld(s1,  used,       cs, gw, gh);
                ReserveWorld(run, jl == 1 ? usedMid : usedBot, cs, gw, gh);   // usedL is declared below this local fn
                ReserveWorld(s2,  used,       cs, gw, gh);
            }

            // ---- 2-LAYER OCTILINEAR ROUTER (learned from Freerouting on this exact board: ~60% of
            //      routing runs on the BOTTOM layer, strictly 0/45/90, with cheap frequent vias).
            //      Each relationship is its own net: escape the pad on TOP, dive to the open BOTTOM
            //      for the long haul, pop back up at the destination pad. ----
            // THREE routing layers: 0 = top (blocks hardware), 1 & 2 = inner/bottom (open, run under chips).
            // More planes -> less congestion -> cleaner routing. Vias are through-hole (reach any layer).
            bool[][,] blkL = { blk, blkBot, blkBot };
            int[][,] usedL = { used, usedMid, usedBot };
            const int nL = 3;
            // ---- MULTI-PIN NETS routed as TREES (Steiner-style, the way Freerouting/LibrePCB treat a
            //      net): one relationship CLASS into one destination is ONE net with many pins --
            //      (Model Space, Own) = Model Space + every entity it owns; (layer X, Layer) = the layer
            //      record + every entity on it. The pin nearest the destination lays the trunk; every
            //      further pin routes to the NEAREST copper already laid for its net and joins it at a
            //      junction. Hubs therefore get ONE approach per net instead of one per relationship
            //      (measured: per-relationship wires saturated the hub pads -- 118 of 450 routed), and
            //      branches merge into trunks, which is the bus look of a real board. ----
            double ELen(int ei) { Part A = parts[edges[ei].A], B = parts[edges[ei].B]; double dx = A.Cx - B.Cx, dy = A.Cy - B.Cy; return dx * dx + dy * dy; }
            var netKey = new Dictionary<(int dest, ECls cls), List<int>>();   // net -> its edge (pin) indices
            for (int i = 0; i < edges.Count; i++)
            { var k = (edges[i].B, edges[i].Cls); if (!netKey.TryGetValue(k, out var l)) netKey[k] = l = new List<int>(); l.Add(i); }
            var netOrder = new List<(int dest, ECls cls)>(netKey.Keys);
            netOrder.Sort((a, c) => {   // ownership nets first (the copper), biggest trees first (they define the trunks);
                int r = (a.cls != ECls.Own).CompareTo(c.cls != ECls.Own); if (r != 0) return r;
                return a.cls == ECls.Own ? netKey[c].Count.CompareTo(netKey[a].Count)
                                         : netKey[a].Count.CompareTo(netKey[c].Count); });   // references SMALLEST first: a
                                         // one-target net is fragile and must claim corridors before big trees that can join anywhere
            // LAYER ASSIGNMENT for the giant reference nets (the ones touching nearly every entity): largest
            // prefers inner plane 1, second plane 2, third plane 1 -- spreading them across planes instead of
            // all three competing for the same corridors (measured: they held every remaining dropped pin).
            var giantPref = new Dictionary<(int dest, ECls cls), int>();
            {
                var refs = new List<(int dest, ECls cls)>(); foreach (var k in netOrder) if (k.cls != ECls.Own) refs.Add(k);
                refs.Sort((a, c) => netKey[c].Count.CompareTo(netKey[a].Count));
                int[] planes = { 1, 2, 1 }; for (int i = 0; i < refs.Count && i < 3; i++) giantPref[refs[i]] = planes[i];
            }
            // PIN SLOTS: a part with k nets gets k distinct pins spread along its perimeter (distinct grid cells)
            var pinTotal = new int[parts.Count]; var pinNext = new int[parts.Count];
            foreach (var kv in netKey) { pinTotal[kv.Key.dest]++; foreach (int ei in kv.Value) pinTotal[edges[ei].A]++; }
            const double pinPitch = 0.9;   // >= cs*sqrt(2): neighbouring pins land in different cells
            Point3d PinPos(Part p, double tx, double ty)
            {
                var bp = PadPos(p, tx, ty);
                int k = pinTotal[p.Idx], s = pinNext[p.Idx]++;
                double ko = KeepOut(p) + 0.35;
                double hw = p.Vw / 2 + ko, hd = p.Vd / 2 + ko;   // same courtyard-edge offset as PadPos
                double W = 2 * hw, H = 2 * hd, per = 2 * (W + H);
                // Pins WALK THE WHOLE PERIMETER (arc length, CCW from the SW corner) instead of clamping to
                // one side: on the small entity parts one side cannot hold k pins at pinPitch, and clamped
                // pins piled onto the same cells (a later net then started inside another net's core).
                double u;
                if (Math.Abs(bp.Y - (p.Cy - hd)) < 1e-6)      u = bp.X - (p.Cx - hw);                 // bottom edge
                else if (Math.Abs(bp.X - (p.Cx + hw)) < 1e-6) u = W + (bp.Y - (p.Cy - hd));           // right edge
                else if (Math.Abs(bp.Y - (p.Cy + hd)) < 1e-6) u = W + H + ((p.Cx + hw) - bp.X);       // top edge
                else                                          u = 2 * W + H + ((p.Cy + hd) - bp.Y);   // left edge
                u += (s - (k - 1) / 2.0) * pinPitch;
                u = ((u % per) + per) % per;
                double x, y;
                if (u < W)              { x = p.Cx - hw + u;             y = p.Cy - hd; }
                else if (u < W + H)     { x = p.Cx + hw;                 y = p.Cy - hd + (u - W); }
                else if (u < 2 * W + H) { x = p.Cx + hw - (u - W - H);   y = p.Cy + hd; }
                else                    { x = p.Cx - hw;                 y = p.Cy + hd - (u - 2 * W - H); }
                return new Point3d(x, y, 0);
            }
            List<(int, int, int)> Route(int sx, int sy, int tx, int ty, HashSet<(int, int, int)> goalSet = null, int pref = -1)
            {
                bool bs = blk[sx, sy], bt = blk[tx, ty]; blk[sx, sy] = false; blk[tx, ty] = false;
                var p = AStar2(sx, sy, tx, ty, blkL, viaOk, usedL, nL, gw, gh, 55.0, false, goalSet, pref, silk);   // vias expensive -> stay on top, dive only when forced
                blk[sx, sy] = bs; blk[tx, ty] = bt; return p;
            }
            // SECOND CHANCE for a pin the top-first router could not place: block the whole top plane so
            // the pin vias at its pad and runs the open inner planes (cheap vias). Replaces most raw jumpers.
            bool[,] blkTopAll = null;
            List<(int, int, int)> RouteDive(int sx, int sy, int tx, int ty, HashSet<(int, int, int)> goalSet = null, int pref = -1)
            {
                if (blkTopAll == null) { blkTopAll = new bool[gw, gh]; for (int i = 0; i < gw; i++) for (int j = 0; j < gh; j++) blkTopAll[i, j] = true; }
                bool[][,] bl = { blkTopAll, blkBot, blkBot };
                return AStar2(sx, sy, tx, ty, bl, viaOk, usedL, nL, gw, gh, 6.0, true, goalSet, pref, silk);
            }
            // DESTINATION PIN ROWS. Every OWNERSHIP relationship lands on its OWN pad on the destination chip (a
            // real chip has one pin per connection), spread along the chip's perimeter in the order the
            // connections arrive from, at PinRowPitch. Reference nets keep one shared pad (tree) -- that sharing
            // is what keeps hundreds of hairlines routable. (Defect this replaces: every branch of a table's
            // tree converged on one pad.)
            double ArcOf(Part p, double tx, double ty)   // arc-length (CCW from the SW corner) of the facing point
            {
                var bp = PadPos(p, tx, ty);
                double ko = KeepOut(p) + 0.35, hw = p.Vw / 2 + ko, hd = p.Vd / 2 + ko, W = 2 * hw, H = 2 * hd;
                if (Math.Abs(bp.Y - (p.Cy - hd)) < 1e-6) return bp.X - (p.Cx - hw);
                if (Math.Abs(bp.X - (p.Cx + hw)) < 1e-6) return W + (bp.Y - (p.Cy - hd));
                if (Math.Abs(bp.Y - (p.Cy + hd)) < 1e-6) return W + H + ((p.Cx + hw) - bp.X);
                return 2 * W + H + ((p.Cy + hd) - bp.Y);
            }
            double PerOf(Part p) { double ko = KeepOut(p) + 0.35; return 2 * ((p.Vw + 2 * ko) + (p.Vd + 2 * ko)); }
            Point3d AtArc(Part p, double u)
            {
                double ko = KeepOut(p) + 0.35, hw = p.Vw / 2 + ko, hd = p.Vd / 2 + ko, W = 2 * hw, H = 2 * hd, per = 2 * (W + H);
                u = ((u % per) + per) % per;
                if (u < W)         return new Point3d(p.Cx - hw + u,           p.Cy - hd, 0);
                if (u < W + H)     return new Point3d(p.Cx + hw,               p.Cy - hd + (u - W), 0);
                if (u < 2 * W + H) return new Point3d(p.Cx + hw - (u - W - H), p.Cy + hd, 0);
                return new Point3d(p.Cx - hw, p.Cy + hd - (u - 2 * W - H), 0);
            }
            var pinPt = new Dictionary<int, Point3d>();                       // edge index -> source-side pin
            var destPt = new Dictionary<(int dest, ECls cls), Point3d>();      // reference net -> its shared destination pad
            var destPin = new Dictionary<int, Point3d>();                      // ownership edge -> its OWN destination pad
            var rows = new Dictionary<int, List<(double u, int ei, (int dest, ECls cls) key)>>();   // chip -> requested pads
            foreach (var key in netOrder)
            {
                var eis = netKey[key]; Part D = parts[key.dest];
                eis.Sort((a, c) => ELen(a).CompareTo(ELen(c)));
                foreach (int ei in eis) pinPt[ei] = PinPos(parts[edges[ei].A], D.Cx, D.Cy);
                if (!rows.TryGetValue(key.dest, out var lst)) rows[key.dest] = lst = new List<(double, int, (int, ECls))>();
                if (key.cls == ECls.Own) foreach (int ei in eis) { Part A = parts[edges[ei].A]; lst.Add((ArcOf(D, A.Cx, A.Cy), ei, key)); }
                else { Part A0 = parts[edges[eis[0]].A]; lst.Add((ArcOf(D, A0.Cx, A0.Cy), -1, key)); }
            }
            foreach (var kv in rows)   // spread each chip's pads along its perimeter: arrival order kept, pitch enforced
            {
                Part D = parts[kv.Key]; var lst = kv.Value; double per = PerOf(D);
                lst.Sort((a, c) => a.u.CompareTo(c.u));
                double pitch = Math.Min(PinRowPitch, per / Math.Max(1, lst.Count));   // never more pads than the ring holds
                var us = new double[lst.Count];
                for (int i = 0; i < lst.Count; i++) us[i] = i == 0 ? lst[i].u : Math.Max(lst[i].u, us[i - 1] + pitch);
                if (lst.Count > 1 && us[lst.Count - 1] - us[0] > per - pitch)          // wrapped onto the first pad: even spread
                    for (int i = 0; i < lst.Count; i++) us[i] = us[0] + i * (per / lst.Count);
                for (int i = 0; i < lst.Count; i++)
                {
                    var pt = AtArc(D, us[i]);
                    if (lst[i].ei >= 0) destPin[lst[i].ei] = pt; else destPt[lst[i].key] = pt;
                }
            }
            // Destination pads are walled off so no trace may run through another connection's landing cell
            // (walling every SOURCE pin as well was measured to remove more corridor capacity than it saved).
            foreach (var pt in destPin.Values) { var (px, py) = Cell(pt); blk[px, py] = true; }
            foreach (var pt in destPt.Values)  { var (px, py) = Cell(pt); blk[px, py] = true; }
            var jobs = new List<(int ei, int sx, int sy, int tx, int ty, double[] paC, double[] pbC, AcColor col, double w)>();
            var jpath = new List<List<(int, int, int)>>();
            var fallbacks = new List<(double[] paC, double[] pbC, AcColor col)>(); int refDrop = 0, pinsTotal = 0;
            var dropBy = new Dictionary<(int dest, ECls cls), int>();   // diagnostics: which nets lose reference pins
            int dropStartBlocked = 0, dropStartRing = 0;                 // ...and whether the failed pin's start cell / its 8-ring was already core
            foreach (var key in netOrder)
            {
                var eis = netKey[key]; Part D = parts[key.dest];
                bool isRef = key.cls != ECls.Own;
                int pref = giantPref.TryGetValue(key, out int gp) ? gp : -1;   // preferred plane for the giant reference nets
                AcColor col = isRef ? Dim(NetCol(parts, edges[eis[0]]), 0.42) : NetCol(parts, edges[eis[0]]);   // references: dim
                double w = isRef ? 0.15 : 0.4, land = isRef ? 0.8 : 1.4;
                eis.Sort((a, c) => ELen(a).CompareTo(ELen(c)));   // nearest pin first: it lays the trunk
                var netCells = new HashSet<(int, int, int)>();     // copper laid so far for this net (any layer)
                double[] dCref = null; int rx0 = -1, ry0 = -1; bool destLand = false;   // the reference net's shared pad
                foreach (int ei in eis)
                {
                    pinsTotal++;
                    var e = edges[ei]; Part A = parts[e.A];
                    // copper INTO entity hardware is a thinner class (small parts, small traces, small lands)
                    bool ent = A.Band == "entity";
                    double wp = ent ? (isRef ? 0.12 : 0.24) : w, lp = ent ? (isRef ? 0.6 : 1.0) : land;
                    Point3d pa = pinPt[ei];                              // pre-assigned (and pre-blocked) pin
                    var (sx, sy) = Cell(pa);
                    double[] paC = { sx * cs + cs / 2, sy * cs + cs / 2 };
                    b.Box(lp, lp, 0.14, new Point3d(paC[0], paC[1], 0.07), LNet, col);       // every pin ends on a land
                    List<(int, int, int)> path = null; int dx0, dy0; double[] dC;
                    if (!isRef)   // OWNERSHIP: this connection's OWN pad on the destination chip (pin row) -- no tree join
                    {
                        (dx0, dy0) = Cell(destPin[ei]); dC = new[] { dx0 * cs + cs / 2, dy0 * cs + cs / 2 };
                        path = Route(sx, sy, dx0, dy0, null, pref);
                        if (path == null || path.Count < 2) path = RouteDive(sx, sy, dx0, dy0, null, pref);
                        if (path != null && path.Count >= 2)
                        { double dl = Math.Min(land, PinRowPitch * 0.85); b.Box(dl, dl, 0.14, new Point3d(dC[0], dC[1], 0.07), LNet, col); }
                    }
                    else if (dCref == null)   // REFERENCE: the net's shared pad; if the trunk cannot reach it, try further slots
                    {
                        for (int attempt = 0; attempt < 4 && (path == null || path.Count < 2); attempt++)
                        {
                            Point3d pb = attempt == 0 ? destPt[key] : PinPos(D, A.Cx, A.Cy); (rx0, ry0) = Cell(pb);
                            netCells.Clear(); netCells.Add((rx0, ry0, 0));
                            path = Route(sx, sy, rx0, ry0, netCells, pref);
                            if (path == null || path.Count < 2) path = RouteDive(sx, sy, rx0, ry0, netCells, pref);
                        }
                        dCref = new[] { rx0 * cs + cs / 2, ry0 * cs + cs / 2 }; dx0 = rx0; dy0 = ry0; dC = dCref;
                    }
                    else
                    {
                        dx0 = rx0; dy0 = ry0; dC = dCref;
                        path = Route(sx, sy, dx0, dy0, netCells, pref);
                        if (path == null || path.Count < 2) path = RouteDive(sx, sy, dx0, dy0, netCells, pref);   // 2nd chance: dive, run the inner planes
                    }
                    if (path == null || path.Count < 2)
                    {
                        if (!isRef) fallbacks.Add((paC, dC, col));
                        else
                        {
                            refDrop++;
                            dropBy[key] = dropBy.TryGetValue(key, out int dn) ? dn + 1 : 1;
                            if (usedL[0][sx, sy] >= CoreMark) dropStartBlocked++;
                            int ring = 0;
                            for (int q8 = 0; q8 < 8; q8++) { int rx = sx + D8x[q8], ry = sy + D8y[q8]; if (rx >= 0 && ry >= 0 && rx < gw && ry < gh && usedL[0][rx, ry] >= CoreMark) ring++; }
                            if (ring >= 6) dropStartRing++;
                        }
                        continue;
                    }
                    ReserveLayers(path, usedL, gw, gh);
                    if (isRef && !destLand) { b.Box(land, land, 0.14, new Point3d(dC[0], dC[1], 0.07), LNet, col); destLand = true; }   // shared pad once a route exists
                    if (isRef) foreach (var c3 in path) netCells.Add(c3);   // only reference nets grow a tree
                    var endC = path[path.Count - 1];
                    double[] joinC = { endC.Item1 * cs + cs / 2, endC.Item2 * cs + cs / 2 };   // where it joined: the dest pin or a junction
                    jobs.Add((ei, sx, sy, dx0, dy0, paC, joinC, col, wp)); jpath.Add(path);
                }
            }
            // OPTIMIZER (Freerouting BatchOptimizer idea): rip up each net and re-route it with all
            // the others present; keep the new route if it is shorter. Over passes the traces pull
            // tight and, with the bundle bias, settle into parallel buses.
            const int optPasses = 0;   // rip-up/reroute passes. OFF: on sparse database netlists the
                                       // initial routes are already near-shortest, so it changes nothing
                                       // yet costs ~50s/pass. Kept for a future bus-heavy, congested board.
            for (int pass = 0; pass < optPasses; pass++)
                for (int q = 0; q < jobs.Count; q++)
                {
                    var j = jobs[q]; var cur = jpath[q];
                    ReserveLayers(cur, usedL, gw, gh, -1);   // rip up
                    var np = Route(j.sx, j.sy, j.tx, j.ty);
                    var keep = (np != null && np.Count >= 2 && PathCost(np) < PathCost(cur)) ? np : cur;
                    jpath[q] = keep;
                    ReserveLayers(keep, usedL, gw, gh);
                }
            var junctions = new HashSet<(int, int, int)>();   // every cell where a branch joined its net's copper
            foreach (var pth in jpath) junctions.Add(pth[pth.Count - 1]);
            for (int q = 0; q < jobs.Count; q++)   // draw final: ownership copper 0.4, reference hairlines 0.15
                PathToNets(jpath[q], jobs[q].paC, jobs[q].pbC, jobs[q].col, cs, gw, gh, blkL, b, nets, jobs[q].ei, jobs[q].w, usedL, junctions, silk);
            foreach (var f in fallbacks)
                BottomJumper(f.paC, f.pbC, f.col, 0.3,
                    (i, j) => blk[i, j]    || used[i, j]    >= CoreMark,
                    (i, j) => blkBot[i, j] || usedMid[i, j] >= CoreMark,
                    (i, j) => blkBot[i, j] || usedBot[i, j] >= CoreMark);
            try   // routing success diagnostics for the audit loop
            {
                File.WriteAllText(Paths.Out("pcd_route_stats.txt"),
                    "EDGES=" + edges.Count + "\nNETS=" + netKey.Count + "\nPINS=" + pinsTotal + "\nROUTED=" + jobs.Count + "\nFALLBACK=" + fallbacks.Count + "\nREFDROP=" + refDrop
                    + "\nDROP_START_BLOCKED=" + dropStartBlocked + "\nDROP_START_RING6=" + dropStartRing
                    + "\nJUMP_MID=" + jumpMid + "\nJUMP_DIRTY=" + jumpDirty + "\n" + DropReport(dropBy, netKey, parts));
            } catch { }
            foreach (var net in nets) DrawTrace(b, net.Wp, net.Z, net.Col, net.W);   // render all routed segments
        }

        // 2-layer A*: state (x,y,layer,dir). Top blocks hardware; bottom only the board edge (so the
        // bottom can run under chips). A via switches layer at a legal cell for viaCost. Strictly 0/45/90.
        private static List<(int x, int y, int l)> AStar2(int sx, int sy, int gx, int gy,
            bool[][,] blkL, bool[,] viaOk, int[][,] usedL, int nL,
            int gw, int gh, double viaCost, bool viaAtEnds = false, HashSet<(int, int, int)> goalSet = null, int prefLayer = -1,
            int[,] silk = null)
        {   // goalSet (tree routing): reaching ANY of these (x,y,layer) cells -- the net's existing copper -- is the goal
            double H(int x, int y) { int ax = Math.Abs(x - gx), ay = Math.Abs(y - gy); return Math.Max(ax, ay) + 0.4142 * Math.Min(ax, ay); }
            var g = new Dictionary<(int, int, int, int), double>();
            var prev = new Dictionary<(int, int, int, int), (int, int, int, int)>();
            var pq = new PriorityQueue<(int x, int y, int l, int d), double>();
            g[(sx, sy, 0, -1)] = 0; pq.Enqueue((sx, sy, 0, -1), H(sx, sy));
            (int, int, int, int) goal = (-9, -9, -9, -9);
            while (pq.Count > 0)
            {
                var cur = pq.Dequeue();
                bool atGoal = goalSet != null ? goalSet.Contains((cur.x, cur.y, cur.l)) : (cur.x == gx && cur.y == gy && cur.l == 0);
                if (atGoal && !(cur.x == sx && cur.y == sy && cur.l == 0)) { goal = (cur.x, cur.y, cur.l, cur.d); break; }
                if (!g.TryGetValue((cur.x, cur.y, cur.l, cur.d), out double cg)) continue;
                bool[,] blk = blkL[cur.l]; int[,] used = usedL[cur.l];
                for (int k = 0; k < 8; k++)
                {
                    int nx = cur.x + D8x[k], ny = cur.y + D8y[k];
                    if (nx < 0 || ny < 0 || nx >= gw || ny >= gh) continue;
                    if (blk[nx, ny] && !(nx == gx && ny == gy && cur.l == 0)) continue;
                    bool ownNet = goalSet != null && goalSet.Contains((nx, ny, cur.l));   // this net's own copper is a goal, never a block
                    if (used[nx, ny] >= CoreMark && !(nx == gx && ny == gy) && !ownNet) continue;   // HARD block another trace's core on this layer -> no overlaps; cross by via
                    // a diagonal step whose two orthogonal neighbours are both another trace's core would
                    // cross that trace's diagonal at the cell corner without sharing a cell -> forbid
                    if (D8x[k] != 0 && D8y[k] != 0 && used[cur.x + D8x[k], cur.y] >= CoreMark && used[cur.x, cur.y + D8y[k]] >= CoreMark
                        && !(goalSet != null && (goalSet.Contains((cur.x + D8x[k], cur.y, cur.l)) || goalSet.Contains((cur.x, cur.y + D8y[k], cur.l))))) continue;
                    if (CornerCut(cur.x, cur.y, k, blk)) continue;
                    double nc = cg + StepCost(k) + TurnCost(cur.d, k) + (used[nx, ny] % CoreMark) * 4.0    // buffer count -> soft cost
                             + (cur.l == 0 && silk != null ? silk[nx, ny] : 0)                                  // top copper off the lettering
                              + (prefLayer >= 0 && cur.l != prefLayer ? 0.35 : 0.0);                           // per-net layer preference (giant nets)
                    // BUNDLE bias: reward running parallel-adjacent to an existing trace (forms buses)
                    int px = -D8y[k], py = D8x[k];
                    if ((nx + px >= 0 && ny + py >= 0 && nx + px < gw && ny + py < gh && used[nx + px, ny + py] >= CoreMark) ||
                        (nx - px >= 0 && ny - py >= 0 && nx - px < gw && ny - py < gh && used[nx - px, ny - py] >= CoreMark)) nc -= 0.5;
                    var ns = (nx, ny, cur.l, k);
                    if (!g.TryGetValue(ns, out double og) || nc < og)
                    { g[ns] = nc; prev[ns] = (cur.x, cur.y, cur.l, cur.d); pq.Enqueue((nx, ny, cur.l, k), nc + H(nx, ny)); }
                }
                // vias ONLY in open space (viaOk) -- never at a pad, so no via-wall along chip edges.
                // viaAtEnds (second-chance routing of a net that failed on top): a via is allowed at the
                // two pad cells so the net can dive straight into the open inner planes, like a THT pin.
                if (viaOk[cur.x, cur.y] || (viaAtEnds && ((cur.x == sx && cur.y == sy) || (cur.x == gx && cur.y == gy))))
                    for (int nl = 0; nl < nL; nl++)   // through-hole via reaches any other layer
                    {
                        if (nl == cur.l) continue;
                        // inner<->inner vias (both planes off the top) sit ~0.09 apart in z: nearly invisible
                        // and rarely necessary. Price them up so A* uses a visible top<->inner via instead.
                        double viaMul = (cur.l != 0 && nl != 0) ? 4.0 : 1.0;
                        double nc = cg + viaCost * viaMul; var ns = (cur.x, cur.y, nl, -1);
                        if (!g.TryGetValue(ns, out double og) || nc < og)
                        { g[ns] = nc; prev[ns] = (cur.x, cur.y, cur.l, cur.d); pq.Enqueue((cur.x, cur.y, nl, -1), nc + H(cur.x, cur.y)); }
                    }
            }
            if (goal.Item1 == -9) return null;
            var path = new List<(int, int, int)>(); var s = goal;
            while (!(s.Item1 == sx && s.Item2 == sy && s.Item3 == 0 && s.Item4 == -1))
            { path.Add((s.Item1, s.Item2, s.Item3)); if (!prev.TryGetValue(s, out s)) return null; }
            path.Add((sx, sy, 0)); path.Reverse(); return CanonPath(path);
        }

        /// <summary>Remove via barrels that carry no trace: a layer change sandwiched between the SAME
        /// grid cell on both sides is a vertical toggle no copper travels through (0->1->0 null round
        /// trip, or 0->1->2 pass-through). Collapsing these leaves only vias whose two sides both carry
        /// a moving trace, so every drawn via actually joins copper on two layers.</summary>
        private static List<(int x, int y, int l)> CanonPath(List<(int x, int y, int l)> p)
        {
            var q = new List<(int, int, int)>();
            foreach (var c in p) if (q.Count == 0 || q[q.Count - 1] != c) q.Add(c);   // drop consecutive duplicates
            bool changed = true;
            while (changed)
            {
                changed = false;
                for (int i = 1; i + 1 < q.Count; i++)
                {
                    bool sameXY = q[i - 1].Item1 == q[i].Item1 && q[i - 1].Item2 == q[i].Item2
                               && q[i].Item1 == q[i + 1].Item1 && q[i].Item2 == q[i + 1].Item2;
                    if (!sameXY) continue;                       // middle layer never moves horizontally -> carries no trace
                    q.RemoveAt(i);                               // drop the pass-through layer
                    if (q[i - 1] == q[i]) q.RemoveAt(i);         // null round trip collapsed to one cell -> dedup
                    changed = true; break;
                }
            }
            return q;
        }

        // diagnostics: dropped reference pins grouped by net, most-dropped first (no Linq)
        private static string DropReport(Dictionary<(int dest, ECls cls), int> dropBy,
                                         Dictionary<(int dest, ECls cls), List<int>> netKey, List<Part> parts)
        {
            var rows = new List<KeyValuePair<(int dest, ECls cls), int>>(dropBy);
            rows.Sort((a, b) => b.Value.CompareTo(a.Value));
            var sb = new System.Text.StringBuilder();
            foreach (var kv in rows)
                sb.Append("DROP ").Append(kv.Value).Append("  ").Append(kv.Key.cls).Append(" -> ")
                  .Append(parts[kv.Key.dest].Title).Append("  (net pins=").Append(netKey[kv.Key].Count).Append(")\n");
            return sb.ToString();
        }

        /// <summary>Every segment of a straightened world-coordinate path is clear of <paramref name="blocked"/>.
        /// CleanG's 2-point branch hands back the RAW straight line when no dogleg is clear, so a caller that
        /// must not cross anything has to re-check what it got.</summary>
        private static bool PathClearG(List<double[]> wp, Func<int, int, bool> blocked, double cs, int gw, int gh)
        {
            if (wp == null || wp.Count < 2) return false;
            for (int i = 1; i < wp.Count; i++)
                if (!SegClearG(blocked, cs, gw, gh, wp[i - 1][0], wp[i - 1][1], wp[i][0], wp[i][1])) return false;
            return true;
        }

        /// <summary>Mark a world-coordinate polyline into one layer's occupancy grid the way ReserveLayers marks
        /// an A* cell path: CoreMark on the cells the copper covers, +1 on the 4-neighbour buffer. Bottom jumpers
        /// are emitted as geometry rather than cell paths, so without this every jumper is invisible to the next.</summary>
        private static void ReserveWorld(List<double[]> wp, int[,] u, double cs, int gw, int gh)
        {
            if (wp == null || wp.Count < 2) return;
            var seen = new HashSet<(int, int)>();
            for (int i = 1; i < wp.Count; i++)
            {
                double ax = wp[i - 1][0], ay = wp[i - 1][1], bx = wp[i][0], by = wp[i][1];
                double len = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
                int steps = Math.Max(1, (int)(len / (cs * 0.5)));
                for (int t = 0; t <= steps; t++)
                {
                    double f = (double)t / steps, x = ax + (bx - ax) * f, y = ay + (by - ay) * f;
                    int ux = Cl((int)(x / cs), 0, gw - 1), uy = Cl((int)(y / cs), 0, gh - 1);
                    if (!seen.Add((ux, uy))) continue;
                    u[ux, uy] += CoreMark;
                    if (ux > 0) u[ux - 1, uy]++; if (ux < gw - 1) u[ux + 1, uy]++;
                    if (uy > 0) u[ux, uy - 1]++; if (uy < gh - 1) u[ux, uy + 1]++;
                }
            }
        }

        private static void ReserveLayers(List<(int x, int y, int l)> path, int[][,] usedL, int gw, int gh, int sign = 1)
        {
            foreach (var (ux, uy, l) in path)
            {
                int[,] u = usedL[l];
                u[ux, uy] += CoreMark * sign;
                if (ux > 0) u[ux - 1, uy] += sign; if (ux < gw - 1) u[ux + 1, uy] += sign;
                if (uy > 0) u[ux, uy - 1] += sign; if (uy < gh - 1) u[ux, uy + 1] += sign;
            }
        }
        // Freerouting-style cost for the pull-tight optimizer: shorter + fewer vias is better.
        private static double PathCost(List<(int x, int y, int l)> path)
        {
            double c = path.Count; int vias = 0;
            for (int k = 1; k < path.Count; k++) if (path[k].l != path[k - 1].l) vias++;
            return c + vias * 10.0;
        }

        // split a 2-layer cell path into per-layer octilinear net segments + a via at each layer switch
        private static void PathToNets(List<(int x, int y, int l)> path, double[] pa, double[] pb, AcColor col,
            double cs, int gw, int gh, bool[][,] blkL, Pcb b, List<Net> nets, int seed, double w = 0.3, int[][,] usedL = null,
            HashSet<(int, int, int)> junctions = null, int[,] silk = null)
        {
            double C(int v) => v * cs + cs / 2;
            int i = 0;
            while (i < path.Count)
            {
                int layer = path[i].l; int j = i; while (j < path.Count && path[j].l == layer) j++;
                double[] startPt = i == 0 ? pa : new[] { C(path[i].x), C(path[i].y) };
                double[] endPt = j == path.Count ? pb : new[] { C(path[j - 1].x), C(path[j - 1].y) };
                var pts = new List<double[]> { startPt }; var forced = new HashSet<int>();
                for (int k = i + 1; k < j - 1; k++)
                {
                    var a = path[k - 1]; var m = path[k]; var n = path[k + 1];
                    bool turn = (m.x - a.x) != (n.x - m.x) || (m.y - a.y) != (n.y - m.y);
                    bool junc = junctions != null && junctions.Contains((m.x, m.y, layer));   // a branch joins here
                    if (turn || junc) { if (junc) forced.Add(pts.Count); pts.Add(new[] { C(m.x), C(m.y) }); }
                }
                pts.Add(endPt);
                // straighten against hardware AND every OTHER net's core cells on this layer (own cells stay free);
                // junction cells stay as vertices so every branch end lands exactly on the drawn trunk
                var own = new HashSet<(int, int)>(); for (int k = i; k < j; k++) own.Add((path[k].x, path[k].y));
                bool[,] bg = blkL[layer]; int[,] ug = usedL?[layer];
                // On the top layer the lettering is a hard stop for STRAIGHTENING: A* already paid to
                // route around it, and a dogleg that cuts the corner would put the trace straight back
                // across the text. If that leaves nothing to straighten, CleanG keeps the A* path --
                // a failed straighten costs a few bends, never a dropped net.
                Func<int, int, bool> blocked = (x, y) => bg[x, y] || (ug != null && ug[x, y] >= CoreMark && !own.Contains((x, y)))
                                                     || (layer == 0 && silk != null && silk[x, y] > 0);
                var clean = CleanG(pts, blocked, cs, gw, gh, forced);
                double z = layer == 0 ? ZTop + (seed % 5) * 0.004 : Zof(layer);   // top face / inner plane 1 / inner plane 2
                if (clean.Count >= 2) nets.Add(new Net { Wp = clean, Z = z, Col = col, Top = layer == 0, W = w });
                // RE-RESERVE WHAT WAS ACTUALLY DRAWN. usedL holds each path's ORIGINAL A* cells; CleanG then
                // moves the copper OFF them to cut corners. Without this, net A vacates cells into free space
                // and net B straightens straight through that space -- both see only the other's original
                // cells, so two straightened doglegs cross where neither A* path ever went. (Measured: 4
                // same-layer crossings on the bottom plane, every one of them a straightened run.)
                if (ug != null && clean.Count >= 2) ReserveWorld(clean, ug, cs, gw, gh);
                if (j < path.Count) DropVia(b, C(path[j - 1].x), C(path[j - 1].y), col, layer, path[j].l, w < 0.3);   // blind/buried via; thin classes get the small via
                i = j;
            }
        }

        // octilinear cleanup against a BLOCKED predicate: hardware / board edge AND other nets' core cells,
        // so straightening a path can never cut across another trace on the same layer (the old grid-only
        // form ignored other nets and produced same-layer crossings the router itself had avoided)
        private static bool SegClearG(Func<int, int, bool> blocked, double cs, int gw, int gh, double ax, double ay, double bx, double by)
        {
            double len = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
            int steps = Math.Max(1, (int)(len / (cs * 0.5)));
            for (int s = 1; s < steps; s++)
            { double t = (double)s / steps, x = ax + (bx - ax) * t, y = ay + (by - ay) * t; if (blocked(Cl((int)(x / cs), 0, gw - 1), Cl((int)(y / cs), 0, gh - 1))) return false; }
            // A 45-degree chord can cross another 45-degree chord at a shared LATTICE CORNER without either
            // ever entering the other's cells (measured: every residual crossing sat on the lattice). At each
            // corner this chord steps through, the two cells on the OTHER diagonal may not both be blocked --
            // the router's own corner rule, applied to the straightened geometry.
            double ddx = bx - ax, ddy = by - ay;
            if (Math.Abs(Math.Abs(ddx) - Math.Abs(ddy)) < 1e-6 && Math.Abs(ddx) > cs)
            {
                int sx = Math.Sign(ddx), sy = Math.Sign(ddy), n = (int)Math.Round(Math.Abs(ddx) / cs);
                int i0 = Cl((int)(ax / cs), 0, gw - 1), j0 = Cl((int)(ay / cs), 0, gh - 1);
                for (int k = 1; k <= n; k++)
                {
                    int px = i0 + (k - 1) * sx, py = j0 + (k - 1) * sy, nx = i0 + k * sx, ny = j0 + k * sy;   // cell before / after the corner
                    int o1x = px, o1y = ny, o2x = nx, o2y = py;                                                    // the other diagonal's two cells
                    if (o1x < 0 || o1y < 0 || o2x < 0 || o2y < 0 || o1x >= gw || o1y >= gh || o2x >= gw || o2y >= gh) continue;
                    if (blocked(o1x, o1y) && blocked(o2x, o2y)) return false;
                }
            }
            return true;
        }
        private static double[] DoglegG(Func<int, int, bool> blocked, double cs, int gw, int gh, double[] A, double[] B)
        {
            double dx = B[0] - A[0], dy = B[1] - A[1], sx = Math.Sign(dx), sy = Math.Sign(dy), mm = Math.Min(Math.Abs(dx), Math.Abs(dy));
            double[] cd = { A[0] + sx * mm, A[1] + sy * mm }, cst = { B[0] - sx * mm, B[1] - sy * mm };
            if (SegClearG(blocked, cs, gw, gh, A[0], A[1], cd[0], cd[1]) && SegClearG(blocked, cs, gw, gh, cd[0], cd[1], B[0], B[1])) return cd;
            if (SegClearG(blocked, cs, gw, gh, A[0], A[1], cst[0], cst[1]) && SegClearG(blocked, cs, gw, gh, cst[0], cst[1], B[0], B[1])) return cst;
            return null;
        }
        private static List<double[]> CleanG(List<double[]> pts, Func<int, int, bool> blocked, double cs, int gw, int gh, HashSet<int> forced = null)
        {   // forced: indices into pts that must survive as vertices (net junctions) -- never straightened past
            if (pts.Count <= 1) return pts;
            if (pts.Count == 2) { var d = DoglegG(blocked, cs, gw, gh, pts[0], pts[1]); return d != null ? new List<double[]> { pts[0], d, pts[1] } : pts; }
            var outp = new List<double[]> { pts[0] }; int anchor = 0;
            while (anchor < pts.Count - 1)
            {
                int limit = pts.Count - 1;
                if (forced != null) for (int f = anchor + 1; f < pts.Count; f++) if (forced.Contains(f)) { limit = f; break; }   // stop at the next junction
                int best = -1; double[] corner = null;
                for (int j = limit; j > anchor; j--) { var c = DoglegG(blocked, cs, gw, gh, pts[anchor], pts[j]); if (c != null) { best = j; corner = c; break; } }
                if (best < 0) { anchor++; outp.Add(pts[anchor]); continue; }
                bool degen = (Math.Abs(corner[0] - pts[anchor][0]) < 0.05 && Math.Abs(corner[1] - pts[anchor][1]) < 0.05)
                          || (Math.Abs(corner[0] - pts[best][0]) < 0.05 && Math.Abs(corner[1] - pts[best][1]) < 0.05);
                if (!degen) outp.Add(corner);
                outp.Add(pts[best]); anchor = best;
            }
            return outp;
        }
        // grid-only shims (block grid alone) for callers without a used-grid
        private static bool SegClearG(bool[,] grid, double cs, int gw, int gh, double ax, double ay, double bx, double by) => SegClearG((i, j) => grid[i, j], cs, gw, gh, ax, ay, bx, by);
        private static double[] DoglegG(bool[,] grid, double cs, int gw, int gh, double[] A, double[] B) => DoglegG((i, j) => grid[i, j], cs, gw, gh, A, B);
        private static List<double[]> CleanG(List<double[]> pts, bool[,] grid, double cs, int gw, int gh) => CleanG(pts, (i, j) => grid[i, j], cs, gw, gh);

        private static int Depth(Part p) =>
            p.Kind == Kind.Die ? 0 : (p.Kind == Kind.Table || p.Kind == Kind.Nod) ? 1
          : (p.Kind == Kind.Record || p.Kind == Kind.Gpu) ? 2 : 3;

        // COPPER-LAYER STACKUP. The board slab spans z=0 (top face) to z=-BT (bottom face). Layer 0 is
        // the top copper, just proud of the top face; layers 1 and 2 are INNER planes embedded WITHIN
        // the board (not stacked underneath it), so a via to them is a blind/buried via that stops at
        // the plane, and the translucent substrate lets the inner routing show. A real 4-layer stackup.
        private const double ZTop = 0.085;              // top copper, just above the top face (z=0)
        // 3-layer stackup: TOP on the top face, MIDDLE in the true middle of the slab, BOTTOM on the
        // bottom face (proud below it, mirroring the top). Board spans z=0 to z=-BT.
        private static double Zof(int layer) => layer == 0 ? ZTop : layer == 1 ? -BT * 0.5 : -(BT + 0.085);

        /// <summary>A parallel copy of a polyline offset by <paramref name="d"/> (mitred corners keep
        /// the lanes evenly spaced through 45deg / 90deg bends). Used for CPU bundles.</summary>
        private static List<double[]> OffsetPath(List<double[]> p, double d)
        {
            int n = p.Count; var o = new List<double[]>(n);
            for (int i = 0; i < n; i++)
            {
                double[] din = i > 0 ? Unit(p[i - 1], p[i]) : null, dout = i < n - 1 ? Unit(p[i], p[i + 1]) : null;
                double nx, ny;
                if (din == null) { nx = -dout[1]; ny = dout[0]; }
                else if (dout == null) { nx = -din[1]; ny = din[0]; }
                else
                {
                    double bx = -din[1] - dout[1], by = din[0] + dout[0], bl = Math.Sqrt(bx * bx + by * by);
                    if (bl < 1e-6) { nx = -din[1]; ny = din[0]; }
                    else { nx = bx / bl; ny = by / bl; double ch = nx * (-din[1]) + ny * din[0]; if (Math.Abs(ch) > 0.3) { nx /= ch; ny /= ch; } }
                }
                o.Add(new[] { p[i][0] + nx * d, p[i][1] + ny * d });
            }
            return o;
        }
        private static double[] Unit(double[] a, double[] c)
        { double dx = c[0] - a[0], dy = c[1] - a[1], l = Math.Sqrt(dx * dx + dy * dy); return l < 1e-9 ? new[] { 1.0, 0.0 } : new[] { dx / l, dy / l }; }

        // ======================================================================
        //  NETS: crossing resolution (vias + bottom-layer hops), like a real 2-layer board
        // ======================================================================
        private sealed class Net { public List<double[]> Wp; public double Z; public AcColor Col; public bool Top; public double W = 0.3; }

        private static void ResolveCrossings(Pcb b, List<Net> nets, Func<double, double, bool> viaLegal)
        {
            int n = nets.Count;
            var cum = new double[n][]; var bb = new double[n][];
            for (int i = 0; i < n; i++)
            {
                var wp = nets[i].Wp; cum[i] = new double[wp.Count];
                double x0 = 1e18, y0 = 1e18, x1 = -1e18, y1 = -1e18;
                for (int k = 0; k < wp.Count; k++)
                {
                    if (k > 0) cum[i][k] = cum[i][k - 1] + Dist(wp[k - 1], wp[k]);
                    x0 = Math.Min(x0, wp[k][0]); y0 = Math.Min(y0, wp[k][1]); x1 = Math.Max(x1, wp[k][0]); y1 = Math.Max(y1, wp[k][1]);
                }
                bb[i] = new[] { x0, y0, x1, y1 };
            }
            var hops = new List<double>[n]; for (int i = 0; i < n; i++) hops[i] = new List<double>();
            for (int i = 0; i < n; i++)
            {
                if (!nets[i].Top) continue;
                for (int j = i + 1; j < n; j++)
                {
                    if (!nets[j].Top) continue;
                    if (SameNet(nets[i].Col, nets[j].Col)) continue;   // same net may join: no via needed
                    if (bb[i][2] < bb[j][0] || bb[j][2] < bb[i][0] || bb[i][3] < bb[j][1] || bb[j][3] < bb[i][1]) continue;
                    var A = nets[i].Wp; var B = nets[j].Wp;
                    for (int a = 0; a + 1 < A.Count; a++)
                        for (int c = 0; c + 1 < B.Count; c++)
                            if (SegCross(A[a], A[a + 1], B[c], B[c + 1], out _, out double u))
                                hops[j].Add(cum[j][c] + u * (cum[j][c + 1] - cum[j][c]));   // the LATER net dives
                }
            }
            const double hh = 1.7;   // half-length of a dive: via clearance either side of the crossing
            for (int i = 0; i < n; i++)
            {
                var net = nets[i]; var wp = net.Wp; double L = cum[i][wp.Count - 1];
                if (!net.Top || hops[i].Count == 0 || L < 5.0) { DrawTrace(b, wp, net.Z, net.Col, net.W); continue; }
                hops[i].Sort();
                var iv = new List<(double a, double c)>();
                foreach (double s in hops[i])
                {
                    double a = Math.Max(1.2, s - hh), c = Math.Min(L - 1.2, s + hh); if (c <= a) continue;
                    if (iv.Count > 0 && a <= iv[iv.Count - 1].c + 0.4) iv[iv.Count - 1] = (iv[iv.Count - 1].a, Math.Max(iv[iv.Count - 1].c, c));
                    else iv.Add((a, c));
                }
                // keep only dives whose BOTH via points are legal (off hardware/pads); max 2 dives = <=4 vias
                var kept = new List<(double a, double c)>();
                foreach (var (a, c) in iv)
                {
                    if (kept.Count >= 2) break;
                    var seg = SubPath(wp, cum[i], a, c); if (seg.Count < 2) continue;
                    if (viaLegal(seg[0][0], seg[0][1]) && viaLegal(seg[seg.Count - 1][0], seg[seg.Count - 1][1])) kept.Add((a, c));
                }
                if (kept.Count == 0) { DrawTrace(b, wp, net.Z, net.Col, net.W); continue; }
                double pos = 0;
                foreach (var (a, c) in kept)
                {
                    var top = SubPath(wp, cum[i], pos, a); if (top.Count >= 2) DrawTrace(b, top, net.Z, net.Col, net.W);
                    var bot = SubPath(wp, cum[i], a, c);
                    if (bot.Count >= 2)
                    {
                        DropVia(b, bot[0][0], bot[0][1], net.Col); DropVia(b, bot[bot.Count - 1][0], bot[bot.Count - 1][1], net.Col);
                        DrawTrace(b, bot, -BT - 0.09, net.Col, net.W);
                    }
                    pos = c;
                }
                var tail = SubPath(wp, cum[i], pos, L); if (tail.Count >= 2) DrawTrace(b, tail, net.Z, net.Col, net.W);
            }
        }

        private static bool SameNet(AcColor a, AcColor c) => a.Red == c.Red && a.Green == c.Green && a.Blue == c.Blue;

        /// <summary>Proper interior crossing of two segments (T-junctions / shared endpoints do not count).</summary>
        private static bool SegCross(double[] p, double[] p2, double[] q, double[] q2, out double t, out double u)
        {
            t = u = 0;
            double rx = p2[0] - p[0], ry = p2[1] - p[1], sx = q2[0] - q[0], sy = q2[1] - q[1];
            double den = rx * sy - ry * sx; if (Math.Abs(den) < 1e-9) return false;
            double qpx = q[0] - p[0], qpy = q[1] - p[1];
            t = (qpx * sy - qpy * sx) / den; u = (qpx * ry - qpy * rx) / den;
            const double e = 0.03;
            return t > e && t < 1 - e && u > e && u < 1 - e;
        }

        private static List<double[]> SubPath(List<double[]> wp, double[] cum, double a, double c)
        {
            var o = new List<double[]>();
            for (int k = 0; k + 1 < wp.Count; k++)
            {
                double s0 = cum[k], s1 = cum[k + 1]; if (s1 <= a || s0 >= c) continue;
                if (o.Count == 0) o.Add(Lerp(wp[k], wp[k + 1], s0, s1, Math.Max(a, s0)));
                if (s1 <= c) o.Add(new[] { wp[k + 1][0], wp[k + 1][1] });
                else { o.Add(Lerp(wp[k], wp[k + 1], s0, s1, c)); break; }
            }
            return o;
        }
        private static double[] Lerp(double[] p, double[] q, double s0, double s1, double s)
        { double f = s1 > s0 ? (s - s0) / (s1 - s0) : 0; return new[] { p[0] + (q[0] - p[0]) * f, p[1] + (q[1] - p[1]) * f }; }
        private static double Dist(double[] p, double[] q) => Math.Sqrt((q[0] - p[0]) * (q[0] - p[0]) + (q[1] - p[1]) * (q[1] - p[1]));

        // ======================================================================
        //  BOARD OUTLINE: the source drawing's convex hull, fitted around the content
        // ======================================================================
        private static List<Point2d> BoardOutline(double w, double h, double cx0, double cy0, double cx1, double cy1)
        {
            var hull = ConvexHull(_srcPts);
            if (hull.Count < 3 || PolyArea(hull.ConvertAll(q => new Point2d(q.x, q.y))) < 1e-6) return ChamferedRect(w, h);
            double hx0 = 1e18, hy0 = 1e18, hx1 = -1e18, hy1 = -1e18;
            foreach (var (x, y) in hull) { hx0 = Math.Min(hx0, x); hy0 = Math.Min(hy0, y); hx1 = Math.Max(hx1, x); hy1 = Math.Max(hy1, y); }
            double sx = w / Math.Max(1e-9, hx1 - hx0), sy = h / Math.Max(1e-9, hy1 - hy0);
            var poly = new List<Point2d>();
            foreach (var (x, y) in hull) poly.Add(new Point2d((x - hx0) * sx, (y - hy0) * sy));
            // grow about the board center until the content CORE is fully inside the outline
            var corners = new[] { (cx0, cy0), (cx1, cy0), (cx1, cy1), (cx0, cy1) };
            for (int k = 0; k < 14; k++)
            {
                bool ok = true; foreach (var (x, y) in corners) if (!PointInPoly(poly, x, y)) { ok = false; break; }
                if (ok) break;
                for (int i = 0; i < poly.Count; i++)
                    poly[i] = new Point2d(w / 2 + (poly[i].X - w / 2) * 1.05, h / 2 + (poly[i].Y - h / 2) * 1.05);
            }
            // a hull that is essentially the rectangle still gets a keyed chamfer
            return PolyArea(poly) > 0.93 * w * h ? ChamferedRect(w, h) : poly;
        }

        private static List<Point2d> ChamferedRect(double w, double h)
        {
            Seed(0x51ED270Bu ^ (uint)(w * 3.0 + h * 5.0));
            double c = RR(9, 15);
            return new List<Point2d> { new Point2d(c, 0), new Point2d(w, 0), new Point2d(w, h), new Point2d(0, h), new Point2d(0, c) };
        }

        private static List<(double x, double y)> ConvexHull(List<(double x, double y)> pts)
        {
            var p = new List<(double x, double y)>(pts); if (p.Count < 3) return p;
            p.Sort((a, c) => a.x != c.x ? a.x.CompareTo(c.x) : a.y.CompareTo(c.y));
            double Cross((double x, double y) o, (double x, double y) a, (double x, double y) c) => (a.x - o.x) * (c.y - o.y) - (a.y - o.y) * (c.x - o.x);
            var lo = new List<(double x, double y)>();
            foreach (var q in p) { while (lo.Count >= 2 && Cross(lo[lo.Count - 2], lo[lo.Count - 1], q) <= 0) lo.RemoveAt(lo.Count - 1); lo.Add(q); }
            var up = new List<(double x, double y)>();
            for (int i = p.Count - 1; i >= 0; i--) { var q = p[i]; while (up.Count >= 2 && Cross(up[up.Count - 2], up[up.Count - 1], q) <= 0) up.RemoveAt(up.Count - 1); up.Add(q); }
            lo.RemoveAt(lo.Count - 1); up.RemoveAt(up.Count - 1); lo.AddRange(up);
            return lo;
        }

        private static bool PointInPoly(List<Point2d> poly, double x, double y)
        {
            bool inside = false; int n = poly.Count;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                double xi = poly[i].X, yi = poly[i].Y, xj = poly[j].X, yj = poly[j].Y;
                if ((yi > y) != (yj > y) && x < (xj - xi) * (y - yi) / (yj - yi) + xi) inside = !inside;
            }
            return inside;
        }
        private static double PolyArea(List<Point2d> poly)
        {
            double a = 0; for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++) a += (poly[j].X + poly[i].X) * (poly[j].Y - poly[i].Y);
            return Math.Abs(a) / 2;
        }

        /// <summary>Shortest distance from a point to the polygon's boundary (min over edges).</summary>
        private static double DistToPolyEdge(List<Point2d> poly, double px, double py)
        {
            double best = 1e18;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            {
                double ax = poly[j].X, ay = poly[j].Y, bx = poly[i].X, by = poly[i].Y;
                double dx = bx - ax, dy = by - ay, l2 = dx * dx + dy * dy;
                double t = l2 < 1e-9 ? 0 : Math.Max(0, Math.Min(1, ((px - ax) * dx + (py - ay) * dy) / l2));
                double cx = ax + dx * t, cy = ay + dy * t, d = Math.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy));
                if (d < best) best = d;
            }
            return best;
        }

        private static void BuildBoardPoly(Pcb b, List<Point2d> poly, double w, double h,
                                           double cx0, double cy0, double cx1, double cy1)
        {
            Seed(0x9E3779B1u ^ (uint)(w * 7.0 + h * 13.0));
            var board = b.PolySolid(poly, BT);
            void Cut(Solid3d s) { board.BooleanOperation(BooleanOperationType.BoolSubtract, s); s.Dispose(); }
            // NOTE: perimeter bites + edge notch are DISABLED. They cut the board but the router's
            // polygon didn't reflect them, so traces routed into the bitten margin looked like they
            // ran off the board. Board == routed polygon now (its source-hull shape is variety enough).
            // mounting holes at outline vertices, inset toward the centroid
            double cxm = 0, cym = 0; foreach (var q in poly) { cxm += q.X; cym += q.Y; } cxm /= poly.Count; cym /= poly.Count;
            var holes = new List<(double x, double y, double r)>();
            int step = Math.Max(1, poly.Count / 6);
            for (int i = 0; i < poly.Count && holes.Count < 6; i += step)
            {
                double dx = cxm - poly[i].X, dy = cym - poly[i].Y, len = Math.Sqrt(dx * dx + dy * dy); if (len < 1e-6) continue;
                double ins = RR(7, 9);
                holes.Add((poly[i].X + dx / len * ins, poly[i].Y + dy / len * ins, RR(2.0, 2.7)));
            }
            foreach (var (hx, hy, r) in holes)
            {
                var drill = new Solid3d(); drill.CreateFrustum(BT + 2, r, r, r);
                drill.TransformBy(Matrix3d.Displacement(new Vector3d(hx, hy, -BT / 2)));
                Cut(drill);
            }
            b.Add(board, LBoard, Board);
            _holes.Clear(); _holes.AddRange(holes);   // the router clears copper around these
            foreach (var (hx, hy, r) in holes)
            {
                b.Torus(r + 0.9, 0.32, new Point3d(hx, hy, 0.075), LPad, Pad);
                b.Torus(r + 0.9, 0.32, new Point3d(hx, hy, -BT - 0.075), LPad, Pad);
            }
        }

        private static (double w, double d, double z, AcColor col) PartFoot(Part p)
        {
            switch (p.Kind)
            {
                case Kind.Die:       return (p.W, p.D, p.TopZ, DieBody);   // CPU (largest)
                case Kind.Table:     return (p.W, p.D, p.TopZ, IcBody);    // table IC (large)
                case Kind.Nod:       return (p.W, p.D, p.TopZ, IcBody);
                case Kind.Record:    return (13, 8, 2.6, RecBody);         // record chip (medium)
                case Kind.Gpu:       return (p.W, p.D, p.TopZ, GpuSub);    // *Model_Space (2nd big die)
                // discrete passives are SMALL relative to the ICs (like a real board)
                case Kind.Resistor:  return (5.0, 2.0, 1.3, Resistor);
                case Kind.Capacitor: return (3.0, 3.0, 3.0, Capacitor);
                case Kind.Inductor:  return (5.0, 2.6, 1.7, Inductor);
                case Kind.TextPart:  return (6.0, 3.0, 1.7, Generic);
                case Kind.Array:     return (6.5, 3.0, 1.7, Resistor);
                case Kind.Socket:    return (7.0, 4.0, 2.2, Terminal);
                case Kind.TestPad:   return (2.2, 2.2, 0.4, Pad);
                // ---- extended package family (detailed geometry drawn in PlacePart) ----
                case Kind.Diode:     return (5.0, 2.2, 1.4, DiodeBk);      // SMD diode w/ cathode band
                case Kind.Led:       return (3.4, 3.4, 2.4, LedLens[0]);   // domed lens (tint per-part)
                case Kind.Transistor:return (5.2, 4.4, 4.6, DiodeBk);      // TO-92 half-can + 3 leads
                case Kind.Crystal:   return (7.2, 3.6, 2.8, CanSilver);    // HC-49 metal can
                case Kind.Connector: return (10.0, 4.2, 5.4, Plastic);     // pin header in a plastic base
                case Kind.Relay:     return (9.0, 7.0, 8.0, RelayBlue);    // sealed relay can
                case Kind.Dip:       return (12.0, 6.0, 3.0, IcBody);      // DIP IC, notch + 2 lead rows
                case Kind.Pot:       return (7.0, 7.0, 4.5, PotBody);      // potentiometer w/ shaft
                case Kind.Shield:    return (9.0, 7.0, 2.4, ShieldTin);    // perforated EMI shield can
                case Kind.Sensor:    return (4.6, 4.6, 2.2, IcBody);       // sensor pkg + metal lid + port
                case Kind.Coil:      return (6.4, 6.4, 4.2, Inductor);     // air-core wound inductor
                case Kind.Antenna:   return (7.4, 2.8, 1.3, Plastic);      // chip antenna + meander whip
                case Kind.Memory:    return (16.0, 3.2, 7.2, Board);       // memory edge card, standing
                case Kind.Fuse:      return (6.4, 2.8, 2.4, FuseGl);       // cartridge fuse in clips
                case Kind.Heatsink:  return (8.0, 8.0, 6.0, HeatAl);       // finned heatsink
                case Kind.Ribbon:    return (11.0, 4.8, 4.4, Plastic);     // shrouded IDC ribbon header
                case Kind.Display:   return (12.0, 8.0, 1.8, Glass);       // glass display module + FPC
                case Kind.Rail:      return (10.0, 1.8, 1.8, CopTop);      // bus bar on two standoffs
                case Kind.Opaque:    return (p.W > 0 ? p.W : 8.0, p.D > 0 ? p.D : 6.0, p.TopZ > 0 ? p.TopZ : 3.2, OpaqueBk);
                default:             return (4.5, 2.6, 1.5, Generic);
            }
        }

        private static void PlacePart(Pcb b, Part part)
        {
            AcColor col = part.Col;
            double w = part.Vw, d = part.Vd, topZ = Math.Max(0.6, part.Vz), cx = part.Cx, cy = part.Cy;
            int v = part.Variant;
            double faceH = 0;   // >0 : this package's top is obstructed -> data prints on its front side
            bool isChip = part.Kind == Kind.Die || part.Kind == Kind.Table || part.Kind == Kind.Nod
                       || part.Kind == Kind.Record || part.Kind == Kind.Gpu;
            string layer = isChip ? LIc : LPart;

            if (part.Kind == Kind.Die)
            {
                // CPU: green substrate + LGA land ring + stepped metallic heatspreader + pin-1
                double subZ = 1.6;
                b.Box(w, d, subZ, new Point3d(cx, cy, subZ / 2), LIc, AcColor.FromRgb(30, 46, 34));
                FourPads(b, cx, cy, w, d);                          // 4 silver hold-down pads
                b.Box(w * 0.84, d * 0.84, 0.8, new Point3d(cx, cy, subZ + 0.4), LPart, Terminal);       // IHS lip
                double ihsH = 2.0, ihsBase = subZ + 0.8;
                b.Box(w * 0.6, d * 0.6, ihsH, new Point3d(cx, cy, ihsBase + ihsH / 2), LPart, Terminal); // IHS
                b.Cyl(0.6, 0.25, new Point3d(cx - w / 2 + 3, cy - d / 2 + 3, subZ), LIc, Pin1);          // pin-1
                w *= 0.6; d *= 0.6; topZ = ihsBase + ihsH;      // the DATABASE pod lands on the heatspreader
            }
            else if (part.Kind == Kind.Gpu)
            {
                // GPU: blue-violet substrate, four flanking VRAM packages, a bare die under a cold
                // plate. Deliberately NOT the CPU package (no stepped IHS) so the two big dies read
                // apart at a glance.
                double subZ = 1.4;
                b.Box(w, d, subZ, new Point3d(cx, cy, subZ / 2), LIc, GpuSub);
                FourPads(b, cx, cy, w, d);                          // 4 silver hold-down pads
                double vw = w * 0.17, vd = d * 0.17, vz = 1.0;      // VRAM, one package per quadrant
                for (int sx = -1; sx <= 1; sx += 2)
                    for (int sy = -1; sy <= 1; sy += 2)
                    {
                        double vx = cx + sx * w * 0.39, vy = cy + sy * d * 0.39;
                        b.Box(vw, vd, vz, new Point3d(vx, vy, subZ + vz / 2), LIc, IcBody);
                        b.Cyl(Math.Min(0.3, vw * 0.12), 0.14,
                              new Point3d(vx - vw / 2 + vw * 0.2, vy + vd / 2 - vd * 0.2, subZ + vz), LIc, Silk);
                    }
                double dieH = 1.2, dieBase = subZ;
                b.Box(w * 0.5, d * 0.5, dieH, new Point3d(cx, cy, dieBase + dieH / 2), LIc, GpuDie);
                b.Box(w * 0.56, d * 0.56, 0.35, new Point3d(cx, cy, dieBase + dieH + 0.175), LPart, CanSilver);
                b.Cyl(0.6, 0.25, new Point3d(cx - w / 2 + 3, cy - d / 2 + 3, subZ), LIc, Silk);   // pin-1
                w *= 0.56; d *= 0.56; topZ = dieBase + dieH + 0.35;  // the pod lands on the cold plate
            }
            else if (isChip)
            {
                b.Box(w, d, topZ, new Point3d(cx, cy, topZ / 2), layer, col);
                FourPads(b, cx, cy, w, d);                          // 4 silver hold-down pads only
                // package family encodes importance: >=6 records QFP (leads all round),
                // 2..5 SOIC (two sides), 0..1 QFN (bare body). Records stay bare bank chips.
                if (part.Kind == Kind.Table || part.Kind == Kind.Nod)
                {
                    if (part.Rec >= 6) Leads(b, cx, cy, w, d, true);
                    else if (part.Rec >= 2) Leads(b, cx, cy, w, d, false);
                }
                b.Cyl(0.5, 0.2, new Point3d(cx - w / 2 + 1.6, cy + d / 2 - 1.6, topZ), LIc, Pin1);
            }
            else switch (part.Kind)
            {
                case Kind.Capacitor:
                    double cr = Math.Max(w, d) / 2.0;
                    if (cr < 1.35) {                                        // tiny -> MLCC ceramic block
                        double mh = Math.Max(0.8, topZ * 0.5);
                        b.Box(w, d, mh, new Point3d(cx, cy, mh / 2), layer, Mlcc);
                        Terminals(b, cx, cy, w, d, mh); topZ = mh + 0.22; ShrinkToBody(ref w, ref d); }
                    else if (v == 2 && cr < 2.4) { double hh = Math.Max(1.2, topZ * 0.45);   // ceramic disc
                        b.Cyl(cr, hh, new Point3d(cx, cy, 0), layer, col); topZ = hh; w = d = cr * 1.3; }
                    else {                                                  // electrolytic / tantalum can
                        b.Cyl(cr, topZ, new Point3d(cx, cy, 0), layer, col);
                        b.Torus(cr, 0.3, new Point3d(cx, cy, topZ), LPart, CapRim);
                        if (v == 1) b.Box(0.55, cr * 0.9, topZ, new Point3d(cx - cr + 0.3, cy, topZ / 2), LPart, Silk);
                        w = d = cr * 1.32; }
                    break;
                case Kind.Resistor:
                    double rw = v == 1 ? w * 0.72 : w, rd = v == 1 ? d * 0.66 : d;
                    b.Box(rw, rd, topZ, new Point3d(cx, cy, topZ / 2), layer, col);
                    Terminals(b, cx, cy, w, d, topZ);
                    topZ += 0.22; ShrinkToBody(ref w, ref d);              // text above the caps, footprint on the body between them
                    break;
                case Kind.Inductor:
                    if (v == 1) {                                          // drum coil
                        double ir = Math.Max(w, d) / 2;
                        b.Cyl(ir * 0.78, topZ, new Point3d(cx, cy, 0), layer, col);
                        for (int i = 1; i <= 3; i++) b.Torus(ir * 0.82, 0.22, new Point3d(cx, cy, topZ * i / 4.0), LPart, CopTop);
                        w = d = ir * 1.3;
                    } else b.Box(w, d, topZ, new Point3d(cx, cy, topZ / 2), layer, col);
                    break;
                case Kind.Diode:                                           // SMD diode: dark body + silver cathode band
                    b.Box(w, d, topZ, new Point3d(cx, cy, topZ / 2), layer, col);
                    b.Box(w * 0.14, d * 1.02, topZ * 1.04, new Point3d(cx - w / 2 + w * 0.13, cy, topZ / 2), LPart, Terminal);
                    Terminals(b, cx, cy, w, d, topZ);
                    topZ += 0.22; ShrinkToBody(ref w, ref d);              // text above the caps/band, footprint on the body
                    break;
                case Kind.Led:                                             // metal base flange + colored dome
                {
                    double lr = Math.Max(w, d) / 2;
                    AcColor lens = LedLens[(int)(part.Idx % LedLens.Length)];
                    b.Cyl(lr, topZ * 0.26, new Point3d(cx, cy, 0), LPart, Terminal);
                    b.Cyl(lr * 0.8, topZ * 0.5, new Point3d(cx, cy, topZ * 0.26), layer, lens);
                    b.Sphere(lr * 0.8, new Point3d(cx, cy, topZ * 0.76), layer, lens);
                    topZ = topZ * 0.76 + lr * 0.8; w = d = lr * 2;
                    break;
                }
                case Kind.Transistor:                                      // TO-can: tapered body + 3 leads
                {
                    double tr = Math.Max(w, d) / 2;
                    b.Cone(tr, tr * 0.8, topZ, new Point3d(cx, cy, 0.3), layer, col);
                    for (int i = -1; i <= 1; i++)
                        b.Cyl(0.26, 1.4, new Point3d(cx + i * tr * 0.5, cy - tr * 0.55, -0.4), LPart, Terminal);
                    w = d = tr * 2;
                    topZ = topZ + 0.3;                                     // cone TOP (base sits at 0.3): text lands on the can, not inside it
                    break;
                }
                case Kind.Crystal:                                         // HC-49 metal can + 2 leads
                    b.Box(w, d, topZ, new Point3d(cx, cy, topZ / 2), layer, col);
                    b.Box(w * 0.88, d * 0.7, topZ * 0.16, new Point3d(cx, cy, topZ), LPart, CanSilver);
                    b.Cyl(0.3, 1.4, new Point3d(cx - w / 2 + 1.2, cy, -0.4), LPart, Terminal);
                    b.Cyl(0.3, 1.4, new Point3d(cx + w / 2 - 1.2, cy, -0.4), LPart, Terminal);
                    topZ = topZ * 1.08;                                    // top of the can ridge
                    break;
                case Kind.Connector:                                       // pin header: plastic base + gold pins
                {
                    b.Box(w, d, topZ * 0.55, new Point3d(cx, cy, topZ * 0.275), layer, col);
                    faceH = topZ * 0.55;   // the black base: data block goes on ITS side, clear of the pins
                    int np = Math.Max(2, Math.Min(14, (int)(w / 2.6)));
                    for (int i = 0; i < np; i++) {
                        double px = cx - w / 2 + 1.3 + i * (w - 2.6) / Math.Max(1, np - 1);
                        b.Box(1.0, d * 0.7, topZ * 0.55, new Point3d(px, cy, topZ * 0.275), LPart, Pin1);   // shroud rib
                        b.Cyl(0.24, topZ, new Point3d(px, cy, topZ * 0.4), LPart, CopTop);                  // gold pin
                    }
                    topZ = topZ * 1.4 + 0.05;                              // above the pin tips: text floats clear of the pins
                    break;
                }
                case Kind.Relay:                                           // sealed relay can + base seam + top mark
                    b.Box(w, d, topZ, new Point3d(cx, cy, topZ / 2), layer, col);
                    b.Box(w * 1.03, d * 1.03, topZ * 0.08, new Point3d(cx, cy, topZ * 0.1), LPart, Pin1);
                    b.Box(w * 0.66, d * 0.44, 0.06, new Point3d(cx, cy, topZ + 0.03), LSilk, Silk);
                    topZ += 0.1;                                           // above the top mark
                    break;
                case Kind.Dip:                                             // DIP IC: body + notch + pin-1 + 2 lead rows
                {
                    b.Box(w, d, topZ, new Point3d(cx, cy, topZ / 2), layer, col);
                    b.Cyl(d * 0.16, topZ * 0.6, new Point3d(cx - w / 2 + 0.1, cy, topZ * 0.7), LIc, Pin1);   // pin-1 notch
                    b.Cyl(0.4, 0.25, new Point3d(cx - w / 2 + 1.6, cy - d / 2 + 1.6, topZ), LIc, Pin1);      // pin-1 dot
                    int ndp = Math.Max(3, Math.Min(10, (int)(w / 2.54)));
                    for (int i = 0; i < ndp; i++) {
                        double px = cx - w / 2 + 1.27 + i * (w - 2.54) / Math.Max(1, ndp - 1);
                        b.Box(0.7, 1.0, 0.25, new Point3d(px, cy + d / 2 + 0.5, 0.25), LPart, Terminal);
                        b.Box(0.7, 1.0, 0.25, new Point3d(px, cy - d / 2 - 0.5, 0.25), LPart, Terminal);
                    }
                    break;
                }
                case Kind.Pot:                                             // potentiometer: round body + metal shaft
                {
                    double pr = Math.Max(w, d) / 2;
                    b.Cyl(pr, topZ * 0.6, new Point3d(cx, cy, 0), layer, col);
                    b.Cyl(pr * 0.34, topZ * 0.55, new Point3d(cx, cy, topZ * 0.6), LPart, Terminal);
                    b.Box(pr * 0.5, 0.22, topZ * 0.2, new Point3d(cx, cy, topZ * 0.6 + topZ * 0.45), LPart, Pin1);  // slotted top
                    w = d = pr * 2;
                    topZ = topZ * 1.15 + 0.05;                             // above the shaft tip: text never inside the shaft
                    break;
                }
                case Kind.Shield:                                          // HATCH: perforated EMI can
                {
                    b.Box(w, d, topZ, new Point3d(cx, cy, topZ / 2), LPart, ShieldTin);
                    b.Box(w * 1.06, d * 1.06, 0.25, new Point3d(cx, cy, 0.12), LPart, Terminal);   // solder skirt
                    int hx2 = Math.Max(2, Math.Min(6, (int)(w / 2.2))), hy2 = Math.Max(2, Math.Min(6, (int)(d / 2.2)));
                    for (int i = 0; i < hx2; i++)
                        for (int q2 = 0; q2 < hy2; q2++)
                            b.Cyl(0.26, 0.14, new Point3d(cx - w / 2 + (i + 0.5) * w / hx2,
                                                          cy - d / 2 + (q2 + 0.5) * d / hy2, topZ - 0.06), LPart, Pin1);
                    break;
                }
                case Kind.Sensor:                                          // DIMENSION: sensor + port
                    b.Box(w, d, topZ * 0.62, new Point3d(cx, cy, topZ * 0.31), layer, col);
                    b.Box(w * 0.84, d * 0.84, topZ * 0.3, new Point3d(cx, cy, topZ * 0.77), LPart, CanSilver);
                    b.Cyl(Math.Min(w, d) * 0.13, topZ * 0.3, new Point3d(cx, cy, topZ * 0.8), LIc, Pin1);   // port hole
                    Terminals(b, cx, cy, w, d, topZ * 0.62);
                    topZ = topZ * 0.92;
                    break;
                case Kind.Coil:                                            // SPLINE: air-core wound
                {
                    double kr = Math.Max(w, d) / 2;
                    b.Cyl(kr * 0.32, topZ, new Point3d(cx, cy, 0), LPart, Plastic);          // bobbin
                    for (int i = 0; i < 5; i++)
                        b.Torus(kr * 0.7, kr * 0.15, new Point3d(cx, cy, topZ * (0.16 + 0.17 * i)), LPart, CopTop);
                    b.Cyl(0.26, 1.3, new Point3d(cx - kr * 0.7, cy, -0.4), LPart, Terminal);
                    b.Cyl(0.26, 1.3, new Point3d(cx + kr * 0.7, cy, -0.4), LPart, Terminal);
                    b.Cyl(kr * 0.95, topZ * 0.09, new Point3d(cx, cy, topZ), LPart, Plastic);   // top flange
                    topZ = topZ * 1.09;                     // flat flange face: the block sits ON it
                    w = d = kr * 1.34;                      // and is sized to the flange, not the windings
                    break;
                }
                case Kind.Antenna:                                         // LEADER: chip antenna + whip
                {
                    b.Box(w * 0.46, d, topZ, new Point3d(cx - w * 0.27, cy, topZ / 2), layer, col);
                    double seg = w * 0.54 / 4;
                    for (int i = 0; i < 4; i++)                            // meander whip, copper on the face
                    {
                        double mx = cx - w * 0.04 + i * seg;
                        b.Box(0.16, d * 0.86, 0.1, new Point3d(mx, cy, 0.05), LNet, CopTop);
                        b.Box(seg, 0.16, 0.1, new Point3d(mx + seg / 2, cy + (i % 2 == 0 ? d * 0.43 : -d * 0.43), 0.05), LNet, CopTop);
                    }
                    b.Box(0.9, 0.9, 0.2, new Point3d(cx - w * 0.5, cy, 0.1), LPart, Pad);     // feed pad
                    cx = cx - w * 0.27; w = w * 0.46;        // the block prints ON the ceramic, not over the whip
                    break;
                }
                case Kind.Memory:                                          // ACAD_TABLE: DIMM edge card
                {
                    double card = 0.5;
                    b.Box(w, card, topZ, new Point3d(cx, cy, topZ / 2), LIc, Board);          // the card, edge-on
                    int nd = Math.Max(2, Math.Min(8, (int)(w / 3.6)));
                    for (int i = 0; i < nd; i++)
                        b.Box(w / nd * 0.68, card + 0.9, topZ * 0.28,
                              new Point3d(cx - w / 2 + (i + 0.5) * w / nd, cy, topZ * 0.6), LIc, IcBody);
                    int nfg = nd * 3;
                    for (int i = 0; i < nfg; i++)                          // gold edge fingers
                        b.Box(w / nfg * 0.5, card + 0.16, 0.5,
                              new Point3d(cx - w / 2 + (i + 0.5) * w / nfg, cy, 0.25), LPart, CopTop);
                    d = card + 0.9;
                    faceH = topZ * 0.9;   // a card is too thin to print on top: the block goes on its face
                    break;
                }
                case Kind.Fuse:                                            // ATTDEF: cartridge in clips
                    b.Box(w * 0.62, d * 0.72, topZ * 0.64, new Point3d(cx, cy, topZ * 0.5), LPart, FuseGl);
                    b.Box(w * 0.15, d * 0.92, topZ * 0.8, new Point3d(cx - w * 0.42, cy, topZ * 0.44), LPart, Terminal);
                    b.Box(w * 0.15, d * 0.92, topZ * 0.8, new Point3d(cx + w * 0.42, cy, topZ * 0.44), LPart, Terminal);
                    b.Box(w * 0.56, 0.14, 0.1, new Point3d(cx, cy, topZ * 0.5), LPart, Pin1);   // the element
                    topZ = topZ * 0.82; w = w * 0.62; d = d * 0.72;   // land on the cartridge, not above it
                    break;
                case Kind.Heatsink:                                        // WIPEOUT: finned block
                {
                    double bh = topZ * 0.2;
                    b.Box(w, d, bh, new Point3d(cx, cy, bh / 2), LPart, HeatAl);
                    int nf = Math.Max(3, Math.Min(9, (int)(w / 1.3)));
                    for (int i = 0; i < nf; i++)
                        b.Box(w / nf * 0.42, d, topZ - bh,
                              new Point3d(cx - w / 2 + (i + 0.5) * w / nf, cy, bh + (topZ - bh) / 2), LPart, HeatAl);
                    break;
                }
                case Kind.Ribbon:                                          // MLINE: shrouded IDC header
                {
                    b.Box(w, d, topZ, new Point3d(cx, cy, topZ / 2), layer, Plastic);
                    b.Box(w * 0.84, d * 0.46, topZ * 0.72, new Point3d(cx, cy, topZ * 0.66), LIc, Pin1);   // cavity
                    faceH = topZ * 0.9;   // pins fill the top -> the data block prints on the side
                    int nr = Math.Max(4, Math.Min(16, (int)(w / 1.0)));
                    for (int i = 0; i < nr; i++)
                    {
                        double px = cx - w * 0.38 + i * (w * 0.76) / Math.Max(1, nr - 1);
                        b.Box(0.32, 0.32, topZ * 0.5, new Point3d(px, cy + d * 0.11, topZ * 0.45), LPart, CopTop);
                        b.Box(0.32, 0.32, topZ * 0.5, new Point3d(px, cy - d * 0.11, topZ * 0.45), LPart, CopTop);
                    }
                    b.Box(w * 0.18, 0.3, topZ * 0.16, new Point3d(cx, cy + d / 2, topZ), LPart, Plastic);  // polarising key
                    break;
                }
                case Kind.Display:                                         // 3DFACE: glass module + FPC
                    b.Box(w, d, topZ * 0.36, new Point3d(cx, cy, topZ * 0.18), LIc, IcBody);          // carrier
                    b.Box(w * 0.92, d * 0.88, topZ * 0.5, new Point3d(cx, cy, topZ * 0.61), LPart, Glass);
                    b.Box(w * 0.78, d * 0.7, 0.06, new Point3d(cx, cy, topZ * 0.87), LSilk, Capacitor); // active area
                    b.Box(w * 0.3, d * 0.18, 0.12, new Point3d(cx, cy - d / 2 - d * 0.09, 0.06), LPart, CopTop);  // FPC tail
                    topZ = topZ * 0.86; w = w * 0.92; d = d * 0.88;   // the block prints on the glass
                    break;
                case Kind.Rail:                                            // XLINE / RAY: bus bar
                    b.Box(w, d, topZ * 0.3, new Point3d(cx, cy, topZ * 0.72), LPart, CopTop);
                    b.Cyl(d * 0.32, topZ * 0.6, new Point3d(cx - w * 0.36, cy, 0), LPart, Terminal);   // standoffs
                    b.Cyl(d * 0.32, topZ * 0.6, new Point3d(cx + w * 0.36, cy, 0), LPart, Terminal);
                    b.Cyl(d * 0.15, 0.2, new Point3d(cx - w * 0.36, cy, topZ * 0.87), LPart, Pin1);    // bolt heads
                    b.Cyl(d * 0.15, 0.2, new Point3d(cx + w * 0.36, cy, topZ * 0.87), LPart, Pin1);
                    topZ = topZ * 0.87;                      // the bar's top face, between the bolt heads
                    break;
                default:
                    b.Box(w, d, topZ, new Point3d(cx, cy, topZ / 2), layer, col);
                    break;
            }
            double om = (part.Kind == Kind.Table || part.Kind == Kind.Nod) ? 3.4 : 2.0;  // leaded pkgs: wider courtyard
            b.Outline(part.Cx, part.Cy, part.Vw + om, part.Vd + om, 0.04, LSilk, Silk, 0.22);    // silkscreen
            bool zoneNamed = part.Kind == Kind.Table || part.Kind == Kind.Nod;   // the zone label already names it
            if (!string.IsNullOrEmpty(part.RefDes) && !zoneNamed)
            {
                double rh = isChip ? 1.9 : Math.Max(1.0, Math.Min(1.9, part.Vw * 0.3));
                RefDes(b, part.RefDes, part.Cx, part.Cy - part.Vd / 2 - 1.0 - rh, rh);
            }
            // part data prints ON the hardware body itself (auto-fit to the part), entities
            // included; the binary/katakana rain still rises from the part.
            if (faceH > 0) PodFace(b, part.Title, part.Rows, cx, cy, w * 0.9, d, faceH, topZ, part.Idx);
            else Pod(b, part.Title, part.Rows, cx, cy, w * 0.9, d * 0.86, topZ, part.Kind == Kind.Opaque, part.Idx);
        }

        /// <summary>Four silver hold-down pads at a chip's corners (mechanical, not signal).</summary>
        private static void FourPads(Pcb b, double cx, double cy, double w, double d)
        {
            double hx = w / 2, hy = d / 2;
            b.Box(2.4, 2.4, 0.35, new Point3d(cx - hx, cy - hy, 0.18), LPart, Terminal);
            b.Box(2.4, 2.4, 0.35, new Point3d(cx + hx, cy - hy, 0.18), LPart, Terminal);
            b.Box(2.4, 2.4, 0.35, new Point3d(cx - hx, cy + hy, 0.18), LPart, Terminal);
            b.Box(2.4, 2.4, 0.35, new Point3d(cx + hx, cy + hy, 0.18), LPart, Terminal);
        }

        /// <summary>Grow any chip whose perimeter cannot hold one pad per OWNERSHIP connection (plus one shared
        /// pad per reference net) at PinRowPitch. Uniform growth keeps the package proportions.</summary>
        private static void SizeByPins(List<Part> parts, List<Edge> edges)
        {
            var own = new int[parts.Count]; var refs = new HashSet<(int, ECls)>();
            foreach (var e in edges) { if (e.Cls == ECls.Own) own[e.B]++; else refs.Add((e.B, e.Cls)); }
            var nref = new int[parts.Count]; foreach (var k in refs) nref[k.Item1]++;
            foreach (var p in parts)
            {
                int n = own[p.Idx] + nref[p.Idx]; if (n == 0) continue;
                double ko = KeepOut(p) + 0.35, need = n * PinRowPitch + 4.0, have = 2 * ((p.Vw + 2 * ko) + (p.Vd + 2 * ko));
                if (need <= have) continue;
                double f = (need - 8 * ko) / (2 * (p.Vw + p.Vd));   // exact: new perimeter (incl. keep-out ring) == need
                if (f > 1) { p.Vw *= f; p.Vd *= f; }
            }
        }

        /// <summary>Top-layer keep-out half-margin beyond a part's body: its COURTYARD (the silkscreen ring),
        /// which also encloses the corner hold-down pads (they reach 1.2 past the body) and gull-wing leads.
        /// Copper routes OUTSIDE courtyards, as on a real board (measured defect: traces over corner pads).</summary>
        private static double KeepOut(Part p)
        {
            bool chip = p.Kind == Kind.Die || p.Kind == Kind.Table || p.Kind == Kind.Nod
                     || p.Kind == Kind.Record || p.Kind == Kind.Gpu;
            double om = (p.Kind == Kind.Table || p.Kind == Kind.Nod) ? 3.4 : 2.0;
            return chip ? Math.Max(om / 2, 1.2) : om / 2;
        }
        /// <summary>The refdes silkscreen label rectangle {x0,y0,x1,y1} below the part (same geometry PlacePart
        /// draws), or null when the part carries no label. Copper stays clear of it (silk keep-out).</summary>
        private static double[] LabelBox(Part p)
        {
            bool chip = p.Kind == Kind.Die || p.Kind == Kind.Table || p.Kind == Kind.Nod
                     || p.Kind == Kind.Record || p.Kind == Kind.Gpu;
            bool zoneNamed = p.Kind == Kind.Table || p.Kind == Kind.Nod;
            if (string.IsNullOrEmpty(p.RefDes) || zoneNamed) return null;
            double rh = chip ? 1.9 : Math.Max(1.0, Math.Min(1.9, p.Vw * 0.3));
            double yc = p.Cy - p.Vd / 2 - 1.0 - rh, hw = p.RefDes.Length * CharW * rh / 2 + 0.3, hh = rh / 2 + 0.3;
            return new[] { p.Cx - hw, yc - hh, p.Cx + hw, yc + hh };
        }

        /// <summary>Perimeter point on part <paramref name="p"/> facing (tx,ty) — where a trace lands.</summary>
        private static Point3d PadPos(Part p, double tx, double ty)
        {
            double dx = tx - p.Cx, dy = ty - p.Cy;
            if (Math.Abs(dx) < 1e-6 && Math.Abs(dy) < 1e-6) return new Point3d(p.Cx, p.Cy, 0);
            double ko = KeepOut(p) + 0.35;                        // pad centre just outside the courtyard keep-out: its grid cell
            double hw = p.Vw / 2 + ko, hd = p.Vd / 2 + ko;       // (was: 0.6 body margin + 1.3) -- the old comment lines below still apply:
                                                               // is routable; the 1.4 land spans back to the body edge, so the trace visibly
                                                               // meets the part (vias stay off hardware via viaOk, not via this offset)
            double tX = Math.Abs(dx) > 1e-6 ? hw / Math.Abs(dx) : 1e9;
            double tY = Math.Abs(dy) > 1e-6 ? hd / Math.Abs(dy) : 1e9;
            double t = Math.Min(tX, tY);
            return new Point3d(p.Cx + dx * t, p.Cy + dy * t, 0);
        }

        /// <summary>Silver gull-wing lead stubs hugging the package body — SOIC (2 sides)
        /// or QFP (all 4). Part of the PACKAGE, not board pads (those stay 4 per chip).</summary>
        private static void Leads(Pcb b, double cx, double cy, double w, double d, bool quad)
        {
            int nx = Math.Max(4, (int)(w / 3.2));
            for (int i = 0; i < nx; i++)
            {
                double px = cx - w / 2 + 1.6 + i * (w - 3.2) / Math.Max(1, nx - 1);
                b.Box(0.8, 1.2, 0.25, new Point3d(px, cy + d / 2 + 0.55, 0.3), LPart, Terminal);
                b.Box(0.8, 1.2, 0.25, new Point3d(px, cy - d / 2 - 0.55, 0.3), LPart, Terminal);
            }
            if (!quad) return;
            int ny = Math.Max(4, (int)(d / 3.2));
            for (int i = 0; i < ny; i++)
            {
                double py = cy - d / 2 + 1.6 + i * (d - 3.2) / Math.Max(1, ny - 1);
                b.Box(1.2, 0.8, 0.25, new Point3d(cx - w / 2 - 0.55, py, 0.3), LPart, Terminal);
                b.Box(1.2, 0.8, 0.25, new Point3d(cx + w / 2 + 0.55, py, 0.3), LPart, Terminal);
            }
        }

        /// <summary>Silver end terminals on a part's longer axis (SMD-style).</summary>
        private static void Terminals(Pcb b, double cx, double cy, double w, double d, double topZ)
        {
            if (w >= d)
            {
                double e = w * 0.16 + 0.5;
                b.Box(e, d + 0.6, topZ + 0.2, new Point3d(cx - w / 2 + e / 2, cy, (topZ + 0.2) / 2), LPart, Terminal);
                b.Box(e, d + 0.6, topZ + 0.2, new Point3d(cx + w / 2 - e / 2, cy, (topZ + 0.2) / 2), LPart, Terminal);
            }
            else
            {
                double e = d * 0.16 + 0.5;
                b.Box(w + 0.6, e, topZ + 0.2, new Point3d(cx, cy - d / 2 + e / 2, (topZ + 0.2) / 2), LPart, Terminal);
                b.Box(w + 0.6, e, topZ + 0.2, new Point3d(cx, cy + d / 2 - e / 2, (topZ + 0.2) / 2), LPart, Terminal);
            }
        }

        /// <summary>Shrink a part's text footprint to the body BETWEEN its end terminals (the same
        /// end-cap width Terminals uses), so the part-data text never overruns onto the caps.</summary>
        private static void ShrinkToBody(ref double w, ref double d)
        {
            if (w >= d) { double e = w * 0.16 + 0.5; w = Math.Max(w * 0.4, w - 2 * e - 0.3); }
            else        { double e = d * 0.16 + 0.5; d = Math.Max(d * 0.4, d - 2 * e - 0.3); }
        }

        // ---- copper router: class-colored L-trace; ownership on top, refs on bottom ----
        private static void Route(Pcb b, double x0, double y0, double x1, double y1, ECls cls, int ia, int ib, bool bottom = false)
        {
            AcColor col = EdgeColor(cls);
            // one copper plane per layer; a tiny per-trace offset only to avoid z-fighting.
            double z = bottom ? (-BT - 0.09) : 0.085 + ((ia * 7 + ib) % 5) * 0.004;
            double dx = x1 - x0, dy = y1 - y0;
            if (Math.Abs(dx) < 0.3 || Math.Abs(dy) < 0.3) { Seg(b, x0, y0, x1, y1, z, col); return; }
            // clean single-corner orthogonal route with a 45deg mitre
            double c = Math.Min(2.0, Math.Min(Math.Abs(dx), Math.Abs(dy)) * 0.5);
            bool hFirst = ((ia * 31 + ib) & 1) == 0;
            if (hFirst)
            {
                double xk = x1 - Math.Sign(dx) * c;
                Seg(b, x0, y0, xk, y0, z, col);
                Seg(b, xk, y0, x1, y0 + Math.Sign(dy) * c, z, col);
                Seg(b, x1, y0 + Math.Sign(dy) * c, x1, y1, z, col);
            }
            else
            {
                double yk = y1 - Math.Sign(dy) * c;
                Seg(b, x0, y0, x0, yk, z, col);
                Seg(b, x0, yk, x0 + Math.Sign(dx) * c, y1, z, col);
                Seg(b, x0 + Math.Sign(dx) * c, y1, x1, y1, z, col);
            }
        }

        /// <summary>Draw a routed path as ONE continuous copper polyline. Corners are cut MAXIMALLY
        /// (up to half the shorter leg) so orthogonal grid paths render as the long 45deg diagonals
        /// that dominate real routing (measured: 45deg is the most common segment angle).</summary>
        private static void DrawTrace(Pcb b, List<double[]> wp, double z, AcColor col, double tw = 0.3)
        {
            // draw the clean octilinear polyline AS-IS (no chamfer -> no arbitrary micro-segments);
            // collapse consecutive duplicate points so Trace gets a valid vertex list.
            var pts = new List<Point2d>(wp.Count);
            foreach (var p in wp)
            {
                var q = new Point2d(p[0], p[1]);
                if (pts.Count == 0 || Math.Abs(pts[pts.Count - 1].X - q.X) > 1e-6 || Math.Abs(pts[pts.Count - 1].Y - q.Y) > 1e-6) pts.Add(q);
            }
            if (pts.Count >= 2) b.Trace(pts, tw, z, LNet, col);
        }

        private static void DrawChain(Pcb b, List<double[]> pts, double z, AcColor col)
        {
            const double c = 1.5;
            for (int i = 0; i < pts.Count - 1; i++)
            {
                double ax = pts[i][0], ay = pts[i][1], bx = pts[i + 1][0], by = pts[i + 1][1];
                double len = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
                if (len < 0.1) continue;
                double ux = (bx - ax) / len, uy = (by - ay) / len;
                double s0 = i > 0 ? c : 0, s1 = i < pts.Count - 2 ? c : 0;
                if (s0 + s1 >= len) { s0 = 0; s1 = 0; }
                Seg(b, ax + ux * s0, ay + uy * s0, bx - ux * s1, by - uy * s1, z, col);
                if (i < pts.Count - 2)
                {
                    double nx = pts[i + 2][0], ny = pts[i + 2][1];
                    double nl = Math.Sqrt((nx - bx) * (nx - bx) + (ny - by) * (ny - by));
                    if (nl > 0.1)
                    {
                        double vx = (nx - bx) / nl, vy = (ny - by) / nl;
                        Seg(b, bx - ux * c, by - uy * c, bx + vx * c, by + vy * c, z, col);  // 45deg miter
                    }
                }
            }
        }

        private static int Cl(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);

        /// <summary>A* on the routing grid, avoiding blocked (chip) cells; turn-penalised for straight runs.</summary>
        // 8-direction step set: orthogonal + 45deg diagonals (real traces bend at 45deg)
        private static readonly int[] D8x = { 1, -1, 0, 0, 1, 1, -1, -1 }, D8y = { 0, 0, 1, -1, 1, -1, 1, -1 };
        private static double StepCost(int k) => k < 4 ? 1.0 : 1.41421356;
        private static double TurnCost(int from, int k)
        {
            if (from < 0 || from == k) return 0;
            double dot = (D8x[from] * D8x[k] + D8y[from] * D8y[k])
                       / Math.Sqrt((double)(D8x[from] * D8x[from] + D8y[from] * D8y[from]) * (D8x[k] * D8x[k] + D8y[k] * D8y[k]));
            return dot > 0.7 ? 3.0 : dot > -0.1 ? 7.0 : 14.0;  // HIGH turn cost -> long straight/45 runs, few corners
        }
        private static bool CornerCut(int x, int y, int k, bool[,] blk) => k >= 4 && (blk[x + D8x[k], y] || blk[x, y + D8y[k]]);

        private static List<(int, int)> AStar(int sx, int sy, int gx, int gy, bool[,] blk, int[,] used, int gw, int gh)
        {
            double H(int x, int y) { int ax = Math.Abs(x - gx), ay = Math.Abs(y - gy); return Math.Max(ax, ay) + 0.4142 * Math.Min(ax, ay); }
            var g = new Dictionary<(int, int, int), double>();
            var prev = new Dictionary<(int, int, int), (int, int, int)>();
            var pq = new PriorityQueue<(int x, int y, int d), double>();
            (int, int, int) start = (sx, sy, -1);
            g[start] = 0;
            pq.Enqueue((sx, sy, -1), H(sx, sy));
            (int, int, int) goal = (-9, -9, -9);
            while (pq.Count > 0)
            {
                var cur = pq.Dequeue();
                if (cur.x == gx && cur.y == gy) { goal = (cur.x, cur.y, cur.d); break; }
                if (!g.TryGetValue((cur.x, cur.y, cur.d), out double cg)) continue;
                for (int k = 0; k < 8; k++)
                {
                    int nx = cur.x + D8x[k], ny = cur.y + D8y[k];
                    if (nx < 0 || ny < 0 || nx >= gw || ny >= gh) continue;
                    if (blk[nx, ny] && !(nx == gx && ny == gy)) continue;
                    if (CornerCut(cur.x, cur.y, k, blk)) continue;
                    double nc = cg + StepCost(k) + TurnCost(cur.d, k) + used[nx, ny] * 4.0;
                    var ns = (nx, ny, k);
                    if (!g.TryGetValue(ns, out double og) || nc < og)
                    { g[ns] = nc; prev[ns] = (cur.x, cur.y, cur.d); pq.Enqueue((nx, ny, k), nc + H(nx, ny)); }
                }
            }
            if (goal.Item1 == -9) return null;
            var path = new List<(int, int)>();
            var s = goal;
            while (!(s.Item1 == sx && s.Item2 == sy && s.Item3 == -1))
            {
                path.Add((s.Item1, s.Item2));
                if (!prev.TryGetValue(s, out s)) return null;
            }
            path.Add((sx, sy));
            path.Reverse();
            return path;
        }

        /// <summary>Multi-goal search: from a cell to the NEAREST cell of an existing net (Dijkstra,
        /// turn-penalised). This is how a later source merges into a net already on the board.</summary>
        private static List<(int, int)> AStarToSet(int sx, int sy, HashSet<(int, int)> goals,
                                                   bool[,] blk, int[,] used, int gw, int gh)
        {
            if (goals.Count == 0) return null;
            var g = new Dictionary<(int, int, int), double>();
            var prev = new Dictionary<(int, int, int), (int, int, int)>();
            var pq = new PriorityQueue<(int x, int y, int d), double>();
            g[(sx, sy, -1)] = 0; pq.Enqueue((sx, sy, -1), 0);
            (int, int, int) goal = (-9, -9, -9);
            while (pq.Count > 0)
            {
                var cur = pq.Dequeue();
                if (goals.Contains((cur.x, cur.y)) && !(cur.x == sx && cur.y == sy)) { goal = (cur.x, cur.y, cur.d); break; }
                if (!g.TryGetValue((cur.x, cur.y, cur.d), out double cg)) continue;
                for (int k = 0; k < 8; k++)
                {
                    int nx = cur.x + D8x[k], ny = cur.y + D8y[k];
                    if (nx < 0 || ny < 0 || nx >= gw || ny >= gh) continue;
                    bool isGoal = goals.Contains((nx, ny));
                    if (blk[nx, ny] && !isGoal) continue;
                    if (CornerCut(cur.x, cur.y, k, blk)) continue;
                    double nc = cg + StepCost(k) + TurnCost(cur.d, k) + used[nx, ny] * 4.0;
                    var ns = (nx, ny, k);
                    if (!g.TryGetValue(ns, out double og) || nc < og)
                    { g[ns] = nc; prev[ns] = (cur.x, cur.y, cur.d); pq.Enqueue((nx, ny, k), nc); }
                }
            }
            if (goal.Item1 == -9) return null;
            var path = new List<(int, int)>();
            var s = goal;
            while (!(s.Item1 == sx && s.Item2 == sy && s.Item3 == -1))
            {
                path.Add((s.Item1, s.Item2));
                if (!prev.TryGetValue(s, out s)) return null;
            }
            path.Add((sx, sy));
            path.Reverse();
            return path;
        }

        // ======================================================================
        //  DSN EXPORT (study only): write the real board as a Specctra .dsn so a
        //  professional autorouter (Freerouting) can route it and I can LEARN the
        //  patterns. NOT a runtime dependency -- PCD's own router is the shipped code.
        // ======================================================================
        private const int FRScale = 1000;   // 1 PCD unit -> 1000 um (1 mm)
        private static void WriteDsn(List<Part> parts, List<Edge> edges, List<Point2d> poly, string path)
        {
            int S(double v) => (int)Math.Round(v * FRScale);
            // incident edges per part, each becomes one perimeter pin
            var inc = new Dictionary<int, List<int>>();
            void Add(int p, int e) { if (!inc.TryGetValue(p, out var l)) { l = new List<int>(); inc[p] = l; } l.Add(e); }
            for (int ei = 0; ei < edges.Count; ei++) { Add(edges[ei].A, ei); Add(edges[ei].B, ei); }

            // pin number within each part's image, and local offset (pins on a ring, ordered by
            // the direction to the connected part so they roughly face their target)
            var pinNo = new Dictionary<(int part, int edge), int>();
            var pinOff = new Dictionary<(int part, int edge), (double x, double y)>();
            foreach (var kv in inc)
            {
                int p = kv.Key; var eids = kv.Value;
                eids.Sort((a, bb) =>
                {
                    Part oa = parts[edges[a].A == p ? edges[a].B : edges[a].A];
                    Part ob = parts[edges[bb].A == p ? edges[bb].B : edges[bb].A];
                    return Math.Atan2(oa.Cy - parts[p].Cy, oa.Cx - parts[p].Cx)
                        .CompareTo(Math.Atan2(ob.Cy - parts[p].Cy, ob.Cx - parts[p].Cx));
                });
                double r = Math.Max(parts[p].Vw, parts[p].Vd) / 2 + 1.0;
                for (int k = 0; k < eids.Count; k++)
                {
                    double ang = 2 * Math.PI * k / eids.Count;
                    pinNo[(p, eids[k])] = k + 1;
                    pinOff[(p, eids[k])] = (r * Math.Cos(ang), r * Math.Sin(ang));
                }
            }

            var sb = new StringBuilder();
            sb.Append("(pcb pcd\n");   // Specctra root token MUST be 'pcb'
            sb.Append("  (parser (space_in_quoted_tokens on) (host_cad \"PCD\") (host_version \"1\"))\n");
            sb.Append("  (resolution um 1)\n  (unit um)\n");
            sb.Append("  (structure\n    (layer F.Cu (type signal))\n    (layer B.Cu (type signal))\n");
            sb.Append("    (boundary (polygon pcb 0");
            foreach (var v in poly) sb.Append(" " + S(v.X) + " " + S(v.Y));
            sb.Append(" " + S(poly[0].X) + " " + S(poly[0].Y) + "))\n");
            sb.Append("    (via \"Via\")\n    (rule (width 300) (clearance 250))\n  )\n");

            sb.Append("  (placement\n");
            foreach (var kv in inc)
                sb.Append("    (component IMG" + kv.Key + " (place C" + kv.Key + " " + S(parts[kv.Key].Cx) + " " + S(parts[kv.Key].Cy) + " front 0))\n");
            sb.Append("  )\n");

            sb.Append("  (library\n");
            foreach (var kv in inc)
            {
                sb.Append("    (image IMG" + kv.Key + "\n");
                foreach (int e in kv.Value)
                { var o = pinOff[(kv.Key, e)]; sb.Append("      (pin Pad " + pinNo[(kv.Key, e)] + " " + S(o.x) + " " + S(o.y) + ")\n"); }
                sb.Append("    )\n");
            }
            sb.Append("    (padstack Pad (shape (circle F.Cu 500)) (shape (circle B.Cu 500)) (attach off))\n");
            sb.Append("    (padstack \"Via\" (shape (circle F.Cu 600)) (shape (circle B.Cu 600)) (attach off))\n");
            sb.Append("  )\n");

            sb.Append("  (network\n");
            for (int ei = 0; ei < edges.Count; ei++)
            {
                var e = edges[ei];
                if (!pinNo.ContainsKey((e.A, ei)) || !pinNo.ContainsKey((e.B, ei))) continue;
                sb.Append("    (net N" + ei + " (pins C" + e.A + "-" + pinNo[(e.A, ei)] + " C" + e.B + "-" + pinNo[(e.B, ei)] + "))\n");
            }
            sb.Append("  )\n  (wiring)\n)\n");

            File.WriteAllText(path, sb.ToString());
        }

        private static bool CrossesRect(double ax, double ay, double bx, double by,
                                        double rx, double ry, double hw, double hd)
        {
            for (int i = 1; i < 12; i++)
            {
                double t = i / 12.0, x = ax + (bx - ax) * t, y = ay + (by - ay) * t;
                if (Math.Abs(x - rx) < hw && Math.Abs(y - ry) < hd) return true;
            }
            return false;
        }

        /// <summary>A via that spans ONLY the two copper layers it joins (la, lb): a BLIND via when it
        /// reaches the top face (layer 0), a BURIED via when both ends are inner planes. The barrel stops
        /// at the deeper plane instead of drilling the whole board, and an annular pad sits on each end
        /// plane — so through the translucent substrate every via visibly lands on the trace it connects.
        /// small = a reference-hairline micro-via (proportional to the 0.15 trace, dim-colored).</summary>
        private static void DropVia(Pcb b, double x, double y, AcColor col, int la = 0, int lb = 2, bool small = false)
        {
            double za = Zof(la), zb = Zof(lb);
            double barBot = Math.Min(za, zb) - 0.05, barTop = Math.Max(za, zb) + 0.05;   // poke just past each pad
            double r = small ? 0.14 : 0.26;
            // Cyl's point is the BASE (it extrudes UP by height): pass barBot, NOT the center, so the
            // barrel actually spans barBot..barTop and meets the trace on each connected plane.
            b.Cyl(r, barTop - barBot, new Point3d(x, y, barBot), LVia, Via);   // plated barrel, connected layers only
            double padR = small ? 0.27 : 0.5, padT = small ? 0.07 : 0.12;
            b.Torus(padR, padT, new Point3d(x, y, za), LNet, col);     // annular pad on each connected plane
            b.Torus(padR, padT, new Point3d(x, y, zb), LNet, col);
        }

        private static void Seg(Pcb b, double ax, double ay, double bx, double by, double z, AcColor col)
        {
            double len = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
            if (len < 0.15) return;
            double ang = Math.Atan2(by - ay, bx - ax);
            b.Bar(len + 0.15, 0.42, 0.08, new Point3d((ax + bx) / 2, (ay + by) / 2, z), ang, LNet, col);
        }

        // ======================================================================
        //  shared render helpers
        // ======================================================================
        private static void Setup(Pcb b)
        {
            b.EnsureLayer(LBoard, Board); b.EnsureLayer(LSilk, Silk); b.EnsureLayer(LData, Data);
            b.EnsureLayer(LIc, IcBody); b.EnsureLayer(LPart, Silk); b.EnsureLayer(LVia, Via);
            b.EnsureLayer(LPad, Pad); b.EnsureLayer(LPlume, Plume); b.EnsureLayer(LNet, CopTop);
            b.EnsureLayer(LKata, KataRed);
            _mono = b.EnsureTextStyle("PCD-MONO", "monotxt.shx");
            _kata = b.EnsureCjkStyle("PCD-KATA-F", "MS Gothic", 128);
            b.SetLayerMaterial(LBoard, b.CreateMaterial("PCD-Mask", Board, 0.25, 0.06));   // OPAQUE substrate (user: board is never transparent)
            b.SetLayerTransparency(LBoard, 1.0);   // force fully opaque, overriding any prior transparency in the drawing
            ObjectId copper = b.CreateMaterial("PCD-Cu", CopTop, 0.9, 0.6);
            b.SetLayerMaterial(LPad, copper); b.SetLayerMaterial(LVia, copper);
            b.SetLayerMaterial(LNet, b.CreateMaterial("PCD-Net", CopTop, 0.85, 0.5, true));   // color from the entity (per-table nets)
            b.SetLayerMaterial(LIc, b.CreateMaterial("PCD-Plas", IcBody, 0.6, 0.12));
        }

        private static void BuildBoard(Pcb b, double w, double h)
        {
            Seed(0x9E3779B1u ^ (uint)(w * 7.0 + h * 13.0));      // stable, but varies with board size
            var board = new Solid3d(); board.CreateBox(w, h, BT);
            board.TransformBy(Matrix3d.Displacement(new Vector3d(w / 2, h / 2, -BT / 2)));

            void Cut(Solid3d s) { board.BooleanOperation(BooleanOperationType.BoolSubtract, s); s.Dispose(); }

            // chamfer the bottom-left corner (rotated box) -> a keyed / non-rectangular outline
            double cs = RR(9, 15);
            var ch = new Solid3d(); ch.CreateBox(cs, cs, BT + 2);
            ch.TransformBy(Matrix3d.Rotation(Math.PI / 4, Vector3d.ZAxis, Point3d.Origin));
            Cut(ch);
            // a notch on the top edge
            var nt = new Solid3d(); nt.CreateBox(RR(5, 9), RR(6, 11), BT + 2);
            nt.TransformBy(Matrix3d.Displacement(new Vector3d(w * RR(0.35, 0.62), h, -BT / 2)));
            Cut(nt);

            // holes: 3 corners (skip the chamfered BL) + a couple of extra, varied inset & radius
            var holes = new List<(double x, double y, double r)>
            {
                (w - RR(6, 9), RR(6, 9), RR(2.2, 2.8)),
                (RR(6, 9), h - RR(6, 9), RR(2.2, 2.8)),
                (w - RR(6, 9), h - RR(6, 9), RR(2.2, 2.8)),
            };
            int extra = 2 + (int)(Rnd() % 3);
            for (int i = 0; i < extra; i++)
                holes.Add((RR(0.18, 0.82) * w, (Rnd() % 2 == 0) ? RR(5, 8) : h - RR(5, 8), RR(1.6, 2.6)));

            foreach (var (hx, hy, r) in holes)
            {
                var drill = new Solid3d(); drill.CreateFrustum(BT + 2, r, r, r);
                drill.TransformBy(Matrix3d.Displacement(new Vector3d(hx, hy, -BT / 2)));
                Cut(drill);
            }
            b.Add(board, LBoard, Board);
            _holes.Clear(); _holes.AddRange(holes);   // the router clears copper around these
            foreach (var (hx, hy, r) in holes)
            {
                b.Torus(r + 0.9, 0.32, new Point3d(hx, hy, 0.075), LPad, Pad);
                b.Torus(r + 0.9, 0.32, new Point3d(hx, hy, -BT - 0.075), LPad, Pad);
            }
        }

        private static void RefDes(Pcb b, string tag, double x, double y, double h = 1.9) =>
            b.Text(tag, new Point3d(x, y, 0.02), h, LSilk, Silk, Up);

        private static void Pod(Pcb b, string title, Row[] rows, double cx, double cy, double w, double d,
                                double ztop, bool opaque = false, int seed = 0)
        {
            var lines = new string[rows.Length];
            for (int i = 0; i < rows.Length; i++)
                lines[i] = rows[i].Code.ToString().PadRight(3) + rows[i].Name.PadRight(11) + rows[i].Disp;
            FitBlock(b, title, lines, cx, cy, w, d, ztop + 0.02);
            if (opaque) { RainKata(b, cx, cy, ztop, seed); return; }   // opaque ACIS -> red static
            var cols = new List<string>();
            foreach (Row rr in rows) foreach (double v in rr.Reals) cols.Add(Bits64(v));
            Rain(b, cx, cy, ztop, cols);
        }

        /// <summary>Small-part pod: one readable type label on the part; the full entget
        /// pod lives in the PART DATA legend instead. The 64-bit rain stays here.</summary>
        private static void PodSmall(Pcb b, Part p, double cx, double cy, double w, double d, double ztop)
        {
            string ty = p.Title; int cut = ty.IndexOf(" :: ", StringComparison.Ordinal);
            if (cut > 0) ty = ty.Substring(0, cut);
            double h = Math.Min((w * 0.9 * Margin) / (Math.Max(2, ty.Length) * CharW), d * 0.55);
            b.Text(ty, new Point3d(cx, cy, ztop + 0.02), h, LSilk, Silk, Up);
            if (p.Kind == Kind.Opaque) { RainKata(b, cx, cy, ztop, p.Idx); return; }
            var cols = new List<string>();
            foreach (Row rr in p.Rows) foreach (double v in rr.Reals) cols.Add(Bits64(v));
            Rain(b, cx, cy, ztop, cols);
        }

        /// <summary>Red katakana "static" rising from an opaque-ACIS black box (== NXCYBER's).
        /// Same compact-tower bundle as the binary rain, but TALLER (3D solids get a bigger
        /// plume) and denser.</summary>
        private static void RainKata(Pcb b, double cx, double cy, double ztop, int seed, double tallMul = 2.2)
        {
            const int cols = 16, gx = 4;                         // more streams, wider bundle
            int gy = (cols + gx - 1) / gx;
            int tall = (int)(44 * tallMul);                      // taller for a 3D-solid chip
            double x0 = cx - (gx - 1) * KataColStep / 2.0, y0 = cy - (gy - 1) * KataColStep / 2.0;
            for (int c = 0; c < cols; c++)
            {
                double x = x0 + (c % gx) * KataColStep, y = y0 + (c / gx) * KataColStep;
                for (int k = 0; k < tall; k++)
                {
                    int idx = Math.Abs(seed * 131 + c * 37 + k * 17) % KataPool.Length;
                    b.Glyph(KataPool[idx].ToString(), new Point3d(x, y, ztop + 1.0 + k * KataStep),
                            BitH * 1.05, LKata, KataRed, FaceY, _kata);   // spaced vertically (KataStep)
                }
            }
        }

        /// <summary>Pod for a part whose TOP is obstructed (pin header): the entget block prints on the
        /// FRONT SIDE of the body, where pins cannot run through it. The rain still rises from the part.</summary>
        private static void PodFace(Pcb b, string title, Row[] rows, double cx, double cy, double w, double d,
                                    double bodyH, double ztop, int seed = 0)
        {
            var lines = new string[rows.Length];
            for (int i = 0; i < rows.Length; i++)
                lines[i] = rows[i].Code.ToString().PadRight(3) + rows[i].Name.PadRight(11) + rows[i].Disp;
            FitBlockFace(b, title, lines, cx, cy - d / 2 - 0.04, w, bodyH);
            var cols = new List<string>();
            foreach (Row rr in rows) foreach (double v in rr.Reals) cols.Add(Bits64(v));
            Rain(b, cx, cy, ztop, cols);
        }

        /// <summary>FitBlock laid on a vertical front face: the block is fitted to the face's width x height
        /// and centred on it, reading in +X with line spacing running down in -Z.</summary>
        private static void FitBlockFace(Pcb b, string header, string[] lines, double cx, double yFace, double w, double faceH)
        {
            int lmax = header.Length;
            foreach (var t in lines) if (t.Length > lmax) lmax = t.Length;
            int rows = lines.Length + 1;
            double h = Math.Min((w * Margin) / (lmax * CharW), (faceH * Margin) / (rows * Pitch));
            double headH = h * 1.15, gap = h * Pitch, blockH = headH + lines.Length * gap;
            double x = cx - (w * Margin) / 2.0, zHead = faceH / 2.0 + blockH / 2.0 - headH;
            b.TextLeftFace(header, new Point3d(x, yFace, zHead), headH, LSilk, Silk, _mono);
            for (int i = 0; i < lines.Length; i++)
                b.TextLeftFace(lines[i], new Point3d(x, yFace, zHead - gap * (i + 1)), h, LData, Data, _mono);
        }

        private static void FitBlock(Pcb b, string header, string[] lines, double cx, double cy, double w, double d, double z)
        {
            int lmax = header.Length;
            foreach (var s in lines) if (s.Length > lmax) lmax = s.Length;
            int rows = lines.Length + 1;
            double h = Math.Min((w * Margin) / (lmax * CharW), (d * Margin) / (rows * Pitch));
            double headH = h * 1.15, gap = h * Pitch, blockH = headH + lines.Length * gap;
            double x = cx - (w * Margin) / 2.0, yHead = cy + blockH / 2.0 - headH;
            b.TextLeft(header, new Point3d(x, yHead, z), headH, LSilk, Silk, _mono);
            for (int i = 0; i < lines.Length; i++)
                b.TextLeft(lines[i], new Point3d(x, yHead - gap * (i + 1), z), h, LData, Data, _mono);
        }

        // Plume = a NARROW, TALL data-cable rising straight up in +Z (matches Drawing1):
        // the value-columns are bundled into a compact square cross-section instead of a
        // wide 1-D row, so towers keep a tiny board footprint and don't collide.
        private const double PlumeStep = 0.55;   // tight column spacing in the bundle
        private static void Rain(Pcb b, double cx, double cy, double ztop, List<string> cols, double tallMul = 1.0)
        {
            int n = cols.Count; if (n == 0) return;
            int gx = (int)Math.Ceiling(Math.Sqrt(n));            // square-ish bundle footprint
            double x0 = cx - (gx - 1) * PlumeStep / 2.0;
            int gy = (int)Math.Ceiling((double)n / gx);
            double y0 = cy - (gy - 1) * PlumeStep / 2.0;
            double bs = BitStep * tallMul;
            for (int c = 0; c < n; c++)
            {
                double x = x0 + (c % gx) * PlumeStep, y = y0 + (c / gx) * PlumeStep;
                string bits = cols[c];
                for (int k = 0; k < bits.Length; k++)
                    b.Text(bits[k].ToString(), new Point3d(x, y, ztop + 1.0 + k * bs), BitH, LPlume, Plume, FaceY);
            }
        }

        // ======================================================================
        //  synthetic fallback (empty drawing)
        // ======================================================================
        private static void Synthetic(Pcb b)
        {
            var parts = new List<Part>
            {
                new Part { Idx=0, Kind=Kind.Die, Band="table", RefDes="DB", Col=DieBody, W=30, D=17, TopZ=3.6,
                    Cx=40, Cy=60, Title="DATABASE :: (demo)", Rows=new[]{ Txt(0,"root object","DATABASE"),
                        Txt(1,"detail","no source geometry"), Txt(2,"owns","9 tables + NOD") } },
                new Part { Idx=1, Kind=Kind.Resistor, Band="entity", RefDes="R1", Cx=40, Cy=20, Title="LINE :: 2A4",
                    Rows=new[]{ Txt(0,"entity type","LINE"), Txt(5,"handle","2A4"), Num(62,"color ACI",5),
                        Rl(48,"lt scale",1.0), Pt(10,"start pt",12.5,8,0), Pt(11,"end pt",48,33,0) } },
            };
            Vary(parts);
            BuildBoard(b, 90, 90);
            foreach (var p in parts) PlacePart(b, p);
            var pa = PadPos(parts[1], parts[0].Cx, parts[0].Cy);
            var pb = PadPos(parts[0], parts[1].Cx, parts[1].Cy);
            // flat copper polyline (same primitive as the real path -- no 3D-body traces anywhere)
            DrawTrace(b, new List<double[]> { new[] { pa.X, pa.Y }, new[] { pb.X, pa.Y }, new[] { pb.X, pb.Y } },
                      0.085, EdgeColor(ECls.Own));
        }
    }
}
