using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

namespace ArxisStudio.ProjectSystem.NuGet;

/// <summary>What a rewrite did, or why it did nothing.</summary>
internal enum RewriteOutcome
{
    /// <summary>The document changed.</summary>
    Changed,

    /// <summary>The document already said what was asked for.</summary>
    NothingToDo,
}

/// <summary>
/// Adds, changes and removes item elements in an MSBuild project file, leaving the rest of the file
/// exactly as it was.
/// </summary>
/// <remarks>
/// <para>
/// A pure function over an <see cref="XDocument"/>, which is the whole reason the interesting
/// decisions here are testable: no file, no project evaluation, no restore. The editor above it
/// deals with reading, writing and rolling back, and has almost no decisions in it.
/// </para>
/// <para>
/// <b>Formatting is preserved by editing rather than regenerating.</b> A project file is something a
/// person wrote and keeps reading — comments, blank lines, the order they chose, the indentation
/// their team argued about. A rewriter that parsed and re-serialised would produce a correct file
/// and an unreviewable diff.
/// </para>
/// </remarks>
internal static class PackageReferenceRewriter
{
    /// <summary>Adds an item, or reports that an equivalent one is already there.</summary>
    /// <param name="document">The document to edit in place.</param>
    /// <param name="itemType">The element name, such as <c>PackageReference</c>.</param>
    /// <param name="identifier">The value of <c>Include</c>.</param>
    /// <param name="attributes">Attributes to set beside it, in order. Null values are skipped.</param>
    /// <returns>What happened.</returns>
    internal static RewriteOutcome Add(
        XDocument document,
        string itemType,
        string identifier,
        params (string Name, string? Value)[] attributes)
    {
        if (Find(document, itemType, identifier) is not null)
        {
            return RewriteOutcome.NothingToDo;
        }

        XNamespace ns = Namespace(document);
        var item = new XElement(ns + itemType, new XAttribute("Include", identifier));

        foreach ((string name, string? value) in attributes)
        {
            if (value is not null)
            {
                item.Add(new XAttribute(name, value));
            }
        }

        Insert(document, itemType, identifier, item);

        return RewriteOutcome.Changed;
    }

    /// <summary>Sets an attribute on an existing item, or reports that it already said that.</summary>
    /// <param name="document">The document to edit in place.</param>
    /// <param name="itemType">The element name, such as <c>PackageVersion</c>.</param>
    /// <param name="identifier">The value of <c>Include</c> or <c>Update</c> to look for.</param>
    /// <param name="attribute">The attribute to set.</param>
    /// <param name="value">The value to set it to.</param>
    /// <returns>What happened, including when there was no such item.</returns>
    internal static RewriteOutcome Set(
        XDocument document,
        string itemType,
        string identifier,
        string attribute,
        string value)
    {
        if (Find(document, itemType, identifier) is not { } item)
        {
            return RewriteOutcome.NothingToDo;
        }

        if (string.Equals(item.Attribute(attribute)?.Value, value, StringComparison.Ordinal))
        {
            return RewriteOutcome.NothingToDo;
        }

        item.SetAttributeValue(attribute, value);

        return RewriteOutcome.Changed;
    }

    /// <summary>Removes an item, and any group it was alone in.</summary>
    /// <param name="document">The document to edit in place.</param>
    /// <param name="itemType">The element name.</param>
    /// <param name="identifier">The value of <c>Include</c> or <c>Update</c> to look for.</param>
    /// <returns>What happened.</returns>
    internal static RewriteOutcome Remove(XDocument document, string itemType, string identifier)
    {
        if (Find(document, itemType, identifier) is not { } item)
        {
            return RewriteOutcome.NothingToDo;
        }

        XElement? group = item.Parent;

        RemoveWithSurroundingWhitespace(item);

        // An ItemGroup that exists to hold nothing is noise the next reader has to skip past. Its
        // comments are not: a group left empty but commented was deliberate, and the comment
        // explains items that may come back.
        if (group is not null
            && !group.Elements().Any()
            && !group.Nodes().OfType<XComment>().Any())
        {
            RemoveWithSurroundingWhitespace(group);
        }

        return RewriteOutcome.Changed;
    }

    /// <summary>Whether the document declares an item of this type for this identifier.</summary>
    internal static bool Contains(XDocument document, string itemType, string identifier) =>
        Find(document, itemType, identifier) is not null;

    /// <summary>Reads an attribute of an item, when it has one.</summary>
    internal static string? Read(XDocument document, string itemType, string identifier, string attribute) =>
        Find(document, itemType, identifier)?.Attribute(attribute)?.Value;

    /// <summary>
    /// The document's default namespace, which is empty for an SDK-style project and the 2003
    /// MSBuild one for anything older.
    /// </summary>
    /// <remarks>
    /// Taken from the document rather than assumed, because an element created in the wrong
    /// namespace is invisible to MSBuild while looking perfectly correct in the file.
    /// </remarks>
    private static XNamespace Namespace(XDocument document) =>
        document.Root?.GetDefaultNamespace() ?? XNamespace.None;

