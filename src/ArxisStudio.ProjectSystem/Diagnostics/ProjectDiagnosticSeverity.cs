namespace ArxisStudio.ProjectSystem;

/// <summary>How seriously to take a <see cref="ProjectDiagnostic"/>.</summary>
public enum ProjectDiagnosticSeverity
{
    /// <summary>Something worth telling the user, which prevents nothing.</summary>
    Info = 0,

    /// <summary>Something probably wrong, which the result survives.</summary>
    Warning = 1,

    /// <summary>Something that prevents a correct result.</summary>
    Error = 2,
}
