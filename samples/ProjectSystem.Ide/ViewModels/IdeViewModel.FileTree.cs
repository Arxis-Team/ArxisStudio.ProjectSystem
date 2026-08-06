using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using ArxisStudio.ProjectSystem;

namespace ProjectSystem.Ide.ViewModels;

public sealed partial class IdeViewModel
{
    /// <summary>The project as it sits on disk, rather than grouped by item type.</summary>
    public ObservableCollection<TreeNode> FileTree { get; } = [];

    public TreeNode? SelectedFileNode
    {
        get;
        set
        {
            if (Set(ref field, value) && value is not null)
            {
                Select(value);
            }
        }
    }

    /// <summary>
    /// Builds the folder view of a snapshot.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The shape comes entirely out of the model: the folders are the distinct directories of the
    /// project's items relative to its own, the files are the items, and the dependencies are the
    /// four kinds of reference a project snapshot already carries. No directory is enumerated, so a
    /// file nothing includes is correctly absent.
    /// </para>
    /// <para>
    /// <b>Items are kept only if the file is there,</b> and that is not belt and braces. A project
    /// evaluates to more than its contents: <c>PotentialEditorConfigFiles</c> alone contributes an
    /// <c>.editorconfig</c> and a <c>.globalconfig</c> for every directory holding source, none of
    /// which exists — MSBuild produces the list precisely so that it can test each one. Showing
    /// them put two phantom files in every folder. A tab called Files should show files.
    /// </para>
    /// <para>
    /// Consulting the disk is this application's business rather than the library's: a snapshot
    /// says where things would be, and deciding whether that matters belongs to whoever is
    /// displaying it.
    /// </para>
    /// <para>
    /// Items under <c>obj</c> and <c>bin</c> are left out as well. Those do exist, and showing them
    /// would bury the project's own files under the build's.
    /// </para>
    /// </remarks>
    private void BuildFileTree(SolutionSnapshot snapshot)
    {
        FileTree.Clear();

        var root = new TreeNode(
            TreeNodeKind.Solution,
            snapshot.Name,
            Describe(snapshot.Projects.Length, "project"))
        {
            HasErrors = snapshot.HasErrors,
        };

        foreach (ProjectSnapshot project in snapshot.Projects)
        {
            root.Children.Add(FileNodeFor(project));
        }

        FileTree.Add(root);
    }

    private static TreeNode FileNodeFor(ProjectSnapshot project)
    {
        var node = new TreeNode(TreeNodeKind.Project, project.Name)
        {
            Project = project.Identity,
            File = project.ProjectFilePath,
            HasErrors = project.HasErrors,
        };

        node.Children.Add(DependenciesOf(project));

        // Folders are created on demand as the items are walked, so a directory only exists once
        // something in it does.
        var folders = new Dictionary<string, TreeNode>(StringComparer.OrdinalIgnoreCase)
        {
            [string.Empty] = node,
        };

        var placed = new HashSet<CanonicalPath>();

        foreach (ProjectItem item in project.Items)
        {
            if (item.FullPath.IsEmpty
                || !item.FullPath.StartsWith(project.ProjectDirectory)
                || !placed.Add(item.FullPath)
                || !System.IO.File.Exists(item.FullPath.Value))
            {
                continue;
            }

            string relative = item.FullPath.Value[project.ProjectDirectory.Value.Length..]
                .Replace('\\', '/')
                .TrimStart('/');

            if (IsBuildOutput(relative))
            {
                continue;
            }

            int slash = relative.LastIndexOf('/');
            string directory = slash < 0 ? string.Empty : relative[..slash];

            Folder(folders, node, directory).Children.Add(new TreeNode(
                TreeNodeKind.File,
                item.FullPath.FileName)
            {
                Project = project.Identity,
                File = item.FullPath,
            });
        }

        Nest(node);
        Sort(node);

        return node;
    }

