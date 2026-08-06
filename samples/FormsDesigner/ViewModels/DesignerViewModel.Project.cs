using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using ArxisStudio.Markup;
using ArxisStudio.Markup.Xaml;
using ArxisStudio.Markup.Xaml.Loader;
using ArxisStudio.ProjectSystem;
using ArxisStudio.ProjectSystem.Markup.Xaml;
using Avalonia;

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

    public FormFile? SelectedProjectForm
    {
        get;
        set
        {
            if (Set(ref field, value) && value is not null)
            {
                RunDetached(() => OpenFormAsync(value));
            }
        }
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

        Log($"  {Describe(ProjectForms.Count, "form")} in the project");

        if (previous is not null && ProjectForms.FirstOrDefault(f => f.Path == previous.Path) is { } same)
        {
            SelectedProjectForm = same;
        }
    }

    private static bool IsMarkup(CanonicalPath file) =>
        file.Extension.Equals(".axaml", StringComparison.OrdinalIgnoreCase)
            || file.Extension.Equals(".xaml", StringComparison.OrdinalIgnoreCase);

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

        if (_workspace.CurrentSnapshot is not { } snapshot)
        {
            return;
        }

        Log($"Opening {form.Name}…");

        var opened = new FormViewModel(form.Path, NextFreeSpot());

        Forms.Add(opened);
        ActiveForm = opened;

        await LoadIntoAsync(opened, snapshot, form.Project, await ReadAsync(form.Path));
    }

    /// <summary>Parses text into a document and builds its objects into a form.</summary>
    private async Task LoadIntoAsync(
        FormViewModel form,
        SolutionSnapshot snapshot,
        ProjectIdentity project,
        string text)
    {
        XamlDocument document = XamlDocument.Parse(text);

        XamlLoadEnvironment environment = EnvironmentFor(snapshot, project);

        (XamlLoadSession? session, XamlLoadResult result) =
            await XamlLoadSession.TryCreateAsync(document, environment, null, _shutdown.Token);

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

        Log($"  {form.Name} loaded, {Describe(session.Objects.MappedElements.Count, "element")} mapped");
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
