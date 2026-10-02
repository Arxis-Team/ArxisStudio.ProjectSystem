using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace ArxisStudio.ProjectSystem.Tests;

/// <summary>
/// A batch says what is true at its end, path by path — ADR 0025.
/// </summary>
/// <remarks>
/// Every batch here is taken with <see cref="FileChangeCoalescer.Flush"/>, so what is asserted is the
/// netting and never the timing, which <see cref="CoalescingTests"/> owns.
/// </remarks>
public sealed class FileChangeNettingTests
{
    private static CanonicalPath Form => TestPaths.At("src", "App", "MainWindow.axaml");

    // How JetBrains Rider names the two files its safe write passes through.
    private static CanonicalPath Temporary => TestPaths.At("src", "App", "MainWindow.axaml___jb_tmp___");
    private static CanonicalPath Aside => TestPaths.At("src", "App", "MainWindow.axaml___jb_old___");

    private static CanonicalPath A => TestPaths.At("src", "App", "A.cs");
    private static CanonicalPath B => TestPaths.At("src", "App", "B.cs");
    private static CanonicalPath C => TestPaths.At("src", "App", "C.cs");

    private static FileChangeCoalescingOptions Options { get; } = new()
    {
        QuietPeriod = TimeSpan.FromSeconds(1),
        MaximumDelay = TimeSpan.FromSeconds(10),
    };

    [Fact]
    public void ForChanges_AnAtomicSave_IsOneChangeToTheSavedFile()
    {
        (FileChangeCoalescer coalescer, List<ImmutableArray<FileChange>> batches) = Create();

        SaveLikeRider(coalescer.Add);
        coalescer.Flush();

        Assert.Equal([new FileChange(Form, FileChangeKind.Changed)], Assert.Single(batches));
    }

    [Fact]
    public void ForChanges_AFileCreatedAndDeletedInOneBatch_IsNothing()
    {
        (FileChangeCoalescer coalescer, List<ImmutableArray<FileChange>> batches) = Create();

        coalescer.Add(new FileChange(A, FileChangeKind.Created));
        coalescer.Add(new FileChange(A, FileChangeKind.Changed));
        coalescer.Add(new FileChange(A, FileChangeKind.Deleted));
        coalescer.Flush();

        Assert.Empty(batches);
    }

    [Fact]
    public void ForChanges_AFileDeletedAndCreatedAgain_IsChanged()
    {
        (FileChangeCoalescer coalescer, List<ImmutableArray<FileChange>> batches) = Create();

        coalescer.Add(new FileChange(A, FileChangeKind.Deleted));
        coalescer.Add(new FileChange(A, FileChangeKind.Created));
        coalescer.Flush();

        Assert.Equal([new FileChange(A, FileChangeKind.Changed)], Assert.Single(batches));
    }

    [Fact]
    public void ForChanges_ARename_StaysARename()
    {
        (FileChangeCoalescer coalescer, List<ImmutableArray<FileChange>> batches) = Create();

        coalescer.Add(FileChange.Renamed(A, B));
        coalescer.Flush();

        Assert.Equal([FileChange.Renamed(A, B)], Assert.Single(batches));
    }

    [Fact]
    public void ForChanges_ChainedRenames_AreOneRenameFromTheFirstPathToTheLast()
    {
        (FileChangeCoalescer coalescer, List<ImmutableArray<FileChange>> batches) = Create();

        coalescer.Add(FileChange.Renamed(A, B));
        coalescer.Add(FileChange.Renamed(B, C));
        coalescer.Flush();

        Assert.Equal([FileChange.Renamed(A, C)], Assert.Single(batches));
    }

