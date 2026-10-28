using KarSlides.Core.Audio;

namespace KarSlides.Control;

/// <summary>Active playback devices, from the app (KarSlides.Audio); the API does not enumerate devices itself.</summary>
public sealed record AudioDeviceSource(Func<IReadOnlyList<PlaybackDevice>> List);

/// <summary>A device id from the settings and its name now (null: not connected; "" id = Windows default).</summary>
public sealed record AudioDeviceSpec(string Id, string? Name);

public sealed record AudioDevicesResponse(AudioDeviceSpec Current, AudioDeviceSpec Saved, IReadOnlyList<PlaybackDevice> Devices);

public static class AudioDeviceChoices
{
    public static AudioDevicesResponse Build(IReadOnlyList<PlaybackDevice> devices, string current, string saved)
    {
        AudioDeviceSpec Resolve(string id) => new(id, id.Length == 0
            ? devices.FirstOrDefault(d => d.IsDefault)?.Name
            : devices.FirstOrDefault(d => d.Id.Equals(id, StringComparison.OrdinalIgnoreCase))?.Name);
        return new AudioDevicesResponse(Resolve(current), Resolve(saved), devices);
    }
}
