using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VoicemeterAlt.Host.Ipc;

namespace VoicemeterAlt.Host.Audio;

/// <summary>
/// Background watchdog (BE-106) that auto-rebinds <see cref="ProcessLoopbackCapture"/>
/// instances when their target PID dies and the same-named process restarts.
///
/// Walks every active capture in the running engine on a fixed cadence; any
/// <see cref="ProcessLoopbackCapture"/> whose target PID has exited triggers
/// an <see cref="EngineHost.RebuildAsync"/>. Because process-loopback channel
/// ids are <c>process:&lt;name&gt;</c> (not PID-bound), the rebuild's
/// name-based resolution in <see cref="EngineFactory.Build"/> automatically
/// re-binds the channel to the new PID — Chrome closes and reopens, the
/// channel keeps the same id and the user's slot binding survives.
///
/// Cadence is 3 s — fast enough that Chrome restart feels nearly seamless,
/// slow enough not to thrash CPU on PID liveness checks. Each rebuild itself
/// has the usual ~200 ms WASAPI re-open gap; that's the price until BE-110
/// lands true atomic capture-array swapping.
/// </summary>
public sealed class ProcessHealthMonitor : IHostedService, IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    private readonly EngineHost _engineHost;
    private readonly EngineFactory _engineFactory;
    private readonly ILogger _logger;

    private CancellationTokenSource? _cts;
    private Task? _loop;

    public ProcessHealthMonitor(EngineHost engineHost, EngineFactory engineFactory, ILoggerFactory loggerFactory)
    {
        _engineHost    = engineHost;
        _engineFactory = engineFactory;
        _logger        = loggerFactory.CreateLogger("ProcessHealthMonitor");
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts  = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loop = Task.Run(() => RunAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* expected */ }
        }
    }

    public void Dispose()
    {
        _cts?.Dispose();
        _cts = null;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        // First tick is one interval after start so the engine has had a
        // moment to come up.
        try { await Task.Delay(PollInterval, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (HasDeadProcessLoopback(out var deadName, out var deadPid))
                {
                    _logger.LogInformation(
                        "Process loopback target '{Name}' (PID {Pid}) is gone; rebuilding engine to re-resolve.",
                        deadName, deadPid);
                    await _engineHost.RebuildAsync(_engineFactory, ct: ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                // Watchdog must not crash the host. Log and keep going.
                _logger.LogWarning(ex, "Process health check failed; will retry on next tick.");
            }

            try { await Task.Delay(PollInterval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// True if any active <see cref="ProcessLoopbackCapture"/> in the running
    /// engine points at a PID that has since exited. Returns the first
    /// offender's name+PID for log clarity. Captures whose channel is already
    /// marked unavailable (e.g. a previous rebuild left it null) are skipped
    /// — no point firing another rebuild on something we already know is
    /// gone and couldn't be re-resolved.
    /// </summary>
    private bool HasDeadProcessLoopback(out string deadName, out int deadPid)
    {
        deadName = string.Empty;
        deadPid  = 0;

        var engine = _engineHost.Current;
        if (engine is null) return false;

        foreach (var cap in engine.Captures)
        {
            if (cap is not ProcessLoopbackCapture plc) continue;
            if (IsProcessAlive(plc.ProcessId)) continue;

            // Strip the "process:" prefix for the log line.
            var name = plc.Id.StartsWith("process:") ? plc.Id["process:".Length..] : plc.Id;
            deadName = name;
            deadPid  = plc.ProcessId;
            return true;
        }
        return false;
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch
        {
            return false;
        }
    }
}