    [Fact]
    public void ForChanges_AFileRenamedAwayAndAnotherWrittenInItsPlace_IsAChangeAndAnArrival()
    {
        (FileChangeCoalescer coalescer, List<ImmutableArray<FileChange>> batches) = Create();

        coalescer.Add(FileChange.Renamed(A, B));
        coalescer.Add(new FileChange(A, FileChangeKind.Created));
        coalescer.Flush();

        Assert.Equal(
            [new FileChange(A, FileChangeKind.Changed), new FileChange(B, FileChangeKind.Created)],
            Assert.Single(batches));
    }

    [Fact]
    public void ForChanges_AFileRenamedOntoOneDeletedEarlier_IsAChangeAndADeparture()
    {
        // The file at B was there before the batch and is there after it, with other contents: a
        // change, not an arrival — and whatever had B open has to hear about it.
        (FileChangeCoalescer coalescer, List<ImmutableArray<FileChange>> batches) = Create();

        coalescer.Add(new FileChange(B, FileChangeKind.Deleted));
        coalescer.Add(FileChange.Renamed(A, B));
        coalescer.Flush();

        Assert.Equal(
            [new FileChange(B, FileChangeKind.Changed), new FileChange(A, FileChangeKind.Deleted)],
            Assert.Single(batches));
    }

    [Fact]
    public void ForChanges_TwoFilesRenamedOntoOnePath_IsOneRenameAndADeparture()
    {
        (FileChangeCoalescer coalescer, List<ImmutableArray<FileChange>> batches) = Create();

        coalescer.Add(FileChange.Renamed(A, C));
        coalescer.Add(FileChange.Renamed(B, C));
        coalescer.Flush();

        Assert.Equal(
            [FileChange.Renamed(A, C), new FileChange(B, FileChangeKind.Deleted)],
            Assert.Single(batches));
    }

    [Fact]
    public void ForChanges_ADeletionReportedTwice_IsOneDeletion()
    {
        (FileChangeCoalescer coalescer, List<ImmutableArray<FileChange>> batches) = Create();

        coalescer.Add(new FileChange(A, FileChangeKind.Deleted));
        coalescer.Add(new FileChange(A, FileChangeKind.Deleted));
        coalescer.Flush();

        Assert.Equal([new FileChange(A, FileChangeKind.Deleted)], Assert.Single(batches));
    }

    [Fact]
    public void ForChanges_ADepartureAndAnArrivalOfOneName_IsAMove()
    {
        // How a watcher reports a file moved to another folder, even inside one watch.
        CanonicalPath from = TestPaths.At("src", "App", "Views", "MainWindow.axaml");
        CanonicalPath to = TestPaths.At("src", "App", "Pages", "mainwindow.AXAML");
        (FileChangeCoalescer coalescer, List<ImmutableArray<FileChange>> batches) = Create();

        coalescer.Add(new FileChange(from, FileChangeKind.Deleted));
        coalescer.Add(new FileChange(to, FileChangeKind.Created));
        coalescer.Flush();

        Assert.Equal([FileChange.Renamed(from, to)], Assert.Single(batches));
    }

    [Fact]
    public void ForChanges_TwoDeparturesOfOneName_AreNotGuessedAt()
    {
        CanonicalPath views = TestPaths.At("src", "App", "Views", "Item.axaml");
        CanonicalPath old = TestPaths.At("src", "App", "Old", "Item.axaml");
        CanonicalPath pages = TestPaths.At("src", "App", "Pages", "Item.axaml");
        (FileChangeCoalescer coalescer, List<ImmutableArray<FileChange>> batches) = Create();

        coalescer.Add(new FileChange(views, FileChangeKind.Deleted));
        coalescer.Add(new FileChange(old, FileChangeKind.Deleted));
        coalescer.Add(new FileChange(pages, FileChangeKind.Created));
        coalescer.Flush();

        Assert.Equal(
            [
                new FileChange(views, FileChangeKind.Deleted),
                new FileChange(old, FileChangeKind.Deleted),
                new FileChange(pages, FileChangeKind.Created),
            ],
            Assert.Single(batches));
    }

