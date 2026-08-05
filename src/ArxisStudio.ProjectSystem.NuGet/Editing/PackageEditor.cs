using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace ArxisStudio.ProjectSystem.NuGet;

/// <summary>
/// Installs, updates and removes package references, as an edit that either happens completely or
/// not at all.
/// </summary>
/// <remarks>
/// <para>
/// One change can touch two files. Under central package management the reference goes in the
/// project and the version goes in <c>Directory.Packages.props</c>, and a half-applied edit leaves a
/// repository that does not restore: a reference with no version, or a version for a package nobody
/// references. So the files are written together and, if any write fails, the ones already written
/// are put back exactly as they were.
/// </para>
/// <para>
/// <b>This changes files and nothing else.</b> It does not restore, does not evaluate, and does not
/// publish a snapshot — for the same reason a build does not
/// (<see href="../../docs/adr/0014-an-operation-is-not-a-mutation.md">ADR 0014</see>): it changed
/// what is on disk, not what the workspace knows. A caller restores through the workspace and
/// refreshes when it wants the model to catch up.
/// </para>
/// <para>
/// Static because it holds nothing. Editing a file is a function of the request and the file, and
/// an object to construct first would exist only to be constructed. If package <em>search</em>
/// later needs a feed, credentials and a cache, that is a different type with a different lifetime
/// rather than state grafted onto this one.
/// </para>
/// </remarks>
public static class PackageEditor
{
    /// <summary>The element a project uses to declare a package.</summary>
    private const string PackageReference = "PackageReference";

    /// <summary>The element central package management uses to give one a version.</summary>
    private const string PackageVersion = "PackageVersion";

    private const string Version = "Version";

    /// <summary>Applies one change.</summary>
    /// <param name="request">What to do.</param>
    /// <param name="layout">Where the versions live, or <see langword="null"/> to work it out from the project.</param>
    /// <param name="cancellationToken">A token to observe.</param>
    /// <returns>Whether it happened, and anything worth saying about it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// An install or update carries no version. That is a broken call rather than a project problem:
    /// there is nothing to write and no sensible default to invent.
    /// </exception>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    public static async ValueTask<ProjectOperationResult> ApplyAsync(
        PackageEditRequest request,
        PackageVersionLayout? layout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        cancellationToken.ThrowIfCancellationRequested();

        if (request.Kind is PackageEditKind.Install or PackageEditKind.Update
            && string.IsNullOrWhiteSpace(request.Version))
        {
            throw new ArgumentException(
                $"{request.Kind} needs a version to write.", nameof(request));
        }

        if (!File.Exists(request.ProjectFilePath.Value))
        {
            return Failed(
                PackageDiagnosticCodes.ProjectFileNotFound,
                $"There is nothing at '{request.ProjectFilePath}'.",
                request.ProjectFilePath);
        }

        layout ??= PackageVersionLayout.Beside;

        if (layout.IsCentral && layout.VersionsFilePath.IsEmpty)
        {
            return Failed(
                PackageDiagnosticCodes.CentralVersionFileNotFound,
                "Central package management is on, but no file was given to hold the versions.",
                request.ProjectFilePath);
        }

        if (layout.IsCentral && !File.Exists(layout.VersionsFilePath.Value))
        {
            return Failed(
                PackageDiagnosticCodes.CentralVersionFileNotFound,
                $"Central package management is on, and '{layout.VersionsFilePath}' is not there.",
                layout.VersionsFilePath);
        }

        var transaction = new FileTransaction();

        try
        {
            return await ApplyAsync(request, layout, transaction, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            await transaction.RollBackAsync(CancellationToken.None).ConfigureAwait(false);

            throw;
        }
    }

    private static async ValueTask<ProjectOperationResult> ApplyAsync(
        PackageEditRequest request,
        PackageVersionLayout layout,
        FileTransaction transaction,
        CancellationToken cancellationToken)
    {
        CanonicalPath project = request.ProjectFilePath;

        if (await ReadAsync(project, transaction, cancellationToken).ConfigureAwait(false) is not { } projectXml)
        {
            return transaction.Failure!;
        }

        XDocument? versionsXml = null;

        if (layout.IsCentral)
        {
            versionsXml = await ReadAsync(layout.VersionsFilePath, transaction, cancellationToken)
                .ConfigureAwait(false);

            if (versionsXml is null)
            {
                return transaction.Failure!;
            }
        }

        var changed = new List<CanonicalPath>();

        if (Rewrite(request, layout, projectXml, versionsXml) is { } refusal)
        {
            return refusal;
        }

        if (transaction.HasChanged(projectXml))
        {
            changed.Add(project);
        }

        if (versionsXml is not null && transaction.HasChanged(versionsXml))
        {
            changed.Add(layout.VersionsFilePath);
        }

        try
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            await transaction.RollBackAsync(CancellationToken.None).ConfigureAwait(false);

            return Failed(
                PackageDiagnosticCodes.FileNotWritable,
                $"'{request.PackageId}' was not changed: {exception.Message}",
                project);
        }

