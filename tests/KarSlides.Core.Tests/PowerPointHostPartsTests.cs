using KarSlides.PowerPoint;
using Microsoft.Extensions.Logging.Abstractions;

namespace KarSlides.Core.Tests;

public sealed class MonitorSettingsGuardTests : IDisposable
{
    private sealed class MemoryStore : IPowerPointOptionsStore
    {
        public readonly Dictionary<string, object> Values = new();
        public string? GetString(string name) => Values.GetValueOrDefault(name) as string;
        public void SetString(string name, string? value) => Set(name, value);
        public int? GetDword(string name) => Values.GetValueOrDefault(name) as int?;
        public void SetDword(string name, int? value) => Set(name, value);

        private void Set(string name, object? value)
        {
            if (value is null) Values.Remove(name);
            else Values[name] = value;
        }
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"karslides-guard-{Guid.NewGuid():N}");
    private string BackupFile => Path.Combine(_dir, "backup.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private MonitorSettingsGuard Guard(MemoryStore store) => new(store, BackupFile, NullLogger.Instance);

    [Fact]
    public void ApplyThenRestore_ReturnsUserValues()
    {
        var store = new MemoryStore();
        store.SetString("DisplayMonitor", @"\\.\DISPLAY1");
        store.SetDword("UseAutoMonSelection", 1);
        var guard = Guard(store);

        guard.Apply(@"\\.\DISPLAY5");
        Assert.Equal(@"\\.\DISPLAY5", store.GetString("DisplayMonitor"));
        Assert.Equal(0, store.GetDword("UseAutoMonSelection"));

        Assert.True(guard.Restore());
        Assert.Equal(@"\\.\DISPLAY1", store.GetString("DisplayMonitor"));
        Assert.Equal(1, store.GetDword("UseAutoMonSelection"));
        Assert.False(File.Exists(BackupFile));
    }

    [Fact]
    public void AbsentValues_AreDeletedAgainOnRestore()
    {
        var store = new MemoryStore();
        var guard = Guard(store);

        guard.Apply(@"\\.\DISPLAY5");
        guard.Restore();

        Assert.Empty(store.Values);
    }

    [Fact]
    public void CrashBetweenApplyAndRestore_IsRecoveredByNextInstance()
    {
        var store = new MemoryStore();
        store.SetString("DisplayMonitor", @"\\.\DISPLAY1");
        store.SetDword("UseAutoMonSelection", 1);
        Guard(store).Apply(@"\\.\DISPLAY7"); // process dies here

        Assert.True(Guard(store).Restore()); // next start

        Assert.Equal(@"\\.\DISPLAY1", store.GetString("DisplayMonitor"));
        Assert.Equal(1, store.GetDword("UseAutoMonSelection"));
    }

    [Fact]
    public void SecondApplyWithoutRestore_KeepsTheTrueOriginal()
    {
        // The bug seen in M0: a second run recorded already changed values as "original".
        var store = new MemoryStore();
        store.SetString("DisplayMonitor", @"\\.\DISPLAY1");
        store.SetDword("UseAutoMonSelection", 1);

        Guard(store).Apply(@"\\.\DISPLAY7");
        Guard(store).Apply(@"\\.\DISPLAY5");
        Guard(store).Restore();

        Assert.Equal(@"\\.\DISPLAY1", store.GetString("DisplayMonitor"));
        Assert.Equal(1, store.GetDword("UseAutoMonSelection"));
    }

    [Fact]
    public void RestoreWithoutBackup_DoesNothing()
    {
        var store = new MemoryStore();
        store.SetString("DisplayMonitor", "x");
        Assert.False(Guard(store).Restore());
        Assert.Equal("x", store.GetString("DisplayMonitor"));
    }

    [Fact]
    public void DamagedBackup_IsDroppedWithoutTouchingRegistry()
    {
        var store = new MemoryStore();
        store.SetString("DisplayMonitor", "x");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(BackupFile, "{not json");

        Assert.False(Guard(store).Restore());
        Assert.Equal("x", store.GetString("DisplayMonitor"));
        Assert.False(File.Exists(BackupFile));
    }
}

public sealed class StaDispatcherTests : IDisposable
{
    private readonly StaDispatcher _sta = new("TestSta", TimeSpan.FromSeconds(2), NullLogger.Instance);

    public void Dispose() => _sta.Dispose();

    [Fact]
    public async Task RunsOnOneStaThread()
    {
        var apartments = new List<ApartmentState>();
        var threads = new HashSet<int>();
        for (var i = 0; i < 5; i++)
        {
            await _sta.InvokeAsync("probe", () =>
            {
                apartments.Add(Thread.CurrentThread.GetApartmentState());
                threads.Add(Environment.CurrentManagedThreadId);
            });
        }

        Assert.All(apartments, a => Assert.Equal(ApartmentState.STA, a));
        Assert.Single(threads);
    }

    [Fact]
    public async Task KeepsOrder()
    {
        var order = new List<int>();
        var tasks = Enumerable.Range(0, 50).Select(i => _sta.InvokeAsync($"n{i}", () => order.Add(i))).ToArray();
        await Task.WhenAll(tasks);
        Assert.Equal(Enumerable.Range(0, 50), order);
    }

    [Fact]
    public async Task PropagatesExceptions()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sta.InvokeAsync<int>("boom", () => throw new InvalidOperationException("boom")));
        Assert.Equal("boom", ex.Message);
    }

    [Fact]
    public async Task TimesOut_AndKeepsWorkingAfterTheHungCallFinishes()
    {
        using var release = new ManualResetEventSlim(false);
        var hung = _sta.InvokeAsync("hang", () => release.Wait(), TimeSpan.FromMilliseconds(100));

        var ex = await Assert.ThrowsAsync<TimeoutException>(() => hung);
        Assert.Contains("hang", ex.Message);

        release.Set();
        Assert.Equal(42, await _sta.InvokeAsync("after", () => 42));
    }
}
