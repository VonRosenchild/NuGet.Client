// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using NuGet.Common;
using NuGet.Packaging.Core;
using NuGet.Test.Utility;
using NuGet.XPlat.FuncTest;
using Xunit;
using NuGet.Common;
using Newtonsoft.Json.Linq;
using System.Text;
using System.IO.Enumeration;
using System.Runtime.CompilerServices;

namespace Dotnet.Integration.Test
{
    public class MsbuildIntegrationTestFixture : IDisposable
    {
        private readonly TestDirectory _cliDirectory;
        private readonly TestDirectory _templateDirectory;
        private readonly string _dotnetCli = DotnetCliUtil.GetDotnetCli();
        internal readonly string TestDotnetCli;
        internal readonly string MsBuildSdksPath;
        private readonly Dictionary<string, string> _processEnvVars = new Dictionary<string, string>();

        public MsbuildIntegrationTestFixture()
        {
            _cliDirectory = CopyLatestCliForPack();
            var dotnetExecutableName = RuntimeEnvironmentHelper.IsWindows ? "dotnet.exe" : "dotnet";
            TestDotnetCli = Path.Combine(_cliDirectory, dotnetExecutableName);
            var sdkPaths = Directory.GetDirectories(Path.Combine(_cliDirectory, "sdk"));

            //Temporary patching process for System.Security.Cryptography.Pkcs.dll and deps.json files
            //Will be removed when shipping
            var patchDir = sdkPaths.Where(path => path.Split(Path.DirectorySeparatorChar).Last().StartsWith("5")).First();
            TempPatching(patchDir);

            string packagePath = null;
            if (RuntimeEnvironmentHelper.IsWindows)
            {
                packagePath = Path.Combine(Environment.GetEnvironmentVariable("USERPROFILE"), ".nuget", "packages");
            }
            else
            {
                packagePath = Path.Combine(Environment.GetEnvironmentVariable("HOME"), ".local", "share", "NuGet", "v3-cache");
            }

            var pathCopyFrom = Path.Combine(packagePath, "system.security.cryptography.pkcs", "5.0.0-alpha1.19473.1", "lib", "netstandard2.1");
            var pathCopyTo = patchDir;
            var dlls = new string[1] { "System.Security.Cryptography.Pkcs.dll" };
            TempCopyNewlyAddedDlls(dlls, pathCopyFrom, pathCopyTo);
            //end of Temporary patching

#if NETCORE5_0
            MsBuildSdksPath = Path.Combine(
             sdkPaths.Where(path => path.Split(Path.DirectorySeparatorChar).Last().StartsWith("5")).First()
             , "Sdks");
#elif NETCORE3_0
            MsBuildSdksPath = Path.Combine(
             sdkPaths.Where(path => path.Split(Path.DirectorySeparatorChar).Last().StartsWith("3")).First()
             , "Sdks");
#else
            MsBuildSdksPath = Path.Combine(
             sdkPaths.Where(path => path.Split(Path.DirectorySeparatorChar).Last().StartsWith("2")).First()
             , "Sdks");
#endif
            _templateDirectory = TestDirectory.Create();
            _processEnvVars.Add("MSBuildSDKsPath", MsBuildSdksPath);
            _processEnvVars.Add("UseSharedCompilation", "false");
            _processEnvVars.Add("DOTNET_MULTILEVEL_LOOKUP", "0");
            _processEnvVars.Add("MSBUILDDISABLENODEREUSE ", "true");
        }

