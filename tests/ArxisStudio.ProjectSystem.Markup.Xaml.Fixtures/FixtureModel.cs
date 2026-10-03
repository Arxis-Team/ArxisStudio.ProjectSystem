namespace ArxisStudio.ProjectSystem.Markup.Xaml.Fixtures;

/// <summary>
/// A view model of the project's own: what a designer offers as a document's data type.
/// </summary>
/// <remarks>
/// Not an Avalonia object, which is what makes it data to the type catalog rather than a control.
/// </remarks>
public sealed class FixtureModel
{
    /// <summary>Gets or sets what a binding to the model reads.</summary>
    public string Caption { get; set; } = "Fixture";
}
