﻿/*
    Copyright (c) 2017 Marcin Szeniak (https://github.com/Klocman/)
    Apache License Version 2.0
*/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Klocman.Extensions;
using Klocman.Forms.Tools;
using Klocman.IO;
using Klocman.Tools;
using UninstallTools.Factory.InfoAdders;
using UninstallTools.Properties;

namespace UninstallTools.Factory
{
    public static class ApplicationUninstallerFactory
    {
        public static IList<ApplicationUninstallerEntry> GetUninstallerEntries(ListGenerationProgress.ListGenerationCallback callback)
        {
            const int totalStepCount = 7;
            var currentStep = 1;

            var infoAdder = new InfoAdderManager();

            // Find msi products
            var msiProgress = new ListGenerationProgress(currentStep++, totalStepCount, Localisation.Progress_MSI);
            callback(msiProgress);
            var msiGuidCount = 0;
            var msiProducts = MsiTools.MsiEnumProducts().DoForEach(x =>
            {
                msiProgress.Inner = new ListGenerationProgress(0, -1, string.Format(Localisation.Progress_MSI_sub, ++msiGuidCount));
                callback(msiProgress);
            }).ToList();

            // Find stuff mentioned in registry
            List<ApplicationUninstallerEntry> registryResults;
            if (UninstallToolsGlobalConfig.ScanRegistry)
            {
                var regProgress = new ListGenerationProgress(currentStep++, totalStepCount,
                    Localisation.Progress_Registry);
                callback(regProgress);
                var registryFactory = new RegistryFactory(msiProducts);
                registryResults = registryFactory.GetUninstallerEntries(report =>
                {
                    regProgress.Inner = report;
                    callback(regProgress);
                }).ToList();

                // Fill in instal llocations for the drive search
                var installLocAddProgress = new ListGenerationProgress(currentStep++, totalStepCount, Localisation.Progress_GatherUninstallerInfo);
                callback(installLocAddProgress);
                var installLocAddCount = 0;
                foreach (var result in registryResults)
                {
                    installLocAddProgress.Inner = new ListGenerationProgress(installLocAddCount++, registryResults.Count, result.DisplayName ?? string.Empty);
                    callback(installLocAddProgress);

                    infoAdder.AddMissingInformation(result, true);
                }
            }
            else
            {
                registryResults = new List<ApplicationUninstallerEntry>();
            }

            // Look for entries on drives, based on info in registry. Need to check for duplicates with other entries later
            List<ApplicationUninstallerEntry> driveResults;
            if (UninstallToolsGlobalConfig.ScanDrives)
            {
                var driveProgress = new ListGenerationProgress(currentStep++, totalStepCount, Localisation.Progress_DriveScan);
                callback(driveProgress);
                var driveFactory = new DirectoryFactory(registryResults);
                driveResults = driveFactory.GetUninstallerEntries(report =>
                {
                    driveProgress.Inner = report;
                    callback(driveProgress);
                }).ToList();
            }
            else
            {
                driveResults = new List<ApplicationUninstallerEntry>();
            }

            // Get misc entries that use fancy logic
            var miscProgress = new ListGenerationProgress(currentStep++, totalStepCount, Localisation.Progress_AppStores);
            callback(miscProgress);
            var otherResults = GetMiscUninstallerEntries(report =>
            {
                miscProgress.Inner = report;
                callback(miscProgress);
            });

            // Handle duplicate entries
            var mergeProgress = new ListGenerationProgress(currentStep++, totalStepCount, Localisation.Progress_Merging);
            callback(mergeProgress);
            var mergedResults = registryResults.ToList();
            mergedResults = MergeResults(mergedResults, otherResults, infoAdder, report =>
            {
                mergeProgress.Inner = report;
                report.TotalCount *= 2;
                report.Message = Localisation.Progress_Merging_Stores;
                callback(mergeProgress);
            });
            // Make sure to merge driveResults last
            mergedResults = MergeResults(mergedResults, driveResults, infoAdder, report =>
            {
                mergeProgress.Inner = report;
                report.CurrentCount += report.TotalCount;
                report.TotalCount *= 2;
                report.Message = Localisation.Progress_Merging_Drives;
                callback(mergeProgress);
            });

            // Fill in any missing information
            var infoAddProgress = new ListGenerationProgress(currentStep, totalStepCount, Localisation.Progress_GeneratingInfo);
            callback(infoAddProgress);
            var infoAddCount = 0;
            foreach (var result in mergedResults)
            {
                infoAddProgress.Inner = new ListGenerationProgress(infoAddCount++, registryResults.Count, result.DisplayName ?? string.Empty);
                callback(infoAddProgress);

                infoAdder.AddMissingInformation(result);
                result.IsValid = CheckIsValid(result, msiProducts);
            }

            //callback(new GetUninstallerListProgress(currentStep, totalStepCount, "Finished"));
            return mergedResults;
        }

