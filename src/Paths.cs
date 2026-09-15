using System;
using System.IO;

namespace PCD
{
    /// <summary>
    /// Resolves where PCD writes its diagnostic files (route stats, template report, and the optional
    /// Freerouting DSN study dump). Resolution order:
    ///   1. the PCD_OUT environment variable, if set;
    ///   2. otherwise %TEMP%\PCD.
    /// The directory is created on demand. Every caller wraps the write in try/catch, so a
    /// non-writable location degrades to no file rather than throwing during a render.
    ///
    /// This replaced three absolute paths that were compiled into the binary, one of which pointed
    /// at a per-session temp directory that does not exist on any other machine. PCD_OUT lets the
    /// measurement loop redirect diagnostics to a known location without a rebuild.
    /// </summary>
    internal static class Paths
    {
        internal static string OutDir()
        {
            string dir = Environment.GetEnvironmentVariable("PCD_OUT");
            if (string.IsNullOrEmpty(dir))
                dir = Path.Combine(Path.GetTempPath(), "PCD");
            Directory.CreateDirectory(dir);
            return dir;
        }

        internal static string Out(string filename) => Path.Combine(OutDir(), filename);

        /// <summary>The Freerouting DSN export is a study artifact, not part of a render. It is written
        /// only when PCD_DSN is set (to any value), so shipped renders do not pay to build it.</summary>
        internal static bool DsnEnabled => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PCD_DSN"));
    }
}