        return ProjectOperationResult.Succeeded();
    }

    /// <summary>
    /// Applies the change to the documents, or returns the result explaining why nothing happened.
    /// </summary>
    private static ProjectOperationResult? Rewrite(
        PackageEditRequest request,
        PackageVersionLayout layout,
        XDocument projectXml,
        XDocument? versionsXml)
    {
        string id = request.PackageId;

        switch (request.Kind)
        {
            case PackageEditKind.Install when PackageReferenceRewriter.Contains(projectXml, PackageReference, id):
                return Nothing($"'{id}' is already referenced by '{request.ProjectFilePath.FileName}'.", request);

            case PackageEditKind.Install:
                // Under central management the project carries the reference and nothing else: a
                // Version attribute there is an error NuGet reports rather than a preference it
                // honours.
                PackageReferenceRewriter.Add(
                    projectXml,
                    PackageReference,
                    id,
                    (Version, layout.IsCentral ? null : request.Version),
                    (nameof(request.PrivateAssets), request.PrivateAssets),
                    (nameof(request.IncludeAssets), request.IncludeAssets),
                    (nameof(request.ExcludeAssets), request.ExcludeAssets));

                if (layout.IsCentral)
                {
                    Upsert(versionsXml!, id, request.Version!);
                }

                return null;

            case PackageEditKind.Update when !PackageReferenceRewriter.Contains(projectXml, PackageReference, id):
                return Nothing($"'{id}' is not referenced by '{request.ProjectFilePath.FileName}'.", request);

            case PackageEditKind.Update:
                if (layout.IsCentral)
                {
                    Upsert(versionsXml!, id, request.Version!);
                }
                else
                {
                    PackageReferenceRewriter.Set(projectXml, PackageReference, id, Version, request.Version!);
                }

                return null;

            case PackageEditKind.Uninstall when !PackageReferenceRewriter.Contains(projectXml, PackageReference, id):
                return Nothing($"'{id}' is not referenced by '{request.ProjectFilePath.FileName}'.", request);

            case PackageEditKind.Uninstall:
                PackageReferenceRewriter.Remove(projectXml, PackageReference, id);

                // The central version is deliberately left alone. It may still be pinning the
                // package for another project in the repository, and this editor was given one
                // project and cannot see the others. Removing it here would break them silently.
                return null;

            default:
                throw new ArgumentOutOfRangeException(nameof(request), request.Kind, "Unknown edit kind.");
        }
    }

    private static void Upsert(XDocument versionsXml, string id, string version)
    {
        if (PackageReferenceRewriter.Contains(versionsXml, PackageVersion, id))
        {
            PackageReferenceRewriter.Set(versionsXml, PackageVersion, id, Version, version);

            return;
        }

        PackageReferenceRewriter.Add(versionsXml, PackageVersion, id, (Version, version));
    }

    /// <summary>Reads a document into the transaction, or records why it could not be.</summary>
    private static async ValueTask<XDocument?> ReadAsync(
        CanonicalPath path,
        FileTransaction transaction,
        CancellationToken cancellationToken)
    {
        string text;

        try
        {
            text = await File.ReadAllTextAsync(path.Value, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            transaction.Failure = Failed(
                PackageDiagnosticCodes.FileNotWritable, $"'{path}' could not be read: {exception.Message}", path);

            return null;
        }

        try
        {
            // PreserveWhitespace is the difference between an edit and a reformat: without it every
            // change rewrites the layout of the whole file.
            XDocument document = XDocument.Parse(text, LoadOptions.PreserveWhitespace);

            transaction.Track(path, text, document);

            return document;
        }
        catch (XmlException exception)
        {
            transaction.Failure = Failed(
                PackageDiagnosticCodes.FileNotWellFormed, $"'{path}' is not usable XML: {exception.Message}", path);

            return null;
        }
    }

    private static ProjectOperationResult Nothing(string message, PackageEditRequest request) =>
        ProjectOperationResult.Succeeded(
            [
                new ProjectDiagnostic(
                    PackageDiagnosticCodes.NothingToChange, message, ProjectDiagnosticSeverity.Warning)
                {
                    FilePath = request.ProjectFilePath,
                    ProviderName = "NuGet",
                },
            ]);

    private static ProjectOperationResult Failed(string code, string message, CanonicalPath path) =>
        ProjectOperationResult.Failed(
            new ProjectDiagnostic(code, message, ProjectDiagnosticSeverity.Error)
            {
                FilePath = path,
                ProviderName = "NuGet",
            });
}
