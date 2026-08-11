using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Threading;

namespace ArxisStudio.ProjectSystem.Markup.Xaml;

/// <summary>
/// Giving a generation back to the process, and proving it went.
/// </summary>
/// <remarks>
/// Kept in its own file because it is a separate capability with a separate boundary: hosting a
/// generation and ending one are different jobs, and this one reaches places the other never does.
/// </remarks>
public sealed partial class ProjectAssemblyContext
{
    /// <summary>How many times to ask the collector before answering that it will not happen.</summary>
    private const int ReclaimRounds = 10;

    private int _reclaiming;
    private int _reclaimed;

    /// <summary>
    /// Unloads this generation, removes what the process would otherwise keep of it, and says
    /// whether it is provably gone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Call this instead of <see cref="Dispose"/> when a successor generation is about to be
    /// created for the same project. <see langword="true"/> means every assembly this generation
    /// loaded is unreachable and a successor may be created; <see langword="false"/> means
    /// something still roots it, and the only honest successor is a new process — creating one
    /// here would put two copies of one type in the process, which is the failure
    /// <c>docs/adr/0021</c> measured.
    /// </para>
    /// <para>
    /// Everything the host built from these assemblies must be released first: sessions retired,
    /// populations disposed, live objects dropped, and — the one that is easy to miss — any
    /// <c>Window</c> the generation produced closed, because constructing one puts it in the
    /// windowing platform's own list and retiring the session does not take it out.
    /// </para>
    /// <para>
    /// What this removes is what measurement showed a process keeps of a generation it has
    /// unloaded: the runtime XAML compiler's emitted state when it was emitted here; every
    /// registration in Avalonia's process-wide property registry, both what its own
    /// unregistration API removes and the residue it leaves; the component-model converter cache;
    /// and, best effort, the asset loader's assembly-by-name cache. See
    /// <c>docs/adr/0023</c> for what each of those cost to find.
    /// </para>
    /// <para>
    /// The wait is bounded and never throws for a generation that will not go — the boolean is
    /// the answer. Cancellation is the exception, as everywhere else.
    /// </para>
    /// </remarks>
    /// <param name="cancellationToken">A token to observe between collections.</param>
    /// <returns><see langword="true"/> when the generation is provably gone.</returns>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    public async ValueTask<bool> TryReclaimAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _reclaiming, 1) != 0)
        {
            // A second caller is told what the first found rather than made to repeat it.
            return Volatile.Read(ref _reclaimed) != 0;
        }

        if (IsUnloaded)
        {
            // Plain disposal ran first, so the assembly list is gone and nothing here can either
            // clean up after it or prove anything about it.
            return false;
        }

        WeakReference[] traces = ForgetGeneration();

        for (var round = 0; round < ReclaimRounds; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect();

            if (Array.TrueForAll(traces, static trace => !trace.IsAlive))
            {
                Volatile.Write(ref _reclaimed, 1);

                return true;
            }

            await LetTheFrameFinishAsync().ConfigureAwait(false);
        }

