using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using ArxisStudio.Markup;
using ArxisStudio.Markup.Xaml;
using ArxisStudio.Markup.Xaml.Design;
using ArxisStudio.Markup.Xaml.Loader;
using ArxisStudio.ProjectSystem;
using ArxisStudio.ProjectSystem.Markup.Xaml;
using Avalonia;
using Avalonia.Threading;

namespace FormsDesigner.ViewModels;

/// <summary>A form the open project contains, whether or not it is on the canvas.</summary>
public sealed record FormFile(string Name, string Folder, CanonicalPath Path, ProjectIdentity Project)
{
    public override string ToString() => Name;
}

public sealed partial class DesignerViewModel
{
    private ProjectAssemblyContext? _assemblies;
    private XamlLoadEnvironment? _environment;
    private ProjectIdentity _environmentProject;

    /// <summary>Every markup document the open project declares.</summary>
    public ObservableCollection<FormFile> ProjectForms { get; } = [];

    /// <summary>Files an open is already under way for.</summary>
    private readonly HashSet<CanonicalPath> _opening = [];

    /// <summary>What the project pane has selected.</summary>
    /// <remarks>
    /// Selecting is state; opening is an act, and <see cref="OpenFile"/> performs it. Assigning
    /// used to open, and that is how one double-click opened the same form twice: the caller set
    /// this and then opened, not knowing the setter had already started an open of its own, and two
    /// detached opens both read an empty <c>Forms</c> before either added to it. Rebuilding the
    /// project tree assigns this too, and a refresh has no business reopening anything.
    /// </remarks>
    public FormFile? SelectedProjectForm
    {
        get;
        set => Set(ref field, value);
    }

    private bool CanSave => ActiveForm is { IsDirty: true };

    /// <summary>
    /// Lists the project's markup, which is what a designer's project panel is for.
    /// </summary>
    /// <remarks>
    /// From <c>Items</c> rather than from a directory walk, so a document the project does not
    /// include is correctly absent — a file sitting in the folder that nothing compiles is not part
    /// of the application, and offering it would invite editing something that has no effect.
    /// </remarks>
    private void BuildProjectTree(SolutionSnapshot snapshot)
    {
        FormFile? previous = SelectedProjectForm;

        ProjectForms.Clear();

        foreach (ProjectSnapshot project in snapshot.Projects)
        {
            foreach (ProjectItem item in project.Items)
            {
                if (item.FullPath.IsEmpty
                    || !IsMarkup(item.FullPath)
                    || !IsDesignable(item.FullPath)
                    || !item.FullPath.StartsWith(project.ProjectDirectory))
                {
                    continue;
                }

                string relative = item.FullPath.Value[project.ProjectDirectory.Value.Length..]
                    .Replace('\\', '/')
                    .TrimStart('/');

                int slash = relative.LastIndexOf('/');

                ProjectForms.Add(new FormFile(
                    item.FullPath.FileName,
                    slash < 0 ? project.Name : $"{project.Name}/{relative[..slash]}",
                    item.FullPath,
                    project.Identity));
            }
        }

        BuildProjectPane(snapshot);

        Log($"  {Describe(ProjectForms.Count, "form")} in the project");

        if (previous is not null && ProjectForms.FirstOrDefault(f => f.Path == previous.Path) is { } same)
        {
            SelectedProjectForm = same;
        }
    }

    private static bool IsMarkup(CanonicalPath file) =>
        file.Extension.Equals(".axaml", StringComparison.OrdinalIgnoreCase)
            || file.Extension.Equals(".xaml", StringComparison.OrdinalIgnoreCase);

    /// <summary>Avalonia's own roots that declare no control, and so no form.</summary>
    private static readonly string[] NotAForm =
        ["Application", "ResourceDictionary", "Styles", "Style", "ControlTheme"];

