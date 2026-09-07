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

        /// <summary>Creates a failed result with the given error message.</summary>
        public static BuildResult Failed(string error)
        {
            return new BuildResult { Success = false, Error = error, Output = new List<string>() };
        }
    }

    /// <summary>
    /// Compiles the worker project using MSBuild in-process. Captures build
    /// output via a custom <see cref="ILogger"/> and returns a
    /// <see cref="BuildResult"/> that gates the reload cycle.
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
        /// Builds the specified project file in-process using MSBuild.
        /// </summary>
        /// <param name="projectPath">Absolute path to a .csproj file.</param>
        /// <returns>A <see cref="BuildResult"/> indicating success or failure.</returns>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="projectPath"/> is null or empty.
        /// </exception>
        public BuildResult Build(string projectPath)
        {
            if (string.IsNullOrWhiteSpace(projectPath))
                throw new ArgumentException("projectPath is required.", "projectPath");

            if (!File.Exists(projectPath))
                return BuildResult.Failed("Project file not found: " + projectPath);

            var logger = new BuildLogger();

            try
            {
                var projectCollection = new ProjectCollection();
                var project = projectCollection.LoadProject(projectPath);

                var parameters = new BuildParameters(projectCollection)
                {
                    Loggers = new[] { logger }
                };

                var request = new BuildRequestData(
                    project.CreateProjectInstance(),
                    new[] { "Build" });

                BuildManager.DefaultBuildManager.Build(parameters, request);

                return new BuildResult
                {
                    Success = logger.Errors.Count == 0,
                    Output = logger.Output
                };
            }
            catch (Exception ex)
            {
                return BuildResult.Failed("MSBuild exception: " + ex.Message);
            }
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
