using System;
using System.Collections.Generic;
using Avalonia.Media;
using Avalonia.Styling;

namespace FormsDesigner.ViewModels;

/// <summary>
/// The few tokens the canvas needs, held as values rather than looked up.
/// </summary>
/// <remarks>
/// <para>
/// Everywhere else in this window a colour is a <c>DynamicResource</c> and the variant decides
/// which one — that is the whole point of <c>Views/Theme.axaml</c>. The form's card is the one
/// place it does not work: the editor realises an item's content away from the window's tree, so
/// the lookup has no variant to resolve against, and asking the application with the variant stated
/// answers with the wrong one. Painting the card literally red proved the layer was mine; painting
/// it with the token proved the token was not arriving.
/// </para>
/// <para>
/// So these six values are duplicated from the theme, deliberately and with the reason attached.
/// It is the smallest honest fix: three colours, both variants, and a designer whose forms are the
/// colour the design says they are. If the resolution is ever fixed, this file is what gets deleted.
/// </para>
/// </remarks>
public static class Palette
{
    private static readonly Dictionary<string, (Color Dark, Color Light)> Tokens = new(StringComparer.Ordinal)
    {
        ["Bg1"] = (Color.Parse("#1E1F22"), Color.Parse("#FFFFFF")),
        ["Bg2"] = (Color.Parse("#2B2D30"), Color.Parse("#F7F8FA")),
        ["Brd"] = (Color.Parse("#393B40"), Color.Parse("#EBECF0")),
    };

    /// <summary>The brush a token names in a variant, or <see langword="null"/> for an unknown one.</summary>
    public static IBrush? Brush(string key, ThemeVariant variant) =>
        Tokens.TryGetValue(key, out (Color Dark, Color Light) token)
            ? new SolidColorBrush(variant == ThemeVariant.Light ? token.Light : token.Dark)
            : null;
}
