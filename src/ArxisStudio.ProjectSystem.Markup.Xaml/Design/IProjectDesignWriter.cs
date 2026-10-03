using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ArxisStudio.Markup;

namespace ArxisStudio.ProjectSystem.Markup.Xaml;

/// <summary>Writes a document's text to its file when a <see cref="ProjectDesignHost"/> saves it.</summary>
/// <remarks>
/// <para>
/// Without one (<see cref="ProjectDesignHostOptions.Writer"/>) the host writes the file itself. An IDE
/// whose own service writes the solution's files — recording the write in a local history, telling its
/// watchers the change is its own, refusing to write over a file that moved on — gives the host a writer
/// over that service, and the design host's saves become the IDE's saves (ADR 0032).
/// </para>
/// <para>
/// The text carries the encoding and the byte-order mark it was read with, and a writer keeps both. A
/// writer that cannot write throws, and the document stays unsaved.
/// </para>
/// </remarks>
public interface IProjectDesignWriter
{
    /// <summary>Writes the text to the file.</summary>
    /// <param name="file">The document's file.</param>
    /// <param name="text">What the document says, in the encoding it was read in.</param>
    /// <param name="cancellationToken">A token to observe.</param>
    /// <returns>A task that completes once the file holds the text.</returns>
    /// <exception cref="IOException">The file could not be written.</exception>
    ValueTask WriteAsync(CanonicalPath file, SourceText text, CancellationToken cancellationToken);
}

/// <summary>Writes the file in place, as the host always did.</summary>
internal sealed class DiskDesignWriter : IProjectDesignWriter
{
    /// <summary>Gets the one instance; it holds nothing.</summary>
    internal static DiskDesignWriter Instance { get; } = new();

    public async ValueTask WriteAsync(CanonicalPath file, SourceText text, CancellationToken cancellationToken)
    {
        byte[] preamble = text.HasByteOrderMark ? text.Encoding.GetPreamble() : [];
        byte[] body = text.Encoding.GetBytes(text.ToString());

        FileStream stream = new(file.Value, FileMode.Create, FileAccess.Write, FileShare.Read, 4096, useAsync: true);

        await using (stream.ConfigureAwait(false))
        {
            await stream.WriteAsync(preamble, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        }
    }
}