    /// <summary>
    /// Finds an item by identifier, matching case-insensitively and accepting either attribute.
    /// </summary>
    /// <remarks>
    /// Package identifiers are case-insensitive to NuGet, so a project that says <c>serilog</c> and
    /// a caller that says <c>Serilog</c> mean the same package and must not produce two items.
    /// <c>Update</c> is accepted as well as <c>Include</c> because that is how a project adjusts a
    /// reference something else declared — a transitively pinned version, or one an SDK contributed.
    /// </remarks>
    private static XElement? Find(XDocument document, string itemType, string identifier) =>
        Items(document, itemType).FirstOrDefault(item =>
            string.Equals(Identifier(item), identifier, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<XElement> Items(XDocument document, string itemType) =>
        document.Root?.Elements(Namespace(document) + "ItemGroup")
            .Elements(Namespace(document) + itemType)
        ?? [];

    private static string? Identifier(XElement item) =>
        item.Attribute("Include")?.Value ?? item.Attribute("Update")?.Value;

    /// <summary>
    /// Puts a new item beside its own kind if there are any, and in a new group otherwise.
    /// </summary>
    /// <remarks>
    /// Alphabetically within that group, but only when the group already is alphabetical. Imposing
    /// an order on a list somebody grouped by meaning would rearrange their file as a side effect of
    /// adding one line; appending to a list that was sorted would equally undo the sorting they were
    /// maintaining. Neither is a decision this code should make on its own, so it copies whichever
    /// one the file is already making.
    /// </remarks>
    private static void Insert(XDocument document, string itemType, string identifier, XElement item)
    {
        XNamespace ns = Namespace(document);
        List<XElement> existing = [.. Items(document, itemType)];

        if (existing.Count == 0)
        {
            Append(document, new XElement(ns + "ItemGroup", item));

            return;
        }

        if (IsSorted(existing)
            && existing.Find(other => string.Compare(
                Identifier(other), identifier, StringComparison.OrdinalIgnoreCase) > 0) is { } successor)
        {
            // The whitespace has to be read before the insert, because afterwards the successor's
            // previous node is the new item rather than the indentation it used to sit behind.
            string? indentation = Whitespace(successor.PreviousNode);

            successor.AddBeforeSelf(item);

            if (indentation is not null)
            {
                successor.AddBeforeSelf(new XText(indentation));
            }

            return;
        }

        XElement last = existing[^1];

        last.AddAfterSelf(item);

        if (Whitespace(last.PreviousNode) is { } trailing)
        {
            item.AddBeforeSelf(new XText(trailing));
        }
    }

    private static bool IsSorted(List<XElement> items)
    {
        for (int index = 1; index < items.Count; index++)
        {
            if (string.Compare(
                Identifier(items[index - 1]), Identifier(items[index]), StringComparison.OrdinalIgnoreCase) > 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>A node's text when it is whitespace, which is what indentation is made of.</summary>
    private static string? Whitespace(XNode? node) =>
        node is XText text && string.IsNullOrWhiteSpace(text.Value) ? text.Value : null;

    /// <summary>
    /// Puts a group at the end of the project, laid out the way the rest of the file is.
    /// </summary>
    /// <remarks>
    /// The whitespace already before <c>&lt;/Project&gt;</c> is reused rather than added to. Simply
    /// appending would leave the closing tag's own indentation sitting in front of the new group and
    /// produce a stray blank line — the sort of thing that is invisible in a review and permanent in
    /// a file.
    /// </remarks>
    private static void Append(XDocument document, XElement group)
    {
        XElement root = document.Root!;
        string newLine = NewLine(document);
        bool hasContent = root.Elements().Any();

        foreach (XElement child in group.Elements())
        {
            child.AddBeforeSelf(new XText(newLine + "    "));
        }

        group.Add(new XText(newLine + "  "));

        // A blank line before a new group, but only when there is something to be separated from.
        string before = (hasContent ? newLine + newLine : newLine) + "  ";

        if (root.LastNode is XText closing && string.IsNullOrWhiteSpace(closing.Value))
        {
            closing.Value = before;
            closing.AddAfterSelf(group);
            group.AddAfterSelf(new XText(newLine));

            return;
        }

        root.Add(new XText(before));
        root.Add(group);
        root.Add(new XText(newLine));
    }

    /// <summary>
    /// The newline the document already uses, so a group added to it does not disagree with the rest
    /// of the file.
    /// </summary>
    /// <remarks>
    /// Read from the file rather than taken from the platform. A repository that normalises to LF is
    /// not made an exception to its own rule by being edited on Windows, which is a change nobody
    /// asked for showing up in a diff about a package.
    /// </remarks>
    private static string NewLine(XDocument? document)
    {
        foreach (XNode node in document?.Root?.Nodes() ?? [])
        {
            if (node is XText text && text.Value.Contains('\n'))
            {
                return text.Value.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            }
        }

        return Environment.NewLine;
    }

    /// <summary>
    /// Removes a node together with the whitespace that laid it out, so deleting the only item in a
    /// group does not leave the blank line it used to occupy.
    /// </summary>
    private static void RemoveWithSurroundingWhitespace(XNode node)
    {
        if (node.PreviousNode is XText whitespace && string.IsNullOrWhiteSpace(whitespace.Value))
        {
            whitespace.Remove();
        }

        node.Remove();
    }
}