    /// <summary>
    /// Whether a markup document is one this designer can put on the canvas.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A project's markup is not all forms. <c>App.axaml</c> declares the application — the styles
    /// and resources every form is then drawn with — and a resource dictionary or a style sheet
    /// declares no control at all. Loading one produces an object that is not a <c>Control</c>, so
    /// the canvas showed a card with an error written across it, the tab strip offered it beside the
    /// real forms, and the status bar counted it as one.
    /// </para>
    /// <para>
    /// The root element is what answers this, and it is read with an XML reader rather than by
    /// parsing the document: the answer is the first tag, and a designer that parsed every markup
    /// file in the solution to build a list would pay for it again on every refresh.
    /// </para>
    /// <para>
    /// The list is of Avalonia's own non-visual roots, so a document rooted in anything else is still
    /// offered — including a control this designer has never heard of. The test on the way in is a
    /// convenience; the open path remains the authority and still says, in words, when a document
    /// turned out to produce something that cannot be shown.
    /// </para>
    /// </remarks>
    private static bool IsDesignable(CanonicalPath file)
    {
        try
        {
            using System.Xml.XmlReader reader = System.Xml.XmlReader.Create(
                file.Value,
                new System.Xml.XmlReaderSettings
                {
                    DtdProcessing = System.Xml.DtdProcessing.Ignore,
                    IgnoreComments = true,
                    IgnoreProcessingInstructions = true,
                    IgnoreWhitespace = true,
                });

            return reader.MoveToContent() != System.Xml.XmlNodeType.Element
                || !NotAForm.Contains(reader.LocalName, StringComparer.Ordinal);
        }
        catch (Exception error)
            when (error is System.IO.IOException
                or UnauthorizedAccessException
                or System.Xml.XmlException)
        {
            // A file that cannot be read is not a file to hide. Offering it puts the reason in front
            // of somebody, in words, instead of leaving a document silently missing from the list.
            return true;
        }
    }

    /// <summary>
    /// Opens a form: read the file, parse it, build the live objects, put it on the canvas.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The environment comes from the adapter, which is the one piece that knows both families: it
    /// turns a project snapshot into the assemblies a document may name and loads them into a
    /// collectible context. That is what lets a form use the project's own controls rather than only
    /// Avalonia's — after the project has been built, because a control that has not been compiled
    /// is not a type anything can create.
    /// </para>
    /// <para>
    /// A document that fails to load is reported on the form itself rather than thrown. Half the
    /// documents a designer is pointed at are mid-edit, and a designer that refuses to open one is
    /// less useful than one that shows what is wrong with it.
    /// </para>
    /// </remarks>
    private async Task OpenFormAsync(FormFile form)
    {
        if (Forms.FirstOrDefault(open => open.File == form.Path) is { } already)
        {
            ActiveForm = already;

            return;
        }

        // The "already open" test above is read before the first await and acted on long after it,
        // so it cannot settle a race on its own: two opens of one file both find nothing and both
        // add. One form per file is the invariant, and this is where it is kept.
        if (!_opening.Add(form.Path))
        {
            return;
        }

        try
        {
            await OpenFormCoreAsync(form).ConfigureAwait(true);
        }
        finally
        {
            _opening.Remove(form.Path);
        }
    }

    private async Task OpenFormCoreAsync(FormFile form)
    {

        if (_workspace.CurrentSnapshot is not { } snapshot)
        {
            return;
        }

        Log($"Opening {form.Name}…");

        string text = await ReadAsync(form.Path);

        // Parsed with the URI the document will have once it is embedded, because a relative URI
        // means nothing without one. A freshly created Avalonia window says
        // Icon="/Assets/avalonia-logo.ico", and without a base the load fails on it -- correctly,
        // and uselessly, since the file is right there in the project.
        XamlDocument document = XamlDocument.Parse(
            text,
            new XamlParseOptions { DocumentUri = AvaresUriFor(snapshot, form) });

        // Built first when it has to be. A document naming an x:Class names a type, and a type in a
        // project nobody has compiled does not exist -- so opening a form in a freshly created
        // application failed with "unable to resolve type", which is true, unactionable, and the
        // first thing anybody sees. Building is what a designer does about it.
        if (NeedsBuilding(snapshot, form.Project, document))
        {
            snapshot = await BuildBeforeOpeningAsync(snapshot, form.Project) ?? snapshot;
        }

        var opened = new FormViewModel(form.Path, NextFreeSpot())
        {
        };

        Forms.Add(opened);
        ActiveForm = opened;

        await LoadIntoAsync(opened, snapshot, form.Project, document, text);
    }

    /// <summary>
    /// The <c>avares</c> URI a project file will be embedded under.
    /// </summary>
    /// <remarks>
    /// Composed rather than looked up, because the map answers the other direction: it says which
    /// file a URI names, and a project item does not carry the URI it will be given. The rule is the
    /// one the adapter documents — the producing assembly is the authority, the path is relative to
    /// the project directory — and the segments are escaped, because a file name is not a URI and a
    /// '#' in one would truncate the path.
    /// </remarks>
    private static Uri? AvaresUriFor(SolutionSnapshot snapshot, FormFile form)
    {
        if (!snapshot.TryGetProject(form.Project, out ProjectSnapshot? project)
            || !form.Path.StartsWith(project.ProjectDirectory))
        {
            return null;
        }

        string assembly = project.Properties.TryGetValue("AssemblyName", out string? name) && name.Length > 0
            ? name
            : project.Name;

        string relative = form.Path.Value[project.ProjectDirectory.Value.Length..]
            .Replace('\\', '/')
            .TrimStart('/');

        string escaped = string.Join('/', relative.Split('/').Select(Uri.EscapeDataString));

        return new Uri($"avares://{assembly}/{escaped}");
    }

