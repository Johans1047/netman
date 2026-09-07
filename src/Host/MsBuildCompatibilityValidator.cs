using System;
using System.Reflection;
using Microsoft.Build.Evaluation;

namespace HotReloadTool.Host
{
    /// <summary>
    /// Validates that the <c>Microsoft.Build</c> assemblies are available
    /// on the current .NET Framework runtime.
    /// </summary>
    public static class MsBuildCompatibilityValidator
    {
        /// <summary>
        /// Validates that MSBuild is available by checking that the core
        /// types can be resolved from the referenced assemblies.
        /// </summary>
        public static void ValidateRuntime()
        {
            // Verify MSBuild is usable by instantiating a ProjectCollection.
            // This will fail if Microsoft.Build or Microsoft.Build.Framework are not available.
            try
            {
                new ProjectCollection();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "MSBuild is not available. Ensure Microsoft.Build and Microsoft.Build.Framework are referenced.", ex);
            }
        }
    }
}
