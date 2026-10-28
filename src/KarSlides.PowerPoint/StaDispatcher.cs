using System.Collections.Concurrent;
using System.Diagnostics;
using KarSlides.PowerPoint.Interop;
using Microsoft.Extensions.Logging;

namespace KarSlides.PowerPoint;

/// <summary>
/// The only thread that touches PowerPoint COM: STA with a Win32 message loop, work queue, per-call timeout.
/// A timeout does not abort the COM call (impossible); it is reported to the caller, which is the
/// watchdog signal (M2). Every call is logged with queue wait and execution time.
/// </summary>
public sealed class StaDispatcher : IDisposable
{
    private readonly ILogger _log;
    private readonly Thread _thread;
    private readonly ConcurrentQueue<Action> _queue = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly ManualResetEventSlim _started = new(false);
    private volatile bool _stop;

    public TimeSpan DefaultTimeout { get; }

    public StaDispatcher(string name, TimeSpan defaultTimeout, ILogger log)
    {
        _log = log;
        DefaultTimeout = defaultTimeout;
        _thread = new Thread(Loop) { IsBackground = true, Name = name };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _started.Wait();
        _log.LogInformation("STA thread '{Name}' started, COM timeout {Timeout} ms", name, defaultTimeout.TotalMilliseconds);
    }

    /// <summary>True when called on the dispatcher thread.</summary>
    public bool IsOnThread => Thread.CurrentThread == _thread;

    private void Loop()
    {
        _started.Set();
        var handles = new[] { _wake.SafeWaitHandle.DangerousGetHandle() };
        while (!_stop)
        {
            Win32.MsgWaitForMultipleObjectsEx(1, handles, Win32.INFINITE, Win32.QS_ALLINPUT, Win32.MWMO_INPUTAVAILABLE);
            while (Win32.PeekMessage(out var msg, IntPtr.Zero, 0, 0, Win32.PM_REMOVE))
            {
                if (msg.message == Win32.WM_QUIT)
                {
                    _stop = true;
                    break;
                }
                Win32.TranslateMessage(ref msg);
                Win32.DispatchMessage(ref msg);
            }
            while (_queue.TryDequeue(out var work)) work();
        }
        while (_queue.TryDequeue(out var work)) work(); // cleanup queued together with stop
    }

    public Task InvokeAsync(string name, Action action, TimeSpan? timeout = null, CancellationToken ct = default) =>
        InvokeAsync<object?>(name, () => { action(); return null; }, timeout, ct);

    public async Task<T> InvokeAsync<T>(string name, Func<T> func, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_stop, this);

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var enqueued = Stopwatch.GetTimestamp();
        _queue.Enqueue(() =>
        {
            var started = Stopwatch.GetTimestamp();
            try
            {
                tcs.TrySetResult(func());
                _log.LogDebug("COM {Name}: ok, wait {Wait:F1} ms, exec {Exec:F1} ms",
                    name, Ms(started - enqueued), Ms(Stopwatch.GetTimestamp() - started));
            }
            catch (Exception ex)
            {
                _log.LogWarning("COM {Name}: {Type} after {Exec:F1} ms: {Message}",
                    name, ex.GetType().Name, Ms(Stopwatch.GetTimestamp() - started), ex.Message);
                tcs.TrySetException(ex);
            }
        });
        _wake.Set();

        var limit = timeout ?? DefaultTimeout;
        try
        {
            return await tcs.Task.WaitAsync(limit, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _log.LogError("COM {Name}: TIMEOUT after {Limit} ms, call still pending on the STA thread", name, limit.TotalMilliseconds);
            throw new TimeoutException($"PowerPoint did not answer '{name}' within {limit.TotalMilliseconds:F0} ms");
        }
    }

    private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    public void Dispose()
    {
        if (_stop) return;
        _queue.Enqueue(() => _stop = true);
        _wake.Set();
        if (!_thread.Join(TimeSpan.FromSeconds(10)))
            _log.LogError("STA thread did not stop within 10 s");
        _wake.Dispose();
    }
}
