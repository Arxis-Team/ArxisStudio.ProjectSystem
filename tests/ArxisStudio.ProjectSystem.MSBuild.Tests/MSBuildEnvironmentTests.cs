using System;
using System.IO;
using Xunit;

namespace ArxisStudio.ProjectSystem.MSBuild.Tests;

/// <summary>
/// Locating MSBuild, which is the one thing in this package that cannot be tested against a fake:
/// the whole point is finding a real SDK.
/// </summary>
/// <remarks>
/// <para>
/// The .NET SDK is already required to build this repository and <c>global.json</c> pins which one,
/// so depending on it here is not the "installed IDE or MSBuild workload" the contract rules out —
/// it is the same toolchain the build already used. Nothing here restores packages, reaches the
/// network, or names a machine-specific path.
/// </para>
/// <para>
/// Registration is process-global and cannot be undone, so these tests deliberately do not try. They
/// assert the property that survives repetition: the first call decides, and every later call agrees
/// with it.
/// </para>
/// </remarks>
public sealed class MSBuildEnvironmentTests
{
    [Fact]
    public void Register_FindsAnSdk()
    {
        MSBuildRegistration registration = MSBuildEnvironment.Register();

        Assert.False(string.IsNullOrWhiteSpace(registration.Name));
        Assert.False(string.IsNullOrWhiteSpace(registration.Version));
        Assert.False(registration.Path.IsEmpty);

        // The located directory is the one MSBuild will actually load from, so if it does not hold
        // MSBuild.dll the registration found something that will fail later and less clearly.
        Assert.True(
            File.Exists(Path.Combine(registration.Path.Value, "MSBuild.dll")),
            $"'{registration.Path}' was registered but holds no MSBuild.dll.");
    }

    [Fact]
    public void Register_IsIdempotent()
    {
        MSBuildRegistration first = MSBuildEnvironment.Register();
        MSBuildRegistration second = MSBuildEnvironment.Register();

        Assert.Equal(first, second);
        Assert.True(MSBuildEnvironment.IsRegistered);
        Assert.Equal(first, MSBuildEnvironment.Current);
    }

    /// <summary>
    /// One MSBuild per process, so a later request for a different one is answered with what is
    /// already registered rather than by failing or by quietly loading a second engine.
    /// </summary>
    [Fact]
    public void Register_WithAPathAfterRegistering_ReturnsWhatIsAlreadyThere()
    {
        MSBuildRegistration first = MSBuildEnvironment.Register();
        MSBuildRegistration again = MSBuildEnvironment.Register(first.Path);

        Assert.Equal(first, again);
    }

    [Fact]
    public void Register_WithAnAbsentPath_Throws()
    {
        Assert.Throws<ArgumentException>(() => MSBuildEnvironment.Register(CanonicalPath.None));
    }
}
