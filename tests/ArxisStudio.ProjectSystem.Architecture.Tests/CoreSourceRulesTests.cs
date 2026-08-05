using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace ArxisStudio.ProjectSystem.Architecture.Tests;

/// <summary>
/// Rules about the shipping source that reflection cannot reach, checked by reading it.
/// </summary>
/// <remarks>
/// A text scan is a blunt instrument and these are deliberately narrow patterns. Each one stands in
/// for a promise the package makes in its README, and a promise nothing checks is a promise that
/// quietly stops being true. Comments and documentation are stripped first, so a rule can be
/// discussed in prose without tripping over itself.
/// </remarks>
public sealed class CoreSourceRulesTests
{
    /// <summary>"No <c>.Result</c>, no <c>.Wait()</c>, no sync-over-async."</summary>
    [Theory]
    [InlineData(@"\.Result\b")]
    [InlineData(@"\.Wait\(\)")]
    [InlineData(@"GetAwaiter\(\)\.GetResult\(\)")]
    [InlineData(@"Task\.Run\(")]
    [InlineData(@"Thread\.Sleep")]
    public void TheCore_NeverBlocksOnAsynchronousWork(string pattern) => Forbid(pattern);

    /// <summary>
    /// "Does not depend on the current working directory after a request has been accepted."
    /// </summary>
    [Theory]
    [InlineData(@"Directory\.GetCurrentDirectory")]
    [InlineData(@"Environment\.CurrentDirectory")]
    public void TheCore_NeverDependsOnTheCurrentDirectory(string pattern) => Forbid(pattern);

    /// <summary>
    /// The single-argument overload resolves a relative path against the process's current
    /// directory, which is the one door the path policy exists to keep shut.
    /// </summary>
    [Fact]
    public void EveryGetFullPathCall_PassesABaseDirectory()
    {
        var offenders = new List<string>();

        foreach ((string path, string source) in Sources())
        {
            foreach (Match match in Regex.Matches(source, @"Path\.GetFullPath\("))
            {
                if (!HasTopLevelComma(source, match.Index + match.Length))
                {
                    offenders.Add($"{Path.GetFileName(path)} at offset {match.Index}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "These calls use the single-argument Path.GetFullPath, which consults the current " +
            "directory: " + string.Join(", ", offenders) + ".");
    }

    /// <summary>
    /// "This package executes nothing... It has no filesystem access at all." Stronger than the
    /// contract's "do not call File.Exists from equality or hashing", and free, because the core
    /// has nothing to read.
    /// </summary>
    [Theory]
    [InlineData(@"\bFile\.")]
    [InlineData(@"\bDirectory\.[A-Z]")]
    [InlineData(@"\bFileStream\b")]
    [InlineData(@"\bStreamReader\b")]
    [InlineData(@"\bStreamWriter\b")]
    public void TheCore_NeverTouchesTheFileSystem(string pattern) => Forbid(pattern);

    /// <summary>
    /// "Avoids using <c>Uri</c> as a substitute for normal file-system paths." URI semantics —
    /// escaping, schemes, authority — are not file-system semantics, and this library's paths are
    /// not URIs.
    /// </summary>
    [Theory]
    [InlineData(@"\bnew Uri\(")]
    [InlineData(@"System\.Uri\b")]
    public void TheCore_NeverUsesUriForPaths(string pattern) => Forbid(pattern);

    /// <summary>"It does not load assemblies... or instantiate project code."</summary>
    [Theory]
    [InlineData(@"using System\.Reflection")]
    [InlineData(@"Assembly\.Load")]
    [InlineData(@"Activator\.CreateInstance")]
    public void TheCore_NeverReflects(string pattern) => Forbid(pattern);

    /// <summary>
    /// "Do not add placeholder public methods that throw NotImplementedException." A concept that
    /// is not implemented belongs in the roadmap, not in the surface.
    /// </summary>
    [Fact]
    public void TheCore_ShipsNoPlaceholder() => Forbid(@"NotImplementedException");

    /// <summary>
    /// "Must not capture a synchronization context." That is a property of every await in the
    /// assembly, which makes it the compiler's job; this checks the compiler was actually given it.
    /// </summary>
    [Fact]
    public void ConfigureAwait_IsEnforcedByTheCompiler()
    {
        string editorConfig = File.ReadAllText(Path.Combine(RepositoryLayout.RepositoryRoot, ".editorconfig"));

        Assert.Contains("[src/**/*.cs]", editorConfig, StringComparison.Ordinal);
        Assert.Contains("dotnet_diagnostic.CA2007.severity = error", editorConfig, StringComparison.Ordinal);
    }

    [Fact]
    public void TheScanSeesSomething()
    {
        // A stripper with a bug would make every rule above pass by reading nothing at all.
        Assert.NotEmpty(Sources());
        Assert.Contains(Sources(), static pair => pair.Source.Contains("ProjectWorkspace", StringComparison.Ordinal));
    }

    private static void Forbid(string pattern)
    {
        List<string> offenders = [.. Sources()
            .Where(pair => Regex.IsMatch(pair.Source, pattern))
            .Select(static pair => Path.GetFileName(pair.Path))
            .Order(StringComparer.Ordinal)];

        Assert.True(
            offenders.Count == 0,
            $"'{pattern}' appears in shipping source: " + string.Join(", ", offenders) + ".");
    }

    private static IReadOnlyList<(string Path, string Source)> Sources() =>
        [.. RepositoryLayout.CoreSourceFiles()
            .Select(static path => (path, StripComments(File.ReadAllText(path))))];

    /// <summary>Removes documentation, line and block comments so prose cannot trip a rule.</summary>
    private static string StripComments(string source)
    {
        string withoutBlocks = Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);

        return Regex.Replace(withoutBlocks, @"//.*$", string.Empty, RegexOptions.Multiline);
    }

    /// <summary>Whether a call's argument list has a comma at its own nesting level.</summary>
    private static bool HasTopLevelComma(string source, int start)
    {
        int depth = 0;

        for (int index = start; index < source.Length; index++)
        {
            switch (source[index])
            {
                case '(':
                    depth++;

                    break;

                case ')' when depth == 0:
                    return false;

                case ')':
                    depth--;

                    break;

                case ',' when depth == 0:
                    return true;

                default:
                    break;
            }
        }

        return false;
    }
}
