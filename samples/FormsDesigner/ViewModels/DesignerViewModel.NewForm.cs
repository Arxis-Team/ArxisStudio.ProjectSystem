using System;
using System.Globalization;
using System.Threading.Tasks;
using ArxisStudio.ProjectSystem;

namespace FormsDesigner.ViewModels;

public sealed partial class DesignerViewModel
{
    /// <summary>
    /// Creates a new user control in the project and opens it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A <c>UserControl</c> with a <c>Canvas</c> in it, because a canvas is the one panel where
    /// dropping a control at a point means what it looks like it means. A form starting as a
    /// <c>Grid</c> would take every drop into the same cell and stack the controls on top of one
    /// another, which reads as a broken designer rather than as a correct one.
    /// </para>
    /// <para>
    /// No <c>x:Class</c> and no code-behind. A partial class the project has not compiled is a type
    /// that does not exist, so a document naming one cannot be loaded at all until the next build —
    /// and a designer that cannot show the form it has just created is a poor introduction. Adding
    /// the class is what the user does when they want behaviour, and the SDK's globbing picks the
    /// file up either way.
    /// </para>
    /// </remarks>
    private async Task NewFormAsync()
    {
        if (Project() is not { } project)
        {
            return;
        }

        if (AskForName is null || await AskForName("NewForm") is not { Length: > 0 } chosen)
        {
            return;
        }

        string name = chosen.Trim();

        if (!name.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase))
        {
            name += ".axaml";
        }

        CanonicalPath file = project.ProjectDirectory.Combine(name);

        if (System.IO.File.Exists(file.Value))
        {
            Log($"! {name} already exists");

            return;
        }

        await System.IO.File.WriteAllTextAsync(file.Value, Template(), _shutdown.Token);

        Log($"Created {name}");

        // The project has to be read again before the new file is one of its items: the SDK's globs
        // are evaluated, and a file that appeared after the evaluation is not in the result. This is
        // the same reason a restore is followed by a refresh.
        await _workspace.RefreshAsync(_shutdown.Token);

        await OpenFormAsync(new FormFile(name, project.Name, file, project.Identity));
    }

    private static string Template() => string.Create(
        CultureInfo.InvariantCulture,
        $"""
        <UserControl xmlns="https://github.com/avaloniaui"
                     xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                     Width="480" Height="320">

          <Canvas>
          </Canvas>

        </UserControl>

        """);
}
