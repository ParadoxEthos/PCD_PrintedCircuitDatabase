using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Colors;

namespace PCD
{
    /// <summary>
    /// Builds the minimum drawing that lights up EVERY feature PCD renders, exactly once, leaving
    /// the drawing small enough that entities can keep being added to it by hand.
    ///
    /// This is the ObjectARX rewrite of what was first attempted as an AutoLISP script. The LISP
    /// version was abandoned for cause: (command ...) prompt sequences differ across hosts and can
    /// hang mid-prompt with no error, DIMSTYLE is read-only so setvar on it aborts a whole defun,
    /// and commands queued through SendCommand can sit in the command line without executing. None
    /// of that exists here — every object is constructed directly and appended, the result is
    /// deterministic, and failures are per-step and reported rather than inferred.
    ///
    /// PCD's relationship model, which this is designed against:
    ///   Own    every model-space entity -> *Model_Space (the GPU); record -> table; table -> DATABASE
    ///   Layer  every entity -> its LAYER record
    ///   Ltype  every entity -> its LTYPE record
    ///   Style  DBText / MText -> its STYLE record
    ///   Block  a BlockReference -> its BLOCK record
    ///   Dim    a Dimension -> its DIMSTYLE record
    ///   App    an entity carrying xdata -> the APPID that registered it
    /// A symbol table only shows traffic on the board if an entity actually points at one of its
    /// records, so entities are spread across layers, linetypes and styles deliberately.
    ///
    /// Entity sub-variety is picked by (abs handle) % N, so N of a type are appended BACK TO BACK:
    /// consecutive handles give consecutive residues, covering the whole family.
    ///   LINE %3 -> Diode/Resistor      CIRCLE %4 -> Led/Transistor/Pot/Capacitor
    ///   ARC  %3 -> Crystal/Inductor    ELLIPSE %2 -> Crystal/Capacitor
    ///   closed LWPOLYLINE %2 -> Dip/Connector      INSERT %3 -> Relay/Connector/Socket
    ///
    /// Never saves the drawing. The caller owns the transaction.
    /// </summary>
    public static class Template
    {
        private const string L1 = "T-STRUCT", L2 = "T-ANNO", L3 = "T-DETAIL", L4 = "T-SOLID", L5 = "T-MISC";
        private const string App1 = "PCD_TEST", App2 = "PCD_META";

        private static readonly List<string> _ok = new List<string>();
        private static readonly List<string> _fail = new List<string>();
        private static BlockTableRecord _ms;
        private static Transaction _tr;
        private static Database _db;

        /// <summary>Run one build step; a failure costs that step only and is reported, never guessed at.</summary>
        private static void Step(string tag, Action a)
        {
            try { a(); _ok.Add(tag); }
            catch (System.Exception ex) { _fail.Add(tag + " -- " + ex.GetType().Name + ": " + ex.Message); }
        }

        private static ObjectId Add(Entity e, string layer, string linetype = null)
        {
            e.Layer = layer;
            if (linetype != null) e.Linetype = linetype;
            ObjectId id = _ms.AppendEntity(e);
            _tr.AddNewlyCreatedDBObject(e, true);
            return id;
        }

        // ---- symbol table records ----------------------------------------------------------
        private static void EnsureLayer(string name, short aci)
        {
            var lt = (LayerTable)_tr.GetObject(_db.LayerTableId, OpenMode.ForRead);
            if (lt.Has(name)) return;
            lt.UpgradeOpen();
            var r = new LayerTableRecord { Name = name, Color = Color.FromColorIndex(ColorMethod.ByAci, aci) };
            lt.Add(r); _tr.AddNewlyCreatedDBObject(r, true);
        }

        private static void EnsureLinetype(string name)
        {
            var lt = (LinetypeTable)_tr.GetObject(_db.LinetypeTableId, OpenMode.ForRead);
            if (lt.Has(name)) return;
            _db.LoadLineTypeFile(name, "acad.lin");   // pulls the definition from the shipped library
        }

