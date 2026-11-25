using NPEduTools.Contracts;

namespace NPEduTools.App;

internal sealed record RecordingSources(RecordingDisplay Display, RecordingDevice? Speaker, RecordingDevice? Microphone)
{
    internal static RecordingSources? Resolve(RecordingOptions options, RecordingEnvironment? environment)
    {
        if (environment?.Ready != true || environment.Displays.FirstOrDefault(d => d.Id == options.Display) is not { } display)
            return null;
        var speaker = options.SystemAudio ? Find(environment.Speakers, options.SpeakerId) : null;
        var microphone = options.Microphone ? Find(environment.Microphones, options.MicrophoneId) : null;
        return options.SystemAudio && speaker is null || options.Microphone && microphone is null
            ? null : new(display, speaker, microphone);
    }

    internal static RecordingDevice? Find(RecordingDevice[] devices, string selection) =>
        selection == "default" ? devices.FirstOrDefault(d => d.IsDefault) : devices.FirstOrDefault(d => d.Id == selection);

    internal RecordingOptions Pin(RecordingOptions options) => options with
        { SpeakerId = Speaker?.Id ?? options.SpeakerId, MicrophoneId = Microphone?.Id ?? options.MicrophoneId };

    internal bool Matches(RecordingSources? current) => current is not null && current.Display.Id == Display.Id &&
        current.Display.Left == Display.Left && current.Display.Top == Display.Top &&
        current.Display.Width == Display.Width && current.Display.Height == Display.Height &&
        current.Speaker?.Id == Speaker?.Id && current.Microphone?.Id == Microphone?.Id;

    internal static string DeviceName(RecordingDevice? device) =>
        device is null ? "关闭" : string.IsNullOrWhiteSpace(device.Label) ? device.Id : device.Label;

    internal string Summary => $"屏幕　{(string.IsNullOrWhiteSpace(Display.Label) ? Display.Id : Display.Label)}\n系统声音　{DeviceName(Speaker)}\n麦克风　{DeviceName(Microphone)}";
}
