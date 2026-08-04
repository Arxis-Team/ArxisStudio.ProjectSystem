using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;

namespace ArxisStudio.ProjectSystem;

/// <summary>
/// A read-only string map, used both for item metadata and for evaluated project properties.
/// </summary>
/// <remarks>
/// <para>
/// The model gives a deliberate typed core to the things every consumer needs, and puts everything
/// else here. Exposing a dedicated property for each of the hundreds of MSBuild properties a
/// project can evaluate would be a public surface nobody could keep, and would have to grow every
/// time a provider learned something new.
/// </para>
/// <para>
/// <b>Keys compare case-insensitively; values compare exactly.</b> That is not a preference — MSBuild
/// property and metadata names are case-insensitive, so <c>TargetFramework</c> and
/// <c>targetframework</c> are one property, and a map that disagreed would report a project as
/// having two. Values are data and are compared ordinally.
/// </para>
/// <para>
/// Equality is structural and order-independent, because this type lives inside records whose
/// equality is structural. The hash is computed once at construction, since the contents never
/// change.
/// </para>
/// </remarks>
[SuppressMessage(
    "Naming",
    "CA1710:Identifiers should have correct suffix",
    Justification =
        "The rule wants a 'Dictionary' suffix on anything implementing IReadOnlyDictionary, to stop a " +
        "reader mistaking a collection for a scalar. 'Metadata' is already a collective noun, so there " +
        "is no such mistake to prevent, and the suffix would put the word 'Dictionary' at every use " +
        "site in the model -- 'item.Metadata' reads correctly and 'item.MetadataDictionary' does not. " +
        "The interface is kept because foreach and LINQ over a property bag are worth having.")]
public sealed class ProjectMetadata : IReadOnlyDictionary<string, string>, IEquatable<ProjectMetadata>
{
    private readonly ImmutableDictionary<string, string> _entries;
    private readonly int _hash;

    private ProjectMetadata(ImmutableDictionary<string, string> entries)
    {
        _entries = entries;

        // Order-independent by construction: addition is commutative, so two maps with the same
        // pairs hash alike however they were built. Unchecked because overflow is meaningless here.
        int hash = 0;

        foreach (KeyValuePair<string, string> entry in entries)
        {
            unchecked
            {
                hash += HashCode.Combine(
                    StringComparer.OrdinalIgnoreCase.GetHashCode(entry.Key),
                    StringComparer.Ordinal.GetHashCode(entry.Value));
            }
        }

        _hash = hash;
    }

    /// <summary>Gets the empty map.</summary>
    public static ProjectMetadata Empty { get; } = new(
        ImmutableDictionary.Create<string, string>(StringComparer.OrdinalIgnoreCase, StringComparer.Ordinal));

    /// <summary>Gets the number of entries.</summary>
    public int Count => _entries.Count;

    /// <summary>Gets the keys, in no particular order.</summary>
    public IEnumerable<string> Keys => _entries.Keys;

    /// <summary>Gets the values, in no particular order.</summary>
    public IEnumerable<string> Values => _entries.Values;

    /// <summary>Gets the value for a key.</summary>
    /// <param name="key">The key, matched case-insensitively.</param>
    /// <returns>The value.</returns>
    /// <exception cref="KeyNotFoundException">The key is not present.</exception>
    public string this[string key] => _entries[key];

    /// <summary>Creates a map from a sequence of pairs, copying it.</summary>
    /// <param name="entries">The pairs. A later pair wins over an earlier one with the same key.</param>
    /// <returns>The map, or <see cref="Empty"/> when the sequence is empty.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="entries"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A key is null or blank.</exception>
    public static ProjectMetadata Create(IEnumerable<KeyValuePair<string, string>> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        ImmutableDictionary<string, string>.Builder builder = ImmutableDictionary.CreateBuilder<string, string>(
            StringComparer.OrdinalIgnoreCase, StringComparer.Ordinal);

        foreach (KeyValuePair<string, string> entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Key))
            {
                throw new ArgumentException("A metadata key cannot be null or blank.", nameof(entries));
            }

            builder[entry.Key] = entry.Value ?? string.Empty;
        }

        return builder.Count == 0 ? Empty : new ProjectMetadata(builder.ToImmutable());
    }

    /// <summary>Determines whether two maps have the same contents.</summary>
    /// <param name="left">The first map.</param>
    /// <param name="right">The second map.</param>
    /// <returns><see langword="true"/> when they are equal.</returns>
    public static bool operator ==(ProjectMetadata? left, ProjectMetadata? right) =>
        ReferenceEquals(left, right) || (left is not null && left.Equals(right));

    /// <summary>Determines whether two maps differ.</summary>
    /// <param name="left">The first map.</param>
    /// <param name="right">The second map.</param>
    /// <returns><see langword="true"/> when they are not equal.</returns>
    public static bool operator !=(ProjectMetadata? left, ProjectMetadata? right) => !(left == right);

    /// <summary>Determines whether a key is present.</summary>
    /// <param name="key">The key, matched case-insensitively.</param>
    /// <returns><see langword="true"/> when the key is present.</returns>
    public bool ContainsKey(string key) => _entries.ContainsKey(key);

    /// <summary>Attempts to get the value for a key.</summary>
    /// <param name="key">The key, matched case-insensitively.</param>
    /// <param name="value">The value, when the key is present.</param>
    /// <returns><see langword="true"/> when the key is present.</returns>
    public bool TryGetValue(string key, [MaybeNullWhen(false)] out string value) =>
        _entries.TryGetValue(key, out value);

    /// <summary>Gets the value for a key, or <see langword="null"/> when it is absent.</summary>
    /// <param name="key">The key, matched case-insensitively.</param>
    /// <returns>The value, or <see langword="null"/>.</returns>
    public string? GetValueOrDefault(string key) => _entries.TryGetValue(key, out string? value) ? value : null;

    /// <summary>Returns an enumerator over the entries.</summary>
    /// <returns>The enumerator.</returns>
    public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => _entries.GetEnumerator();

    /// <summary>Determines whether this map has the same contents as another.</summary>
    /// <param name="other">The map to compare with.</param>
    /// <returns><see langword="true"/> when they are equal.</returns>
    public bool Equals(ProjectMetadata? other)
    {
        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (other is null || other.Count != Count || other._hash != _hash)
        {
            return false;
        }

        foreach (KeyValuePair<string, string> entry in _entries)
        {
            if (!other._entries.TryGetValue(entry.Key, out string? value)
                || !string.Equals(value, entry.Value, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as ProjectMetadata);

    /// <inheritdoc />
    public override int GetHashCode() => _hash;

    /// <summary>Returns the entry count, or the entries themselves when there are few.</summary>
    /// <returns>A readable representation of the map.</returns>
    public override string ToString() =>
        Count == 0
            ? "(empty)"
            : Count <= 4
                ? string.Join(", ", _entries.Select(static entry => $"{entry.Key}={entry.Value}").Order(StringComparer.Ordinal))
                : string.Create(CultureInfo.InvariantCulture, $"{Count} entries");

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