        private static void EnsureStyle(string name, string font)
        {
            var st = (TextStyleTable)_tr.GetObject(_db.TextStyleTableId, OpenMode.ForRead);
            if (st.Has(name)) return;
            st.UpgradeOpen();
            var r = new TextStyleTableRecord { Name = name, FileName = font, TextSize = 0.0, XScale = 1.0 };
            st.Add(r); _tr.AddNewlyCreatedDBObject(r, true);
        }

        private static ObjectId EnsureDimStyle(string name)
        {
            var dt = (DimStyleTable)_tr.GetObject(_db.DimStyleTableId, OpenMode.ForRead);
            if (dt.Has(name)) return dt[name];
            dt.UpgradeOpen();
            var r = new DimStyleTableRecord { Name = name };
            r.Dimscale = 1.0; r.Dimtxt = 2.0;
            ObjectId id = dt.Add(r); _tr.AddNewlyCreatedDBObject(r, true);
            return id;
        }

        private static void EnsureRegApp(string name)
        {
            var rt = (RegAppTable)_tr.GetObject(_db.RegAppTableId, OpenMode.ForRead);
            if (rt.Has(name)) return;
            rt.UpgradeOpen();
            var r = new RegAppTableRecord { Name = name };
            rt.Add(r); _tr.AddNewlyCreatedDBObject(r, true);
        }

        private static void EnsureUcs(string name, Vector3d xAxis, Vector3d yAxis)
        {
            var ut = (UcsTable)_tr.GetObject(_db.UcsTableId, OpenMode.ForRead);
            if (ut.Has(name)) return;
            ut.UpgradeOpen();
            var r = new UcsTableRecord { Name = name, Origin = Point3d.Origin, XAxis = xAxis, YAxis = yAxis };
            ut.Add(r); _tr.AddNewlyCreatedDBObject(r, true);
        }

        private static void EnsureView(string name, double h, double w, Point2d ctr)
        {
            var vt = (ViewTable)_tr.GetObject(_db.ViewTableId, OpenMode.ForRead);
            if (vt.Has(name)) return;
            vt.UpgradeOpen();
            var r = new ViewTableRecord { Name = name, Height = h, Width = w, CenterPoint = ctr };
            vt.Add(r); _tr.AddNewlyCreatedDBObject(r, true);
        }

        /// <summary>A block DEFINITION. PCD reads model space only, so the entities inside never
        /// become parts — the record earns its traffic from the BlockReferences that point at it.</summary>
        private static void EnsureBlock(string name, double r)
        {
            var bt = (BlockTable)_tr.GetObject(_db.BlockTableId, OpenMode.ForRead);
            if (bt.Has(name)) return;
            bt.UpgradeOpen();
            var btr = new BlockTableRecord { Name = name, Origin = Point3d.Origin };
            bt.Add(btr); _tr.AddNewlyCreatedDBObject(btr, true);
            var c = new Circle(Point3d.Origin, Vector3d.ZAxis, r);
            btr.AppendEntity(c); _tr.AddNewlyCreatedDBObject(c, true);
            var ln = new Line(Point3d.Origin, new Point3d(r, 0, 0));
            btr.AppendEntity(ln); _tr.AddNewlyCreatedDBObject(ln, true);
        }

        private static void Xdata(ObjectId id, string app, string text)
        {
            EnsureRegApp(app);
            var e = (Entity)_tr.GetObject(id, OpenMode.ForWrite);
            e.XData = new ResultBuffer(new TypedValue((int)DxfCode.ExtendedDataRegAppName, app),
                                       new TypedValue((int)DxfCode.ExtendedDataAsciiString, text));
        }

        // ---- entry points --------------------------------------------------------------------

        /// <summary>Erase every model-space entity so Build can run from a clean slate. Symbol table
        /// records are left alone — Build guards those and will not duplicate them.</summary>
        public static string Reset(Database db, Transaction tr)
        {
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
            int n = 0;
            foreach (ObjectId id in ms)
            {
                var e = tr.GetObject(id, OpenMode.ForWrite) as Entity;
                if (e == null) continue;
                e.Erase(); n++;
            }
            return "model space cleared: " + n + " entities erased";
        }

