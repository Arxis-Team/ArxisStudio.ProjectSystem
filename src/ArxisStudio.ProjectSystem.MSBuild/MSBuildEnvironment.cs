using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.Build.Locator;

namespace ArxisStudio.ProjectSystem.MSBuild;

/// <summary>
/// Finds an MSBuild for this process to use, and registers it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Registration is process-global and happens once.</b> That is not this library's choice — MSBuild
/// resolves its assemblies through an <see cref="AppDomain"/> resolver that the locator installs, and
/// there is one of those per process. Two different MSBuilds cannot coexist, so the first
/// registration wins and every later call returns it.
/// </para>
/// <para>
/// Because it is global, it is also explicit. A host that wants to choose the SDK calls
/// <see cref="Register(ArxisStudio.ProjectSystem.CanonicalPath)"/> before anything else touches
/// MSBuild; a host that does not care lets the provider call <see cref="Register()"/> on first use.
/// Nothing here runs from a static constructor, because a library that silently reconfigures its
/// host's process on type load is a library that is very hard to debug.
/// </para>
/// <para>
/// <b>The ordering rule this type exists to respect.</b> The locator must run before any
/// <c>Microsoft.Build</c> type is loaded, and the runtime loads an assembly when a method referring
/// to its types is first entered — not when the line executes. So nothing in this file may name a
/// <c>Microsoft.Build.*</c> type other than the locator's own, and the code that does evaluate
/// projects lives in a separate type that is only entered afterwards. Getting this wrong produces a
/// <see cref="TypeInitializationException"/> or a silently wrong MSBuild, in a place unrelated to
/// the mistake.
/// </para>
/// </remarks>
public static class MSBuildEnvironment
{
    private static readonly Lock Sync = new();

    private static MSBuildRegistration? _current;

    /// <summary>Gets a value indicating whether an MSBuild has been registered in this process.</summary>
    public static bool IsRegistered => Volatile.Read(ref _current) is not null;

    /// <summary>Gets the registration, or <see langword="null"/> if none has happened yet.</summary>
    public static MSBuildRegistration? Current => Volatile.Read(ref _current);

    /// <summary>
    /// Registers the newest MSBuild the locator can find, or returns the existing registration.
    /// </summary>
    /// <returns>What is registered for this process.</returns>
    /// <exception cref="InvalidOperationException">
    /// No .NET SDK could be found. This is an environment failure rather than a project problem, so
    /// it throws here; a provider calling this turns it into a diagnostic, because a consumer opening
    /// a project should be told what is wrong rather than handed an exception.
    /// </exception>
    public static MSBuildRegistration Register()
    {
        if (Volatile.Read(ref _current) is { } already)
        {
            return already;
        }

        lock (Sync)
        {
            if (_current is { } raced)
            {
                return raced;
            }

            // Somebody else in this process may have registered without going through here -- a host
            // that uses the locator directly, or another library that does. Adopting that rather than
            // failing is the only workable answer: there is one resolver per process either way.
            if (MSBuildLocator.IsRegistered)
            {
                return Adopt(Newest() ?? throw NotFound());
            }

            VisualStudioInstance instance = Newest() ?? throw NotFound();

            MSBuildLocator.RegisterInstance(instance);

            return Adopt(instance);
        }
    }

    /// <summary>
    /// Registers a specific SDK directory, or returns the existing registration.
    /// </summary>
    /// <param name="sdkDirectory">The directory holding <c>MSBuild.dll</c>.</param>
    /// <returns>What is registered for this process.</returns>
    /// <exception cref="ArgumentException"><paramref name="sdkDirectory"/> is absent.</exception>
    /// <exception cref="InvalidOperationException">The directory does not hold a usable MSBuild.</exception>
    public static MSBuildRegistration Register(CanonicalPath sdkDirectory)
    {
        if (sdkDirectory.IsEmpty)
        {
            throw new ArgumentException("An SDK directory is needed.", nameof(sdkDirectory));
        }

        if (Volatile.Read(ref _current) is { } already)
        {
            return already;
        }

        lock (Sync)
        {
            if (_current is { } raced)
            {
                return raced;
            }

            if (MSBuildLocator.IsRegistered)
            {
                return Adopt(Newest() ?? throw NotFound());
            }

            try
            {
                MSBuildLocator.RegisterMSBuildPath(sdkDirectory.Value);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                throw new InvalidOperationException(
                    $"'{sdkDirectory}' does not hold a usable MSBuild: {exception.Message}", exception);
            }

            MSBuildRegistration registration = new()
            {
                Name = "Explicit",
                Version = "unknown",
                Path = sdkDirectory,
            };

            Volatile.Write(ref _current, registration);

            return registration;
        }
    }

    private static VisualStudioInstance? Newest()
    {
        IEnumerable<VisualStudioInstance> instances = MSBuildLocator.QueryVisualStudioInstances();

        return instances.OrderByDescending(static instance => instance.Version).FirstOrDefault();
    }

    private static MSBuildRegistration Adopt(VisualStudioInstance instance)
    {
        MSBuildRegistration registration = new()
        {
            Name = instance.Name,
            Version = instance.Version.ToString(),
            Path = CanonicalPath.Create(instance.MSBuildPath),
        };

        Volatile.Write(ref _current, registration);

        return registration;
    }

    private static InvalidOperationException NotFound() =>
        new("No .NET SDK could be found. Install one, or register an explicit path with " +
            $"{nameof(MSBuildEnvironment)}.{nameof(Register)}(CanonicalPath).");
}
