using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using ArxisStudio.ProjectSystem;
using ArxisStudio.ProjectSystem.NuGet;

namespace ProjectSystem.Ide.ViewModels;

/// <summary>A package a feed offered, flattened for a list.</summary>
public sealed record PackageRow(string Id, string Version, string Description, string Authors)
{
    public static PackageRow From(FoundPackage package) => new(
        package.Id,
        package.LatestVersion ?? "—",
        package.Description ?? string.Empty,
        package.Authors ?? string.Empty);
}

public sealed partial class IdeViewModel
{
    private readonly HttpClient _http = new();
    private readonly Latest _versions = new();

    /// <summary>
    /// Typed as the interface on purpose, which is the seam the library exists to offer.
    /// </summary>
    /// <remarks>
    /// A host with a private source, a credential provider or an offline mirror implements
    /// <see cref="IPackageFeed"/> and everything below keeps working — so the sample programs
    /// against it even though it only ever constructs the one implementation. CA1859 would rather
    /// this were the concrete type; taking that advice would hide the very thing being demonstrated.
    /// </remarks>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance",
        "CA1859:Use concrete types when possible for improved performance",
        Justification = "The interface is the point: it is where a host substitutes its own feed.")]
    private IPackageFeed? _feed;

    public ObservableCollection<PackageRow> FoundPackages { get; } = [];

    public ObservableCollection<string> AvailableVersions { get; } = [];

    public RelayCommand SearchPackagesCommand { get; }

    public RelayCommand InstallPackageCommand { get; }

    public RelayCommand UninstallPackageCommand { get; }

    public string PackageQuery
    {
        get;
        set => Set(ref field, value);
    } = "Avalonia";

    public bool IncludePrerelease
    {
        get;
        set => Set(ref field, value);
    }

    public PackageRow? SelectedPackage
    {
        get;
        set
        {
            if (Set(ref field, value))
            {
                RefreshAllCommands();

                // Detached: listing versions is a consequence of the selection, and a selection can
                // move while an install is still in flight. Through Run it was dropped, leaving the
                // version list describing the previously selected package.
                RunDetached(LoadVersionsAsync);
            }
        }
    }

    public string? SelectedVersion
    {
        get;
        set
        {
            if (Set(ref field, value))
            {
                RefreshAllCommands();
            }
        }
    }

    /// <summary>
    /// Lets go of the feed, which holds a gate serialising the one fetch of the service index.
    /// </summary>
    /// <remarks>
    /// Not the <see cref="HttpClient"/>: that is this class's, and it is disposed with it.
    /// </remarks>
    private void ReleaseFeed()
    {
        (_feed as IDisposable)?.Dispose();
        _feed = null;
    }

    private ProjectSnapshot? SelectedProject =>
        _workspace.CurrentSnapshot is { } snapshot
            && Current is { } node
            && snapshot.TryGetProject(node.Project, out ProjectSnapshot? project)
                ? project
                : null;

    private bool CanInstallPackage() =>
        !IsBusy && SelectedProject is not null && SelectedPackage is not null && SelectedVersion is not null;

    private bool CanUninstallPackage() =>
        !IsBusy && SelectedProject is not null && SelectedPackage is not null;

    /// <summary>
    /// Searches a feed.
    /// </summary>
    /// <remarks>
    /// The result keeps two answers apart that an empty list would merge: nothing matched, and
    /// nobody answered. On an aeroplane the second is what happens, and it reads very differently.
    /// </remarks>
    private async Task SearchPackagesAsync()
    {
        _feed ??= new NuGetHttpFeed(_http);

        Log($"Searching {_feed.Name} for “{PackageQuery}”…");

        FeedResult<FoundPackage> result = await _feed.SearchAsync(
            new PackageSearchRequest
            {
                Query = PackageQuery,
                IncludePrerelease = IncludePrerelease,
                Take = 30,
            },
            _shutdown.Token);

        FoundPackages.Clear();
        AvailableVersions.Clear();

        if (result.HasErrors)
        {
            ShowDiagnostics(result.Diagnostics);
            Log($"  the feed did not answer — {result.Diagnostics.Length} diagnostic(s)");

            return;
        }

        foreach (FoundPackage package in result.Items)
        {
            FoundPackages.Add(PackageRow.From(package));
        }

        Log($"  {Describe(result.Items.Length, "package")}");
    }

    /// <summary>
    /// Lists a package's versions, newest first, and preselects the one an install should default
    /// to — the latest stable unless prereleases were asked for.
    /// </summary>
    private async Task LoadVersionsAsync()
    {
        long ticket = _versions.Take();

        AvailableVersions.Clear();
        SelectedVersion = null;

        if (_feed is null || SelectedPackage is null)
        {
            return;
        }

        FeedResult<string> versions = await _feed.GetVersionsAsync(SelectedPackage.Id, _shutdown.Token);

        if (!_versions.IsCurrent(ticket))
        {
            // The selection moved on while the feed was answering. Filling the list now would
            // describe a package the user is no longer looking at.
            return;
        }

        if (versions.HasErrors)
        {
            ShowDiagnostics(versions.Diagnostics);

            return;
        }

        foreach (string version in versions.Items)
        {
            AvailableVersions.Add(version);
        }

        SelectedVersion = PackageVersions.Latest(versions.Items, IncludePrerelease)
            ?? versions.Items.FirstOrDefault();
    }

    /// <summary>
    /// Installs the selected package, or changes its version when the project already has it.
    /// </summary>
    /// <remarks>
    /// The kind has to be chosen rather than assumed. Install on a package the project already
    /// references is a documented no-op: the editor returns <c>APS4004</c> as a <em>warning</em>
    /// with a succeeded status, so a button labelled "install or update" that always installed
    /// reported success and changed nothing whenever it was asked to update.
    /// </remarks>
    private Task InstallPackageAsync() => ChangePackageAsync(
        SelectedProject is { } project && Declares(project, SelectedPackage?.Id)
            ? PackageEditKind.Update
            : PackageEditKind.Install);

    private Task UninstallPackageAsync() => ChangePackageAsync(PackageEditKind.Uninstall);

    /// <summary>Whether the project already declares a package, compared the way NuGet compares.</summary>
    private static bool Declares(ProjectSnapshot project, string? packageId) =>
        packageId is { Length: > 0 }
            && project.PackageReferences.Any(
                reference => string.Equals(reference.PackageId, packageId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Changes what the selected project references, and restores.
    /// </summary>
    /// <remarks>
    /// <see cref="PackageVersionLayout.From"/> is the part worth noticing: under central package
    /// management the reference and the version go into two different files, and which files those
    /// are comes off the evaluated project rather than out of a filename convention. If the restore
    /// fails, the edit is put back — so a failed install leaves a project that still builds.
    /// </remarks>
    private async Task ChangePackageAsync(PackageEditKind kind)
    {
        if (SelectedProject is not { } project || SelectedPackage is not { } package)
        {
            return;
        }

        PackageVersionLayout layout = PackageVersionLayout.From(project);

        Log($"{kind} {package.Id} {SelectedVersion} in {project.Name}  [{layout}]");

        var progress = new Progress<ProjectOperationProgress>(report => Progress = report.Message);

        ProjectOperationResult result = await PackageInstaller.ApplyAndRestoreAsync(
            new PackageEditRequest
            {
                Kind = kind,
                ProjectFilePath = project.ProjectFilePath,
                PackageId = package.Id,
                Version = kind == PackageEditKind.Uninstall ? null : SelectedVersion,
            },
            _workspace,
            layout,
            progress,
            _shutdown.Token);

        Log($"  {result.Status}");

        ShowDiagnostics(result.Diagnostics);

        if (result.Status == ProjectOperationStatus.Succeeded)
        {
            await RefreshAsync();
        }
    }
}
