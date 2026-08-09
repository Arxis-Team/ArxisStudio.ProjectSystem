using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using ArxisStudio.ProjectSystem;
using Avalonia.Media;

namespace FormsDesigner.ViewModels;

/// <summary>A folder of the open project, flattened with its depth like the hierarchy.</summary>
public sealed record FolderRow(string Name, string Path, double Indent, bool HasChildren);

/// <summary>A file, as the design's grid draws it: a large glyph over a wrapped name.</summary>
public sealed record FileTile(string Name, CanonicalPath Path, Geometry Glyph, string Hue);

public sealed partial class DesignerViewModel
{
    /// <summary>Every log line, split the way the design splits it.</summary>
    public ObservableCollection<LogRow> Logs { get; } = [];

    /// <summary>The project's folders, for the left half of the Project tab.</summary>
    public ObservableCollection<FolderRow> ProjectFolders { get; } = [];

    /// <summary>What is in the selected folder, for the right half.</summary>
    public ObservableCollection<FileTile> ProjectFiles { get; } = [];

    public FolderRow? SelectedFolder
    {
        get;
        set
        {
            if (Set(ref field, value))
            {
                ShowFolder();
            }
        }
    }

    /// <summary>How many problems the dock's tab shows beside its name.</summary>
    public string ProblemCount => Diagnostics.Count == 0 ? string.Empty : Diagnostics.Count.ToString(CultureInfo.CurrentCulture);

    private readonly Dictionary<string, List<FileTile>> _filesByFolder = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Turns a log line into the design's three columns.
    /// </summary>
    /// <remarks>
    /// The level is read off the text rather than passed in, because everything that logs here
    /// already says what it means: a line beginning with an exclamation is a failure, an indented
    /// one is detail about the line above it, and a bare one is a step. Inventing a level parameter
    /// would mean editing forty call sites to say what they already say.
    /// </remarks>
    private void Record(string message)
    {
        string level = message.StartsWith("! ", StringComparison.Ordinal)
            || message.StartsWith("  ! ", StringComparison.Ordinal)
                ? "ERROR"
                : message.StartsWith("  │", StringComparison.Ordinal)
                    ? "RUN"
                    : message.StartsWith("  ", StringComparison.Ordinal)
                        ? string.Empty
                        : "INFO";

        Logs.Add(new LogRow(DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture), level, message.Trim()));

        while (Logs.Count > 500)
        {
            Logs.RemoveAt(0);
        }
    }

    /// <summary>
    /// Builds the folder tree and the file index from the snapshot.
    /// </summary>
    /// <remarks>
    /// From the project's items, like everything else in this window: a folder exists because
    /// something the project compiles is in it. A directory full of files nothing includes is not
    /// part of the application and showing it invites editing something with no effect.
    /// </remarks>
    private void BuildProjectPane(SolutionSnapshot snapshot)
    {
        ProjectFolders.Clear();
        ProjectFiles.Clear();
        _filesByFolder.Clear();

        var order = new List<string>();

        foreach (ProjectSnapshot project in snapshot.Projects)
        {
            Add(project.Name, string.Empty);

            foreach (ProjectItem item in project.Items)
            {
                if (item.FullPath.IsEmpty
                    || !item.FullPath.StartsWith(project.ProjectDirectory)
                    || !System.IO.File.Exists(item.FullPath.Value))
                {
                    continue;
                }

                string relative = item.FullPath.Value[project.ProjectDirectory.Value.Length..]
                    .Replace('\\', '/')
                    .TrimStart('/');

                if (relative.StartsWith("obj/", StringComparison.OrdinalIgnoreCase)
                    || relative.StartsWith("bin/", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                int slash = relative.LastIndexOf('/');
                string folder = slash < 0 ? project.Name : $"{project.Name}/{relative[..slash]}";

                Add(project.Name, slash < 0 ? string.Empty : relative[..slash]);

                if (!_filesByFolder.TryGetValue(folder, out List<FileTile>? files))
                {
                    _filesByFolder[folder] = files = [];
                }

                files.Add(new FileTile(
                    item.FullPath.FileName,
                    item.FullPath,
                    Glyphs.ForFile(item.FullPath.Extension),
                    Glyphs.HueOfFile(item.FullPath.Extension)));
            }
        }

        foreach (string path in order)
        {
            int depth = path.Count(c => c == '/');
            int slash = path.LastIndexOf('/');

            ProjectFolders.Add(new FolderRow(
                slash < 0 ? path : path[(slash + 1)..],
                path,
                8 + (depth * 16),
                order.Any(other => other.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase))));
        }

        SelectedFolder = ProjectFolders.FirstOrDefault();

        void Add(string project, string relative)
        {
            // Every folder on the way down, so a file three deep does not appear under a root that
            // has no path to it.
            string[] parts = relative.Length == 0 ? [] : relative.Split('/');
            string path = project;

            if (!order.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                order.Add(path);
            }

            foreach (string part in parts)
            {
                path = path + "/" + part;

                if (!order.Contains(path, StringComparer.OrdinalIgnoreCase))
                {
                    order.Add(path);
                }
            }
        }
    }

    private void ShowFolder()
    {
        ProjectFiles.Clear();

        if (SelectedFolder is not { } folder
            || !_filesByFolder.TryGetValue(folder.Path, out List<FileTile>? files))
        {
            return;
        }

        foreach (FileTile file in files.OrderBy(static f => f.Name, StringComparer.OrdinalIgnoreCase))
        {
            ProjectFiles.Add(file);
        }
    }

    /// <summary>
    /// Opens a file from the grid, when it is one this designer can open.
    /// </summary>
    /// <remarks>
    /// A designer opens markup; the grid shows everything in the folder, because a project pane
    /// that hid what it could not open would be lying about the project. A double click on a
    /// <c>.cs</c> file therefore says why nothing happened, which is the whole difference between a
    /// tool that declined and a tool that is broken.
    /// </remarks>
    public void OpenFile(FileTile file)
    {
        if (ProjectForms.FirstOrDefault(form => form.Path == file.Path) is { } form)
        {
            // Opened directly rather than by assigning the selection. The setter acts only on a
            // change, and FormFile is a record compared by value, so double-clicking the file that
            // was already selected did nothing at all — not even re-activate the form it names,
            // which is the whole of what a second double-click is asking for.
            SelectedProjectForm = form;

            RunDetached(() => OpenFormAsync(form));

            return;
        }

        // Markup that is not on the list is markup that declares no form -- App.axaml and its kind.
        // Saying which of the two it is, rather than one message for both, is the difference between
        // "this designer cannot open that" and "that is not a thing to open".
        Log(IsMarkup(file.Path)
            ? $"! {file.Name} declares the application's styles and resources, not a form"
            : $"! {file.Name} is not markup — this designer opens .axaml and .xaml");
    }
}