        /// <summary>
        /// Creates a new dotnet project of the specified type. Note that restore/build are not run when this command is invoked. 
        /// That is because the project generation is cached.
        /// </summary>
        internal void CreateDotnetNewProject(string solutionRoot, string projectName, string args = "console", int timeOut = 60000)
        {
            args = args.Trim();
            var workingDirectory = Path.Combine(solutionRoot, projectName);
            if (!Directory.Exists(workingDirectory))
            {
                Directory.CreateDirectory(workingDirectory);
            }
            var templateDirectory = new DirectoryInfo(Path.Combine(_templateDirectory.Path, args));

            if (!templateDirectory.Exists)
            {
                templateDirectory.Create();

                var result = CommandRunner.Run(TestDotnetCli,
                    templateDirectory.FullName,
                    $"new {args}",
                    waitForExit: true,
                    timeOutInMilliseconds: timeOut,
                    environmentVariables: _processEnvVars);
                Assert.True(result.Success, $"Creating project failed with following log information :\n {result.AllOutput}");
                Assert.True(string.IsNullOrWhiteSpace(result.Errors), $"Creating project failed with following message in error stream :\n {result.AllOutput}");
                // Delete the obj directory because it contains assets generated by running restore at dotnet new <template> time. 
                // These are not relevant when the project is renamed 
                Directory.Delete(Path.Combine(templateDirectory.FullName, "obj"), recursive: true);
            }
            CopyFromTemplate(projectName, args, workingDirectory, templateDirectory);
        }

        private static void CopyFromTemplate(string projectName, string args, string workingDirectory, DirectoryInfo templateDirectoryInfo)
        {
            foreach (var file in templateDirectoryInfo.GetFiles())
            {
                File.Copy(file.FullName, Path.Combine(workingDirectory, file.Name));
            }
            File.Move(
                Path.Combine(workingDirectory, args + ".csproj"),
                Path.Combine(workingDirectory, projectName + ".csproj"));
        }

        internal void CreateDotnetToolProject(string solutionRoot, string projectName, string targetFramework, string rid, string source, IList<PackageIdentity> packages, int timeOut = 60000)
        {
            var workingDirectory = Path.Combine(solutionRoot, projectName);
            if (!Directory.Exists(workingDirectory))
            {
                Directory.CreateDirectory(workingDirectory);
            }

            var projectFileName = Path.Combine(workingDirectory, projectName + ".csproj");

            var restorePackagesPath = Path.Combine(workingDirectory, "tools", "packages");
            var restoreSolutionDirectory = workingDirectory;
            var msbuildProjectExtensionsPath = Path.Combine(workingDirectory);
            var packageReference = string.Empty;
            foreach (var package in packages)
            {
                packageReference = string.Concat(packageReference, Environment.NewLine, $@"<PackageReference Include=""{ package.Id }"" Version=""{ package.Version.ToString()}""/>");
            }

            var projectFile = $@"<Project Sdk=""Microsoft.NET.Sdk"">
                <PropertyGroup><RestoreProjectStyle>DotnetToolReference</RestoreProjectStyle>
                <OutputType>Exe</OutputType>
                <TargetFramework> {targetFramework} </TargetFramework>
                <RuntimeIdentifier>{rid} </RuntimeIdentifier> 
                <!-- Things that do change-->
                <RestorePackagesPath>{restorePackagesPath}</RestorePackagesPath>
                <RestoreSolutionDirectory>{restoreSolutionDirectory}</RestoreSolutionDirectory>
                <MSBuildProjectExtensionsPath>{msbuildProjectExtensionsPath}</MSBuildProjectExtensionsPath>
                <RestoreSources>{source}</RestoreSources>
                <!--Things that don't change -->
                <DisableImplicitFrameworkReferences>true</DisableImplicitFrameworkReferences>
                <RestoreFallbackFolders>clear</RestoreFallbackFolders>
                <RestoreAdditionalProjectSources></RestoreAdditionalProjectSources>
                <RestoreAdditionalProjectFallbackFolders></RestoreAdditionalProjectFallbackFolders>
                <RestoreAdditionalProjectFallbackFoldersExcludes></RestoreAdditionalProjectFallbackFoldersExcludes>
              </PropertyGroup>
                <ItemGroup>
                    {packageReference}
                </ItemGroup>
            </Project>";

            try
            {
                File.WriteAllText(projectFileName, projectFile);
            }
            catch
            {
                // ignore
            }
            Assert.True(File.Exists(projectFileName));
        }

