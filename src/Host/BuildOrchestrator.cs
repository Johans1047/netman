using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Execution;
using Microsoft.Build.Framework;
using Microsoft.Build.Logging;

namespace HotReloadTool.Host
{
    /// <summary>
    /// Result of an MSBuild compilation attempt. Returned by
    /// <see cref="Orchestration.BuildOrchestrator.Build"/> to gate the reload cycle.
    /// </summary>
    public class BuildResult
    {
        /// <summary><c>true</c> when MSBuild reported OverallResult = Success.</summary>
        public bool Success { get; set; }

        /// <summary>Captured MSBuild output lines (warnings, errors, messages).</summary>
        public List<string> Output { get; set; }

        /// <summary>Error message when the build could not be invoked at all.</summary>
        public string Error { get; set; }

        /// <summary>
        /// Absolute path of the primary output assembly (resolved from the
        /// MSBuild <c>TargetPath</c> property). Populated only on success;
        /// <c>null</c> otherwise. Additive — process-mode callers can ignore it.
        /// </summary>
        public string OutputAssemblyPath { get; set; }

        /// <summary>Creates a failed result with the given error message.</summary>
        public static BuildResult Failed(string error)
        {
            return new BuildResult { Success = false, Error = error, Output = new List<string>() };
        }
    }

    /// <summary>
    /// Compiles the worker project using MSBuild in-process. Captures build
    /// output via a custom <see cref="ILogger"/> and returns a
    /// <see cref="BuildResult"/> that gates the reload cycle. Supports building
    /// a single project (.csproj/.vbproj) in-process, or building a solution
    /// (.sln) by shelling out to msbuild and resolving the named project's
    /// output assembly.
    /// </summary>
    /// <remarks>
    /// Build failure MUST NOT terminate the old worker — callers check
    /// <see cref="BuildResult.Success"/> before proceeding to drain/restart.
    /// Uses <see cref="Microsoft.Build.Execution.BuildManager"/> for
    /// automatic incremental builds.
    /// </remarks>
    public class BuildOrchestrator : IBuildOrchestrator
    {
        /// <summary>
        /// Builds the specified project in-process via MSBuild (satisfies
        /// <see cref="IBuildOrchestrator"/>). For solution builds use the
        /// two-argument overload <see cref="Build(string, string)"/>.
        /// </summary>
        /// <param name="projectPath">Absolute path to a .csproj or .vbproj file.</param>
        /// <returns>A <see cref="BuildResult"/> indicating success or failure.</returns>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="projectPath"/> is null or empty.
        /// </exception>
        public BuildResult Build(string projectPath)
        {
            return Build(projectPath, null);
        }

        /// <summary>
        /// Builds the specified project or solution file. For solutions, a
        /// <paramref name="solutionProjectName"/> must be supplied so the
        /// output assembly can be located after the build.
        /// </summary>
        /// <param name="projectPath">Absolute path to a .csproj, .vbproj, or .sln file.</param>
        /// <param name="solutionProjectName">
        /// Project name within the solution whose output assembly to resolve.
        /// Required when <paramref name="projectPath"/> is a .sln; ignored otherwise.
        /// </param>
        /// <returns>A <see cref="BuildResult"/> indicating success or failure.</returns>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="projectPath"/> is null or empty.
        /// </exception>
        public BuildResult Build(string projectPath, string solutionProjectName)
        {
            if (string.IsNullOrWhiteSpace(projectPath))
                throw new ArgumentException("projectPath is required.", "projectPath");

            if (!File.Exists(projectPath))
                return BuildResult.Failed("Project file not found: " + projectPath);

            string extension = Path.GetExtension(projectPath);
            if (string.Equals(extension, ".sln", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(solutionProjectName))
                    return BuildResult.Failed("solutionProjectName is required when building a solution.");

                var logger = new BuildLogger();
                return BuildSolution(projectPath, solutionProjectName, logger);
            }

            var projectLogger = new BuildLogger();
            return BuildProject(projectPath, projectLogger);
        }

        /// <summary>
        /// Builds a single .csproj/.vbproj in-process via MSBuild. VB projects
        /// build identically to C# projects through MSBuild — no special handling.
        /// </summary>
        private BuildResult BuildProject(string projectPath, BuildLogger logger)
        {
            try
            {
                var projectCollection = new ProjectCollection();
                var project = projectCollection.LoadProject(projectPath);

                var parameters = new BuildParameters(projectCollection)
                {
                    Loggers = new[] { logger }
                };

                var projectInstance = project.CreateProjectInstance();
                var request = new BuildRequestData(
                    projectInstance,
                    new[] { "Build" });

                BuildManager.DefaultBuildManager.Build(parameters, request);

                var result = new BuildResult
                {
                    Success = logger.Errors.Count == 0,
                    Output = logger.Output
                };

                if (result.Success)
                    result.OutputAssemblyPath = ResolveOutputAssemblyPath(projectInstance);

                return result;
            }
            catch (Exception ex)
            {
                return BuildResult.Failed("MSBuild exception: " + ex.Message);
            }
        }

