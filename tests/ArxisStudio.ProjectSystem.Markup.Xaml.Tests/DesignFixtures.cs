using System;
using System.IO;

namespace ArxisStudio.ProjectSystem.Markup.Xaml.Tests;

/// <summary>
/// A project folder holding the fixtures' build and documents, as a user's project holds its own.
/// </summary>
/// <remarks>
/// <para>
/// The fixtures are built beside these tests and copied under <c>fixtures/</c>, never referenced as an
/// assembly; this lays them out as a project — <c>App/App.csproj</c>, its documents beside it, its
/// output under <c>bin/</c> — so a generation loads them into a collectible context exactly as it
/// loads a build, and a reclaim can prove them gone.
/// </para>
/// <para>
/// The output keeps the fixtures' own assembly name, because that is the name the compiled markup
/// inside it and the documents' <c>avares</c> URIs use.
/// </para>
/// </remarks>
internal sealed class DesignFixtures : IDisposable
{
    /// <summary>The assembly the fixtures build, which is also the name their resources live under.</summary>
    internal const string AssemblyName = "ArxisStudio.ProjectSystem.Markup.Xaml.Fixtures";

    /// <summary>The namespace of every fixture type.</summary>
    internal const string Namespace = AssemblyName;

    private static readonly string Built = Path.Combine(AppContext.BaseDirectory, "fixtures");

    internal DesignFixtures()
    {
        Root = Path.Combine(Path.GetTempPath(), "arxis-design-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(Path.Combine(Root, "App", "bin"));

        File.Copy(Path.Combine(Built, AssemblyName + ".dll"), Output.Value);

        foreach (string document in Directory.EnumerateFiles(Built, "*.axaml"))
        {
            File.Copy(document, Path.Combine(Root, "App", Path.GetFileName(document)));
        }
    }

    /// <summary>Gets the folder everything is laid out in.</summary>
    internal string Root { get; }

    /// <summary>Gets the project file's path; no file is written there.</summary>
    internal CanonicalPath ProjectFile => CanonicalPath.Create(Path.Combine(Root, "App", "App.csproj"));

    /// <summary>Gets the project's output, a copy of the fixtures' build.</summary>
    internal CanonicalPath Output => CanonicalPath.Create(Path.Combine(Root, "App", "bin", AssemblyName + ".dll"));

    /// <summary>Gets one of the fixtures' documents, in the project folder.</summary>
    /// <param name="name">The document's file name.</param>
    /// <returns>Its path.</returns>
    internal CanonicalPath Document(string name) => CanonicalPath.Create(Path.Combine(Root, "App", name));

    /// <summary>A snapshot of the one project, its documents declared as AvaloniaXaml items.</summary>
    /// <param name="workspace">The workspace the snapshot belongs to.</param>
    /// <returns>The snapshot.</returns>
    internal SolutionSnapshot Snapshot(WorkspaceIdentity workspace) =>
        Snapshot(new WorkspaceLoadRequest { Workspace = workspace, EntryPointPath = ProjectFile });

    /// <summary>
    /// A snapshot of the one project answering a load: its documents declared as AvaloniaXaml items, its
    /// code files as Compile items, and whatever else a test adds.
    /// </summary>
    /// <param name="request">The load the snapshot answers, its workspace's identities included.</param>
    /// <param name="configure">Adds to the project before it is published, or <see langword="null"/>.</param>
    /// <param name="more">Further projects of the solution, or <see langword="null"/>.</param>
    /// <returns>The snapshot.</returns>
    internal SolutionSnapshot Snapshot(
        WorkspaceLoadRequest request,
        Action<ProjectSnapshotBuilder>? configure = null,
        Func<WorkspaceIdentity, ProjectSnapshot[]>? more = null)
    {
        var project = new ProjectSnapshotBuilder
        {
            Identity = Project(request.Workspace),
            Name = "App",
            ProjectFilePath = ProjectFile,
        };

        project.Properties["AssemblyName"] = AssemblyName;
        project.Outputs.Add(new OutputArtifact { Kind = OutputArtifactKind.Assembly, Path = Output });
        project.EvaluationInputs.Add(ProjectFile);

        foreach (string document in Directory.EnumerateFiles(Path.Combine(Root, "App"), "*.axaml"))
        {
            project.Items.Add(new ProjectItem
            {
                ItemType = "AvaloniaXaml",
                Include = Path.GetFileName(document),
                FullPath = CanonicalPath.Create(document),
            });
        }

        foreach (string code in Directory.EnumerateFiles(Path.Combine(Root, "App"), "*.cs"))
        {
            project.Items.Add(new ProjectItem
            {
                ItemType = "Compile",
                Include = Path.GetFileName(code),
                FullPath = CanonicalPath.Create(code),
            });
        }

        configure?.Invoke(project);

        var solution = new SolutionSnapshotBuilder
        {
            Workspace = request.Workspace,
            Name = "App",
            Request = request,
        };

        solution.Projects.Add(project.ToSnapshot());

        foreach (ProjectSnapshot other in more?.Invoke(request.Workspace) ?? [])
        {
            solution.Projects.Add(other);
        }

        return solution.ToSnapshot();
    }

    /// <summary>Writes a file into the project folder, as the other editor would.</summary>
    /// <param name="name">The file's name, in the project folder.</param>
    /// <param name="text">What it says.</param>
    /// <returns>Its path.</returns>
    internal CanonicalPath Write(string name, string text)
    {
        CanonicalPath path = Document(name);

        File.WriteAllText(path.Value, text);

        return path;
    }

    /// <summary>The project's identity in a workspace.</summary>
    /// <param name="workspace">The workspace.</param>
    /// <returns>The identity.</returns>
    internal ProjectIdentity Project(WorkspaceIdentity workspace) => ProjectIdentity.Create(workspace, ProjectFile);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