        internal CommandRunnerResult RestoreToolProject(string workingDirectory, string projectName, string args = "")
        {

            var result = CommandRunner.Run(TestDotnetCli,
                workingDirectory,
                $"restore {projectName}.csproj {args}",
                waitForExit: true,
                environmentVariables: _processEnvVars);
            return result;
        }

        internal void RestoreProject(string workingDirectory, string projectName, string args)
            => RestoreProjectOrSolution(workingDirectory, $"{projectName}.csproj", args);

        internal void RestoreSolution(string workingDirectory, string solutionName, string args)
            => RestoreProjectOrSolution(workingDirectory, $"{solutionName}.sln", args);

        private void RestoreProjectOrSolution(string workingDirectory, string fileName, string args)
        {

            var envVar = new Dictionary<string, string>();
            envVar.Add("MSBuildSDKsPath", MsBuildSdksPath);

            var result = CommandRunner.Run(TestDotnetCli,
                workingDirectory,
                $"restore {fileName} {args}",
                waitForExit: true,
                environmentVariables: _processEnvVars);
            Assert.True(result.Item1 == 0, $"Restore failed with following log information :\n {result.AllOutput}");
            Assert.True(result.Item3 == "", $"Restore failed with following message in error stream :\n {result.AllOutput}");
        }

        /// <summary>
        /// dotnet.exe args
        /// </summary>
        internal CommandRunnerResult RunDotnet(string workingDirectory, string args, bool ignoreExitCode = false)
        {
            var result = CommandRunner.Run(TestDotnetCli,
                workingDirectory,
                args,
                waitForExit: true,
                environmentVariables: _processEnvVars);

            if (!ignoreExitCode)
            {
                Assert.True(result.ExitCode == 0, $"dotnet.exe {args} command failed with following log information :\n {result.AllOutput}");
            }

            return result;
        }

        internal CommandRunnerResult PackProject(string workingDirectory, string projectName, string args, string nuspecOutputPath = "obj", bool validateSuccess = true)
            => PackProjectOrSolution(workingDirectory, $"{projectName}.csproj", args, nuspecOutputPath, validateSuccess);

        internal CommandRunnerResult PackSolution(string workingDirectory, string solutionName, string args, string nuspecOutputPath = "obj", bool validateSuccess = true)
            => PackProjectOrSolution(workingDirectory, $"{solutionName}.sln", args, nuspecOutputPath, validateSuccess);

        private CommandRunnerResult PackProjectOrSolution(string workingDirectory, string file, string args, string nuspecOutputPath, bool validateSuccess)
        {

            var result = CommandRunner.Run(TestDotnetCli,
                workingDirectory,
                $"pack {file} {args} /p:NuspecOutputPath={nuspecOutputPath}",
                waitForExit: true,
                environmentVariables: _processEnvVars);
            if (validateSuccess)
            {
                Assert.True(result.Item1 == 0, $"Pack failed with following log information :\n {result.AllOutput}");
                Assert.True(result.Item3 == "", $"Pack failed with following message in error stream :\n {result.AllOutput}");
            }
            return result;
        }

        internal void BuildProject(string workingDirectory, string projectName, string args)
        {

            var result = CommandRunner.Run(TestDotnetCli,
                workingDirectory,
                $"msbuild {projectName}.csproj {args} /p:AppendRuntimeIdentifierToOutputPath=false",
                waitForExit: true,
                environmentVariables: _processEnvVars);
            Assert.True(result.Item1 == 0, $"Build failed with following log information :\n {result.AllOutput}");
            Assert.True(result.Item3 == "", $"Build failed with following message in error stream :\n {result.AllOutput}");
        }

        private TestDirectory CopyLatestCliForPack()
        {
            var cliDirectory = TestDirectory.Create();
            CopyLatestCliToTestDirectory(cliDirectory);
            UpdateCliWithLatestNuGetAssemblies(cliDirectory);
            return cliDirectory;
        }

