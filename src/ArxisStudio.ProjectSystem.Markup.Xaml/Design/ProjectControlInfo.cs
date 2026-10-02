using ArxisStudio.Markup.Xaml.Loader;

namespace ArxisStudio.ProjectSystem.Markup.Xaml;

/// <summary>A control a project's documents can place, said in names only.</summary>
/// <remarks>
/// <para>
/// Names rather than the type, because what a toolbox lists outlives the generation it was read from:
/// a <see cref="System.Type"/> in it would keep that generation in the process after a swap, and the
/// swap would end in a restart (ADR 0028).
/// </para>
/// <para>
/// A control is <see cref="IsBuilt"/> when the live generation has its class. One that is not is a
/// document of the project declaring a class no build has produced yet — the IDE has just written it —
/// listed all the same, so that a designer can offer it; placing it means building it first
/// (<see cref="ProjectDesignHost.EnsureBuiltAsync"/>).
/// </para>
/// </remarks>
/// <param name="ClassName">The class's full CLR name — what <c>x:Class</c> names.</param>
/// <param name="Name">The name an element of it is written with.</param>
/// <param name="XmlNamespace">
/// The namespace a document writes it in: the one its assembly maps its CLR namespace to, or
/// <c>using:</c> and the CLR namespace — which is also what a control not built yet is written in.
/// </param>
/// <param name="SuggestedPrefix">The prefix its assembly suggests for <paramref name="XmlNamespace"/>, or <see langword="null"/>.</param>
/// <param name="Kinds">
/// What a document can do with it: as the live generation says for a built control, and as its
/// document's root says for one not built yet.
/// </param>
/// <param name="Project">The project that builds it.</param>
/// <param name="Document">Its own markup, or empty for a control written in code alone.</param>
/// <param name="IsBuilt">Whether the live generation has its class.</param>
public sealed record ProjectControlInfo(
    string ClassName,
    string Name,
    string XmlNamespace,
    string? SuggestedPrefix,
    XamlTypeKinds Kinds,
    ProjectIdentity Project,
    CanonicalPath Document,
    bool IsBuilt);
