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
