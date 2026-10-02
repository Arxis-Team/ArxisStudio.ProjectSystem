using Xunit;

namespace ArxisStudio.ProjectSystem.Markup.Xaml.Tests;

/// <summary>
/// The tests that load the fixtures' build into a generation, run one at a time.
/// </summary>
/// <remarks>
/// <para>
/// A process holds one generation of an assembly at a time, and that is not a test's convenience but
/// the rule the adapter exists to keep: Avalonia's runtime compiler resolves a document's names in
/// every assembly the process has loaded, so two generations of the fixtures alive together make a
/// document's <c>x:Class</c> resolve to the other one — "Unable to substitute T with T".
/// </para>
/// <para>
/// xunit runs test classes in parallel, and on the headless dispatcher two classes interleave at every
/// await. Every class that makes a generation of the fixtures belongs here.
/// </para>
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class FixtureGenerations
{
    /// <summary>The collection's name.</summary>
    public const string Name = "Generations of the fixtures";
}