    /// <summary>
    /// Whether this document needs the project compiled before it can produce anything.
    /// </summary>
    /// <remarks>
    /// Two conditions, and both matter. A document with no <c>x:Class</c> names no type of the
    /// project's and loads out of Avalonia alone, so building for it would be a wait bought with
    /// nothing. And a project whose assembly is already there does not need building again — the
    /// designer is not a build system and pressing Build is still the user's to do when they have
    /// changed code.
    /// </remarks>
    private static bool NeedsBuilding(
        SolutionSnapshot snapshot, ProjectIdentity project, XamlDocument document)
    {
        if (document.Root?.GetDirective("Class") is not { Length: > 0 })
        {
            return false;
        }

        if (!snapshot.TryGetProject(project, out ProjectSnapshot? owner))
        {
            return false;
        }

        return owner.Outputs.FirstOrDefault(o => o.Kind == OutputArtifactKind.Assembly) is not { } assembly
            || !System.IO.File.Exists(assembly.Path.Value);
    }

    /// <summary>
    /// Builds one project and returns the snapshot to load against.
    /// </summary>
    /// <remarks>
    /// The refresh afterwards is what makes the new assembly reachable: the environment is keyed on
    /// the snapshot version, so loading against the old one would build a context from paths that
    /// were empty when it was taken.
    /// </remarks>
    private async Task<SolutionSnapshot?> BuildBeforeOpeningAsync(
        SolutionSnapshot snapshot, ProjectIdentity project)
    {
        if (!snapshot.TryGetProject(project, out ProjectSnapshot? owner))
        {
            return null;
        }

        Log($"  {owner.Name} has not been built — building it first");

        if (await ExecuteAsync(ProjectOperationKind.Build, owner) != ProjectOperationStatus.Succeeded)
        {
            Log("  the build failed — the form is opened anyway, and will say what it could not find");

            return null;
        }

        await _workspace.RefreshAsync(_shutdown.Token);

        return _workspace.CurrentSnapshot;
    }

    /// <summary>Parses text into a document and builds its objects into a form.</summary>
    private async Task LoadIntoAsync(
        FormViewModel form,
        SolutionSnapshot snapshot,
        ProjectIdentity project,
        XamlDocument document,
        string text)
    {
        XamlLoadEnvironment environment = EnvironmentFor(snapshot, project);

        // Design mode, which is the whole difference between a designer and a runtime. A form is
        // usually a page of bindings with nothing bound at rest -- the Avalonia template's window is
        // one TextBlock reading {Binding Greeting} -- so loading it the way the application would
        // shows a blank rectangle and nothing to click. Design mode applies what the document says
        // is for looking at: Design.DataContext, d:DesignWidth, the lot.
        var options = new XamlLoadOptions { Mode = XamlLoadMode.Design };

        (XamlLoadSession? session, XamlLoadResult result) =
            await XamlLoadSession.TryCreateAsync(document, environment, options, _shutdown.Token);

        ShowMarkupDiagnostics(result.Diagnostics, text, form.File);

        if (session is null)
        {
            string why = string.Join(
                "; ",
                result.Diagnostics.Where(static d => d.Severity == MarkupDiagnosticSeverity.Error)
                    .Select(static d => d.Message)
                    .DefaultIfEmpty("no diagnostic said why"));

            form.Fail(why);

            // Said out loud, not only shown. "did not load" with the reason in another panel is the
            // shape of unhelpfulness this whole family is written against.
            Log($"  ! {form.Name} did not load: {why}");

            // A missing type is nearly always a project that has not been built, so the answer is
            // in what the context was given. Naming the project's own output is the useful half.
            if (_assemblies is { } context)
            {
                Log("    context held: " + string.Join(
                    ", ",
                    context.Assemblies.Select(static a => a.Path.FileName).Take(8)));
            }

            return;
        }

        await form.AdoptAsync(session);

        SizeToContent(form);
        RebuildHierarchy();
        Raise(nameof(CanvasCaption));

        Log($"  {form.Name} loaded, {Describe(session.Objects.MappedElements.Count, "element")} mapped");

        await ReportShownAsync(form);
    }

