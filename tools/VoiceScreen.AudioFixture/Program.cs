using System.Globalization;
using System.Speech.Synthesis;
using NAudio.Wave;

const string DefaultText =
    "But what is a neural network? It is a computational system inspired by the structure of the brain. " +
    "This deterministic audio fixture verifies that VoiceScreen captures only the selected process.";

var delayMilliseconds = ReadIntArgument(args, "--delay-ms", 2500, 0, 30000);
var repeatCount = ReadIntArgument(args, "--repeat", 1, 1, 10);
var text = ReadStringArgument(args, "--text") ?? DefaultText;
var silent = args.Any(argument => string.Equals(argument, "--silent", StringComparison.OrdinalIgnoreCase));

var wavePath = Path.Combine(Path.GetTempPath(), $"voicescreen-audio-fixture-{Environment.ProcessId}.wav");
try
{
    using (var synthesizer = new SpeechSynthesizer())
    {
        try
        {
            synthesizer.SelectVoiceByHints(VoiceGender.Female, VoiceAge.Adult, 0,
                CultureInfo.GetCultureInfo("en-US"));
        }
        catch
        {
            // The default installed Windows voice is still adequate for the deterministic capture test.
        }
        synthesizer.Rate = 0;
        synthesizer.SetOutputToWaveFile(wavePath);
        synthesizer.Speak(string.Join(" ", Enumerable.Repeat(text, repeatCount)));
    }

    Console.WriteLine($"fixture-ready pid={Environment.ProcessId} delayMs={delayMilliseconds} wav={wavePath}");
    if (delayMilliseconds > 0) await Task.Delay(delayMilliseconds);
    if (silent)
    {
        Console.WriteLine("fixture-silent-complete");
        return;
    }

    using var reader = new WaveFileReader(wavePath);
    using var output = new WaveOutEvent { DeviceNumber = -1, DesiredLatency = 100 };
    var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    output.PlaybackStopped += (_, eventArgs) =>
    {
        if (eventArgs.Exception is null) completed.TrySetResult();
        else completed.TrySetException(eventArgs.Exception);
    };
    output.Init(reader);
    output.Play();
    await completed.Task;
    Console.WriteLine("fixture-complete");
}
finally
{
    try { File.Delete(wavePath); }
    catch { }
}

static int ReadIntArgument(string[] arguments, string name, int fallback, int minimum, int maximum)
{
    for (var index = 0; index + 1 < arguments.Length; index++)
    {
        if (!string.Equals(arguments[index], name, StringComparison.OrdinalIgnoreCase)) continue;
        return int.TryParse(arguments[index + 1], out var value)
            ? Math.Clamp(value, minimum, maximum)
            : fallback;
    }
    return fallback;
}

static string? ReadStringArgument(string[] arguments, string name)
{
    for (var index = 0; index + 1 < arguments.Length; index++)
        if (string.Equals(arguments[index], name, StringComparison.OrdinalIgnoreCase))
            return arguments[index + 1];
    return null;
}
