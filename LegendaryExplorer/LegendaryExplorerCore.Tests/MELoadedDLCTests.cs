using System;
using System.IO;
using System.Linq;
using LegendaryExplorerCore.GameFilesystem;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.TLK;
using LegendaryExplorerCore.TLK.ME2ME3;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorerCore.Tests
{
    [TestClass]
    public class MELoadedDLCTests
    {
        [TestMethod]
        public void GetAllDLCFolders_IncludesDisabledDLC()
        {
            string tempRoot = Path.Combine(Path.GetTempPath(), $"LEX_MELoadedDLCTests_{Guid.NewGuid():N}");
            string dlcRoot = Path.Combine(tempRoot, "BioGame", "DLC");
            string enabledDLC = Path.Combine(dlcRoot, "DLC_MOD_Enabled");
            string disabledDLC = Path.Combine(dlcRoot, "offDLC_MOD_ProjectVariety");
            string unrelatedFolder = Path.Combine(dlcRoot, "Backup");

            try
            {
                Directory.CreateDirectory(enabledDLC);
                Directory.CreateDirectory(disabledDLC);
                Directory.CreateDirectory(unrelatedFolder);

                string[] folderNames = MELoadedDLC.GetAllDLCFolders(MEGame.ME3, tempRoot)
                    .Select(Path.GetFileName)
                    .ToArray();

                CollectionAssert.AreEquivalent(new[] { Path.GetFileName(enabledDLC), Path.GetFileName(disabledDLC) }, folderNames);
            }
            finally
            {
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, true);
                }
            }
        }

        [TestMethod]
        public void ResolveToggledDLCFilePath_UsesDisabledEquivalentOfSavedPath()
        {
            string tempRoot = Path.Combine(Path.GetTempPath(), $"LEX_MELoadedDLCTests_{Guid.NewGuid():N}");
            string oldPath = Path.Combine(tempRoot, "BioGame", "DLC", "DLC_MOD_ProjectVariety", "CookedPCConsole", "DLC_MOD_ProjectVariety_INT.tlk");
            string currentPath = Path.Combine(tempRoot, "BioGame", "DLC", "offDLC_MOD_ProjectVariety", "CookedPCConsole", "DLC_MOD_ProjectVariety_INT.tlk");

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(currentPath)!);
                File.WriteAllBytes(currentPath, []);

                Assert.AreEqual(currentPath, MELoadedDLC.ResolveToggledDLCFilePath(oldPath), true);
                Assert.AreEqual(currentPath, MELoadedDLC.ResolveToggledDLCFilePath(currentPath), true);
            }
            finally
            {
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, true);
                }
            }
        }

        [TestMethod]
        public void TLKSystem_LoadTLKs_LoadsDisabledDLCUsingLogicalName()
        {
            string tempRoot = Path.Combine(Path.GetTempPath(), $"LEX_MELoadedDLCTests_{Guid.NewGuid():N}");
            string cookedPath = Path.Combine(tempRoot, "BioGame", "DLC", "offDLC_MOD_ProjectVariety", "CookedPCConsole");
            string tlkPath = Path.Combine(cookedPath, "DLC_MOD_ProjectVariety_INT.tlk");

            try
            {
                Directory.CreateDirectory(cookedPath);
                var mount = new MountFile(MEGame.LE3)
                {
                    MountPriority = 5000,
                    MountFlags = new MountFlag(EME3MountFileFlag.LoadsInSingleplayer),
                    TLKID = 123456
                };
                mount.WriteMountFile(Path.Combine(cookedPath, "Mount.dlc"));
                HuffmanCompression.SaveToTlkFile(tlkPath, [new TLKStringRef(123456, "Project Variety")]);

                var tlks = TLKSystem.LoadTLKs(MEGame.LE3, MELocalization.INT, true, tempRoot);

                Assert.HasCount(1, tlks);
                Assert.AreEqual("Project Variety", tlks[0].FindDataById(123456, noQuotes: true));
                Assert.AreEqual(tlkPath, tlks[0].Source, true);
            }
            finally
            {
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, true);
                }
            }
        }

        [TestMethod]
        public void TLKSystem_LoadTLKs_MatchesME2ModuleForDisabledDLC()
        {
            string tempRoot = Path.Combine(Path.GetTempPath(), $"LEX_MELoadedDLCTests_{Guid.NewGuid():N}");
            string cookedPath = Path.Combine(tempRoot, "BioGame", "DLC", "offDLC_MOD_ProjectVariety", "CookedPC");
            string tlkPath = Path.Combine(cookedPath, "DLC_5000_INT.tlk");

            try
            {
                Directory.CreateDirectory(cookedPath);
                var mount = new MountFile(MEGame.ME2)
                {
                    MountPriority = 5000,
                    MountFlags = new MountFlag(EME2MountFileFlag.SaveFileDependency),
                    TLKID = 123456,
                    ME2Only_DLCFolderName = "DLC_MOD_ProjectVariety",
                    ME2Only_DLCHumanName = "Project Variety"
                };
                mount.WriteMountFile(Path.Combine(cookedPath, "Mount.dlc"));
                File.WriteAllText(Path.Combine(cookedPath, "BIOEngine.ini"),
                    "[Engine.DLCModules]\r\nDLC_MOD_ProjectVariety=5000\r\n");
                HuffmanCompression.SaveToTlkFile(tlkPath, [new TLKStringRef(123456, "Project Variety")]);

                var tlks = TLKSystem.LoadTLKs(MEGame.ME2, MELocalization.INT, true, tempRoot);

                Assert.HasCount(1, tlks);
                Assert.AreEqual("Project Variety", tlks[0].FindDataById(123456, noQuotes: true));
                Assert.AreEqual(tlkPath, tlks[0].Source, true);
            }
            finally
            {
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, true);
                }
            }
        }
    }
}