    [Fact]
    public void ForChanges_TwoArrivalsOfOneName_AreNotGuessedAt()
    {
        CanonicalPath views = TestPaths.At("src", "App", "Views", "Item.axaml");
        CanonicalPath pages = TestPaths.At("src", "App", "Pages", "Item.axaml");
        CanonicalPath shell = TestPaths.At("src", "App", "Shell", "Item.axaml");
        (FileChangeCoalescer coalescer, List<ImmutableArray<FileChange>> batches) = Create();

        coalescer.Add(new FileChange(views, FileChangeKind.Deleted));
        coalescer.Add(new FileChange(pages, FileChangeKind.Created));
        coalescer.Add(new FileChange(shell, FileChangeKind.Created));
        coalescer.Flush();

        Assert.Equal(
            [
                new FileChange(views, FileChangeKind.Deleted),
                new FileChange(pages, FileChangeKind.Created),
                new FileChange(shell, FileChangeKind.Created),
            ],
            Assert.Single(batches));
    }

    [Fact]
    public void ForChanges_ARenamedFileIsNotMovedAgainByANameItShares()
    {
        // B.cs arrived by a rename; a B.cs going elsewhere is not where it came from.
        CanonicalPath elsewhere = TestPaths.At("src", "Other", "B.cs");
        (FileChangeCoalescer coalescer, List<ImmutableArray<FileChange>> batches) = Create();

        coalescer.Add(FileChange.Renamed(A, B));
        coalescer.Add(new FileChange(elsewhere, FileChangeKind.Deleted));
        coalescer.Flush();

        Assert.Equal(
            [FileChange.Renamed(A, B), new FileChange(elsewhere, FileChangeKind.Deleted)],
            Assert.Single(batches));
    }

    [Fact]
    public void ForChanges_AChangeAfterACreation_IsStillACreation()
    {
        (FileChangeCoalescer coalescer, List<ImmutableArray<FileChange>> batches) = Create();

        coalescer.Add(new FileChange(A, FileChangeKind.Created));
        coalescer.Add(new FileChange(A, FileChangeKind.Changed));
        coalescer.Flush();

        Assert.Equal([new FileChange(A, FileChangeKind.Created)], Assert.Single(batches));
    }

    [Fact]
    public void ForChanges_ADeletionOfAChangedFile_IsADeletion()
    {
        (FileChangeCoalescer coalescer, List<ImmutableArray<FileChange>> batches) = Create();

        coalescer.Add(new FileChange(A, FileChangeKind.Changed));
        coalescer.Add(new FileChange(A, FileChangeKind.Deleted));
        coalescer.Flush();

        Assert.Equal([new FileChange(A, FileChangeKind.Deleted)], Assert.Single(batches));
    }

    [Fact]
    public void ForChanges_PathsComeInTheOrderTheyWereFirstHeardOf()
    {
        (FileChangeCoalescer coalescer, List<ImmutableArray<FileChange>> batches) = Create();

        coalescer.Add(new FileChange(B, FileChangeKind.Changed));
        coalescer.Add(new FileChange(A, FileChangeKind.Created));
        coalescer.Add(new FileChange(B, FileChangeKind.Changed));
        coalescer.Flush();

        Assert.Equal(
            [new FileChange(B, FileChangeKind.Changed), new FileChange(A, FileChangeKind.Created)],
            Assert.Single(batches));
    }

    [Fact]
    public void ForChanges_AnOverflow_ComesFirst()
    {
        (FileChangeCoalescer coalescer, List<ImmutableArray<FileChange>> batches) = Create();

        coalescer.Add(new FileChange(A, FileChangeKind.Changed));
        coalescer.Add(FileChange.Overflow);
        coalescer.Flush();

        Assert.Equal([FileChange.Overflow, new FileChange(A, FileChangeKind.Changed)], Assert.Single(batches));
    }

