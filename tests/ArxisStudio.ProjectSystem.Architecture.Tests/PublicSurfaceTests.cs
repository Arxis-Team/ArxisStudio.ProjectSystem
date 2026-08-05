using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using Xunit;

namespace ArxisStudio.ProjectSystem.Architecture.Tests;

/// <summary>
/// What the public surface may and may not contain, checked against the compiled assembly rather
/// than against anyone's memory of the rules.
/// </summary>
public sealed class PublicSurfaceTests
{
    private static Assembly Core => RepositoryLayout.LoadAssembly(RepositoryLayout.CorePackage);

    /// <summary>
    /// Exactly the root namespace, not merely a prefix of it. Folders organise files; they do not
    /// make namespaces, and a consumer should need one <c>using</c>.
    /// </summary>
    [Fact]
    public void EveryPublicType_LivesInTheRootNamespace()
    {
        List<string> offenders = Core.GetExportedTypes()
            .Where(static type => !string.Equals(
                type.Namespace, RepositoryLayout.CorePackage, StringComparison.Ordinal))
            .Select(static type => type.FullName ?? type.Name)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "These public types are outside the root namespace: " + string.Join(", ", offenders) + ".");
    }

    /// <summary>
    /// No provider implementation type may appear anywhere a consumer can reach, which is the rule
    /// that lets a snapshot outlive the machinery that produced it.
    /// </summary>
    [Fact]
    public void NoPublicMember_ExposesAForbiddenType()
    {
        var offenders = new List<string>();

        foreach (Type type in Core.GetExportedTypes())
        {
            foreach (Type referenced in ReferencedTypes(type))
            {
                string name = referenced.Assembly.GetName().Name ?? string.Empty;

                if (ForbiddenDependencies.IsForbidden(name))
                {
                    offenders.Add($"{type.Name} -> {referenced.FullName}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "These public members expose out-of-scope types: " + string.Join(", ", offenders.Distinct()) + ".");
    }

    /// <summary>
    /// The out-of-process guard, and the most valuable test here.
    /// </summary>
    /// <remarks>
    /// A future MSBuild provider should be able to run in a worker process without a single
    /// consumer-facing type changing. That is only true if the snapshot model is data all the way
    /// down — nothing that has to be alive, nothing that owns a handle, nothing that can only be
    /// dereferenced in the process that made it. Walking the model and refusing those shapes turns
    /// the claim into something checked rather than hoped for.
    /// </remarks>
    [Fact]
    public void TheSnapshotModel_CarriesOnlyData()
    {
        var offenders = new List<string>();
        var visited = new HashSet<Type>();

        foreach (string root in new[] { "WorkspaceLoadResult", "SolutionSnapshot", "ProjectSnapshot" })
        {
            Walk(Core.GetType($"{RepositoryLayout.CorePackage}.{root}")!, visited, offenders);
        }

        Assert.True(
            offenders.Count == 0,
            "These members would not survive a process boundary: " + string.Join(", ", offenders) + ".");
    }

    /// <summary>
    /// A nullable wrapper over a value type that already has an absent value gives absence two
    /// spellings, and every consumer eventually checks only one of them.
    /// </summary>
    [Fact]
    public void NoPublicMember_WrapsACoreValueTypeInNullable()
    {
        string[] haveTheirOwnAbsentValue =
        [
            "CanonicalPath", "ProjectIdentity", "SolutionIdentity", "WorkspaceIdentity",
            "WorkspaceVersion", "FileSpan", "WorkspaceEntryPoint",
        ];

        List<string> offenders = [.. PublicMemberTypes()
            .Where(pair => Nullable.GetUnderlyingType(pair.Type) is { } inner
                && haveTheirOwnAbsentValue.Contains(inner.Name, StringComparer.Ordinal))
            .Select(static pair => pair.Where)];

        Assert.True(
            offenders.Count == 0,
            "These members use a nullable wrapper over a type that already has an absent value: "
            + string.Join(", ", offenders) + ". Use the type's own None instead.");
    }

    [Fact]
    public void NoPublicMember_ExposesAMutableCollection()
    {
        Type[] mutable =
        [
            typeof(List<>), typeof(IList<>), typeof(ICollection<>),
            typeof(Dictionary<,>), typeof(IDictionary<,>),
        ];

        List<string> offenders = [.. PublicMemberTypes()
            .Where(static pair => !pair.Owner.Name.EndsWith("Builder", StringComparison.Ordinal))
            .Where(pair => pair.Type.IsArray
                || (pair.Type.IsGenericType && mutable.Contains(pair.Type.GetGenericTypeDefinition())))
            .Select(static pair => pair.Where)];

        Assert.True(
            offenders.Count == 0,
            "These members expose a mutable collection: " + string.Join(", ", offenders) + ". " +
            "Builders are mutable by design; published state is not.");
    }

    /// <summary>The mechanical form of "no public mutable setters".</summary>
    [Fact]
    public void NoPublicProperty_HasASetterOtherThanInitOrOnABuilder()
    {
        var offenders = new List<string>();

        foreach (Type type in Core.GetExportedTypes().Where(static type => !type.Name.EndsWith("Builder", StringComparison.Ordinal)))
        {
            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (property.SetMethod is { IsPublic: true } setter && !IsInitOnly(setter))
                {
                    offenders.Add($"{type.Name}.{property.Name}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "These properties have a public setter: " + string.Join(", ", offenders) + ".");
    }

    /// <summary>
    /// A raw path string creeping back into the model is how one project ends up in the graph
    /// twice, so no part of the model may hold one.
    /// </summary>
    /// <remarks>
    /// Properties only, deliberately. A string has to become a <c>CanonicalPath</c> somewhere, and
    /// that somewhere is the factory parameters — <c>CanonicalPath.Create(string absolutePath)</c>
    /// is the door, not a leak through the wall. What matters is that nothing the model
    /// <em>stores</em> is a bare path.
    /// </remarks>
    [Fact]
    public void NoPublicProperty_NamedAfterAPath_IsAString()
    {
        List<string> offenders = [.. PublicProperties()
            .Where(static pair => pair.Property.PropertyType == typeof(string))
            .Where(static pair => pair.Property.Name.EndsWith("Path", StringComparison.Ordinal)
                || pair.Property.Name.EndsWith("Directory", StringComparison.Ordinal))
            .Select(static pair => $"{pair.Owner.Name}.{pair.Property.Name}")];

        Assert.True(
            offenders.Count == 0,
            "These properties name a path but are typed string: " + string.Join(", ", offenders) + ". " +
            "Use CanonicalPath, which is normalised and comparable.");
    }

    private static bool IsInitOnly(MethodInfo setter) =>
        setter.ReturnParameter
            .GetRequiredCustomModifiers()
            .Any(static modifier => string.Equals(
                modifier.FullName, "System.Runtime.CompilerServices.IsExternalInit", StringComparison.Ordinal));

    private static IEnumerable<(Type Owner, PropertyInfo Property)> PublicProperties() =>
        Core.GetExportedTypes()
            .SelectMany(static type => type
                .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Select(property => (type, property)));

    private static IEnumerable<(Type Owner, string Name, Type Type, string Where)> PublicMemberTypes()
    {
        foreach (Type type in Core.GetExportedTypes())
        {
            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                yield return (type, property.Name, property.PropertyType, $"{type.Name}.{property.Name}");
            }

            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(static method => !method.IsSpecialName))
            {
                yield return (type, method.Name, method.ReturnType, $"{type.Name}.{method.Name}()");

                foreach (ParameterInfo parameter in method.GetParameters())
                {
                    yield return (type, parameter.Name ?? string.Empty, parameter.ParameterType,
                        $"{type.Name}.{method.Name}({parameter.Name})");
                }
            }
        }
    }

    private static IEnumerable<Type> ReferencedTypes(Type type)
    {
        if (type.BaseType is { } baseType)
        {
            yield return baseType;
        }

        foreach (Type contract in type.GetInterfaces())
        {
            yield return contract;
        }

        foreach ((Type _, string _, Type memberType, string _) in PublicMemberTypes().Where(pair => pair.Owner == type))
        {
            foreach (Type unwrapped in Unwrap(memberType))
            {
                yield return unwrapped;
            }
        }
    }

    private static IEnumerable<Type> Unwrap(Type type)
    {
        yield return type;

        if (type.IsGenericType)
        {
            foreach (Type argument in type.GetGenericArguments())
            {
                foreach (Type nested in Unwrap(argument))
                {
                    yield return nested;
                }
            }
        }
    }

    private static void Walk(Type type, HashSet<Type> visited, List<string> offenders)
    {
        if (!visited.Add(type))
        {
            return;
        }

        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            foreach (Type part in Unwrap(property.PropertyType))
            {
                if (!SurvivesAProcessBoundary(part))
                {
                    offenders.Add($"{type.Name}.{property.Name} ({part.Name})");

                    continue;
                }

                if (part.Assembly == Core && !part.IsEnum)
                {
                    Walk(part, visited, offenders);
                }
            }
        }
    }

    private static bool SurvivesAProcessBoundary(Type type)
    {
        if (type == typeof(object) || type.IsArray)
        {
            return false;
        }

        if (typeof(Delegate).IsAssignableFrom(type)
            || typeof(Stream).IsAssignableFrom(type)
            || typeof(IDisposable).IsAssignableFrom(type)
            || typeof(IAsyncDisposable).IsAssignableFrom(type)
            || typeof(MemberInfo).IsAssignableFrom(type)
            || typeof(Assembly).IsAssignableFrom(type))
        {
            return false;
        }

        if (!type.IsGenericType)
        {
            return true;
        }

        Type definition = type.GetGenericTypeDefinition();

        return definition != typeof(List<>)
            && definition != typeof(IList<>)
            && definition != typeof(ICollection<>)
            && definition != typeof(Dictionary<,>)
            && definition != typeof(IDictionary<,>)
            && definition != typeof(System.Threading.Tasks.Task<>)
            && definition != typeof(System.Threading.Tasks.ValueTask<>)
            && (definition == typeof(ImmutableArray<>)
                || definition == typeof(Nullable<>)
                || definition == typeof(IEnumerable<>)
                || definition == typeof(IReadOnlyList<>)
                || definition == typeof(IReadOnlyCollection<>)
                || definition == typeof(IReadOnlyDictionary<,>)
                || definition == typeof(KeyValuePair<,>));
    }
}