        private void CopyLatestCliToTestDirectory(string destinationDir)
        {
            var cliDir = Path.GetDirectoryName(_dotnetCli);

            //Create sub-directory structure in destination
            foreach (var directory in Directory.GetDirectories(cliDir, "*", SearchOption.AllDirectories))
            {
                var destDir = destinationDir + directory.Substring(cliDir.Length);
                if (!Directory.Exists(destDir))
                {
                    Directory.CreateDirectory(destDir);
                }
            }

            var lastWriteTime = DateTime.Now.AddDays(-2);

            //Copy files recursively to destination directories
            foreach (var fileName in Directory.GetFiles(cliDir, "*", SearchOption.AllDirectories))
            {
                var destFileName = destinationDir + fileName.Substring(cliDir.Length);
                File.Copy(fileName, destFileName);
                File.SetLastWriteTime(destFileName, lastWriteTime);
            }
        }

        private void UpdateCliWithLatestNuGetAssemblies(string cliDirectory)
        {
            var artifactsDirectory = DotnetCliUtil.GetArtifactsDirectoryInRepo();
            var pathToSdkInCli = Path.Combine(
                    Directory.GetDirectories(Path.Combine(cliDirectory, "sdk"))
                        .First());
            const string configuration =
#if DEBUG
                "Debug";
#else
                "Release";
#endif
            const string toolsetVersion = "16.0";
            CopyPackSdkArtifacts(artifactsDirectory, pathToSdkInCli, configuration, toolsetVersion);
            CopyRestoreArtifacts(artifactsDirectory, pathToSdkInCli, configuration, toolsetVersion);
        }

        private void CopyRestoreArtifacts(string artifactsDirectory, string pathToSdkInCli, string configuration, string toolsetVersion)
        {
            const string restoreProjectName = "NuGet.Build.Tasks";
            const string restoreTargetsName = "NuGet.targets";
            var sdkDependencies = new List<string> { restoreProjectName, "NuGet.Versioning", "NuGet.Protocol", "NuGet.ProjectModel", "NuGet.Packaging", "NuGet.LibraryModel", "NuGet.Frameworks", "NuGet.DependencyResolver.Core", "NuGet.Configuration", "NuGet.Common", "NuGet.Commands", "NuGet.CommandLine.XPlat", "NuGet.Credentials" };

            // Copy rest of the NuGet assemblies.
            foreach (var projectName in sdkDependencies)
            {
                var projectArtifactsFolder = new DirectoryInfo(Path.Combine(artifactsDirectory, projectName, toolsetVersion, "bin", configuration));

                var artifactDirectories = projectArtifactsFolder.EnumerateDirectories();

                IEnumerable<DirectoryInfo> frameworkArtifactFolders = artifactDirectories.Where(folder => folder.FullName.Contains("netstandard2.1") || folder.FullName.Contains("netcoreapp5.0"));
                if (!frameworkArtifactsFolders.Any())
                {
                    frameworkArtifactsFolders = frameworkArtifactsFolders.Where(folder => folder.FullName.Contains("netstandard2.0"));
                }
                foreach (var frameworkArtifactsFolder in frameworkArtifactsFolders)
                {
                    var fileName = projectName + ".dll";
                    File.Copy(
                            sourceFileName: Path.Combine(frameworkArtifactsFolder.FullName, fileName),
                            destFileName: Path.Combine(pathToSdkInCli, fileName),
                            overwrite: true);
                    // Copy the restore targets.
                    if (projectName.Equals(restoreProjectName))
                    {
                        File.Copy(
                            sourceFileName: Path.Combine(frameworkArtifactsFolder.FullName, restoreTargetsName),
                            destFileName: Path.Combine(pathToSdkInCli, restoreTargetsName),
                            overwrite: true);
                    }
                }
            }
        }

