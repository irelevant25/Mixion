using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace Mixion.Host.Audio;

/// <summary>
/// Background service that keeps the mixer in step with Windows without anyone
/// pressing Refresh. It drives <see cref="TopologyReconciler"/>:
/// <list type="bullet">
///   <item>every second, to follow apps — an app closed and reopened comes back on the same channel, a newly playing app appears in the picker;</item>
///   <item>shortly after a Windows device notification (plug, unplug, enable, disable, format change), once the burst of notifications settles;</item>
///   <item>every 15 s as a device safety scan, in case a notification was missed.</item>
/// </list>
/// Passes never crash the host; a failing pass logs and the next one retries.
/// </summary>
public sealed class AudioDeviceWatcher : BackgroundService
{
    private static readonly TimeSpan PollInterval             = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DeviceSettleDelay        = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan DeviceSafetyScanInterval = TimeSpan.FromSeconds(15);

    private readonly TopologyReconciler _reconciler;
    private readonly ILogger            _logger;
    private readonly SemaphoreSlim      _wake = new(0, 1);

    private int  _devicesChanged;
    private long _lastDeviceEventMs;

    public AudioDeviceWatcher(TopologyReconciler reconciler, ILoggerFactory loggerFactory)
    {
        _reconciler = reconciler;
        _logger     = loggerFactory.CreateLogger("DeviceWatcher");
    }

    /// <summary>Run a pass as soon as possible — e.g. after a preset added channels that may be attachable right away.</summary>
    public void RequestReconcile() => Wake();

    private void OnDeviceNotification()
    {
        Interlocked.Exchange(ref _lastDeviceEventMs, Environment.TickCount64);
        Interlocked.Exchange(ref _devicesChanged, 1);
        Wake();
    }

    private void Wake()
    {
        try { _wake.Release(); }
        catch (SemaphoreFullException) { /* a wake is already pending */ }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Get off the host's startup path; COM callbacks and scans run on the thread pool (MTA).
        await Task.Yield();

        using var notifications = DeviceNotifications.TryRegister(OnDeviceNotification, _logger);
        var lastDeviceScanMs = Environment.TickCount64;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _wake.WaitAsync(PollInterval, stoppingToken).ConfigureAwait(false);

                // A USB headset raises several notifications (capture, render,
                // properties) within a few hundred ms — act once they settle.
                if (Volatile.Read(ref _devicesChanged) == 1)
                {
                    var sinceEvent = Environment.TickCount64 - Interlocked.Read(ref _lastDeviceEventMs);
                    var remaining  = (long)DeviceSettleDelay.TotalMilliseconds - sinceEvent;
                    if (remaining > 0)
                        await Task.Delay(TimeSpan.FromMilliseconds(remaining), stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var now            = Environment.TickCount64;
            var devicesChanged = Interlocked.Exchange(ref _devicesChanged, 0) == 1;
            var scanDevices    = devicesChanged || now - lastDeviceScanMs >= (long)DeviceSafetyScanInterval.TotalMilliseconds;
            if (scanDevices) lastDeviceScanMs = now;

            try
            {
                await _reconciler.ReconcileAsync(scanDevices, devicesChanged, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Device/app reconciliation failed; retrying on the next pass.");
            }
        }
    }

    public override void Dispose()
    {
        base.Dispose();
        _wake.Dispose();
    }

    /// <summary>
    /// <c>IMMNotificationClient</c> registration. Callbacks arrive on an audio
    /// service thread and must return quickly, so they only flag the change.
    /// </summary>
    private sealed class DeviceNotifications : IMMNotificationClient, IDisposable
    {
        /// <summary><c>PKEY_AudioEngine_DeviceFormat</c> — changes when the user picks another format in Sound settings.</summary>
        private static readonly Guid DeviceFormatKey = new("f19f064d-082c-4e27-bc73-6882a1bb8e4c");

        private readonly MMDeviceEnumerator _enumerator;
        private readonly Action             _onChange;

        private DeviceNotifications(MMDeviceEnumerator enumerator, Action onChange)
        {
            _enumerator = enumerator;
            _onChange   = onChange;
        }

        public static DeviceNotifications? TryRegister(Action onChange, ILogger logger)
        {
            MMDeviceEnumerator? enumerator = null;
            try
            {
                enumerator = new MMDeviceEnumerator();
                var client = new DeviceNotifications(enumerator, onChange);
                enumerator.RegisterEndpointNotificationCallback(client);
                return client;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not subscribe to Windows audio device notifications; relying on periodic scans.");
                enumerator?.Dispose();
                return null;
            }
        }

        public void OnDeviceStateChanged(string deviceId, DeviceState newState) => _onChange();

        public void OnDeviceAdded(string pwstrDeviceId) => _onChange();

        public void OnDeviceRemoved(string deviceId) => _onChange();

        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) { }

        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key)
        {
            if (key.formatId == DeviceFormatKey && key.propertyId == 0) _onChange();
        }

        public void Dispose()
        {
            try { _enumerator.UnregisterEndpointNotificationCallback(this); }
            catch { /* the audio service may already be gone at shutdown */ }
            _enumerator.Dispose();
        }
    }
}