        public static string Build(Database db, Transaction tr)
        {
            _db = db; _tr = tr; _ok.Clear(); _fail.Clear();
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            _ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

            // ---- symbol table records ------------------------------------------------------
            Step("LAYERS", () => {
                EnsureLayer(L1, 1); EnsureLayer(L2, 3); EnsureLayer(L3, 4);
                EnsureLayer(L4, 5); EnsureLayer(L5, 6); });
            Step("LTYPES", () => {
                foreach (string n in new[] { "DASHED", "CENTER", "HIDDEN", "PHANTOM" }) EnsureLinetype(n); });
            Step("STYLES", () => { EnsureStyle("T-ROMANS", "romans.shx"); EnsureStyle("T-TXT", "txt.shx"); });
            ObjectId dimId = ObjectId.Null;
            Step("DIMSTYLE", () => { dimId = EnsureDimStyle("T-DIM"); });
            Step("APPIDS", () => { EnsureRegApp(App1); EnsureRegApp(App2); });
            Step("UCS", () => {
                EnsureUcs("T-UCS-PLAN", Vector3d.XAxis, Vector3d.YAxis);
                EnsureUcs("T-UCS-FRONT", Vector3d.XAxis, Vector3d.ZAxis); });
            Step("VIEWS", () => {
                EnsureView("T-VIEW-ALL", 140, 220, new Point2d(100, 60));
                EnsureView("T-VIEW-LEFT", 70, 110, new Point2d(50, 30)); });
            Step("BLOCKS", () => { EnsureBlock("T-BLK-A", 2.0); EnsureBlock("T-BLK-B", 3.0); EnsureBlock("T-BLK-C", 4.0); });

            // ---- entities: each family appended back to back for handle-residue coverage ----
            ObjectId circ1 = ObjectId.Null, line1 = ObjectId.Null;

            Step("LINE x3", () => {
                line1 = Add(new Line(new Point3d(0, 0, 0), new Point3d(20, 0, 0)), L1, "DASHED");
                Add(new Line(new Point3d(0, 6, 0), new Point3d(20, 6, 0)), L1, "CENTER");
                Add(new Line(new Point3d(0, 12, 0), new Point3d(20, 12, 0)), L3, "HIDDEN"); });

            Step("ARC x3", () => {
                Add(new Arc(new Point3d(30, 0, 0), 5, 0, Math.PI), L1, "PHANTOM");
                Add(new Arc(new Point3d(30, 12, 0), 5, 0, Math.PI), L3);
                Add(new Arc(new Point3d(30, 24, 0), 5, 0, Math.PI), L5); });

            Step("CIRCLE x4", () => {
                circ1 = Add(new Circle(new Point3d(50, 0, 0), Vector3d.ZAxis, 4), L1, "DASHED");
                Add(new Circle(new Point3d(50, 12, 0), Vector3d.ZAxis, 4), L3);
                Add(new Circle(new Point3d(50, 24, 0), Vector3d.ZAxis, 4), L5);
                Add(new Circle(new Point3d(50, 36, 0), Vector3d.ZAxis, 4), L2); });

            Step("ELLIPSE x2", () => {
                Add(new Ellipse(new Point3d(70, 0, 0), Vector3d.ZAxis, new Vector3d(6, 0, 0), 0.5, 0, 2 * Math.PI), L1);
                Add(new Ellipse(new Point3d(70, 14, 0), Vector3d.ZAxis, new Vector3d(6, 0, 0), 0.5, 0, 2 * Math.PI), L3); });

            Step("LWPOLYLINE x3", () => {
                Add(Rect(85, 0, 10, 8, true), L1);
                Add(Rect(85, 14, 10, 8, true), L3);
                var open = new Polyline();
                open.AddVertexAt(0, new Point2d(85, 28), 0, 0, 0);
                open.AddVertexAt(1, new Point2d(92, 32), 0, 0, 0);
                open.AddVertexAt(2, new Point2d(99, 28), 0, 0, 0);
                Add(open, L5, "DASHED"); });

            Step("INSERT x3", () => {
                var bt2 = (BlockTable)_tr.GetObject(_db.BlockTableId, OpenMode.ForRead);
                Add(new BlockReference(new Point3d(110, 0, 0), bt2["T-BLK-A"]), L3);
                Add(new BlockReference(new Point3d(110, 14, 0), bt2["T-BLK-B"]), L5);
                Add(new BlockReference(new Point3d(110, 28, 0), bt2["T-BLK-C"]), L1); });

            Step("POINT", () => Add(new DBPoint(new Point3d(125, 0, 0)), L5));

            Step("TEXT", () => {
                var st = (TextStyleTable)_tr.GetObject(_db.TextStyleTableId, OpenMode.ForRead);
                var t = new DBText { Position = new Point3d(0, 40, 0), Height = 2.5, TextString = "PCD TEMPLATE TEXT" };
                if (st.Has("T-ROMANS")) t.TextStyleId = st["T-ROMANS"];
                Add(t, L2); });

            Step("MTEXT", () => {
                var st = (TextStyleTable)_tr.GetObject(_db.TextStyleTableId, OpenMode.ForRead);
                var m = new MText { Location = new Point3d(0, 46, 0), TextHeight = 2.5, Width = 40,
                                    Contents = "PCD template mtext body" };
                if (st.Has("T-TXT")) m.TextStyleId = st["T-TXT"];
                Add(m, L2); });

            // ---- the types that used to collapse to Kind.Generic ---------------------------
            Step("ATTDEF -> Fuse", () => {
                // A null text-style id throws eNullObjectId here, so resolve a real style first:
                // the template's own if it exists, otherwise the database's current one.
                var st = (TextStyleTable)_tr.GetObject(_db.TextStyleTableId, OpenMode.ForRead);
                ObjectId sid = st.Has("T-ROMANS") ? st["T-ROMANS"] : _db.Textstyle;
                var a = new AttributeDefinition(new Point3d(125, 12, 0), "DEFAULT", "T_TAG",
                                                "Template attribute", sid) { Height = 2.0 };
                Add(a, L2); });

            Step("3DFACE -> Display", () => Add(new Face(new Point3d(140, 0, 0), new Point3d(150, 0, 0),
                    new Point3d(150, 8, 0), new Point3d(140, 8, 0), true, true, true, true), L4));

            Step("XLINE -> Rail", () => Add(new Xline { BasePoint = new Point3d(140, 14, 0), UnitDir = Vector3d.XAxis }, L5));
            Step("RAY -> Rail",   () => Add(new Ray   { BasePoint = new Point3d(140, 18, 0), UnitDir = Vector3d.XAxis }, L5));

            Step("3DSOLID -> Opaque", () => {
                var s = new Solid3d();
                s.CreateBox(10, 8, 5);
                s.TransformBy(Matrix3d.Displacement(new Vector3d(165, 4, 2.5)));
                Add(s, L4); });

            Step("REGION -> Opaque", () => {
                var col = new DBObjectCollection { Rect(160, 14, 10, 8, true) };
                var regs = Region.CreateFromCurves(col);
                if (regs.Count > 0) Add((Region)regs[0], L1);
                foreach (DBObject o in col) o.Dispose(); });

            Step("HATCH -> Shield", () => {
                ObjectId bnd = Add(Rect(160, 28, 12, 10, true), L3);
                var h = new Hatch();
                Add(h, L3);                                        // must be in the db before loops
                h.SetHatchPattern(HatchPatternType.PreDefined, "ANSI31");
                h.Associative = false;
                h.AppendLoop(HatchLoopTypes.Outermost, new ObjectIdCollection { bnd });
                h.EvaluateHatch(true); });

            Step("DIMENSION -> Sensor", () => {
                var d = new RotatedDimension(0, new Point3d(0, 55, 0), new Point3d(20, 55, 0),
                                             new Point3d(10, 60, 0), "", dimId.IsNull ? _db.Dimstyle : dimId);
                Add(d, L2); });

            Step("SPLINE -> Coil", () => {
                var pts = new Point3dCollection {
                    new Point3d(30, 55, 0), new Point3d(36, 62, 0), new Point3d(42, 55, 0), new Point3d(48, 62, 0) };
                Add(new Spline(pts, 3, 0.0), L2); });

            Step("LEADER -> Antenna", () => {
                var l = new Leader();
                l.AppendVertex(new Point3d(60, 55, 0));
                l.AppendVertex(new Point3d(66, 62, 0));
                l.AppendVertex(new Point3d(70, 62, 0));
                l.HasArrowHead = true;
                Add(l, L2); });

            Step("WIPEOUT -> Heatsink", () => {
                var w = new Wipeout();
                var pv = new Point2dCollection {
                    new Point2d(80, 55), new Point2d(92, 55), new Point2d(92, 64), new Point2d(80, 64), new Point2d(80, 55) };
                w.SetFrom(pv, Vector3d.ZAxis);
                Add(w, L5); });

            Step("MLINE -> Ribbon", () => {
                var m = new Mline { Style = _db.CmlstyleID, Normal = Vector3d.ZAxis };
                m.AppendSegment(new Point3d(100, 55, 0));
                m.AppendSegment(new Point3d(115, 55, 0));
                m.AppendSegment(new Point3d(115, 64, 0));
                Add(m, L5); });

            Step("ACAD_TABLE -> Memory", () => {
                var t = new Table { TableStyle = _db.Tablestyle, Position = new Point3d(130, 55, 0) };
                t.SetSize(3, 3);
                t.SetRowHeight(3.0);
                t.SetColumnWidth(10.0);
                t.GenerateLayout();
                Add(t, L3); });

            // ---- xdata: the two ECls.App references ----------------------------------------
            Step("XDATA " + App1, () => { if (!circ1.IsNull) Xdata(circ1, App1, "template marker"); });
            Step("XDATA " + App2, () => { if (!line1.IsNull) Xdata(line1, App2, "template meta"); });

            return Report();
        }

