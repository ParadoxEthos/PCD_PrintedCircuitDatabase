// PCD - Printed Circuit Database. Copyright (C) 2026 ParadoxEthos.
//
// This program is free software: you can redistribute it and/or modify it under
// the terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later version.
//
// This program is distributed in the hope that it will be useful, but WITHOUT ANY
// WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A
// PARTICULAR PURPOSE. See the GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License along with
// this program. If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using AcColor = Autodesk.AutoCAD.Colors.Color;
using GI = Autodesk.AutoCAD.GraphicsInterface;

namespace PCD
{
    /// <summary>
    /// Thin geometry layer over one open transaction: creates real 3D solids and
    /// text on model space and files them into named layers. The board plane is the
    /// world XY plane; the board's top face sits at Z = 0 and parts stand up in +Z.
    /// </summary>
    public sealed class Pcb
    {
        private readonly Database _db;
        private readonly Transaction _tr;
        private readonly BlockTableRecord _ms;

        public Pcb(Database db, Transaction tr)
        {
            _db = db;
            _tr = tr;
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            _ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
        }

        /// <summary>Create a text style bound to the given SHX/TTF font if absent; returns its ObjectId.</summary>
        public ObjectId EnsureTextStyle(string name, string fontFile)
        {
            var tt = (TextStyleTable)_tr.GetObject(_db.TextStyleTableId, OpenMode.ForRead);
            if (tt.Has(name))
                return tt[name];

            tt.UpgradeOpen();
            var r = new TextStyleTableRecord { Name = name, FileName = fontFile };
            ObjectId id = tt.Add(r);
            _tr.AddNewlyCreatedDBObject(r, true);
            return id;
        }

        /// <summary>
        /// Create a text style bound to a specific typeface + charset (e.g. "MS Gothic", charset
        /// 128 = SHIFT-JIS) so CJK/katakana glyphs render instead of "?". Idempotent by name.
        /// </summary>
        public ObjectId EnsureCjkStyle(string name, string typeface, int charset)
        {
            var tt = (TextStyleTable)_tr.GetObject(_db.TextStyleTableId, OpenMode.ForRead);
            if (tt.Has(name)) return tt[name];
            tt.UpgradeOpen();
            var r = new TextStyleTableRecord { Name = name };
            r.Font = new GI.FontDescriptor(typeface, false, false, charset, 0);
            ObjectId id = tt.Add(r);
            _tr.AddNewlyCreatedDBObject(r, true);
            return id;
        }

        /// <summary>Center-justified single glyph in a given style (used for rain columns).</summary>
        public DBText Glyph(string s, Point3d pos, double height, string layer,
                            AcColor color, Vector3d normal, ObjectId styleId)
        {
            var t = new DBText
            {
                TextString = s, Height = height, Normal = normal,
                HorizontalMode = TextHorizontalMode.TextCenter,
                VerticalMode = TextVerticalMode.TextVerticalMid,
            };
            if (!styleId.IsNull) t.TextStyleId = styleId;
            Place(t, layer, color);
            t.AlignmentPoint = pos + Origin;   // alignment overrides Place's transform
            t.AdjustAlignment(_db);
            return t;
        }

        /// <summary>Create the layer if absent; returns its ObjectId. Idempotent.</summary>
        public ObjectId EnsureLayer(string name, AcColor color)
        {
            var lt = (LayerTable)_tr.GetObject(_db.LayerTableId, OpenMode.ForRead);
            if (lt.Has(name))
                return lt[name];

            lt.UpgradeOpen();
            var ltr = new LayerTableRecord { Name = name, Color = color };
            ObjectId id = lt.Add(ltr);
            _tr.AddNewlyCreatedDBObject(ltr, true);
            return id;
        }

        /// <summary>Global displacement of the whole render — set once (e.g. to clear the
        /// source drawing's extents). All geometry passes through <see cref="Place"/>, so
        /// builder math stays in local coordinates.</summary>
        public Vector3d Origin = new Vector3d(0, 0, 0);