        private void CopyPackSdkArtifacts(string artifactsDirectory, string pathToSdkInCli, string configuration, string toolsetVersion)
        {
            var pathToPackSdk = Path.Combine(pathToSdkInCli, "Sdks", "NuGet.Build.Tasks.Pack");

            const string packProjectName = "NuGet.Build.Tasks.Pack";
            const string packTargetsName = "NuGet.Build.Tasks.Pack.targets";
            // Copy the pack SDK.
            var packProjectCoreArtifactsDirectory = new DirectoryInfo(Path.Combine(artifactsDirectory, packProjectName, toolsetVersion, "bin", configuration)).EnumerateDirectories("netstandard*").Single();
            var packAssemblyDestinationDirectory = Path.Combine(pathToPackSdk, "CoreCLR");
            // Be smart here so we don't have to call ILMerge in the VS build. It takes ~15s total.
            // In VisualStudio, simply use the non il merged version.
            var ilMergedPackDirectoryPath = Path.Combine(packProjectCoreArtifactsDirectory.FullName, "ilmerge");
            if (Directory.Exists(ilMergedPackDirectoryPath))
            {
                var packFileName = packProjectName + ".dll";
                // Only use the il merged assembly if it's newer than the build.
                DateTime packAssemblyCreationDate = File.GetCreationTimeUtc(Path.Combine(packProjectCoreArtifactsDirectory.FullName, packFileName));
                DateTime ilMergedPackAssemblyCreationDate = File.GetCreationTimeUtc(Path.Combine(ilMergedPackDirectoryPath, packFileName));
                if (ilMergedPackAssemblyCreationDate > packAssemblyCreationDate)
                {
                    FileUtility.Replace(
                        sourceFileName: Path.Combine(packProjectCoreArtifactsDirectory.FullName, "ilmerge", packFileName),
                        destFileName: Path.Combine(packAssemblyDestinationDirectory, packFileName));
                }
                else
                {
                    foreach (var assembly in packProjectCoreArtifactsDirectory.EnumerateFiles("*.dll"))
                    {
                        File.Copy(
                            sourceFileName: assembly.FullName,
                            destFileName: Path.Combine(packAssemblyDestinationDirectory, assembly.Name),
                            overwrite: true);
                    }
                }
                // Copy the pack targets
                var packTargetsSource = Path.Combine(packProjectCoreArtifactsDirectory.FullName, packTargetsName);
                var targetsDestination = Path.Combine(pathToPackSdk, "build", packTargetsName);
                var targetsDestinationCrossTargeting = Path.Combine(pathToPackSdk, "buildCrossTargeting", packTargetsName);
                File.Copy(packTargetsSource, targetsDestination, overwrite: true);
                File.Copy(packTargetsSource, targetsDestinationCrossTargeting, overwrite: true);
            }
        }

        public void Dispose()
        {
            RunDotnet(Path.GetDirectoryName(TestDotnetCli), "build-server shutdown");
            KillDotnetExe(TestDotnetCli);
            _cliDirectory.Dispose();
            _templateDirectory.Dispose();
        }

        private static void KillDotnetExe(string pathToDotnetExe)
        {
            var processes = Process.GetProcessesByName("dotnet")
                .Where(t => string.Compare(t.MainModule.FileName, Path.GetFullPath(pathToDotnetExe), ignoreCase: true) == 0);
            var testDirProcesses = Process.GetProcesses()
                .Where(t => t.MainModule.FileName.StartsWith(TestFileSystemUtility.NuGetTestFolder, StringComparison.OrdinalIgnoreCase));
            try
            {
                if (processes != null)
                {
                    foreach (var process in processes)
                    {
                        if (string.Compare(process.MainModule.FileName, Path.GetFullPath(pathToDotnetExe), true) == 0)
                        {
                            process.Kill();
                        }
                    }
                }

                if (testDirProcesses != null)
                {
                    foreach (var process in testDirProcesses)
                    {
                        process.Kill();
                    }
                }

            }
            catch { }
        }