        private static Polyline Rect(double x, double y, double w, double h, bool closed)
        {
            var p = new Polyline();
            p.AddVertexAt(0, new Point2d(x, y), 0, 0, 0);
            p.AddVertexAt(1, new Point2d(x + w, y), 0, 0, 0);
            p.AddVertexAt(2, new Point2d(x + w, y + h), 0, 0, 0);
            p.AddVertexAt(3, new Point2d(x, y + h), 0, 0, 0);
            p.Closed = closed;
            return p;
        }

        private static string Report()
        {
            var sb = new StringBuilder();
            var tally = new SortedDictionary<string, int>(StringComparer.Ordinal);
            int n = 0;
            foreach (ObjectId id in _ms)
            {
                var e = _tr.GetObject(id, OpenMode.ForRead) as Entity;
                if (e == null) continue;
                n++;
                string ty = e.GetRXClass().DxfName;
                tally[ty] = tally.TryGetValue(ty, out int c) ? c + 1 : 1;
            }
            sb.Append("MODEL SPACE ENTITIES = ").Append(n).Append('\n');
            foreach (var kv in tally) sb.Append("  ").Append(kv.Key).Append(" = ").Append(kv.Value).Append('\n');

            sb.Append("\nSYMBOL TABLE RECORDS\n");
            sb.Append(TableCount("LAYER", _db.LayerTableId));
            sb.Append(TableCount("LTYPE", _db.LinetypeTableId));
            sb.Append(TableCount("STYLE", _db.TextStyleTableId));
            sb.Append(TableCount("DIMSTYLE", _db.DimStyleTableId));
            sb.Append(TableCount("APPID", _db.RegAppTableId));
            sb.Append(TableCount("VPORT", _db.ViewportTableId));
            sb.Append(TableCount("VIEW", _db.ViewTableId));
            sb.Append(TableCount("UCS", _db.UcsTableId));
            sb.Append(TableCount("BLOCK", _db.BlockTableId));

            sb.Append("\nSTEPS OK = ").Append(_ok.Count).Append('\n');
            sb.Append("STEPS FAILED = ").Append(_fail.Count).Append('\n');
            foreach (string f in _fail) sb.Append("  FAIL ").Append(f).Append('\n');

            try
            {
                File.WriteAllText(Paths.Out("template_report.txt"), sb.ToString());
            }
            catch { }
            return sb.ToString();
        }

        private static string TableCount(string label, ObjectId tableId)
        {
            int n = 0;
            try { var st = (SymbolTable)_tr.GetObject(tableId, OpenMode.ForRead); foreach (ObjectId _ in st) n++; }
            catch { return "  " + label + " = ?\n"; }
            return "  " + label + " = " + n + "\n";
        }
    }
}
