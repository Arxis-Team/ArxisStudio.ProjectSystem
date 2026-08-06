using System;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using ArxisStudio.ProjectSystem;
using ArxisStudio.ProjectSystem.NuGet;

namespace FormsDesigner.ViewModels;

/// <summary>A package a feed offered, flattened for a list.</summary>
public sealed record PackageRow(string Id, string Version, string Description)
{
    public static PackageRow From(FoundPackage package) =>
        new(package.Id, package.LatestVersion ?? "—", package.Description ?? string.Empty);
}

public sealed partial class DesignerViewModel
{
    private readonly HttpClient _http = new();

    /// <summary>
    /// Typed as the interface, which is the seam a host with a private source replaces.
    /// </summary>
    /// <remarks>
    /// CA1859 would rather this were the concrete type; taking that advice would hide the very thing
    /// being demonstrated.
    /// </remarks>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance",
        "CA1859:Use concrete types when possible for improved performance",
        Justification = "The interface is the point: it is where a host substitutes its own feed.")]
    private IPackageFeed? _feed;
    private ImmutableArray<PackageVersionMetadata> _metadata = [];

    public ObservableCollection<PackageRow> FoundPackages { get; } = [];

    public ObservableCollection<string> AvailableVersions { get; } = [];

    public RelayCommand SearchPackagesCommand { get; private set; } = null!;

    public RelayCommand InstallPackageCommand { get; private set; } = null!;

    public string PackageQuery
    {
        get;
        set => Set(ref field, value);
    } = "Avalonia";

    public PackageRow? SelectedPackage
    {
        get;
        set
        {
            if (Set(ref field, value))
            {
                RefreshAllCommands();
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
                Advise();
            }
        }
    }

    /// <summary>The worst thing the feed publishes against the selected version.</summary>
    public string PackageAdvice
    {
        get;
        private set => Set(ref field, value);
    } = string.Empty;

    public string PackageAdviceSeverity
    {
        get;
        private set => Set(ref field, value);
    } = string.Empty;

    private void InitialisePackages()
    {
        SearchPackagesCommand = new RelayCommand(() => Run(SearchPackagesAsync), () => !IsBusy);

        InstallPackageCommand = new RelayCommand(
            () => Run(InstallPackageAsync),
            () => !IsBusy && Project() is not null && SelectedPackage is not null && SelectedVersion is not null);
    }

    private void RefreshPackageCommands()
    {
        SearchPackagesCommand.RaiseCanExecuteChanged();
        InstallPackageCommand.RaiseCanExecuteChanged();
        AddPropertyCommand.RaiseCanExecuteChanged();
        DeleteSelectedCommand.RaiseCanExecuteChanged();
    }

    private async Task SearchPackagesAsync()
    {
        _feed ??= new NuGetHttpFeed(_http);

        Log($"Searching {_feed.Name} for “{PackageQuery}”…");

        FeedResult<FoundPackage> result = await _feed.SearchAsync(
            new PackageSearchRequest { Query = PackageQuery, Take = 30 },
            _shutdown.Token);

        FoundPackages.Clear();
        AvailableVersions.Clear();

        if (result.HasErrors)
        {
            ShowDiagnostics(result.Diagnostics);
            Log("  the feed did not answer");

            return;
        }

        foreach (FoundPackage package in result.Items)
        {
            FoundPackages.Add(PackageRow.From(package));
        }

        Log($"  {Describe(result.Items.Length, "package")}");
    }

    private async Task LoadVersionsAsync()
    {
        AvailableVersions.Clear();
        SelectedVersion = null;
        PackageAdvice = string.Empty;
        _metadata = [];

        if (_feed is null || SelectedPackage is null)
        {
            return;
        }

        FeedResult<string> versions = await _feed.GetVersionsAsync(SelectedPackage.Id, _shutdown.Token);

        if (versions.HasErrors)
        {
            ShowDiagnostics(versions.Diagnostics);

            return;
        }

        foreach (string version in versions.Items)
        {
            AvailableVersions.Add(version);
        }

        SelectedVersion = PackageVersions.Latest(versions.Items) ?? versions.Items.FirstOrDefault();

        FeedResult<PackageVersionMetadata> metadata =
            await _feed.GetMetadataAsync(SelectedPackage.Id, _shutdown.Token);

        if (!metadata.HasErrors)
        {
            _metadata = metadata.Items;

            Advise();
        }
    }

    /// <summary>Says whatever argues against the selected version, and nothing when nothing does.</summary>
    private void Advise()
    {
        PackageVersionMetadata? selected =
            _metadata.FirstOrDefault(metadata => metadata.Version == SelectedVersion);

        if (selected is null || !selected.HasWarnings)
        {
            PackageAdvice = selected?.LicenseExpression is { Length: > 0 } licence ? $"Licence: {licence}" : string.Empty;
            PackageAdviceSeverity = string.Empty;

            return;
        }

        if (!selected.Vulnerabilities.IsEmpty)
        {
            PackageAdvice = $"{Describe(selected.Vulnerabilities.Length, "advisory")} against {selected.Version}";
            PackageAdviceSeverity = "Error";

            return;
        }

        PackageAdvice = $"Deprecated: {selected.Deprecation}";
        PackageAdviceSeverity = "Warning";
    }

    /// <summary>
    /// Adds the package to the project the designer is working in, and restores.
    /// </summary>
    /// <remarks>
    /// Update rather than install when the project's own file already declares it, because install
    /// on an existing reference is a documented no-op that reports success. Whether the file itself
    /// declares it is what <see cref="PackageReferenceInfo.Origin"/> answers — the evaluated list is
    /// a superset, and a package arriving from a shared props file is not in the file the editor is
    /// about to rewrite.
    /// </remarks>
    private async Task InstallPackageAsync()
    {
        if (Project() is not { } project || SelectedPackage is not { } package)
        {
            return;
        }

        PackageEditKind kind = project.PackageReferences.Any(reference =>
            reference.Origin == ProjectItemOrigin.Declared
                && string.Equals(reference.PackageId, package.Id, StringComparison.OrdinalIgnoreCase))
            ? PackageEditKind.Update
            : PackageEditKind.Install;

        PackageVersionLayout layout = PackageVersionLayout.From(project);

        Log($"{kind} {package.Id} {SelectedVersion} in {project.Name}  [{layout}]");

        var progress = new Progress<ProjectOperationProgress>(report => Progress = report.Message);

        ProjectOperationResult result = await PackageInstaller.ApplyAndRestoreAsync(
            new PackageEditRequest
            {
                Kind = kind,
                ProjectFilePath = project.ProjectFilePath,
                PackageId = package.Id,
                Version = SelectedVersion,
            },
            _workspace,
            layout,
            progress,
            _shutdown.Token);

        Log($"  {result.Status}");

        ShowDiagnostics(result.Diagnostics);

        if (result.Status == ProjectOperationStatus.Succeeded)
        {
            // The reference changed what the project resolves to, so the assemblies a form may name
            // changed with it. Refreshing is what makes the next load see them.
            await _workspace.RefreshAsync(_shutdown.Token);
        }
    }

    private void ReleaseFeed()
    {
        (_feed as IDisposable)?.Dispose();
        _feed = null;
    }

    private void DisposeHttp() => _http.Dispose();
}
