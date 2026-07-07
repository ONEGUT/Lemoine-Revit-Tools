namespace LemoineTools.Framework
{
    /// <summary>
    /// Build metadata surfaced in the Settings footer.
    /// <para>
    /// The values are stamped at compile time by the <c>GenerateBuildInfo</c> MSBuild
    /// target (see <c>LemoineTools.csproj</c>), which writes
    /// <c>obj\…\BuildInfo.Generated.cs</c> implementing <see cref="InitGenerated"/>.
    /// The defaults below are the fallback used when that target did not run — e.g.
    /// building from a source archive with no git available — so the type always
    /// resolves and the UI never crashes on missing metadata.
    /// </para>
    /// </summary>
    public static partial class BuildInfo
    {
        /// <summary>Local build timestamp, e.g. <c>2026-06-05 14:32</c>.</summary>
        public static string BuildTime { get; private set; } = "unknown";

        /// <summary>Git branch the build was produced from.</summary>
        public static string GitBranch { get; private set; } = "unknown";

        /// <summary>Short git commit hash the build was produced from.</summary>
        public static string GitCommit { get; private set; } = "unknown";

        // Implemented by the generated partial. When the build target does not run,
        // this classic partial method has no body and the call is elided — leaving the
        // fallback values above untouched.
        static partial void InitGenerated();

        static BuildInfo() => InitGenerated();

        /// <summary>One-line summary for display: build time, branch and commit.</summary>
        public static string Summary =>
            $"Build {BuildTime}  ·  {GitBranch} @ {GitCommit}";
    }
}