        private static List<ApplicationUninstallerEntry> MergeResults(ICollection<ApplicationUninstallerEntry> baseEntries,
            ICollection<ApplicationUninstallerEntry> newResults, InfoAdderManager infoAdder, ListGenerationProgress.ListGenerationCallback progressCallback)
        {
            // Create local copy
            //var baseEntries = baseResults.ToList();
            // Add all of the base results straight away
            var results = new List<ApplicationUninstallerEntry>(baseEntries);
            var progress = 0;
            foreach (var entry in newResults)
            {
                progressCallback(new ListGenerationProgress(progress++, newResults.Count, null));

                var matchedEntries = baseEntries.Where(x => CheckAreEntriesRelated(x, entry)).Take(2).ToList();
                if (matchedEntries.Count == 1)
                {
                    // Prevent setting incorrect UninstallerType
                    if (matchedEntries[0].UninstallPossible)
                        entry.UninstallerKind = UninstallerType.Unknown;

                    infoAdder.CopyMissingInformation(matchedEntries[0], entry);
                    continue;
                }

                // If the entry failed to match to anything, add it to the results
                results.Add(entry);
            }

            return results;
        }

        private static bool CheckIsValid(ApplicationUninstallerEntry target, IEnumerable<Guid> msiProducts)
        {
            if (string.IsNullOrEmpty(target.UninstallerFullFilename))
                return false;

            bool isPathRooted;
            try
            {
                isPathRooted = Path.IsPathRooted(target.UninstallerFullFilename);
            }
            catch (ArgumentException)
            {
                isPathRooted = false;
            }

            if (isPathRooted && File.Exists(target.UninstallerFullFilename))
                return true;

            if (target.UninstallerKind == UninstallerType.Msiexec)
                return msiProducts.Contains(target.BundleProviderKey);

            return !isPathRooted;
        }

        private static bool CheckAreEntriesRelated(ApplicationUninstallerEntry baseEntry, ApplicationUninstallerEntry otherEntry)
        {
            if (PathTools.PathsEqual(baseEntry.InstallLocation, otherEntry.InstallLocation))
                return true;

            if (!string.IsNullOrEmpty(baseEntry.UninstallerLocation) && (!string.IsNullOrEmpty(otherEntry.InstallLocation)
                 && baseEntry.UninstallerLocation.StartsWith(otherEntry.InstallLocation, StringComparison.InvariantCultureIgnoreCase)))
                return true;

            if (!string.IsNullOrEmpty(baseEntry.UninstallString) && !string.IsNullOrEmpty(otherEntry.InstallLocation)
                && baseEntry.UninstallString.Contains(otherEntry.InstallLocation))
                return true;

            if (CompareStrings(baseEntry.Publisher, otherEntry.Publisher))
            {
                if (CompareStrings(baseEntry.DisplayName, otherEntry.DisplayName))
                    return true;
                if (CompareStrings(baseEntry.DisplayNameTrimmed, otherEntry.DisplayNameTrimmed))
                    return true;
                try
                {
                    if (CompareStrings(baseEntry.DisplayNameTrimmed, Path.GetFileName(otherEntry.InstallLocation)))
                        return true;
                }
                catch (Exception ex)
                {
                    Debug.Fail(ex.Message);
                }
            }

            return false;
        }

        private static bool CompareStrings(string a, string b)
        {
            if (a == null || a.Length < 5 || b == null || b.Length < 5)
                return false;

            /* Old algorithm, much slower
            var changesRequired = StringTools.CompareSimilarity(a, b);
            return changesRequired < a.Length / 6;*/

            var changesRequired = Sift4.SimplestDistance(a, b, 3);
            return changesRequired < a.Length / 6;
        }

        private static List<ApplicationUninstallerEntry> GetMiscUninstallerEntries(ListGenerationProgress.ListGenerationCallback progressCallback)
        {
            var otherResults = new List<ApplicationUninstallerEntry>();

            var miscFactories = new Dictionary<IUninstallerFactory, string>();
            if (UninstallToolsGlobalConfig.ScanPreDefined)
                miscFactories.Add(new PredefinedFactory(), Localisation.Progress_AppStores_Templates);
            if (UninstallToolsGlobalConfig.ScanSteam)
                miscFactories.Add(new SteamFactory(), Localisation.Progress_AppStores_Steam);
            if (UninstallToolsGlobalConfig.ScanStoreApps)
                miscFactories.Add(new StoreAppFactory(), Localisation.Progress_AppStores_WinStore);
            if (UninstallToolsGlobalConfig.ScanWinFeatures)
                miscFactories.Add(new WindowsFeatureFactory(), Localisation.Progress_AppStores_WinFeatures);
            if (UninstallToolsGlobalConfig.ScanWinUpdates)
                miscFactories.Add(new WindowsUpdateFactory(), Localisation.Progress_AppStores_WinUpdates);

            var progress = 0;
            foreach (var kvp in miscFactories)
            {
                progressCallback(new ListGenerationProgress(progress++, miscFactories.Count, kvp.Value));
                try
                {
                    otherResults.AddRange(kvp.Key.GetUninstallerEntries(null));
                }
                catch (Exception ex)
                {
                    PremadeDialogs.GenericError(ex);
                }
            }

            return otherResults;
        }

        /// <summary>
        ///     Check if path points to the windows installer program or to a .msi package
        /// </summary>
        /// <param name="path"></param>
        /// <returns></returns>
        public static bool PathPointsToMsiExec(string path)
        {
            if (string.IsNullOrEmpty(path))
                return false;

            return path.ContainsAny(new[] { "msiexec ", "msiexec.exe" }, StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".msi", StringComparison.OrdinalIgnoreCase);
        }
    }
}