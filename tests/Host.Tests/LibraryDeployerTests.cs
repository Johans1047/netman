using System;
using System.IO;
using Xunit;

namespace HotReloadTool.Host.Tests
{
    /// <summary>
    /// Tests for <see cref="LibraryDeployer"/> — build-output deployment with
    /// retry on locked destinations. Uses short injected retry delays so the
    /// suite stays fast.
    /// </summary>
    public class LibraryDeployerTests
    {
        private static string CreateTempDirectory()
        {
            string dir = Path.Combine(Path.GetTempPath(), "hotreload-deploy-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        [Fact]
        public void Deploy_Success_CopiesFileAndReportsDestination()
        {
            string root = CreateTempDirectory();
            try
            {
                string source = Path.Combine(root, "src", "Lib.dll");
                Directory.CreateDirectory(Path.GetDirectoryName(source));
                File.WriteAllText(source, "assembly-content");

                string deployDir = Path.Combine(root, "site", "Bin");
                Directory.CreateDirectory(deployDir);

                var deployer = new LibraryDeployer(maxAttempts: 3, retryDelayMilliseconds: 10);
                DeployResult result = deployer.Deploy(source, deployDir);

                Assert.True(result.Success, "Deploy should succeed. Error: " + result.Error);
                Assert.Equal(1, result.Attempts);
                Assert.Equal(Path.Combine(deployDir, "Lib.dll"), result.DestinationPath);
                Assert.True(File.Exists(result.DestinationPath));
                Assert.Equal("assembly-content", File.ReadAllText(result.DestinationPath));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void Deploy_DestinationLocked_RetriesThenReturnsStructuredFailure()
        {
            string root = CreateTempDirectory();
            try
            {
                string source = Path.Combine(root, "Lib.dll");
                File.WriteAllText(source, "new-content");

                string deployDir = Path.Combine(root, "Bin");
                Directory.CreateDirectory(deployDir);
                string destination = Path.Combine(deployDir, "Lib.dll");
                File.WriteAllText(destination, "old-content");

                var deployer = new LibraryDeployer(maxAttempts: 3, retryDelayMilliseconds: 10);
                DeployResult result;

                // Lock the destination with FileShare.None — File.Copy must fail
                // with a sharing violation (IOException) on every attempt.
                using (var lockStream = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    result = deployer.Deploy(source, deployDir);
                }

                Assert.False(result.Success, "Deploy against a locked destination must fail.");
                Assert.False(string.IsNullOrWhiteSpace(result.Error), "Structured failure must carry an error message.");
                Assert.Equal(3, result.Attempts);
                Assert.Null(result.DestinationPath);

                // Both files must be untouched by the failed deploys.
                Assert.Equal("old-content", File.ReadAllText(destination));
                Assert.Equal("new-content", File.ReadAllText(source));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void Deploy_MissingSource_ReturnsStructuredFailure()
        {
            string root = CreateTempDirectory();
            try
            {
                string missingSource = Path.Combine(root, "Missing.dll");
                string deployDir = Path.Combine(root, "Bin");

                var deployer = new LibraryDeployer(maxAttempts: 3, retryDelayMilliseconds: 10);
                DeployResult result = deployer.Deploy(missingSource, deployDir);

                Assert.False(result.Success, "Deploy with a missing source must fail structurally.");
                Assert.Contains("not found", result.Error);
                Assert.Equal(0, result.Attempts);
                Assert.Null(result.DestinationPath);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void Deploy_MissingDeployDirectory_IsCreated()
        {
            string root = CreateTempDirectory();
            try
            {
                string source = Path.Combine(root, "Lib.dll");
                File.WriteAllText(source, "assembly-content");

                string deployDir = Path.Combine(root, "does", "not", "exist", "Bin");
                Assert.False(Directory.Exists(deployDir));

                var deployer = new LibraryDeployer(maxAttempts: 3, retryDelayMilliseconds: 10);
                DeployResult result = deployer.Deploy(source, deployDir);

                Assert.True(result.Success, "Deploy should create the directory. Error: " + result.Error);
                Assert.True(Directory.Exists(deployDir), "Missing deploy directory should be auto-created.");
                Assert.True(File.Exists(Path.Combine(deployDir, "Lib.dll")));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
