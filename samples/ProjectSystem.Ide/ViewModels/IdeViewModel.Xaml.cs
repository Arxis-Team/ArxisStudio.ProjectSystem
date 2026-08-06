using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using ArxisStudio.Markup;
using ArxisStudio.Markup.Xaml;
using ArxisStudio.Markup.Xaml.Loader;
using ArxisStudio.ProjectSystem;
using ArxisStudio.ProjectSystem.Markup.Xaml;

namespace ProjectSystem.Ide.ViewModels;

public sealed partial class IdeViewModel
{
    private readonly List<DiagnosticRow> _markupRows = [];
    private readonly Latest _document = new();

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

        // Taken even when nothing is loaded below, so a document still being read is abandoned when
        // the selection moves to something that is not one.
        long ticket = _document.Take();

        if (file.IsEmpty || !IsMarkup(file))
        {
            XamlHeader = $"Load context “{_assemblies.Name}” — "
                + $"{Describe(_assemblies.Assemblies.Length, "assembly")}. Select an .axaml item to read one.";
            XamlText = string.Empty;

            return;
        }

        // Detached rather than through Run: this is a consequence of a publication, and a publication
        // arrives while a load is still awaiting the workspace. Run's busy guard dropped it exactly
        // then, leaving one document's text under another document's header.
        RunDetached(() => LoadDocumentAsync(snapshot, file, ticket));
    }

    /// <summary>
    /// Reads a document through the environment the adapter built, by the URI Avalonia would name
    /// it with rather than by its path.
    /// </summary>
    private async Task LoadDocumentAsync(SolutionSnapshot snapshot, CanonicalPath file, long ticket)
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

        if (!_document.IsCurrent(ticket))
        {
            return;
        }

        if (source is null)
        {
            XamlHeader = $"{file.FileName} — owned by {owner} — the environment does not know {uri}";
            XamlText = string.Empty;

            return;
        }

        SourceText text = await source.GetTextAsync(_shutdown.Token);

        if (!_document.IsCurrent(ticket))
        {
            return;
        }

        XamlHeader = $"{file.FileName} — owned by {owner} — resolved as {uri}";
        XamlText = text.ToString();

        // Lexing and parsing a document is per-selection work and belongs where the existence probe
        // went, for the same reason: clicking through a folder should not stall the frame.
        ImmutableArray<MarkupDiagnostic> found = await Task.Run(
            () => XamlDocument.Parse(text).GetDiagnostics().ToImmutableArray(),
            _shutdown.Token);

        if (!_document.IsCurrent(ticket))
        {
            return;
        }

        ShowMarkupDiagnostics(found, XamlText, file);
    }

    /// <summary>
    /// Empties the panel and supersedes whatever document was being read for it.
    /// </summary>
    /// <remarks>
    /// Taking a ticket is the point: a read in flight checks it after every await, so this is what
    /// stops one landing on a selection that is no longer a document.
    /// </remarks>
    private void ClearXamlPanel()
    {
        _document.Take();

        XamlFacts.Clear();
        XamlText = string.Empty;
        XamlHeader = "Select an AvaloniaResource item to load it through the adapter.";
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

        // Escaped per segment, because a file name is not a URI. A '#' would otherwise start a
        // fragment and truncate the path, and a '%' would begin an escape that means something else;
        // the map unescapes what it is given, so this round-trips.
        string escaped = string.Join('/', relative.Split('/').Select(Uri.EscapeDataString));

        var candidate = new Uri($"avares://{assembly}/{escaped}");

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
    /// Turns Markup's diagnostics into the project model's, so one list shows both.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is half the reason the adapter exists: a designer produces both kinds at once and a user
    /// does not care which library noticed. The text is passed so the position survives — Markup
    /// counts characters and the project model counts lines, and without the text the translation
    /// drops the position rather than inventing one.
    /// </para>
    /// <para>
    /// The rows of the previously shown document are taken out first, so selecting through a folder
    /// does not pile one file's syntax errors on the next. They are taken out <b>by reference</b>:
    /// <see cref="DiagnosticRow"/> is a record, so <c>Remove</c> would take the first row equal by
    /// value, and a row this method did not add is not this method's to remove. A row a load has
    /// already cleared is simply not found, which is why no other bookkeeping is needed.
    /// </para>
    /// </remarks>
    private void ShowMarkupDiagnostics(
        IEnumerable<MarkupDiagnostic> diagnostics,
        string documentText,
        CanonicalPath file)
    {
        for (int i = Diagnostics.Count - 1; i >= 0; i--)
        {
            if (_markupRows.Any(row => ReferenceEquals(row, Diagnostics[i])))
            {
                Diagnostics.RemoveAt(i);
            }
        }

        _markupRows.Clear();

        foreach (ProjectDiagnostic translated in
            ProjectMarkupDiagnostics.ToProject(diagnostics, documentText, file))
        {
            var row = DiagnosticRow.From(translated);

            _markupRows.Add(row);
            Diagnostics.Add(row);
        }
    }
}
