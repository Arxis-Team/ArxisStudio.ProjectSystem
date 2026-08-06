using System.Collections.ObjectModel;
using ArxisStudio.ProjectSystem;

namespace ProjectSystem.Ide.ViewModels;

/// <summary>What a node in the solution tree stands for.</summary>
public enum TreeNodeKind
{
    Solution,
    Folder,
    Project,
    Group,
    Item,

    /// <summary>The node collecting everything a project references.</summary>
    Dependencies,

    /// <summary>A file, shown where it sits on disk rather than under its item type.</summary>
    File,
}

/// <summary>
/// One row of the solution explorer.
/// </summary>
/// <remarks>
/// Built fresh from each published snapshot rather than mutated in place. A snapshot is immutable
/// and a tree that tried to patch itself towards the next one would be inventing a diff nobody
/// asked for — and would be wrong the first time a project was removed.
/// </remarks>
public sealed class TreeNode(TreeNodeKind kind, string title, string? detail = null) : Observable
{
    public TreeNodeKind Kind { get; } = kind;

    public string Title { get; } = title;

    public string? Detail { get; } = detail;

    public ObservableCollection<TreeNode> Children { get; } = [];

    /// <summary>The project this node belongs to, so selecting anything can show its details.</summary>
    public ProjectIdentity Project { get; init; }

    /// <summary>The file this node stands for, when it stands for one.</summary>
    public CanonicalPath File { get; init; }

    /// <summary>Whether anything under this node reported an error.</summary>
    public bool HasErrors { get; init; }

    public bool IsExpanded
    {
        get;
        set => Set(ref field, value);
    } = true;

    public string Glyph => Kind switch
    {
        TreeNodeKind.Solution => "▤",
        TreeNodeKind.Folder => "🗀",
        TreeNodeKind.Project => "◆",
        TreeNodeKind.Group => "▪",
        TreeNodeKind.Dependencies => "⚭",
        TreeNodeKind.File => Extension switch
        {
            ".cs" => "C#",
            ".axaml" or ".xaml" => "<>",
            ".csproj" or ".fsproj" or ".vbproj" => "◆",
            ".json" => "{}",
            ".manifest" or ".xml" or ".config" or ".props" or ".targets" => "</>",
            _ => "·",
        },
        _ => "·",
    };

    /// <summary>The file's extension, lowered, or empty when the node is not a file.</summary>
    public string Extension => File.IsEmpty ? string.Empty : File.Extension.ToLowerInvariant();

    /// <summary>Sorts folders above files and then alphabetically, the way an explorer does.</summary>
    internal string SortKey => Kind switch
    {
        TreeNodeKind.Dependencies => "0",
        TreeNodeKind.Folder => "1" + Title,
        _ => "2" + Title,
    };
}
