using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Execution;
using Microsoft.Build.Framework;

namespace ArxisStudio.ProjectSystem.MSBuild;

/// <summary>What running targets produced.</summary>
internal sealed record OperationOutcome
{
    /// <summary>Gets a value indicating whether the engine reported success.</summary>
    public required bool Succeeded { get; init; }

    /// <summary>Gets everything the engine said.</summary>
    public ImmutableArray<EngineMessage> Messages
    {
        get => field;
        init => field = value.IsDefault ? [] : value;
    } = [];
}

/// <summary>
/// Runs MSBuild targets against an entry point.
/// </summary>
/// <remarks>
/// <para>
/// The second type in this package that names <c>Microsoft.Build</c>, and it obeys the same ordering
/// rule as the evaluator: nothing enters it before <see cref="MSBuildEnvironment"/> has registered,
/// and <see cref="MethodImplOptions.NoInlining"/> keeps the JIT from folding it into a caller that
/// runs earlier.
/// </para>
/// <para>
/// <b>One build at a time, per process.</b> <see cref="BuildManager.DefaultBuildManager"/> is a
/// singleton and its convenience overload pairs a begin and an end around each call, so two
/// concurrent builds through it fight. A lock here makes the queueing explicit rather than leaving
/// it to fail confusingly; a caller that wants parallelism across projects gets it from MSBuild's
/// own scheduler inside one build, not by starting several.
/// </para>
/// <para>
/// <b>Cancellation is real here</b>, unlike evaluation: MSBuild can abandon submissions, so a
/// cancelled build stops rather than running to completion and being discarded.
/// </para>
/// </remarks>
internal static class MSBuildOperationRunner
{
    /// <summary>Serialises builds, because the build manager this uses is process-global.</summary>
    private static readonly Lock Sync = new();

    /// <summary>Runs targets and collects what the engine said.</summary>
    /// <param name="entryPoint">The solution or project to build.</param>
    /// <param name="targets">The targets to run.</param>
    /// <param name="globalProperties">Properties to build with.</param>
    /// <param name="onProgress">Called as projects start, or <see langword="null"/> for nowhere.</param>
    /// <param name="cancellationToken">A token to observe.</param>
    /// <returns>What the engine produced.</returns>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static OperationOutcome Run(
        CanonicalPath entryPoint,
        IReadOnlyList<string> targets,
        IReadOnlyDictionary<string, string> globalProperties,
        Action<string>? onProgress,
        CancellationToken cancellationToken)
    {
        var listener = new OperationListener(onProgress);

        var parameters = new BuildParameters
        {
            Loggers = [listener],

            // The engine's own console output belongs to whoever hosts a console. Everything a
            // caller needs arrives through the listener as structured diagnostics.
            DetailedSummary = false,
        };

        // MSBuild declares its global properties as string?, so the dictionary is typed to match
        // rather than fought with a suppression.
        var properties = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (KeyValuePair<string, string> property in globalProperties)
        {
            properties[property.Key] = property.Value;
        }

        var request = new BuildRequestData(
            entryPoint.Value,
            properties,
            toolsVersion: null,
            targetsToBuild: [.. targets],
            hostServices: null);

        lock (Sync)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using CancellationTokenRegistration registration = cancellationToken.Register(Cancel);

            // The engine keeps what it evaluated, and this process is long-lived: a build manager
            // that has seen a project once will build the files it saw, whatever the file system now
            // says. A tool that creates a file and then builds — which is what a designer adding a
            // form does — got a build of the file set from before the file existed, reported as a
            // success, with the class missing from the assembly it produced.
            //
            // So each operation starts from the project as it is on disk. The cost is one
            // evaluation per operation, which is the price of being right in a process that outlives
            // the files it is looking at.
            BuildManager.DefaultBuildManager.ResetCaches();

            // And the projects the engine is holding evaluations for. A build asked for by path
            // loads through the global collection, and a collection that already has that path
            // returns what it evaluated before — items and all. Globs are part of an evaluation, so
            // a file created since then is not in the build, which is reported as a success with the
            // file simply absent from what it produced.
            ProjectCollection.GlobalProjectCollection.UnloadAllProjects();

            BuildResult result = BuildManager.DefaultBuildManager.Build(parameters, request);

            // A cancelled build reports failure with nothing useful to say about it, so the token is
            // what the caller hears rather than a diagnostic invented from a cancellation.
            cancellationToken.ThrowIfCancellationRequested();

            return new OperationOutcome
            {
                Succeeded = result.OverallResult == BuildResultCode.Success,
                Messages = listener.Messages.ToImmutable(),
            };
        }
    }

    /// <summary>
    /// Asks the engine to abandon what it is doing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A cancellation callback runs on whichever thread called <c>Cancel</c>, and an exception it
    /// throws comes back out of that call wrapped in an <see cref="AggregateException"/>. So a
    /// caller cancelling a build would see a failure raised from their own cancellation, in their
    /// own code, about someone else's engine.
    /// </para>
    /// <para>
    /// That is not hypothetical. <c>CancelAllSubmissions</c> throws
    /// <see cref="NullReferenceException"/> from inside MSBuild when no build is in progress — it
    /// reaches for a logging service that only exists between a begin and an end. The race is
    /// unavoidable: the build can finish between the token being cancelled and this registration
    /// being disposed. Swallowing costs nothing, because cancelling a build that has already
    /// finished has nothing left to do, and the token is re-checked after the build either way.
    /// </para>
    /// </remarks>
    private static void Cancel()
    {
        try
        {
            BuildManager.DefaultBuildManager.CancelAllSubmissions();
        }
#pragma warning disable CA1031 // See above: this runs on the canceller's thread, and nothing it
        catch (Exception)      // could throw is that thread's problem to hear about.
#pragma warning restore CA1031
        {
        }
    }

    /// <summary>
    /// Collects what the engine says while building, and reports progress as projects start.
    /// </summary>
    /// <remarks>
    /// Project-started is the granularity a progress bar can use. Target and task events arrive in
    /// their thousands and would turn a progress report into a log.
    /// </remarks>
    private sealed class OperationListener(Action<string>? onProgress) : ILogger
    {
        public ImmutableArray<EngineMessage>.Builder Messages { get; } =
            ImmutableArray.CreateBuilder<EngineMessage>();

        public LoggerVerbosity Verbosity { get; set; } = LoggerVerbosity.Quiet;

        public string? Parameters { get; set; }

        public void Initialize(IEventSource eventSource)
        {
            eventSource.ErrorRaised += (_, e) => Add(e.Code, e.Message, true, e.File, e.LineNumber, e.ColumnNumber);
            eventSource.WarningRaised += (_, e) => Add(e.Code, e.Message, false, e.File, e.LineNumber, e.ColumnNumber);

            if (onProgress is not null)
            {
                eventSource.ProjectStarted += (_, e) => onProgress(
                    string.Create(CultureInfo.InvariantCulture, $"Building {e.ProjectFile}"));
            }
        }

        public void Shutdown()
        {
        }

        private void Add(string? code, string? message, bool isError, string? file, int line, int column)
        {
            Messages.Add(new EngineMessage
            {
                Code = string.IsNullOrWhiteSpace(code) ? "MSBUILD" : code,
                Message = message ?? string.Empty,
                IsError = isError,
                File = CanonicalPath.TryCreate(file, out CanonicalPath path) ? path : CanonicalPath.None,
                Line = line,
                Column = column,
            });
        }
    }
}
