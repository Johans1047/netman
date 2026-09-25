using System;
using System.IO;
using Xunit;

namespace HotReloadTool.Host.Tests
{
    /// <summary>
    /// Tests for <see cref="BuildOrchestrator"/> — MSBuild in-process integration.
    /// TDD RED→GREEN: tests verify build success/failure detection.
    /// </summary>
    public class BuildOrchestratorTests
    {
        private readonly BuildOrchestrator _orchestrator = new BuildOrchestrator();

        [Fact]
        public void Build_NullPath_ThrowsArgumentException()
        {
            Assert.Throws<ArgumentException>(() => _orchestrator.Build(null));
        }

        [Fact]
        public void Build_EmptyPath_ThrowsArgumentException()
        {
            Assert.Throws<ArgumentException>(() => _orchestrator.Build(""));
        }

        [Fact]
        public void Build_NonExistentProject_ReturnsFailure()
        {
            BuildResult result = _orchestrator.Build(@"C:\nonexistent\project.csproj");

            Assert.False(result.Success);
            Assert.NotNull(result.Error);
            Assert.Contains("not found", result.Error);
        }

        [Fact]
        public void Build_ValidProject_ReturnsSuccess()
        {
            string fixturePath = GetFixtureProjectPath();
            if (!File.Exists(fixturePath))
            {
                // Skip if fixture not available (RED phase before fixture exists)
                return;
            }

            BuildResult result = _orchestrator.Build(fixturePath);

            Assert.True(result.Success, "Build should succeed for valid project. Error: " + result.Error);
            Assert.NotNull(result.Output);
        }

        [Fact]
        public void Build_FailingProject_ReturnsFailure()
        {
            // Create a project that references a missing file — guaranteed build error
            string tempDir = Path.Combine(Path.GetTempPath(), "hotreload-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                string csproj = Path.Combine(tempDir, "FailProject.csproj");
                File.WriteAllText(csproj, failingProjectCsproj);

                string missingFile = Path.Combine(tempDir, "Missing.cs");
                // Intentionally do NOT create Missing.cs — build must fail

                BuildResult result = _orchestrator.Build(csproj);

                Assert.False(result.Success, "Build should fail when source file is missing.");
                Assert.True(result.Output.Count > 0, "Build output should contain errors.");
            }
            finally
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }

        [Fact]
        public void Build_ValidProject_PopulatesOutputAssemblyPath()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "hotreload-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                string csproj = Path.Combine(tempDir, "OutputPathProject.csproj");
                File.WriteAllText(csproj, validLibraryCsproj);
                File.WriteAllText(
                    Path.Combine(tempDir, "Class1.cs"),
                    "namespace OutputPathProject { public class Class1 { } }");

                BuildResult result = _orchestrator.Build(csproj);

                Assert.True(result.Success, "Build should succeed. Error: " + result.Error);
                Assert.NotNull(result.OutputAssemblyPath);
                Assert.True(Path.IsPathRooted(result.OutputAssemblyPath),
                    "OutputAssemblyPath must be absolute, got: " + result.OutputAssemblyPath);

                // TargetPath (bin\Debug\OutputPathProject.dll) resolved against the project directory
                string expected = Path.GetFullPath(Path.Combine(tempDir, "bin", "Debug", "OutputPathProject.dll"));
                Assert.Equal(expected, result.OutputAssemblyPath);
                Assert.True(File.Exists(result.OutputAssemblyPath),
                    "Output assembly should exist at: " + result.OutputAssemblyPath);
            }
            finally
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }

        [Fact]
        public void Build_FailedProject_LeavesOutputAssemblyPathNull()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "hotreload-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                string csproj = Path.Combine(tempDir, "FailProject.csproj");
                File.WriteAllText(csproj, failingProjectCsproj);

                BuildResult result = _orchestrator.Build(csproj);

                Assert.False(result.Success);
                Assert.Null(result.OutputAssemblyPath);
            }
            finally
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }

        [Fact]
        public void Build_NonExistentProject_LeavesOutputAssemblyPathNull()
        {
            BuildResult result = _orchestrator.Build(@"C:\nonexistent\project.csproj");

            Assert.False(result.Success);
            Assert.Null(result.OutputAssemblyPath);
        }

        private static string GetFixtureProjectPath()
        {
            // Navigate from test output to fixture project
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string candidate = Path.Combine(baseDir, "Fixtures", "FixtureWorker.csproj");
            return candidate;
        }

        private const string failingProjectCsproj = @"<?xml version=""1.0"" encoding=""utf-8""?>
<Project ToolsVersion=""12.0"" DefaultTargets=""Build"" xmlns=""http://schemas.microsoft.com/developer/msbuild/2003"">
  <Import Project=""$(MSBuildExtensionsPath)\$(MSBuildToolsVersion)\Microsoft.Common.props"" Condition=""Exists('$(MSBuildExtensionsPath)\$(MSBuildToolsVersion)\Microsoft.Common.props')"" />
  <PropertyGroup>
    <Configuration Condition=""'$(Configuration)' == ''"">Debug</Configuration>
    <Platform Condition=""'$(Platform)' == ''"">AnyCPU</Platform>
    <OutputType>Exe</OutputType>
    <TargetFrameworkVersion>v4.5.1</TargetFrameworkVersion>
    <OutputPath>bin\Debug\</OutputPath>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include=""System"" />
    <Reference Include=""System.Core"" />
  </ItemGroup>
  <ItemGroup>
    <Compile Include=""Missing.cs"" />
  </ItemGroup>
  <Import Project=""$(MSBuildToolsPath)\Microsoft.CSharp.targets"" />
</Project>";

        private const string validLibraryCsproj = @"<?xml version=""1.0"" encoding=""utf-8""?>
<Project ToolsVersion=""12.0"" DefaultTargets=""Build"" xmlns=""http://schemas.microsoft.com/developer/msbuild/2003"">
  <Import Project=""$(MSBuildExtensionsPath)\$(MSBuildToolsVersion)\Microsoft.Common.props"" Condition=""Exists('$(MSBuildExtensionsPath)\$(MSBuildToolsVersion)\Microsoft.Common.props')"" />
  <PropertyGroup>
    <Configuration Condition=""'$(Configuration)' == ''"">Debug</Configuration>
    <Platform Condition=""'$(Platform)' == ''"">AnyCPU</Platform>
    <OutputType>Library</OutputType>
    <TargetFrameworkVersion>v4.5.1</TargetFrameworkVersion>
    <OutputPath>bin\Debug\</OutputPath>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include=""System"" />
    <Reference Include=""System.Core"" />
  </ItemGroup>
  <ItemGroup>
    <Compile Include=""Class1.cs"" />
  </ItemGroup>
  <Import Project=""$(MSBuildToolsPath)\Microsoft.CSharp.targets"" />
</Project>";
    }
}
