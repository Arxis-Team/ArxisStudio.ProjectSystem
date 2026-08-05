using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text.Json;

namespace ArxisStudio.ProjectSystem.NuGet;

/// <summary>
/// Reads the three JSON documents a NuGet V3 feed answers with.
/// </summary>
/// <remarks>
/// <para>
/// By hand, with <see cref="System.Text.Json"/>, for the same reason <c>project.assets.json</c> is
/// read by hand — see
/// <see href="../../docs/adr/0012-restore-assets-are-read-not-resolved.md">ADR 0012</see>. The
/// shapes below are a documented protocol and a small part of it; the alternative was
/// <c>NuGet.Protocol</c>, which brings a client stack, a plugin model and a credential system to
/// read four fields.
/// </para>
/// <para>
/// Every reader takes what it recognises and ignores the rest, so a feed that adds a field is read
/// rather than refused. A feed that omits an optional one produces a package with less to say about
/// it, which is not an error: feeds genuinely differ in what they publish.
/// </para>
/// </remarks>
internal static class FeedResponseReader
{
    /// <summary>
    /// Finds a resource in a service index.
    /// </summary>
    /// <remarks>
    /// Resource types are versioned as <c>SearchQueryService/3.5.0</c>, and an index lists several
    /// versions of the same resource. Matching on the prefix takes whichever the feed offers rather
    /// than requiring one this library happened to be written against.
    /// </remarks>
    /// <param name="json">The service index.</param>
    /// <param name="resourceType">The resource type, without its version.</param>
    /// <returns>The address, or <see langword="null"/> when the feed offers no such resource.</returns>
    internal static string? ReadResource(string json, string resourceType)
    {
        using JsonDocument document = JsonDocument.Parse(json);

        if (!document.RootElement.TryGetProperty("resources", out JsonElement resources)
            || resources.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (JsonElement resource in resources.EnumerateArray())
        {
            string? type = String(resource, "@type");

            if (type is null || !Matches(type, resourceType))
            {
                continue;
            }

            if (String(resource, "@id") is { Length: > 0 } address)
            {
                return address;
            }
        }

        return null;
    }

    /// <summary>Reads a search response.</summary>
    /// <param name="json">The response.</param>
    /// <returns>The packages, in the order the feed ranked them.</returns>
    internal static ImmutableArray<FoundPackage> ReadSearch(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);

        if (!document.RootElement.TryGetProperty("data", out JsonElement data)
            || data.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        ImmutableArray<FoundPackage>.Builder packages = ImmutableArray.CreateBuilder<FoundPackage>();

        foreach (JsonElement entry in data.EnumerateArray())
        {
            if (String(entry, "id") is not { Length: > 0 } id)
            {
                continue;
            }

            packages.Add(new FoundPackage
            {
                Id = id,
                LatestVersion = String(entry, "version"),
                Description = String(entry, "description"),
                Authors = Authors(entry),
                ProjectUrl = String(entry, "projectUrl"),
                License = String(entry, "licenseExpression") ?? String(entry, "licenseUrl"),
                TotalDownloads = Number(entry, "totalDownloads"),
                Versions = PackageVersions.Sort(Versions(entry)),
            });
        }

        return packages.ToImmutable();
    }

    /// <summary>Reads a package's version index.</summary>
    /// <param name="json">The index.</param>
    /// <returns>The versions, newest first.</returns>
    internal static ImmutableArray<string> ReadVersions(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);

        if (!document.RootElement.TryGetProperty("versions", out JsonElement versions)
            || versions.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var listed = new List<string?>();

        foreach (JsonElement version in versions.EnumerateArray())
        {
            if (version.ValueKind == JsonValueKind.String)
            {
                listed.Add(version.GetString());
            }
        }

        // The index is ascending and a caller wants newest first, which is also where the sort drops
        // anything unparseable.
        return PackageVersions.Sort(listed);
    }

    /// <summary>Whether a versioned resource type is the one being looked for.</summary>
    private static bool Matches(string declared, string wanted) =>
        string.Equals(declared, wanted, StringComparison.Ordinal)
            || (declared.StartsWith(wanted, StringComparison.Ordinal)
                && declared.Length > wanted.Length
                && declared[wanted.Length] == '/');

    /// <summary>
    /// Authors, which a feed writes as either a string or an array of them depending on its age.
    /// </summary>
    private static string? Authors(JsonElement entry)
    {
        if (!entry.TryGetProperty("authors", out JsonElement authors))
        {
            return null;
        }

        if (authors.ValueKind == JsonValueKind.String)
        {
            return authors.GetString();
        }

        if (authors.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var names = new List<string>();

        foreach (JsonElement author in authors.EnumerateArray())
        {
            if (author.ValueKind == JsonValueKind.String && author.GetString() is { Length: > 0 } name)
            {
                names.Add(name);
            }
        }

        return names.Count == 0 ? null : string.Join(", ", names);
    }

    /// <summary>
    /// The versions a search entry lists, which are objects carrying a version and a download count
    /// rather than plain strings.
    /// </summary>
    private static IEnumerable<string?> Versions(JsonElement entry)
    {
        if (!entry.TryGetProperty("versions", out JsonElement versions)
            || versions.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (JsonElement version in versions.EnumerateArray())
        {
            yield return version.ValueKind == JsonValueKind.String
                ? version.GetString()
                : String(version, "version");
        }
    }

    private static string? String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

    private static long? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out long number)
                ? number
                : null;
}