        private void Place(Entity ent, string layer, AcColor color)
        {
            if (Origin.X != 0 || Origin.Y != 0) ent.TransformBy(Matrix3d.Displacement(Origin));
            _ms.AppendEntity(ent);
            _tr.AddNewlyCreatedDBObject(ent, true);
            ent.Layer = layer;
            ent.Color = color;
        }

        /// <summary>Append an already-built entity (e.g. a boolean-drilled board) to model space.</summary>
        public void Add(Entity ent, string layer, AcColor color) => Place(ent, layer, color);

        /// <summary>
        /// Create a render material with a diffuse color, glossiness and reflectivity, and return
        /// its ObjectId. Idempotent by name. Assign it to a layer with <see cref="SetLayerMaterial"/>.
        /// </summary>
        public ObjectId CreateMaterial(string name, AcColor color, double gloss, double reflectivity,
                                       bool inheritColor = false, double opacity = 1.0)
        {
            var dict = (DBDictionary)_tr.GetObject(_db.MaterialDictionaryId, OpenMode.ForWrite);
            Material mat; ObjectId id;
            if (dict.Contains(name))    // UPDATE the existing one (else re-runs keep stale colors)
            {
                id = dict.GetAt(name);
                mat = (Material)_tr.GetObject(id, OpenMode.ForWrite);
            }
            else
            {
                mat = new Material { Name = name };
                id = dict.SetAt(name, mat);
                _tr.AddNewlyCreatedDBObject(mat, true);
            }
            var map = new GI.MaterialMap();
            // inheritColor: the ENTITY's color drives the diffuse (per-net colored traces on one layer)
            var diff = new GI.MaterialColor(inheritColor ? GI.Method.Inherit : GI.Method.Override, 1.0, color.EntityColor);
            var spec = new GI.MaterialColor(GI.Method.Override, 1.0,
                                            AcColor.FromRgb(255, 255, 255).EntityColor);
            mat.Diffuse = new GI.MaterialDiffuseComponent(diff, map);
            mat.Specular = new GI.MaterialSpecularComponent(spec, map, gloss);
            mat.Reflectivity = reflectivity;
            if (opacity < 1.0)   // translucent substrate: reveals the inner copper planes embedded in the board
                mat.Opacity = new GI.MaterialOpacityComponent(opacity, map);
            return id;
        }

        /// <summary>Bind a material to a layer, so every ByLayer-material entity on it renders with it.</summary>
        public void SetLayerMaterial(string layerName, ObjectId matId)
        {
            var lt = (LayerTable)_tr.GetObject(_db.LayerTableId, OpenMode.ForRead);
            if (!lt.Has(layerName)) return;
            var ltr = (LayerTableRecord)_tr.GetObject(lt[layerName], OpenMode.ForWrite);
            ltr.MaterialId = matId;
        }

        /// <summary>Set a layer's transparency. alpha01 = 1 fully opaque, 0 fully clear. Shows in shaded/
        /// realistic visual styles (broader support than material opacity alone), letting the substrate
        /// reveal the copper planes embedded within it.</summary>
        public void SetLayerTransparency(string layerName, double alpha01)
        {
            var lt = (LayerTable)_tr.GetObject(_db.LayerTableId, OpenMode.ForRead);
            if (!lt.Has(layerName)) return;
            var ltr = (LayerTableRecord)_tr.GetObject(lt[layerName], OpenMode.ForWrite);
            byte a = (byte)Math.Max(0, Math.Min(255, (int)Math.Round(alpha01 * 255)));
            ltr.Transparency = new Autodesk.AutoCAD.Colors.Transparency(a);
        }

        /// <summary>
        /// Axis-aligned box, given by its full lengths and CENTER point. Used for
        /// the board, IC/part bodies, copper pads, and Manhattan copper segments.
        /// </summary>
        public Solid3d Box(double lenX, double lenY, double lenZ, Point3d center,
                           string layer, AcColor color)
        {
            var s = new Solid3d();
            s.CreateBox(lenX, lenY, lenZ);                       // centered at WCS origin
            s.TransformBy(Matrix3d.Displacement(center - Point3d.Origin));
            Place(s, layer, color);
            return s;
        }