        /// <summary>
        /// Depth-first recursive delete, with handling for descendant 
        /// directories open in Windows Explorer or used by another process
        /// </summary>
        private static void DeleteDirectory(string path)
        {
            foreach (string directory in Directory.GetDirectories(path))
            {
                DeleteDirectory(directory);
            }

            try
            {
                Directory.Delete(path, true);
            }
            catch (IOException)
            {
                Directory.Delete(path, true);
            }
            catch (UnauthorizedAccessException)
            {
                var MaxTries = 100;

                for (var i = 0; i < MaxTries; i++)
                {

                    try
                    {
                        Directory.Delete(path, recursive: true);
                        break;
                    }
                    catch (UnauthorizedAccessException) when (i < (MaxTries - 1))
                    {
                        Thread.Sleep(100);
                    }
                }
            }
            catch
            {

            }
        }

        //temporary added methods for processing deps.json files for patching
        private void TempPatching(string patchDir)
        {
            var prefix = patchDir;
            string[] filenames = new string[3] { "dotnet.deps.json", "MSBuild.deps.json", "NuGet.CommandLine.XPlat.deps.json" };
            string[] fullnames = new string[3];
            for (int i = 0; i < filenames.Length; i++)
            {
                fullnames[i] = prefix + Path.DirectorySeparatorChar + filenames[i];
            }
            TempPatchingDepsJsonForNewlyAddedDlls(fullnames);

        }
        private void TempCopyNewlyAddedDlls(string[] dlls, string pathCopyFrom, string PathCopyTo)
        {
            foreach (var dll in dlls)
            {
                var copyFromFullName = Path.Combine(pathCopyFrom, dll);
                var copyToFullName = Path.Combine(PathCopyTo, dll);
                File.Copy(copyFromFullName, copyToFullName);
            }
        }
        private void TempPatchingDepsJsonForNewlyAddedDlls(string[] filePaths)
        {
            string nugetBuildTasksName = "NuGet.Build.Tasks/5.3.0-rtm.6251";
            foreach (string file in filePaths)
            {
                JObject JsonFile = GetJson(file);

                JObject targets = null;
                targets = JsonFile.GetJObjectProperty<JObject>("targets");

                JObject netcoreapp50 = null;
                netcoreapp50 = targets.GetJObjectProperty<JObject>(".NETCoreApp,Version=v5.0");

                JObject NuGet_Build_Tasks = null;
                NuGet_Build_Tasks = netcoreapp50.GetJObjectProperty<JObject>(nugetBuildTasksName);

                JObject runtime = null;
                runtime = NuGet_Build_Tasks.GetJObjectProperty<JObject>("runtime");

                var jproperty = new JProperty("lib/netstandard2.1/System.Security.Cryptography.Pkcs.dll",
                            new JObject
                                {
                                    new JProperty("assemblyVersion","4.0.4.0"),
                                    new JProperty("fileVersion", "5.0.19.47301"),
                                }
                            );
                runtime.Add(jproperty);
                NuGet_Build_Tasks["runtime"] = runtime;
                netcoreapp50[nugetBuildTasksName] = NuGet_Build_Tasks;
                targets[".NETCoreApp,Version=v5.0"] = netcoreapp50;
                JsonFile["targets"] = targets;
                SaveJson(JsonFile, file);
            }
        }
        private JObject GetJson(string jsonFilePath)
        {
            try
            {
                return FileUtility.SafeRead(jsonFilePath, (stream, filePath) =>
                {
                    using (var reader = new StreamReader(stream))
                    {
                        return JObject.Parse(reader.ReadToEnd());
                    }
                });
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    string.Format(jsonFilePath, ex.Message), ex);
            }
        }

        private void SaveJson(JObject json, string jsonFilePath)
        {
            FileUtility.Replace((outputPath) =>
            {
                using (var writer = new StreamWriter(outputPath, false, Encoding.UTF8))
                {
                    writer.Write(json.ToString());
                }
            },
            jsonFilePath);
        }
    }
}
