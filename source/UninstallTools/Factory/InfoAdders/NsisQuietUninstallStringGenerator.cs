/*
    Copyright (c) 2017 Marcin Szeniak (https://github.com/Klocman/)
    Apache License Version 2.0
*/

using System.IO;
using Klocman.Extensions;

namespace UninstallTools.Factory.InfoAdders
{
    public class NsisQuietUninstallStringGenerator : IMissingInfoAdder
    {
        private static readonly string UninstallerAutomatizerPath = Path.Combine(UninstallToolsGlobalConfig.AssemblyLocation, @"UninstallerAutomatizer.exe");
        private static readonly bool UninstallerAutomatizerExists = File.Exists(UninstallerAutomatizerPath);

        public void AddMissingInformation(ApplicationUninstallerEntry target)
        {
            if (target.QuietUninstallPossible || target.UninstallerKind != UninstallerType.Nsis ||
                string.IsNullOrEmpty(target.UninstallString))
                return;

            if (!UninstallToolsGlobalConfig.QuietAutomatization || !UninstallerAutomatizerExists)
                return;

            var nsisCommandStart = $"\"{UninstallerAutomatizerPath}\" {nameof(UninstallerType.Nsis)} ";

            nsisCommandStart = nsisCommandStart.AppendIf(UninstallToolsGlobalConfig.QuietAutomatizationKillStuck, "/K ");

            target.QuietUninstallString = nsisCommandStart.Append(target.UninstallString);
        }

        public string[] RequiredValueNames { get; } = {
            nameof(ApplicationUninstallerEntry.UninstallString),
            nameof(ApplicationUninstallerEntry.UninstallerKind)
        };

        public bool RequiresAllValues { get; } = true;
        public bool AlwaysRun { get; } = false;

        public string[] CanProduceValueNames { get; } = {
            nameof(ApplicationUninstallerEntry.QuietUninstallString)
        };

        public InfoAdderPriority Priority { get; } = InfoAdderPriority.Normal;
    }
}