        return false;
    }

    /// <summary>
    /// Gives the user interface a turn between collections.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A plain yield is not enough when this runs on a UI thread, and the difference is the whole
    /// answer for a designer: layout and rendering are queued <em>below</em> ordinary work, so a
    /// yield comes back before they have run and the objects they are still holding are the
    /// objects being measured. Waiting at the lowest priority is waiting for them to finish.
    /// </para>
    /// <para>
    /// Measured: the same generation that a harness pumping the dispatcher proved dead survived
    /// ten rounds of yielding, on the same project in the same process.
    /// </para>
    /// <para>
    /// Both halves of the test matter. Without a running application there is nothing draining
    /// the dispatcher's queue, so waiting on it would wait for ever — which is what a unit test
    /// calling this from a plain thread found, and why an application has to be there before the
    /// dispatcher is asked for anything.
    /// </para>
    /// </remarks>
    private static async ValueTask LetTheFrameFinishAsync()
    {
        if (Application.Current is not null && Dispatcher.UIThread.CheckAccess())
        {
            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);

            return;
        }

        await Task.Yield();
    }

    /// <summary>
    /// Removes every trace of this generation the process would otherwise keep, and unloads it.
    /// </summary>
    /// <remarks>
    /// Deliberately synchronous and deliberately not inlined. An asynchronous method keeps its
    /// locals in an object that lives as long as the method does, so an assembly named by a local
    /// here would be alive throughout the collection that is trying to prove it dead — the first
    /// draft of the measurement this was written against reported exactly that and meant nothing
    /// by it. A frame that has returned is the one storage the collector is sure about.
    /// </remarks>
    /// <returns>Weak references to everything that should now be gone.</returns>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private WeakReference[] ForgetGeneration()
    {
        List<Assembly> dying;
        AssemblyLoadContext? context;

        lock (_gate)
        {
            context = _context;

            dying =
            [
                .. _resolved.Values
                    .OfType<Assembly>()
                    .Where(assembly => AssemblyLoadContext.GetLoadContext(assembly) == context),
            ];
        }

        WeakReference[] traces =
        [
            .. dying.Select(static assembly => new WeakReference(assembly)),
            .. context is null ? Array.Empty<WeakReference>() : [new WeakReference(context)],
        ];

        if (context is not null)
        {
            // Nothing has entered a successor's scope yet, so the compiler's emitted state is
            // still this generation's and still rooting it.
            RuntimeXamlCompiler.ResetIfEmittedIn(context);
        }

        AvaloniaRegistry.Forget(dying);

        foreach (Assembly assembly in dying)
        {
            // TypeDescriptor keeps a converter per type, for the life of the process, and
            // ArxisStudio.Markup asks it for one whenever a value is converted from text.
            TypeDescriptor.Refresh(assembly);
        }

        AvaloniaAssets.Forget(dying);

        dying.Clear();

        Dispose();

        return traces;
    }

    /// <summary>
    /// Avalonia's process-wide property registry, emptied of a generation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The registry's own <c>UnregisterByModule</c> is asked first, because it knows bookkeeping
    /// nothing outside can do. It is not enough on its own, and that is measured rather than
    /// assumed: it answers <c>True</c> and still leaves the type as a key in its by-type
    /// dictionary — kept there by the properties the type merely inherits — and leaves every
    /// property the type declared in the by-identifier dictionary it never touches. Either
    /// residue keeps the whole generation in the process.
    /// </para>
    /// <para>
    /// So the remainder is swept by hand, in the posture <c>docs/adr/0020</c> established for the
    /// runtime compiler: every member is found by name <em>and</em> checked for shape, and
    /// anything unfamiliar is left alone, which costs a reclaim that answers "no" rather than a
    /// designer that breaks.
    /// </para>
    /// </remarks>
    private static class AvaloniaRegistry
    {
        private static readonly Type? Registry = Type.GetType(
            "Avalonia.AvaloniaPropertyRegistry, Avalonia.Base",
            throwOnError: false);

        internal static void Forget(List<Assembly> dying)
        {
            if (dying.Count == 0 || Instance() is not { } registry)
            {
                return;
            }

            var suspects = new HashSet<Assembly>(dying);

            Unregister(registry, Owners(dying));
            Sweep(registry, suspects);
        }

        private static object? Instance()
        {
            try
            {
                return Registry
                    ?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)
                    ?.GetValue(null);
            }
            catch (Exception exception) when (IsReflection(exception))
            {
                return null;
            }
        }

        /// <summary>The types that can own a property, which is all the registry accepts.</summary>
        /// <remarks>
        /// Offering it everything — a compiled XAML helper, a generated namespace-info class —
        /// made it answer <c>False</c> and change nothing at all.
        /// </remarks>
        private static List<Type> Owners(List<Assembly> dying)
        {
            var owners = new List<Type>();

            foreach (Assembly assembly in dying)
            {
                Type[] types;

                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException incomplete)
                {
                    types = [.. incomplete.Types.OfType<Type>()];
                }
                catch (Exception exception) when (IsReflection(exception))
                {
                    continue;
                }

                owners.AddRange(types.Where(static type => typeof(AvaloniaObject).IsAssignableFrom(type)));
            }

            return owners;
        }

        private static void Unregister(object registry, List<Type> owners)
        {
            if (owners.Count == 0)
            {
                return;
            }

            try
            {
                MethodInfo? unregister = registry.GetType()
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(static method => method.Name == "UnregisterByModule"
                        && method.GetParameters() is [{ ParameterType: { } parameter }]
                        && parameter.IsAssignableFrom(typeof(List<Type>)));

                unregister?.Invoke(registry, [owners]);
            }
            catch (Exception exception) when (IsReflection(exception))
            {
                // A registry that will not be told is a generation that will not be reclaimed,
                // and the caller hears that as a "no" rather than as an exception.
            }
        }

        /// <summary>Removes what the unregistration left behind.</summary>
        private static void Sweep(object registry, HashSet<Assembly> suspects)
        {
            foreach (FieldInfo field in registry.GetType()
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
            {
                IDictionary map;

                try
                {
                    if (field.GetValue(registry) is not IDictionary found)
                    {
                        continue;
                    }

                    map = found;
                }
                catch (Exception exception) when (IsReflection(exception))
                {
                    continue;
                }

                var stale = new List<object>();

                try
                {
                    foreach (DictionaryEntry entry in map)
                    {
                        if ((entry.Key is Type owner && suspects.Contains(owner.Assembly))
                            || (entry.Value is AvaloniaProperty property
                                && suspects.Contains(property.OwnerType.Assembly)))
                        {
                            stale.Add(entry.Key);

                            continue;
                        }

                        // A surviving type's own entries may still list a property this
                        // generation declared.
                        Weed(entry.Value, suspects);
                    }
                }
                catch (InvalidOperationException)
                {
                    // Something else is writing to the registry; a sweep does not get to insist,
                    // and what it misses the answer below reports as a generation that stayed.
                    continue;
                }

                foreach (object key in stale)
                {
                    map.Remove(key);
                }
            }
        }

        private static void Weed(object? value, HashSet<Assembly> suspects)
        {
            switch (value)
            {
                case IDictionary inner:
                {
                    var stale = new List<object>();

                    foreach (DictionaryEntry entry in inner)
                    {
                        if (entry.Value is AvaloniaProperty property
                            && suspects.Contains(property.OwnerType.Assembly))
                        {
                            stale.Add(entry.Key);
                        }
                    }

                    foreach (object key in stale)
                    {
                        inner.Remove(key);
                    }

                    return;
                }

                case IList list:
                {
                    for (int index = list.Count - 1; index >= 0; index--)
                    {
                        if (list[index] is AvaloniaProperty property
                            && suspects.Contains(property.OwnerType.Assembly))
                        {
                            list.RemoveAt(index);
                        }
                    }

                    return;
                }

                default:
                    return;
            }
        }
    }

    /// <summary>
    /// The asset loader's memory of which assembly a name meant, cleared of a generation.
    /// </summary>
    /// <remarks>
    /// It answers a simple name with the assembly it cached first, so a stale entry would both
    /// keep the old generation in the process and send the successor's <c>avares</c> lookups to
    /// it. Entirely internal to Avalonia, so entirely best effort: every step is checked, and a
    /// shape this does not recognise is left alone.
    /// </remarks>
    private static class AvaloniaAssets
    {
        internal static void Forget(List<Assembly> dying)
        {
            if (dying.Count == 0)
            {
                return;
            }

            try
            {
                object? locator = Type.GetType("Avalonia.AvaloniaLocator, Avalonia.Base")
                    ?.GetProperty("Current", BindingFlags.Public | BindingFlags.Static)
                    ?.GetValue(null);

                object? loader = locator?.GetType()
                    .GetMethod("GetService", [typeof(Type)])
                    ?.Invoke(locator, [typeof(Avalonia.Platform.IAssetLoader)]);

                if (loader is null)
                {
                    return;
                }

                foreach (FieldInfo field in loader.GetType()
                    .GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
                {
                    if (!field.FieldType.Name.Contains("AssemblyDescriptorResolver", StringComparison.Ordinal)
                        || field.GetValue(loader) is not { } resolver)
                    {
                        continue;
                    }

                    // Avalonia has a method for this, and it is the one to ask first: the cache is
                    // its own and it may keep more than the dictionary this can see.
                    if (!Invalidate(resolver, dying))
                    {
                        Purge(resolver, dying);
                    }
                }
            }
            catch (Exception exception) when (IsReflection(exception))
            {
            }
        }

        /// <summary>Asks the resolver to forget a name, which is what it offers a method for.</summary>
        private static bool Invalidate(object resolver, List<Assembly> dying)
        {
            MethodInfo? invalidate = resolver.GetType()
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(static method => method.Name == "InvalidateAssemblyCache"
                    && method.GetParameters() is [{ ParameterType: { } parameter }]
                    && parameter == typeof(string));

            if (invalidate is null)
            {
                return false;
            }

            foreach (Assembly assembly in dying)
            {
                if (assembly.GetName().Name is { Length: > 0 } name)
                {
                    invalidate.Invoke(resolver, [name]);
                }
            }

            return true;
        }

        private static void Purge(object resolver, List<Assembly> dying)
        {
            foreach (FieldInfo field in resolver.GetType()
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
            {
                if (field.GetValue(resolver) is not IDictionary cache)
                {
                    continue;
                }

                var stale = new List<object>();

                foreach (DictionaryEntry entry in cache)
                {
                    if (entry.Key is string name
                        && dying.Any(assembly => string.Equals(
                            assembly.GetName().Name, name, StringComparison.OrdinalIgnoreCase)))
                    {
                        stale.Add(entry.Key);
                    }
                }

                foreach (object key in stale)
                {
                    cache.Remove(key);
                }
            }
        }
    }

    /// <summary>What reaching into somebody else's shape is allowed to fail with.</summary>
    private static bool IsReflection(Exception exception) =>
        exception is MemberAccessException
            or TargetInvocationException
            or InvalidOperationException
            or NotSupportedException
            or TypeLoadException
            or System.IO.FileNotFoundException
            or ArgumentException;
}
