using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ArxisStudio.Markup;

namespace ArxisStudio.ProjectSystem.Markup.Xaml;

/// <summary>
/// Opens a document named by an <c>avares://</c> URI, from the project rather than from a build.
/// </summary>
/// <remarks>
/// <para>
/// The other half of translating a project into Markup's world. Markup's own file provider answers
/// <c>file:</c> URIs and nothing else, which is correct for it and leaves a gap: Avalonia identifies
/// a document by its <c>avares</c> URI, and that is how a <c>ResourceInclude</c> or a
/// <c>StyleInclude</c> names one. Without this, following such a reference in a designer would fail
/// on a document sitting in the project all along.
/// </para>
/// <para>
/// Composed ahead of the file provider rather than instead of it: a document a user opened by path
/// is a <c>file:</c> URI, and both kinds turn up in the same session.
/// </para>
/// </remarks>
public sealed class ProjectMarkupSourceProvider : IMarkupSourceProvider
{
    private readonly ProjectResourceMap _map;
    private readonly Encoding? _defaultEncoding;

    /// <summary>Creates a provider over a map.</summary>
    /// <param name="map">Which file each URI names.</param>
    /// <param name="defaultEncoding">
    /// The encoding to assume for a file with no byte-order mark, or <see langword="null"/> for
    /// UTF-8.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="map"/> is <see langword="null"/>.</exception>
    public ProjectMarkupSourceProvider(ProjectResourceMap map, Encoding? defaultEncoding = null)
    {
        ArgumentNullException.ThrowIfNull(map);

        _map = map;
        _defaultEncoding = defaultEncoding;
    }

    /// <summary>Builds a provider over every project in a snapshot.</summary>
    /// <param name="snapshot">The snapshot to read.</param>
    /// <returns>The provider.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is <see langword="null"/>.</exception>
    public static ProjectMarkupSourceProvider Create(SolutionSnapshot snapshot) =>
        new(ProjectResourceMap.Create(snapshot));

    /// <inheritdoc />
    public ValueTask<MarkupSource?> TryGetSourceAsync(Uri uri, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uri);

        cancellationToken.ThrowIfCancellationRequested();

        if (!_map.TryGetFile(uri, null, out CanonicalPath file) || !File.Exists(file.Value))
        {
            return new ValueTask<MarkupSource?>((MarkupSource?)null);
        }

        return new ValueTask<MarkupSource?>(new ProjectFileSource(uri, file.Value, _defaultEncoding));
    }

    /// <summary>
    /// A project file presented under the URI Avalonia names it by.
    /// </summary>
    /// <remarks>
    /// Markup's <c>FileMarkupSource</c> derives its path from its URI, so it cannot serve a file
    /// under an <c>avares</c> identity — the two differ, and that difference is the whole point of
    /// this adapter. <c>StreamMarkupSource</c> would do it but wants the stream up front, which
    /// leaves a handle open until somebody reads the text and leaks it if nobody does. Reading
    /// lazily is a few lines and neither of those problems.
    /// </remarks>
    private sealed class ProjectFileSource(Uri uri, string path, Encoding? defaultEncoding)
        : MarkupSource(uri)
    {
        /// <summary>Gets the file behind this source.</summary>
        public string Path => path;

        public override async ValueTask<SourceText> GetTextAsync(CancellationToken cancellationToken = default)
        {
            FileStream stream = ProjectFiles.OpenRead(path);

            await using (stream.ConfigureAwait(false))
            {

                // The byte-order mark wins when there is one, which is what every editor does and
                // what makes a file written by one readable by the next.
                using var reader = new StreamReader(
                    stream,
                    defaultEncoding ?? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    detectEncodingFromByteOrderMarks: true);

                return SourceText.From(await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false));
            }
        }
    }
}
