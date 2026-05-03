using Microsoft.Extensions.Logging.Abstractions;
using VoicemeterAlt.Host.Audio;
using VoicemeterAlt.Host.State;
using Xunit;

namespace VoicemeterAlt.Host.Tests;

/// <summary>
/// Covers the preset store's metadata bookkeeping: <c>CreatedAt</c> is
/// preserved across overwrites, list-with-metadata reports both timestamps,
/// and <see cref="PresetStore.Rename"/> moves the file + updates the in-JSON
/// name without touching the timestamps.
/// </summary>
public class PresetStoreTests
{
    private static readonly TestEndpoints Endpoints = new();

    [Fact]
    public void Save_PreservesCreatedAt_OnOverwrite()
    {
        using var dir = new TempDir();
        var store = new PresetStore(dir.Path, NullLogger.Instance);

        var first = store.Capture("p", EmptyState(), Endpoints, null, null);
        store.Save("p", first);

        var loadedFirst = store.Load("p");
        Assert.NotNull(loadedFirst.CreatedAt);
        var originalCreated = loadedFirst.CreatedAt!.Value;

        // A subsequent capture has a fresh CreatedAt — Save() must drop it in
        // favour of the on-disk one so "created" remains anchored to the
        // first save.
        Thread.Sleep(15);
        var second = store.Capture("p", EmptyState(), Endpoints, null, null);
        Assert.NotEqual(originalCreated, second.CreatedAt!.Value);
        store.Save("p", second);

        var loadedSecond = store.Load("p");
        Assert.Equal(originalCreated, loadedSecond.CreatedAt);
        Assert.True(loadedSecond.SavedAt > originalCreated);
    }

    [Fact]
    public void ListWithMetadata_ReportsBothTimestamps()
    {
        using var dir = new TempDir();
        var store = new PresetStore(dir.Path, NullLogger.Instance);

        store.Save("a", store.Capture("a", EmptyState(), Endpoints, null, null));
        Thread.Sleep(15);
        store.Save("b", store.Capture("b", EmptyState(), Endpoints, null, null));

        var rows = store.ListWithMetadata();
        Assert.Equal(2, rows.Count);
        var a = rows.Single(r => r.Name == "a");
        var b = rows.Single(r => r.Name == "b");

        Assert.Equal(a.CreatedAt, a.EditedAt);
        Assert.Equal(b.CreatedAt, b.EditedAt);
        Assert.True(b.CreatedAt > a.CreatedAt);
    }

    [Fact]
    public void Rename_MovesFileAndUpdatesInternalName()
    {
        using var dir = new TempDir();
        var store = new PresetStore(dir.Path, NullLogger.Instance);

        store.Save("old", store.Capture("old", EmptyState(), Endpoints, null, null));
        store.SetLastPresetName("old");
        var beforeCreated = store.Load("old").CreatedAt;

        store.Rename("old", "new");

        Assert.False(File.Exists(Path.Combine(dir.Path, "old.json")));
        Assert.True (File.Exists(Path.Combine(dir.Path, "new.json")));

        var renamed = store.Load("new");
        Assert.Equal("new", renamed.Name);
        Assert.Equal(beforeCreated, renamed.CreatedAt);

        Assert.Equal("new", store.GetLastPresetName());
    }

    [Fact]
    public void Rename_ToExistingName_Throws()
    {
        using var dir = new TempDir();
        var store = new PresetStore(dir.Path, NullLogger.Instance);

        store.Save("a", store.Capture("a", EmptyState(), Endpoints, null, null));
        store.Save("b", store.Capture("b", EmptyState(), Endpoints, null, null));

        Assert.Throws<IOException>(() => store.Rename("a", "b"));
        Assert.True(File.Exists(Path.Combine(dir.Path, "a.json")));
    }

    private static MixerState EmptyState() => MixerState.Empty;

    private sealed class TestEndpoints : IEndpointSource
    {
        public IReadOnlyList<AudioEndpoint> Capture() => Array.Empty<AudioEndpoint>();
        public IReadOnlyList<AudioEndpoint> Render()  => Array.Empty<AudioEndpoint>();
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "voicemeter-test-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* best-effort */ }
        }
    }
}
