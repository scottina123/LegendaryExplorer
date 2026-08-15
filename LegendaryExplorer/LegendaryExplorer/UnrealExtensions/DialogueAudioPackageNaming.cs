using System;

namespace LegendaryExplorer.UnrealExtensions
{
    /// <summary>
    /// Resolves the paired sound package used by BioWare dialogue packages.
    /// For example, norvx_relationship_00_h_D stores its Wwise assets in
    /// norvx_relationship_00_h_S.
    /// </summary>
    public static class DialogueAudioPackageNaming
    {
        public static bool TryGetSoundPackageName(string dialoguePackageName, out string soundPackageName)
        {
            if (!string.IsNullOrWhiteSpace(dialoguePackageName) &&
                dialoguePackageName.EndsWith("_D", StringComparison.OrdinalIgnoreCase))
            {
                soundPackageName = $"{dialoguePackageName[..^2]}_S";
                return true;
            }

            soundPackageName = null;
            return false;
        }
    }
}