        /// <summary>
        /// Builds a solution (.sln) by shelling out to msbuild, then resolves the
        /// named project's output assembly by searching under the solution directory,
        /// preferring paths containing \bin\Debug\ over \bin\Release\.
        /// </summary>
        private BuildResult BuildSolution(string solutionPath, string solutionProjectName, BuildLogger logger)
        {
            try
            {
                string arguments = "\"" + solutionPath + "\" /p:Configuration=Debug /nologo";
                var buildInfo = new ProcessStartInfo
                {
                    FileName = "msbuild",
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                string output;
                string errorOutput;
                int exitCode;
                using (var build = Process.Start(buildInfo))
                {
                    output = build.StandardOutput.ReadToEnd();
                    errorOutput = build.StandardError.ReadToEnd();
                    build.WaitForExit();
                    exitCode = build.ExitCode;
                }

                if (!string.IsNullOrEmpty(output))
                    logger.Output.Add(output);
                if (!string.IsNullOrEmpty(errorOutput))
                    logger.Output.Add(errorOutput);

                if (exitCode != 0)
                {
                    logger.Errors.Add("MSBuild exited with code " + exitCode + ".");
                    return new BuildResult
                    {
                        Success = false,
                        Output = logger.Output,
                        Error = "Solution build failed (exit code " + exitCode + ")."
                    };
                }

                // Resolve the named project's output assembly. Prefer Debug over Release.
                string solDir = Path.GetDirectoryName(Path.GetFullPath(solutionPath));
                var candidates = Directory.GetFiles(solDir, solutionProjectName + ".dll", SearchOption.AllDirectories);

                string debugMatch = null;
                string releaseMatch = null;
                string firstMatch = null;
                foreach (string candidate in candidates)
                {
                    if (firstMatch == null)
                        firstMatch = candidate;

                    // The configuration folder is the last directory segment
                    // (e.g. "Debug" in "...\bin\Debug\Assembly.dll").
                    string configurationFolder = Path.GetFileName(Path.GetDirectoryName(candidate));

                    if (debugMatch == null
                        && string.Equals(configurationFolder, "Debug", StringComparison.OrdinalIgnoreCase))
                    {
                        debugMatch = candidate;
                        break; // Debug found — best match
                    }

                    if (releaseMatch == null
                        && string.Equals(configurationFolder, "Release", StringComparison.OrdinalIgnoreCase))
                    {
                        releaseMatch = candidate;
                    }
                }

                string outputPath = debugMatch ?? releaseMatch ?? firstMatch;

                if (outputPath == null)
                    return BuildResult.Failed("Output assembly '" + solutionProjectName + ".dll' not found after building solution.");

                return new BuildResult
                {
                    Success = true,
                    Output = logger.Output,
                    OutputAssemblyPath = Path.GetFullPath(outputPath)
                };
            }
            catch (Exception ex)
            {
                return BuildResult.Failed("MSBuild exception: " + ex.Message);
            }
        }

        /// <summary>
        /// Resolves the absolute path of the primary output assembly from the
        /// MSBuild <see cref="ProjectInstance"/> used for the build.
        /// <c>TargetPath</c> may be relative (e.g. <c>bin\Debug\X.dll</c>), so
        /// it is combined with the project directory and normalized.
        /// Returns <c>null</c> when the property is empty.
        /// </summary>
        /// <param name="projectInstance">The evaluated project instance.</param>
        /// <returns>Absolute path of the output assembly, or <c>null</c>.</returns>
        private static string ResolveOutputAssemblyPath(ProjectInstance projectInstance)
        {
            string targetPath = projectInstance.GetPropertyValue("TargetPath");
            if (string.IsNullOrWhiteSpace(targetPath))
                return null;

            // ProjectInstance exposes the project directory as "Directory"
            // (Evaluation.Project has "DirectoryPath"; ProjectInstance does not).
            return Path.GetFullPath(Path.Combine(projectInstance.Directory, targetPath));
        }
    }

    /// <summary>
    /// Minimal <see cref="ILogger"/> implementation that captures MSBuild output
    /// lines into lists for structured consumption by the Host.
    /// </summary>
    internal class BuildLogger : ILogger
    {
        /// <summary>Captured informational and warning messages.</summary>
        public readonly List<string> Output = new List<string>();

        /// <summary>Captured error messages — presence indicates build failure.</summary>
        public readonly List<string> Errors = new List<string>();

        public LoggerVerbosity Verbosity { get; set; }
        public string Parameters { get; set; }

        public void Initialize(IEventSource eventSource)
        {
            if (eventSource == null)
                return;

            eventSource.ErrorRaised += OnError;
            eventSource.MessageRaised += OnMessage;
            eventSource.WarningRaised += OnWarning;
        }

        public void Shutdown()
        {
        }

        private void OnError(object sender, BuildErrorEventArgs e)
        {
            Errors.Add("ERROR: " + e.Message);
            Output.Add("ERROR: " + e.Message);
        }

        private void OnMessage(object sender, BuildMessageEventArgs e)
        {
            if (e.Importance == MessageImportance.High)
                Output.Add(e.Message);
        }

        private void OnWarning(object sender, BuildWarningEventArgs e)
        {
            Output.Add("WARN: " + e.Message);
        }
    }
}