        /// <summary>Board slab from a closed XY outline: the polygon is extruded to
        /// <paramref name="thick"/> with its TOP face at z=0. Returned un-placed so the caller can
        /// boolean-cut holes/notches first, then <see cref="Add"/> it.</summary>
        public Solid3d PolySolid(List<Point2d> pts, double thick)
        {
            var pl = new Polyline(pts.Count);
            for (int i = 0; i < pts.Count; i++) pl.AddVertexAt(i, pts[i], 0, 0, 0);
            pl.Closed = true;
            var curves = new DBObjectCollection { pl };
            var regs = Region.CreateFromCurves(curves);
            var reg = (Region)regs[0];
            var s = new Solid3d();
            s.Extrude(reg, thick, 0.0);
            s.TransformBy(Matrix3d.Displacement(new Vector3d(0, 0, -thick)));
            reg.Dispose(); pl.Dispose();
            return s;
        }

        /// <summary>Torus in the XY plane (tube around +Z axis) centred at a point. Used for
        /// via annular rings and capacitor top rims.</summary>
        public Solid3d Torus(double majorRadius, double minorRadius, Point3d center,
                             string layer, AcColor color)
        {
            var s = new Solid3d();
            s.CreateTorus(majorRadius, minorRadius);             // centered at origin, axis Z
            s.TransformBy(Matrix3d.Displacement(center - Point3d.Origin));
            Place(s, layer, color);
            return s;
        }

        /// <summary>A continuous copper trace: one open, constant-width polyline flat at z.</summary>
        public void Trace(List<Point2d> pts, double width, double z, string layer, AcColor color)
        {
            if (pts.Count < 2) return;
            var pl = new Polyline(pts.Count);
            for (int i = 0; i < pts.Count; i++) pl.AddVertexAt(i, pts[i], 0, width, width);
            pl.Elevation = z;
            Place(pl, layer, color);
        }

        /// <summary>A closed rectangular silkscreen outline (wide polyline) flat on the board at z.</summary>
        public void Outline(double cx, double cy, double w, double d, double z,
                            string layer, AcColor color, double lineWidth)
        {
            var pl = new Polyline(4);
            pl.AddVertexAt(0, new Point2d(cx - w / 2, cy - d / 2), 0, 0, 0);
            pl.AddVertexAt(1, new Point2d(cx + w / 2, cy - d / 2), 0, 0, 0);
            pl.AddVertexAt(2, new Point2d(cx + w / 2, cy + d / 2), 0, 0, 0);
            pl.AddVertexAt(3, new Point2d(cx - w / 2, cy + d / 2), 0, 0, 0);
            pl.Closed = true;
            pl.Elevation = z;
            pl.ConstantWidth = lineWidth;
            Place(pl, layer, color);
        }

        /// <summary>
        /// A thin bar (box) of given length along X, rotated <paramref name="angleZ"/> about +Z and
        /// centred at <paramref name="mid"/>. Used for angled (45deg) copper trace segments.
        /// </summary>
        public Solid3d Bar(double length, double width, double thick, Point3d mid,
                           double angleZ, string layer, AcColor color)
        {
            var s = new Solid3d();
            s.CreateBox(length, width, thick);
            s.TransformBy(Matrix3d.Rotation(angleZ, Vector3d.ZAxis, Point3d.Origin));
            s.TransformBy(Matrix3d.Displacement(mid - Point3d.Origin));
            Place(s, layer, color);
            return s;
        }

        /// <summary>Vertical cylinder (axis = Z), given base-center and height. Used for caps and vias.</summary>
        public Solid3d Cyl(double radius, double height, Point3d baseCenter,
                           string layer, AcColor color)
        {
            var s = new Solid3d();
            s.CreateFrustum(height, radius, radius, radius);     // centered at origin, axis Z
            var c = new Point3d(baseCenter.X, baseCenter.Y, baseCenter.Z + height / 2.0);
            s.TransformBy(Matrix3d.Displacement(c - Point3d.Origin));
            Place(s, layer, color);
            return s;
        }