    /// <summary>
    /// Says whether the form reached the canvas, once the canvas has had a chance to lay it out.
    /// </summary>
    /// <remarks>
    /// Loading and appearing are different things, and the gap between them is where the worst bug
    /// in this designer lived: a window-rooted form loaded perfectly and then threw during layout,
    /// off the stack of everything that could have reported it, leaving a canvas that was simply
    /// empty. A size measured after the fact is the one statement that cannot be made by a form
    /// nobody can see.
    /// </remarks>
    private async Task ReportShownAsync(FormViewModel form)
    {
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);

        // Asked of the surface, not of the reference. The stand-in is always there — it is a
        // control the form owns from construction — so testing it for null tested nothing, and the
        // one diagnostic this designer has for "the document loaded and there is nothing to show"
        // silently stopped being reachable. HasContent is the question that still has an answer.
        if (!form.Surface.HasContent)
        {
            Log($"  ! {form.Name} produced nothing the canvas can host");

            return;
        }

        XamlDesignSurface surface = form.Surface;

        // Reported, not judged. One yield is enough for the container to exist and not always
        // enough for it to have measured, so a zero here means "not yet or not at all" and this is
        // in no position to say which. The number is still the most useful thing available about a
        // form somebody says they cannot see.
        Log($"  surface {surface.GetType().Name} measured "
            + $"{surface.Bounds.Width:F0}×{surface.Bounds.Height:F0}"
);
    }

    /// <summary>
    /// The environment for a project, rebuilt when the snapshot it was made from is superseded.
    /// </summary>
    /// <remarks>
    /// <c>IsCurrentFor</c> is one integer against the current snapshot, which is the cheap staleness
    /// check the adapter exists to offer. When it disagrees the context is dropped and a new one
    /// built, and forms already open keep the objects they have until they are reloaded — which is
    /// correct, because a rebuilt assembly is a reason to reload deliberately rather than to have
    /// every open form flicker.
    /// </remarks>
    private XamlLoadEnvironment EnvironmentFor(SolutionSnapshot snapshot, ProjectIdentity project)
    {
        if (_environment is not null
            && _assemblies is not null
            && _assemblies.IsCurrentFor(snapshot)
            && _environmentProject == project)
        {
            return _environment;
        }

        _assemblies?.Dispose();

        (_environment, _assemblies) = ProjectXamlEnvironment.CreateFor(snapshot, project);
        _environmentProject = project;

        Log($"  load context “{_assemblies.Name}” — {_assemblies.Assemblies.Length} assemblies");

        return _environment;
    }

    private static async Task<string> ReadAsync(CanonicalPath file) =>
        await System.IO.File.ReadAllTextAsync(file.Value);

    /// <summary>
    /// Makes the canvas item match the form's own size, when the document states one.
    /// </summary>
    /// <remarks>
    /// A window says how big it is and a user control usually does not. Guessing for the second is
    /// better than showing a zero-sized item: the designer's own default is a size somebody can
    /// drag, and the document keeps whatever it said.
    /// </remarks>
    private static void SizeToContent(FormViewModel form)
    {
        if (form.Root is not { } root)
        {
            return;
        }

        if (double.IsFinite(root.Width) && root.Width > 0)
        {
            form.Width = root.Width;
        }

        if (double.IsFinite(root.Height) && root.Height > 0)
        {
            form.Height = root.Height;
        }
    }

    /// <summary>A place on the surface where nothing is yet, so two forms never land on each other.</summary>
    private Point NextFreeSpot()
    {
        const double Step = 60;

        var spot = new Point(80, 80);

        while (Forms.Any(form => form.Location == spot))
        {
            spot += new Point(Step, Step);
        }

        return spot;
    }

    private void CloseAllForms()
    {
        foreach (FormViewModel form in Forms)
        {
            form.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        Forms.Clear();

        _assemblies?.Dispose();
        _assemblies = null;
        _environment = null;
    }

    /// <summary>Turns Markup's diagnostics into the project model's, so one list shows both.</summary>
    private void ShowMarkupDiagnostics(
        IEnumerable<MarkupDiagnostic> diagnostics, string documentText, CanonicalPath file)
    {
        foreach (ProjectDiagnostic translated in
            ProjectMarkupDiagnostics.ToProject(diagnostics, documentText, file))
        {
            Diagnostics.Add(DiagnosticRow.From(translated));
        }
    }
}
