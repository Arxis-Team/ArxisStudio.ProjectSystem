using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using ArxisStudio.Markup;
using ArxisStudio.Markup.Xaml.Loader;
using ArxisStudio.ProjectSystem;
using ArxisStudio.ProjectSystem.Markup.Xaml;
using Avalonia.Threading;

namespace ProjectSystem.Ide.ViewModels;

public sealed partial class IdeViewModel
{
    private ProjectResourceMap? _resources;
    private ProjectAssemblyContext? _assemblies;
    private XamlLoadEnvironment? _environment;

    public ObservableCollection<Fact> XamlFacts { get; } = [];

    public string XamlText
    {
        get;
        private set => Set(ref field, value);
    } = string.Empty;

    public string XamlHeader
    {
        get;
        private set => Set(ref field, value);
    } = "Select an AvaloniaResource item to load it through the adapter.";

    /// <summary>
    /// Rebuilds the resource map whenever a snapshot is published.
    /// </summary>
    /// <remarks>
    /// Cheap and worth doing eagerly: it is what turns an <c>avares</c> URI into a file in the
    /// project, and it is what a designer needs before it can follow a <c>StyleInclude</c>.
    /// </remarks>
    private void BuildResourceMap(SolutionSnapshot snapshot)
    {
        _resources = ProjectResourceMap.Create(snapshot);

        Log($"  {Describe(_resources.Count, "avares resource")} mapped to project files");
    }

    /// <summary>
    /// Shows what the adapter can say about the selected document.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The load context is rebuilt when the snapshot it was made from has been superseded, which is
    /// the stale check the adapter exists to make cheap: one integer, rather than a walk over
    /// everything that might have changed.
    /// </para>
    /// <para>
    /// The old one is disposed as the new one replaces it. That is safe <em>here</em> because this
    /// sample never hands a loaded type to anything that outlives the swap; a real designer holds
    /// controls built from those assemblies and must let go of them first.
    /// </para>
    /// </remarks>
    private void ShowXamlFor(SolutionSnapshot snapshot, ProjectSnapshot project, CanonicalPath file)
    {
        if (_assemblies is null || !_assemblies.IsCurrentFor(snapshot) || _assemblies.Project != project.Identity)
        {
            ReleaseXamlEnvironment();

            (_environment, _assemblies) = ProjectXamlEnvironment.CreateFor(snapshot, project.Identity);
        }

        XamlFacts.Clear();

        foreach (RuntimeAssemblyReference assembly in _assemblies.Assemblies.Take(50))
        {
            XamlFacts.Add(new Fact(assembly.Origin.ToString(), assembly.Path.FileName));
        }

        if (file.IsEmpty || !IsMarkup(file))
        {
            XamlHeader = $"Load context “{_assemblies.Name}” — "
                + $"{Describe(_assemblies.Assemblies.Length, "assembly")}. Select an .axaml item to read one.";
            XamlText = string.Empty;

            return;
        }

        Run(() => LoadDocumentAsync(snapshot, file));
    }

    /// <summary>
    /// Reads a document through the environment the adapter built, by the URI Avalonia would name
    /// it with rather than by its path.
    /// </summary>
    private async Task LoadDocumentAsync(SolutionSnapshot snapshot, CanonicalPath file)
    {
        if (_environment is null)
        {
            return;
        }

        // Which project owns it, which is the first thing a tool needs when a document is opened.
        string owner = snapshot.TryGetProjectForFile(file, out ProjectSnapshot? owning)
            ? owning.Name
            : "(no project)";

        Uri uri = AvaresUriFor(snapshot, file) ?? new Uri(file.Value);

        MarkupSource? source = await _environment.SourceProvider
            .TryGetSourceAsync(uri, _shutdown.Token);

        if (source is null)
        {
            XamlHeader = $"{file.FileName} — owned by {owner} — the environment does not know {uri}";
            XamlText = string.Empty;

            return;
        }

        SourceText text = await source.GetTextAsync(_shutdown.Token);

        XamlHeader = $"{file.FileName} — owned by {owner} — resolved as {uri}";
        XamlText = text.ToString();
    }

    /// <summary>The avares URI a project file is named by, when the map knows one.</summary>
    /// <remarks>
    /// Found by asking the map which file each URI names and matching, because the mapping runs the
    /// other way: a project item does not carry the URI it will be embedded under.
    /// </remarks>
    private Uri? AvaresUriFor(SolutionSnapshot snapshot, CanonicalPath file)
    {
        if (_resources is null || !snapshot.TryGetProjectForFile(file, out ProjectSnapshot? project))
        {
            return null;
        }

        string assembly = project.Properties.TryGetValue("AssemblyName", out string? name) && name.Length > 0
            ? name
            : project.Name;

        if (!file.StartsWith(project.ProjectDirectory))
        {
            return null;
        }

        string relative = file.Value[project.ProjectDirectory.Value.Length..].Replace('\\', '/').TrimStart('/');
        var candidate = new Uri($"avares://{assembly}/{relative}");

        return _resources.TryGetFile(candidate, null, out CanonicalPath mapped) && mapped == file
            ? candidate
            : null;
    }

    private static bool IsMarkup(CanonicalPath file) =>
        file.Extension.Equals(".axaml", StringComparison.OrdinalIgnoreCase)
            || file.Extension.Equals(".xaml", StringComparison.OrdinalIgnoreCase);

    private void ReleaseXamlEnvironment()
    {
        _assemblies?.Dispose();
        _assemblies = null;
        _environment = null;
    }

    /// <summary>
    /// Turns Markup's diagnostics into the project model's, so one list can show both.
    /// </summary>
    /// <remarks>
    /// Unused by the panels above and kept because it is half the reason the adapter exists: a
    /// designer produces both kinds at once and a user does not care which library noticed. The text
    /// is passed so the position survives — Markup counts characters and the project model counts
    /// lines, and without the text the translation drops the position rather than inventing one.
    /// </remarks>
    private void ShowMarkupDiagnostics(System.Collections.Generic.IEnumerable<MarkupDiagnostic> diagnostics,
        string documentText, CanonicalPath file)
    {
        foreach (ProjectDiagnostic translated in
            ProjectMarkupDiagnostics.ToProject(diagnostics, documentText, file))
        {
            Dispatcher.UIThread.Post(() => Diagnostics.Add(DiagnosticRow.From(translated)));
        }
    }
}
