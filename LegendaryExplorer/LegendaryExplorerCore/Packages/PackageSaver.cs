using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LegendaryExplorerCore.GameFilesystem;
using LegendaryExplorerCore.Helpers;
using LegendaryExplorerCore.Localization;
using LegendaryExplorerCore.Memory;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using Newtonsoft.Json;

namespace LegendaryExplorerCore.Packages
{
    public static class PackageSaver
    {
        private static List<string> _me1TextureFiles;
        internal static List<string> ME1TextureFiles => _me1TextureFiles ??= JsonConvert.DeserializeObject<List<string>>(LegendaryExplorerCoreUtilities.LoadStringFromCompressedResource("Infos.zip", "ME1TextureFiles.json"));

        /// <summary>
        /// Callback that is invoked when a package fails to save, hook this up to show a message to the user that something failed
        /// </summary>
        public static Action<string> PackageSaveFailedCallback { get; set; }

        /// <summary>
        /// Optional host confirmation for saving packages with reference issues. Returning false cancels the save.
        /// When unset, package saves retain their normal behavior without running a reference check.
        /// </summary>
        public static Func<IMEPackage, string, ReferenceCheckPackage, bool> PackageSaveReferenceWarningCallback { get; set; }

        public static bool CanReconstruct(this IMEPackage pcc) => CanReconstruct(pcc, pcc.FilePath);

        public static bool CanReconstruct(IMEPackage pckg, string path) =>
            pckg.Game is MEGame.UDK or MEGame.ME3 or MEGame.ME2 || pckg.Game.IsLEGame() || pckg.Game == MEGame.ME1 && ME1TextureFiles.TrueForAll(texFilePath => !path.EndsWith(texFilePath));

        private static Action<MEPackage, string, bool, bool, bool, bool, Lock> MESaveDelegate;
        private static Action<UDKPackage, string, bool, Lock> UDKSaveDelegate;

        public static void Initialize()
        {
            UDKSaveDelegate = UDKPackage.RegisterSaver();
            MESaveDelegate = MEPackage.RegisterSaver();
        }

        /// <summary>
        /// Saves the package to disk. If calling from the UI thread, consider using SaveAsync instead.
        /// </summary>
        /// <param name="package"></param>
        /// <param name="savePath"></param>
        /// <param name="compress"></param>
        /// <param name="includeAdditionalPackagesToCook"></param>
        /// <param name="includeDependencyTable"></param>
        /// <param name="diskIOSyncLock">Object that can be used to force a lock on write operations, which can be used to prevent concurrent operations on the same package file. If null, a lock is not used.</param>
        public static void Save(this IMEPackage package, string savePath = null, bool? compress = null, bool includeAdditionalPackagesToCook = true, bool includeDependencyTable = true, Lock diskIOSyncLock = null)
            => TrySave(package, savePath, compress, includeAdditionalPackagesToCook, includeDependencyTable, diskIOSyncLock);

        /// <summary>Saves a package to disk, returning false if the host cancels the save or saving fails.</summary>
        public static bool TrySave(this IMEPackage package, string savePath = null, bool? compress = null, bool includeAdditionalPackagesToCook = true, bool includeDependencyTable = true, Lock diskIOSyncLock = null)
        {
            if (package == null)
            {
                return false;
            }

            bool compressPackage;
            if (compress.HasValue)
            {
                compressPackage = compress.Value;
            }
            else
            {
                // Compress LE packages by default
                // Do not compress OT packages by default
                compressPackage = package.Game.IsLEGame();
            }

            if ((package.IsMemoryPackage || package.FilePath == null) && savePath == null)
            {
                throw new InvalidOperationException("Cannot save a temporary memory-based package! You must pass a save path to save a memory package.");
            }

            var confirmReferenceIssues = PackageSaveReferenceWarningCallback;
            if (confirmReferenceIssues is not null)
            {
                var references = new ReferenceCheckPackage();
                try
                {
                    EntryChecker.CheckReferences(references, package, LECLocalizationShim.NonLocalizedStringConverter);
                }
                catch (Exception exception)
                {
                    // A malformed name/header can prevent the checker from finishing. Let the host warn about
                    // the incomplete check and still offer saving, just as for the issues it already found.
                    references.AddSignificantIssue($"Reference checking could not finish: {exception.Message}");
                }
                if (references.GetBlockingErrors().Count + references.GetSignificantIssues().Count > 0
                    && !confirmReferenceIssues(package, savePath ?? package.FilePath, references))
                    return false;
            }

            //if this file is open in any tool, saving needs to be done on a different thread.

            switch (package)
            {
                case MEPackage mePackage:
                    return MESave(mePackage, savePath, compressPackage, includeAdditionalPackagesToCook, includeDependencyTable, diskIOSyncLock);
                case UDKPackage udkPackage:
                    return UDKSave(udkPackage, savePath, diskIOSyncLock);
                default:
                    throw new ArgumentOutOfRangeException(nameof(package));
            }
        }