    /// <summary>Finds or creates the chain of folders leading to a relative directory.</summary>
    private static TreeNode Folder(Dictionary<string, TreeNode> folders, TreeNode project, string directory)
    {
        if (folders.TryGetValue(directory, out TreeNode? existing))
        {
            return existing;
        }

        int slash = directory.LastIndexOf('/');
        TreeNode parent = slash < 0 ? project : Folder(folders, project, directory[..slash]);

        var node = new TreeNode(TreeNodeKind.Folder, slash < 0 ? directory : directory[(slash + 1)..])
        {
            Project = project.Project,
            IsExpanded = false,
        };

        parent.Children.Add(node);
        folders[directory] = node;

        return node;
    }

    private static bool IsBuildOutput(string relative) =>
        relative.StartsWith("obj/", StringComparison.OrdinalIgnoreCase)
            || relative.StartsWith("bin/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Everything the project references, which is four separate lists on the snapshot and one node
    /// here.
    /// </summary>
    private static TreeNode DependenciesOf(ProjectSnapshot project)
    {
        var dependencies = new TreeNode(TreeNodeKind.Dependencies, "Dependencies") { IsExpanded = false };

        Add("Projects", project.ProjectReferences.Select(static r => r.ProjectFilePath.FileName));
        Add("Packages", project.PackageReferences.Select(static p =>
            p.VersionText is null ? p.PackageId : $"{p.PackageId}  {p.VersionText}"));
        Add("Frameworks", project.FrameworkReferences.Select(static f => f.Name));
        Add("Assemblies", project.AssemblyReferences.Select(static a => a.Name));
        Add("Analyzers", project.AnalyzerReferences.Select(static a => a.AssemblyPath.FileName));

        return dependencies;

        void Add(string title, IEnumerable<string> names)
        {
            List<string> listed = [.. names];

            if (listed.Count == 0)
            {
                return;
            }

            var group = new TreeNode(
                TreeNodeKind.Group, title, listed.Count.ToString(CultureInfo.CurrentCulture))
            {
                Project = project.Identity,
                IsExpanded = false,
            };

            foreach (string name in listed)
            {
                group.Children.Add(new TreeNode(TreeNodeKind.Item, name) { Project = project.Identity });
            }

            dependencies.Children.Add(group);
        }
    }

    /// <summary>
    /// Tucks a file under the one it belongs to, so <c>MainWindow.axaml.cs</c> hides beneath
    /// <c>MainWindow.axaml</c>.
    /// </summary>
    /// <remarks>
    /// The rule every IDE uses and the only one that needs no configuration: a file whose name is
    /// another file's name plus an extension is that file's. It is applied per folder, because two
    /// files in different folders are unrelated however similar their names.
    /// </remarks>
    private static void Nest(TreeNode folder)
    {
        List<TreeNode> files = [.. folder.Children.Where(static child => child.Kind == TreeNodeKind.File)];

        var byName = new Dictionary<string, TreeNode>(StringComparer.OrdinalIgnoreCase);

        foreach (TreeNode file in files)
        {
            byName.TryAdd(file.Title, file);
        }

        foreach (TreeNode file in files)
        {
            int dot = file.Title.LastIndexOf('.');

            if (dot > 0
                && byName.TryGetValue(file.Title[..dot], out TreeNode? owner)
                && !ReferenceEquals(owner, file))
            {
                owner.Children.Add(file);
                folder.Children.Remove(file);
            }
        }

        foreach (TreeNode child in folder.Children.Where(static c => c.Kind == TreeNodeKind.Folder))
        {
            Nest(child);
        }
    }

    /// <summary>Folders above files, alphabetically within each, and dependencies first.</summary>
    private static void Sort(TreeNode node)
    {
        List<TreeNode> sorted = [.. node.Children.OrderBy(static child => child.SortKey, StringComparer.OrdinalIgnoreCase)];

        node.Children.Clear();

        foreach (TreeNode child in sorted)
        {
            node.Children.Add(child);

            if (child.Kind is TreeNodeKind.Folder or TreeNodeKind.File)
            {
                Sort(child);
            }
        }
    }
}
