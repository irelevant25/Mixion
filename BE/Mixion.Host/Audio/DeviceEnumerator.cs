using NAudio.CoreAudioApi;

namespace Mixion.Host.Audio;

/// <summary>
/// A WASAPI endpoint surfaced as a plain DTO. Decoupled from NAudio types so
/// we can fake it in tests and serialise it over the wire.
/// </summary>
public sealed record AudioEndpoint(
    string Id,
    string FriendlyName,
    string InterfaceName,
    string Direction,
    int SampleRate,
    int Channels,
    int BitsPerSample);

/// <summary>
/// Source of audio endpoints. Abstracted so DriverProbe can be unit-tested
/// against fakes without touching real Windows audio.
/// </summary>
public interface IEndpointSource
{
    IReadOnlyList<AudioEndpoint> Capture();
    IReadOnlyList<AudioEndpoint> Render();
}

public sealed class DeviceEnumerator : IEndpointSource
{
    public IReadOnlyList<AudioEndpoint> Capture() => Enumerate(DataFlow.Capture, "capture");
    public IReadOnlyList<AudioEndpoint> Render()  => Enumerate(DataFlow.Render,  "render");

    private static IReadOnlyList<AudioEndpoint> Enumerate(DataFlow flow, string direction)
    {
        var enumerator = new MMDeviceEnumerator();
        var devices = enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active);
        var list = new List<AudioEndpoint>(devices.Count);

        foreach (var d in devices)
        {
            var fmt = d.AudioClient.MixFormat;
            list.Add(new AudioEndpoint(
                Id: d.ID,
                FriendlyName: d.FriendlyName,
                InterfaceName: d.DeviceFriendlyName,
                Direction: direction,
                SampleRate: fmt.SampleRate,
                Channels: fmt.Channels,
                BitsPerSample: fmt.BitsPerSample));
        }

        return list;
    }
}
