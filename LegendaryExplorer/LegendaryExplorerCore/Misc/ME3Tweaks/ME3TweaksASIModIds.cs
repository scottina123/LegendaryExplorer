// This is direct copy of the file from the ME3TweaksCore repo
// Origin Date: 06/06/2022
// Only change is the namespace to prevent issues of same namespace in M3C
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace LegendaryExplorerCore.Misc.ME3Tweaks
{
    /// <summary>
    /// Contains (some, not all) ASI Update Group IDs that can be used to request install of an ASI. Makes code easier to read.
    /// </summary>
    public static class ASIModIDs
    {
        // This is not comprehensive list. Just here for convenience.

        // ME1 ============================================
        public const int ME1_DLC_MOD_ENABLER = 16;

        // ME2 ============================================

        // ME3 ============================================
        public const int ME3_BALANCE_CHANGES_REPLACER = 5;
        public const int ME3_AUTOTOC = 9;
        public const int ME3_LOGGER = 8;

        // LE1 ============================================
        public const int LE1_AUTOTOC = 29;
        public const int LE1_AUTOLOAD_ENABLER = 32;
        public const int LE1_DEBUGLOGGER_DEV = 70;
        public const int LE1_LEX_INTEROP = 42;
        public const int LE1_SCRIPT_DEBUGGER = 82;
        public const int LE1_TEXTURE_OVERRIDE = 88;

        // LE2 ============================================
        public const int LE2_AUTOTOC = 30;
        public const int LE2_DEBUGLOGGER_DEV = 71;
        public const int LE2_HOT_RELOAD = 78;
        public const int LE2_LEX_INTEROP = 79;
        public const int LE2_SCRIPT_DEBUGGER = 81;
        public const int LE2_TEXTURE_OVERRIDE = 89;

        // LE3 ============================================
        public const int LE3_AUTOTOC = 31;
        public const int LE3_DEBUGLOGGER_DEV = 72;
        public const int LE3_LEX_INTEROP = 80;
        public const int LE3_SCRIPT_DEBUGGER = 86;
        public const int LE3_TEXTURE_OVERRIDE = 87;

        /// <summary>
        /// Returns if the given ASI is installed in the given game directory. This only detects 
        /// ASIs built after 01/25/2026 that embed their ASI Mod ID in the binary metadata.
        /// </summary>
        /// <param name="asiModId">ID to find</param>
        /// <param name="binaryPath">Path to the game binary</param>
        /// <returns></returns>
        public static bool IsASIInstalled(int asiModId, string binaryPath)
        {
            return GetInstalledASIModIds(binaryPath).Contains(asiModId);
        }

        /// <summary>
        /// Gets a list of installed ASI update groups (mod ids)
        /// </summary>
        /// <param name="binaryPath"></param>
        /// <returns></returns>
        public static IEnumerable<int> GetInstalledASIModIds(string binaryPath)
        {
            var asiPath = Path.Combine(binaryPath, "ASI");
            if (!Directory.Exists(asiPath))
            {
                yield break;
            }

            var asiFiles = Directory.GetFiles(asiPath, "*.asi");
            
            foreach (var asiFile in asiFiles)
            {
                int? asiModId = null;
                try
                {
                    var versionInfo = FileVersionInfo.GetVersionInfo(asiFile);
                    if (!string.IsNullOrEmpty(versionInfo.ProductVersion))
                    {
                        var versionParts = versionInfo.ProductVersion.Split('.');
                        if (versionParts.Length >= 4 && int.TryParse(versionParts[3], out int parsedId))
                        {
                            asiModId = parsedId;
                        }
                    }
                }
                catch
                {
                    // Skip files that can't be read or don't have valid version info
                }

                if (asiModId.HasValue)
                {
                    yield return asiModId.Value;
                }
            }
        }
    }
}