        /// <summary>
        /// Saves the package to disk on a different thread
        /// </summary>
        /// <param name="package"></param>
        /// <param name="savePath"></param>
        /// <param name="compress"></param>
        /// <param name="includeAdditionalPackagesToCook"></param>
        /// <param name="includeDependencyTable"></param>
        /// <param name="diskIOSyncLock">Object that can be used to force a lock on write operations, which can be used to prevent concurrent operations on the same package file. If null, a lock is not used.</param>
        public static async Task SaveAsync(this IMEPackage package, string savePath = null, bool? compress = null, bool includeAdditionalPackagesToCook = true, bool includeDependencyTable = true, Lock diskIOSyncLock = null)
            => await TrySaveAsync(package, savePath, compress, includeAdditionalPackagesToCook, includeDependencyTable, diskIOSyncLock);

        /// <summary>Saves on a worker thread, returning false if the host cancels the save or saving fails.</summary>
        public static async Task<bool> TrySaveAsync(this IMEPackage package, string savePath = null, bool? compress = null, bool includeAdditionalPackagesToCook = true, bool includeDependencyTable = true, Lock diskIOSyncLock = null)
        {
            if (package is null)
                return false;
            try
            {
                foreach (IPackageUser packageUser in package.Users)
                {
                    packageUser.HandleSaveStateChange(true);
                }
                return await Task.Run(() => TrySave(package, savePath, compress, includeAdditionalPackagesToCook, includeDependencyTable, diskIOSyncLock));
            }
            finally
            {
                foreach (IPackageUser packageUser in package.Users)
                {
                    packageUser.HandleSaveStateChange(false);
                }
            }
        }

        ///// <summary>
        ///// Saves this package to a stream. This does not mark the package as no longer modified like the save to disk does.
        ///// </summary>
        ///// <param name="package"></param>
        ///// <param name="compress"></param>
        ///// <returns></returns>
        //public static MemoryStream SaveToStream(this IMEPackage package, bool compress = false)
        //{
        //    if (package == null)
        //    {
        //        return null;
        //    }
        //    switch (package)
        //    {
        //        case MEPackage mePackage:
        //            return mePackage.SaveToStream(compress);
        //        case UDKPackage udkPackage:
        //            return udkPackage.SaveToStream(compress);
        //        default:
        //            throw new ArgumentOutOfRangeException(nameof(package));
        //    }
        //}

        /// <summary>
        /// Used to test if ME3 is running. Used by LegendaryExplorer GameController class
        /// </summary>
        public static Func<bool> CheckME3Running { get; set; }
        /// <summary>
        /// Notifies that a TOC update is required for a running instance of a game (for ME3 only).
        /// </summary>
        public static Func<bool> NotifyRunningTOCUpdateRequired { get; set; }

        private static bool MESave(MEPackage pcc, string savePath, bool compress = false, bool includeAdditionalPackagesToCook = true, bool includeDependencyTable = true, Lock diskIOSyncLock = null)
        {
            bool isSaveAs = savePath != null && savePath != pcc.FilePath;
            int originalLength = -1;
            if (pcc.Game == MEGame.ME3 && CheckME3Running is not null && NotifyRunningTOCUpdateRequired is not null && !isSaveAs 
             && ME3Directory.GetBioGamePath() != null && pcc.FilePath.StartsWith(ME3Directory.GetBioGamePath()) && CheckME3Running.Invoke())
            {
                try
                {
                    originalLength = (int)new FileInfo(pcc.FilePath).Length;
                }
                catch
                {
                    originalLength = -1;
                }
            }
            try
            {
                if (CanReconstruct(pcc, savePath ?? pcc.FilePath))
                {
                    MESaveDelegate(pcc, savePath ?? pcc.FilePath, isSaveAs, compress, includeAdditionalPackagesToCook, includeDependencyTable, diskIOSyncLock);
                }
                else
                {
                    PackageSaveFailedCallback?.Invoke($"Cannot save ME1 packages with externally referenced textures. Please make an issue on github: {LegendaryExplorerCoreLib.BugReportURL}");
                    return false;
                }
            }
            catch (Exception ex) when (!LegendaryExplorerCoreLib.IsDebug)
            {
                PackageSaveFailedCallback?.Invoke($"Error saving {pcc.FilePath}:\n{ex.FlattenException()}");
                return false;
            }

            if (originalLength > 0)
            {
                string relativePath = Path.GetFullPath(pcc.FilePath).Substring(Path.GetFullPath(ME3Directory.DefaultGamePath).Length);
                using var bin = MemoryManager.GetMemoryStream();
                bin.WriteInt32(originalLength);
                bin.WriteStringLatin1Null(relativePath);
                File.WriteAllBytes(Path.Combine(ME3Directory.ExecutableFolder, "tocupdate"), bin.ToArray());
                NotifyRunningTOCUpdateRequired();
            }
            return true;
        }

        private static bool UDKSave(UDKPackage pcc, string path, Lock diskIOSyncLock = null)
        {
            if (!pcc.CanSave)
            {
                PackageSaveFailedCallback?.Invoke("Cannot save UDK packages that do not come from the February 2015 version of UDK.");
                return false;
            }
            bool isSaveAs = path != null && path != pcc.FilePath;
            try
            {
                UDKSaveDelegate(pcc, path, isSaveAs, diskIOSyncLock);
            }
            catch (Exception ex) when (!LegendaryExplorerCoreLib.IsDebug)
            {
                PackageSaveFailedCallback?.Invoke($"Error saving {pcc.FilePath}:\n{ex.FlattenException()}");
                return false;
            }
            return true;
        }
    }
}