    [Fact]
    public void ForChanges_AnOverflowAlone_IsStillDelivered()
    {
        (FileChangeCoalescer coalescer, List<ImmutableArray<FileChange>> batches) = Create();

        coalescer.Add(FileChange.Overflow);
        coalescer.Flush();

        Assert.Equal([FileChange.Overflow], Assert.Single(batches));
    }

    [Fact]
    public void Add_ARenameMissingAnEnd_IsTheEndItHas()
    {
        // A watcher can lose half a rename; the half it kept still happened.
        (FileChangeCoalescer coalescer, List<ImmutableArray<FileChange>> batches) = Create();

        coalescer.Add(new FileChange(B, FileChangeKind.Renamed));
        coalescer.Add(new FileChange(CanonicalPath.None, FileChangeKind.Renamed) { OldPath = A });
        coalescer.Add(new FileChange(CanonicalPath.None, FileChangeKind.Renamed));
        coalescer.Flush();

        Assert.Equal(
            [new FileChange(B, FileChangeKind.Created), new FileChange(A, FileChangeKind.Deleted)],
            Assert.Single(batches));
    }

    [Fact]
    public void Constructor_AnAtomicSave_DeliversOnlyTheSavedPath()
    {
        // The batches a path-based host has always had, minus what never happened: the temporary
        // file and the file set aside are gone again by the end of the batch.
        var batches = new List<ImmutableArray<CanonicalPath>>();
        using var coalescer = new FileChangeCoalescer(batches.Add, Options, new FakeTimeProvider());

        SaveLikeRider(coalescer.Add);
        coalescer.Flush();

        Assert.Equal([Form], Assert.Single(batches));
    }

    [Fact]
    public void Constructor_ARename_DeliversBothItsPaths()
    {
        var batches = new List<ImmutableArray<CanonicalPath>>();
        using var coalescer = new FileChangeCoalescer(batches.Add, Options, new FakeTimeProvider());

        coalescer.Add(FileChange.Renamed(A, B));
        coalescer.Flush();

        Assert.Equal([A, B], Assert.Single(batches));
    }

    [Fact]
    public void Constructor_AnOverflowAlone_DeliversNothing()
    {
        // A lost change cannot be said as a path. A path-based host watches with ProjectFileWatcher,
        // which reports every watched path itself when its buffer overflows.
        var batches = new List<ImmutableArray<CanonicalPath>>();
        using var coalescer = new FileChangeCoalescer(batches.Add, Options, new FakeTimeProvider());

        coalescer.Add(FileChange.Overflow);
        coalescer.Flush();

        Assert.Empty(batches);
    }

    [Fact]
    public void ForChanges_NullHandler_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => FileChangeCoalescer.ForChanges(null!));
    }

    [Fact]
    public void FileChange_ToString_SaysTheKindAndThePaths()
    {
        Assert.Equal($"Renamed: {A} -> {B}", FileChange.Renamed(A, B).ToString());
        Assert.Equal($"Created: {A}", new FileChange(A, FileChangeKind.Created).ToString());
        Assert.Equal("Overflow", FileChange.Overflow.ToString());
    }

    /// <summary>
    /// What a file system reports when JetBrains Rider saves: the text to a temporary file, the
    /// original renamed aside, the temporary renamed over it, the original deleted.
    /// </summary>
    private static void SaveLikeRider(Action<FileChange> add)
    {
        add(new FileChange(Temporary, FileChangeKind.Created));
        add(new FileChange(Temporary, FileChangeKind.Changed));
        add(FileChange.Renamed(Form, Aside));
        add(FileChange.Renamed(Temporary, Form));
        add(new FileChange(Aside, FileChangeKind.Deleted));
    }

    private static (FileChangeCoalescer Coalescer, List<ImmutableArray<FileChange>> Batches) Create()
    {
        var batches = new List<ImmutableArray<FileChange>>();

        return (FileChangeCoalescer.ForChanges(batches.Add, Options, new FakeTimeProvider()), batches);
    }
}
