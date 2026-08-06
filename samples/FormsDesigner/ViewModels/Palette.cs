using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;

namespace FormsDesigner.ViewModels;

/// <summary>
/// Looks a design token up by name for a named variant.
/// </summary>
/// <remarks>
/// The canvas realises a form's card away from the window's visual tree, so a
/// <c>DynamicResource</c> in there has no variant to resolve against and falls back to the system's
/// — which is how a dark designer ended up showing a white form. Asking the application directly,
/// with the variant stated, is the one lookup that cannot be affected by where the control ended up.
/// </remarks>
public static class Palette
{
    public static IBrush? Brush(string key, ThemeVariant variant) =>
        Application.Current is { } application
            && application.TryGetResource(key, variant, out object? found)
            && found is IBrush brush
                ? brush
                : null;
}