        /// <summary>Solid sphere centered at <paramref name="center"/> (LED domes, ball features).</summary>
        public Solid3d Sphere(double radius, Point3d center, string layer, AcColor color)
        {
            var s = new Solid3d();
            s.CreateSphere(radius);
            s.TransformBy(Matrix3d.Displacement(center - Point3d.Origin));
            Place(s, layer, color);
            return s;
        }

        /// <summary>Vertical frustum/cone: bottom radius <paramref name="baseR"/>, top radius
        /// <paramref name="topR"/> (0 = full cone). <paramref name="baseCenter"/> is the base.
        /// Used for TO-can transistor bodies, tapered LEDs, standoffs.</summary>
        public Solid3d Cone(double baseR, double topR, double height, Point3d baseCenter,
                            string layer, AcColor color)
        {
            var s = new Solid3d();
            s.CreateFrustum(height, baseR, baseR, Math.Max(1e-4, topR));   // ellipse base (baseR,baseR) -> circular top topR
            var c = new Point3d(baseCenter.X, baseCenter.Y, baseCenter.Z + height / 2.0);
            s.TransformBy(Matrix3d.Displacement(c - Point3d.Origin));
            Place(s, layer, color);
            return s;
        }

        /// <summary>
        /// Single-line text. <paramref name="normal"/> orients the text plane:
        /// (0,0,1) = flat silkscreen on the board; (0,1,0) = upright glyph in a
        /// rising plume. Center-justified about <paramref name="pos"/>.
        /// </summary>
        public DBText Text(string s, Point3d pos, double height, string layer,
                           AcColor color, Vector3d normal, double rotation = 0.0)
        {
            var t = new DBText
            {
                TextString = s,
                Height = height,
                Normal = normal,
                Rotation = rotation,
                HorizontalMode = TextHorizontalMode.TextCenter,
                VerticalMode = TextVerticalMode.TextVerticalMid,
            };
            Place(t, layer, color);
            // Alignment point must be set AFTER the entity is DB-resident and its
            // justification flags are in place, or AutoCAD discards it.
            t.AlignmentPoint = pos + Origin;   // alignment overrides Place's transform
            t.AdjustAlignment(_db);
            return t;
        }

        /// <summary>
        /// Left-justified single-line text at <paramref name="pos"/> (bottom-left), flat on
        /// the board/chip top face (normal +Z). Used for silkscreen field/value blocks;
        /// pass a monospaced style so columns align like printed part markings.
        /// </summary>
        public DBText TextLeft(string s, Point3d pos, double height, string layer,
                               AcColor color, ObjectId styleId)
        {
            var t = new DBText { TextString = s, Height = height, Position = pos };
            if (!styleId.IsNull) t.TextStyleId = styleId;
            Place(t, layer, color);
            return t;
        }

        /// <summary>
        /// Left-justified text standing on a VERTICAL face (the front, -Y side of a part body): built
        /// flat, rotated 90 deg about X so it reads in +X with its height in +Z, then moved to
        /// <paramref name="at"/>. Its normal ends up facing -Y. Used where a part's TOP is obstructed
        /// (a pin header: pins would run straight through text printed on top).
        /// </summary>
        public DBText TextLeftFace(string s, Point3d at, double height, string layer,
                                   AcColor color, ObjectId styleId)
        {
            var t = new DBText { TextString = s, Height = height, Position = Point3d.Origin };
            if (!styleId.IsNull) t.TextStyleId = styleId;
            t.TransformBy(Matrix3d.Rotation(Math.PI / 2.0, Vector3d.XAxis, Point3d.Origin));
            t.TransformBy(Matrix3d.Displacement(at - Point3d.Origin));
            Place(t, layer, color);
            return t;
        }
    }
}
