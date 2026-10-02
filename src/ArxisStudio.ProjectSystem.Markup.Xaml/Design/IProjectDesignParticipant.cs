using System.Threading;
using System.Threading.Tasks;

namespace ArxisStudio.ProjectSystem.Markup.Xaml;

/// <summary>
/// A part of a designer that holds what a generation of the project's types built — a canvas, a
/// selection, an inspector, a toolbox — and lets go of it for a swap.
/// </summary>
/// <remarks>
/// <para>
/// A generation is reclaimed only once nothing in the process holds anything of it, and the host
/// cannot see into a designer's parts. Each one that holds a root, a control, a type or a member is
/// registered (<see cref="ProjectDesignHost.Register"/>), and the host asks it to let go before the
/// documents are detached and the generation is reclaimed, and to take up the successor's once the
/// visible documents show it.
/// </para>
/// <para>
/// Both are called on the user interface thread, in the order the participants were registered. A
/// participant whose release throws is reported and does not stop the others; a swap that then finds
/// the generation held answers with a restart, which is the honest outcome either way.
/// </para>
/// </remarks>
public interface IProjectDesignParticipant
{
    /// <summary>
    /// Lets go of everything built from the current generation: roots shown, controls selected, types
    /// and members cached. A canvas freezes its last frame here, so the person sees the forms rather
    /// than an empty surface while the types are replaced.
    /// </summary>
    /// <param name="cancellationToken">A token to observe.</param>
    /// <returns>A task that completes once nothing of the generation is held.</returns>
    ValueTask ReleaseAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Takes up the successor: the visible documents show it. Not called when the generation could not
    /// be reclaimed — the designer then shows what it froze until it restarts.
    /// </summary>
    /// <param name="cancellationToken">A token to observe.</param>
    /// <returns>A task that completes once the participant shows the successor.</returns>
    ValueTask RestoreAsync(CancellationToken cancellationToken);
}
