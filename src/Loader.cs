using System;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using AcAp = Autodesk.AutoCAD.ApplicationServices.Application;

[assembly: CommandClass(typeof(PCD.Loader))]

namespace PCD
{
    /// <summary>
    /// Stable shim NETLOADed ONCE. The PCD command hot-loads the sibling PCD.Core.dll into a
    /// fresh collectible AssemblyLoadContext, invokes PCD.Demo.Build, then unloads — so the
    /// geometry code can be rebuilt and re-run without restarting AutoCAD or re-NETLOADing.
    /// Core is read into memory (FileShare.ReadWrite) so its file is never locked between runs.
    /// </summary>
    public class Loader
    {
        private static string CorePath()
        {
            string dir = Path.GetDirectoryName(typeof(Loader).Assembly.Location);
            return Path.Combine(dir, "PCD.Core.dll");
        }

        /// <summary>Load probe.</summary>
        [CommandMethod("PCDPING")]
        public void Ping()
        {
            Document doc = AcAp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            doc.Editor.WriteMessage("\nPCD.Loader " + typeof(Loader).Assembly.GetName().Version +
                                    " ready. Core -> " + CorePath() + "\n");
        }

        /// <summary>Hot-load PCD.Core and build the board (rebuildable without restart).</summary>
        [CommandMethod("PCD")]
        public void Run()
        {
            Document doc = AcAp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var ed = doc.Editor;
            string path = CorePath();
            if (!File.Exists(path)) { ed.WriteMessage("\nPCD.Core.dll not found: " + path + "\n"); return; }

            var alc = new AssemblyLoadContext("pcdcore", isCollectible: true);
            try
            {
                Assembly asm;
                // Read bytes (share read/write) so rebuilds can overwrite the file freely.
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    asm = alc.LoadFromStream(fs);

                Type demo = asm.GetType("PCD.Demo");
                MethodInfo build = demo?.GetMethod("Build", new[] { typeof(Database), typeof(Transaction) });
                if (build == null) { ed.WriteMessage("\nPCD.Core: PCD.Demo.Build(Database,Transaction) not found.\n"); return; }

                using (doc.LockDocument())
                using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
                {
                    build.Invoke(null, new object[] { doc.Database, tr });
                    tr.Commit();
                }
                ed.WriteMessage("\nPCD core " + asm.GetName().Version + " built.\n");
            }
            catch (TargetInvocationException tie)
            {
                ed.WriteMessage("\nPCD build error: " + (tie.InnerException?.Message ?? tie.Message) + "\n");
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage("\nPCD error: " + ex.Message + "\n");
            }
            finally
            {
                alc.Unload();
            }
        }

        /// <summary>Build the coverage template into the ACTIVE drawing: the minimum set of entities
        /// and symbol-table records that exercises every package and every relationship class PCD
        /// renders. Never saves the drawing -- do a SAVEAS afterwards to keep it as a .dwg.</summary>
        [CommandMethod("PCDTEMPLATE")]
        public void Template() { InvokeCore("PCD.Template", "Build"); }

        /// <summary>Erase every model-space entity so PCDTEMPLATE can rebuild from a clean slate.</summary>
        [CommandMethod("PCDTEMPLATERESET")]
        public void TemplateReset()
        {
            Document doc = AcAp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var ed = doc.Editor;

            // Count first so the prompt names an exact number. This erases ALL model-space entities,
            // not just PCD output, so it is guarded by an explicit confirmation defaulting to No.
            int n = 0;
            using (doc.LockDocument())
            using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId _ in ms) n++;
                tr.Commit();
            }
            if (n == 0) { ed.WriteMessage("\nModel space is already empty. Nothing to reset.\n"); return; }

            var opts = new PromptKeywordOptions(
                "\nPCDTEMPLATERESET will ERASE all " + n + " model-space entities in " + doc.Name + ". Continue?");
            opts.Keywords.Add("Yes");
            opts.Keywords.Add("No");
            opts.Keywords.Default = "No";
            opts.AllowNone = true;   // Enter accepts the default (No)
            PromptResult r = ed.GetKeywords(opts);
            if (r.Status != PromptStatus.OK || r.StringResult != "Yes")
            {
                ed.WriteMessage("\nPCDTEMPLATERESET cancelled.\n");
                return;
            }
            InvokeCore("PCD.Template", "Reset");
        }

        /// <summary>Hot-load PCD.Core and invoke one static (Database, Transaction) entry point.
        /// The template commands live in THIS assembly rather than a second shim because the loader
        /// is NETLOADed once per session anyway -- one load, every command.</summary>
        private void InvokeCore(string typeName, string method)
        {
            Document doc = AcAp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var ed = doc.Editor;
            string path = CorePath();
            if (!File.Exists(path)) { ed.WriteMessage("\nPCD.Core.dll not found: " + path + "\n"); return; }

            var alc = new AssemblyLoadContext("pcdcore", isCollectible: true);
            try
            {
                Assembly asm;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    asm = alc.LoadFromStream(fs);

                Type t = asm.GetType(typeName);
                MethodInfo mi = t?.GetMethod(method, new[] { typeof(Database), typeof(Transaction) });
                if (mi == null)
                {
                    ed.WriteMessage("\nPCD.Core: " + typeName + "." + method + "(Database,Transaction) not found.\n");
                    return;
                }

                object result;
                using (doc.LockDocument())
                using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
                {
                    result = mi.Invoke(null, new object[] { doc.Database, tr });
                    tr.Commit();
                }
                ed.WriteMessage("\n" + (result as string ?? "done") + "\n");
            }
            catch (TargetInvocationException tie)
            {
                ed.WriteMessage("\n" + method + " error: " + (tie.InnerException?.ToString() ?? tie.Message) + "\n");
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage("\n" + method + " error: " + ex + "\n");
            }
            finally { alc.Unload(); }
        }

        /// <summary>Frames the board: SW isometric, zoom extents, realistic shading.</summary>
        [CommandMethod("PCDVIEW")]
        public void View()
        {
            Document doc = AcAp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            // VPOINT Rotate (angle form) is what actually takes here; the coordinate form and
            // .NET SetCurrentView both failed. 235deg in XY + 35.264deg up = isometric.
            doc.SendStringToExecute("_.VPOINT\n_R\n235\n35.264\n_.ZOOM\n_E\n_.VSCURRENT\n_R\n", true, false, false);
        }
    }
}
