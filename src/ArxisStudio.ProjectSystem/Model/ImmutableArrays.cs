using System.Collections.Immutable;

namespace ArxisStudio.ProjectSystem;

/// <summary>
/// Keeps <see langword="default"/> immutable arrays out of the model.
/// </summary>
/// <remarks>
/// A <c>default(ImmutableArray&lt;T&gt;)</c> is not an empty array — it has no backing store and
/// throws on enumeration. To a consumer that is indistinguishable from this library returning
/// <see langword="null"/> after promising it never would, and it happens whenever a caller leaves
/// an <c>init</c> property alone in a way the compiler is happy with. Every collection property in
/// the model runs its value through here.
/// </remarks>
internal static class ImmutableArrays
{
    /// <summary>Returns the value, or an empty array when it is <see langword="default"/>.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="value">The value to normalise.</param>
    /// <returns>An array that is always safe to enumerate.</returns>
    internal static ImmutableArray<T> OrEmpty<T>(ImmutableArray<T> value) =>
        value.IsDefault ? ImmutableArray<T>.Empty : value;
}
