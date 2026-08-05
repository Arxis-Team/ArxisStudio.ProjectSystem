using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace ArxisStudio.ProjectSystem.NuGet;

/// <summary>
/// Writes a set of files together, and puts them back if any of the writes fails.
/// </summary>
/// <remarks>
/// <para>
/// Not a general-purpose transaction and not pretending to be one: no journal, no crash recovery,
/// no protection against another process writing the same file at the same moment. What it does is
/// the thing that actually goes wrong — the second of two files fails to write because it is open
/// in another editor — and it undoes the first, which is the difference between a repository that
/// still restores and one that does not.
/// </para>
/// <para>
/// A rollback writes back bytes this class already holds, so it needs nothing from the disk to
/// succeed and cannot itself fail for want of information. If the rollback write fails too, the
/// exception is swallowed: there is nothing further to try and the original failure is the one worth
/// reporting.
/// </para>
/// </remarks>
internal sealed class FileTransaction
{
    private readonly List<TrackedFile> _files = [];
    private readonly List<TrackedFile> _written = [];

    /// <summary>Gets or sets why the edit could not proceed, when something was unreadable.</summary>
    internal ProjectOperationResult? Failure { get; set; }

    /// <summary>Remembers a file's original text so it can be restored.</summary>
    internal void Track(CanonicalPath path, string original, XDocument document) =>
        _files.Add(new TrackedFile(path, original, document));

    /// <summary>Whether a document now differs from what was read.</summary>
    internal bool HasChanged(XDocument document)
    {
        foreach (TrackedFile file in _files)
        {
            if (ReferenceEquals(file.Document, document))
            {
                return !string.Equals(file.Original, Serialise(file), StringComparison.Ordinal);
            }
        }

        return false;
    }

    /// <summary>Writes every file that changed, restoring the earlier ones if a later one fails.</summary>
    internal async ValueTask CommitAsync(CancellationToken cancellationToken)
    {
        foreach (TrackedFile file in _files)
        {
            string updated = Serialise(file);

            if (string.Equals(file.Original, updated, StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                await File.WriteAllTextAsync(file.Path.Value, updated, Encoding.UTF8, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                await RollBackAsync(CancellationToken.None).ConfigureAwait(false);

                throw;
            }

            _written.Add(file);
        }
    }

    /// <summary>Puts back everything already written.</summary>
    internal async ValueTask RollBackAsync(CancellationToken cancellationToken)
    {
        foreach (TrackedFile file in _written)
        {
            try
            {
                await File.WriteAllTextAsync(file.Path.Value, file.Original, Encoding.UTF8, cancellationToken)
                    .ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Nothing further to try, and the failure that started this is the
            catch (Exception)  // one worth reporting rather than this one.
#pragma warning restore CA1031
            {
            }
        }

        _written.Clear();
    }

    /// <summary>
    /// Renders a document the way its file was written, so an unchanged file compares equal to
    /// itself and a changed one differs only where it changed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three details, each of which produced a wrong file before it was handled.
    /// </para>
    /// <para>
    /// <b>The declaration is written by hand.</b> Saving to a <see cref="StringWriter"/> makes LINQ
    /// to XML announce the writer's encoding, so every project file it touched would gain
    /// <c>&lt;?xml version="1.0" encoding="utf-16"?&gt;</c> — which is both untrue and not what the
    /// file said. Omitting it entirely is equally wrong for the legacy project files that do carry
    /// one, so the original's own declaration is put back verbatim.
    /// </para>
    /// <para>
    /// <b>Formatting stays disabled.</b> With it on, LINQ to XML re-indents the whole document and
    /// every file looks changed.
    /// </para>
    /// <para>
    /// <b>Nothing is added at either end.</b> Whitespace outside the root element is a node of the
    /// document like any other under <see cref="LoadOptions.PreserveWhitespace"/>, so the trailing
    /// newline a file ends with comes back on its own. Adding one "because Save omits it" doubled
    /// it, and made every untouched file compare as changed.
    /// </para>
    /// </remarks>
    private static string Serialise(TrackedFile file)
    {
        var body = new StringWriter();

        var settings = new XmlWriterSettings
        {
            OmitXmlDeclaration = true,
            Indent = false,
            NewLineHandling = NewLineHandling.None,
            CheckCharacters = false,
        };

        using (XmlWriter writer = XmlWriter.Create(body, settings))
        {
            file.Document.Save(writer);
        }

        return file.Document.Declaration is { } given ? given + body.ToString() : body.ToString();
    }

    private sealed record TrackedFile(CanonicalPath Path, string Original, XDocument Document);
}
