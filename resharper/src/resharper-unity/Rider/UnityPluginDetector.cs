﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using JetBrains.Annotations;
using JetBrains.Metadata.Reader.API;
using JetBrains.ProjectModel;
using JetBrains.ProjectModel.Model2.Assemblies.Interfaces;
using JetBrains.ReSharper.Psi.Modules;
using JetBrains.Util;

namespace JetBrains.ReSharper.Plugins.Unity.Rider
{
    [SolutionComponent]
    public class UnityPluginDetector
    {
        public static readonly Version ZeroVersion = new Version();
        
        private readonly ISolution mySolution;
        private readonly ILogger myLogger;
        private static readonly string[] ourPluginCsFile = {"Unity3DRider.cs"};

        public static readonly InstallationInfo ShouldNotInstall = new InstallationInfo(false, FileSystemPath.Empty,
            EmptyArray<FileSystemPath>.Instance, ZeroVersion);

        public UnityPluginDetector(ISolution solution, ILogger logger)
        {
            mySolution = solution;
            myLogger = logger;
        }

        [NotNull]
        public InstallationInfo GetInstallationInfo(FileSystemPath previousInstallationDir)
        {
            myLogger.Verbose("GetInstallationInfo.");
            try
            {
                var assetsDir = mySolution.SolutionFilePath.Directory.CombineWithShortName(ProjectExtensions.AssetsFolder);
                if (!assetsDir.IsAbsolute)
                {
                    myLogger.Warn($"Computed assetsDir {assetsDir} is not absolute. Skipping installation.");
                    return ShouldNotInstall;
                }
                
                if (!assetsDir.ExistsDirectory)
                {
                    myLogger.Info("No Assets directory in the same directory as solution. Skipping installation.");
                    return ShouldNotInstall;
                }
                
                var defaultDir = assetsDir
                    .CombineWithShortName("Plugins")
                    .CombineWithShortName("Editor")
                    .CombineWithShortName("JetBrains");

                InstallationInfo result;
                
                var isFirstInstall = previousInstallationDir.IsNullOrEmpty();
                if (isFirstInstall)
                {
                    // e.g.: fresh checkout from VCS
                    if (TryFindInSolution(mySolution, out result))
                    {
                        return result;
                    }
                    
                    if (TryFindOnDisk(defaultDir, out result))
                    {
                        return result;
                    }

                    // nothing in solution or default directory on first launch.
                    return NotInstalled(defaultDir);
                }

                // default case: all is good, we have cached the installation dir
                if (TryFindOnDisk(previousInstallationDir, out result))
                {
                    return result;
                }
                
                // e.g.: user has moved the plugin from the time it was last installed
                // In such case we will be able to find if solution was regenerated by Unity after that
                if (TryFindInSolution(mySolution, out result))
                {
                    return result;
                }
                
                // not fresh install, but nothing in previously installed dir on in solution
                myLogger.Info("Plugin not found in previous installation dir '{0}' or in solution. Falling back to default directory.", previousInstallationDir);
                
                return NotInstalled(defaultDir);
            }
            catch (Exception e)
            {
                myLogger.LogExceptionSilently(e);
                return ShouldNotInstall;
            }
        }

        private bool TryFindInSolution(ISolution solution, out InstallationInfo result)
        {
            result = ShouldNotInstall;
            var locationDll = solution.GetAllAssemblies()
                .Select(assembly => assembly.GetLocation())
                .FirstOrDefault(t => t.Name == PluginPathsProvider.BasicPluginDllFile);
            
            if (locationDll == null || !locationDll.ExistsFile)
                return false;
            
            var pluginFiles = solution.GetAllProjects().SelectMany(p=>p.
                GetAllProjectFiles(f =>
                {
                    var location = f.Location;
                    if (location == null || !location.ExistsFile) return false;

                    var fileName = location.Name;
                    return ourPluginCsFile.Contains(fileName);
                }))
                .Select(f => f.Location)
                .ToList();

            pluginFiles.Add(locationDll);
            result = ExistingInstallation(pluginFiles);
            return true;
        }

        private bool TryFindOnDisk(FileSystemPath directory, [NotNull] out InstallationInfo result)
        {
            myLogger.Verbose("Looking for plugin on disk: '{0}'", directory);
            var oldPluginFiles = directory
                .GetChildFiles("*.cs")
                .Where(f => ourPluginCsFile.Contains(f.Name))
                .ToList();
            
            var pluginFiles = directory
                .GetChildFiles("*.dll")
                .Where(f => f.Name == PluginPathsProvider.BasicPluginDllFile)
                .ToList();
            
            pluginFiles.AddRange(oldPluginFiles);

            if (pluginFiles.Count == 0)
            {
                result = ShouldNotInstall;
                return false;
            }

            result = ExistingInstallation(pluginFiles);
            return true;
        }

        [NotNull]
        private static InstallationInfo NotInstalled(FileSystemPath pluginDir)
        {
            return new InstallationInfo(true, pluginDir, EmptyArray<FileSystemPath>.Instance, ZeroVersion);
        }

        [NotNull]
        private InstallationInfo ExistingInstallation(List<FileSystemPath> pluginFiles)
        {
            var parentDirs = pluginFiles.Select(f => f.Directory).Distinct().ToList();
            if (parentDirs.Count > 1)
            {
                myLogger.Warn("Plugin files detected in more than one directory.");
                return new InstallationInfo(false, FileSystemPath.Empty, pluginFiles, ZeroVersion);
            }

            if (parentDirs.Count == 0)
            {
                myLogger.Warn("Plugin files do not have parent directory (?).");
                return new InstallationInfo(false, FileSystemPath.Empty, pluginFiles, ZeroVersion);
            }

            var pluginDir = parentDirs[0];

            if (pluginFiles.Count == 1 && pluginFiles[0].Name == PluginPathsProvider.BasicPluginDllFile && pluginFiles[0].ExistsFile)
            {
                try
                {
                    // https://github.com/JetBrains/resharper-unity/issues/541
                    var version = new Version(FileVersionInfo.GetVersionInfo(pluginFiles[0].FullPath).FileVersion);
                    return new InstallationInfo(version != ZeroVersion, pluginDir, pluginFiles, version);
                }
                catch (Exception)
                {
                    // file may be in Solution-csproj, but doesn't exist on disk
                    return new InstallationInfo(true, pluginDir, pluginFiles, ZeroVersion);
                }
            }

            // update from Unity3dRider.cs to dll
            // or
            // both old and new plugins together
            return new InstallationInfo(true, pluginDir, pluginFiles, ZeroVersion);
        }

        public class InstallationInfo
        {
            public readonly bool ShouldInstallPlugin;

            [NotNull]
            public readonly FileSystemPath PluginDirectory;

            [NotNull]
            public readonly ICollection<FileSystemPath> ExistingFiles;

            [NotNull]
            public readonly Version Version;

            public InstallationInfo(bool shouldInstallPlugin, [NotNull] FileSystemPath pluginDirectory,
                [NotNull] ICollection<FileSystemPath> existingFiles, [NotNull] Version version)
            {
                ShouldInstallPlugin = shouldInstallPlugin;
                PluginDirectory = pluginDirectory;
                ExistingFiles = existingFiles;
                Version = version;
            }
        }
    }
}