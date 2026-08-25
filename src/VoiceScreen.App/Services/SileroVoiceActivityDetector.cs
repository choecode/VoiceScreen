using System.Buffers.Binary;
using Microsoft.ML.OnnxRuntime;
using VoiceScreen.App.Diagnostics;

namespace VoiceScreen.App.Services;

/// <summary>
/// Silero VAD v6.2 ONNX streaming wrapper. The model consumes 512 new 16 kHz
/// samples plus 64 samples of caller-managed context and carries a recurrent
/// state between windows. Inference stays on CPU and normally takes under 1ms.
/// </summary>
internal sealed class SileroVoiceActivityDetector : IDisposable
{
    public const string ModelFileName = "silero_vad_16k_op15.onnx";
    private const int SampleRate = 16_000;
    private const int WindowSamples = 512;
    private const int ContextSamples = 64;
    private const float PositiveThreshold = 0.5f;
    private const float NegativeThreshold = 0.35f;

    private static readonly string[] OutputNames = ["output", "stateN"];
    private readonly InferenceSession _session;
    private readonly float[] _window = new float[WindowSamples];
    private readonly float[] _context = new float[ContextSamples];
    private readonly float[] _input = new float[ContextSamples + WindowSamples];
    private readonly float[] _state = new float[2 * 128];
    private readonly long[] _sampleRate = [SampleRate];
    private int _windowCount;
    private bool _speechActive;
    private bool _disabled;

    public SileroVoiceActivityDetector(string modelPath)
    {
        if (!File.Exists(modelPath))
            throw new FileNotFoundException("Silero VAD model is missing", modelPath);

        var options = new SessionOptions
        {
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            InterOpNumThreads = 1,
            IntraOpNumThreads = 1,
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR
        };
        try
        {
            _session = new InferenceSession(modelPath, options);
        }
        finally
        {
            options.Dispose();
        }
    }

    /// <summary>
    /// Returns the latest streaming speech decision. The RMS decision is used
    /// only if ONNX inference ever fails, keeping capture usable in degraded
    /// installations instead of terminating the selected-process session.
    /// </summary>
    public bool IsSpeech(ReadOnlySpan<byte> pcm16Mono16Khz, bool rmsFallback)
    {
        if (_disabled) return rmsFallback;
        try
        {
            for (var offset = 0; offset + 1 < pcm16Mono16Khz.Length; offset += 2)
            {
                var sample = BinaryPrimitives.ReadInt16LittleEndian(pcm16Mono16Khz[offset..]);
                _window[_windowCount++] = sample / 32768f;
                if (_windowCount < WindowSamples) continue;

                Buffer.BlockCopy(_context, 0, _input, 0, ContextSamples * sizeof(float));
                Buffer.BlockCopy(_window, 0, _input, ContextSamples * sizeof(float),
                    WindowSamples * sizeof(float));
                var probability = RunWindow();
                Buffer.BlockCopy(_window, (WindowSamples - ContextSamples) * sizeof(float),
                    _context, 0, ContextSamples * sizeof(float));
                _windowCount = 0;

                if (probability >= PositiveThreshold) _speechActive = true;
                else if (probability < NegativeThreshold) _speechActive = false;
            }
            return _speechActive;
        }
        catch (Exception ex)
        {
            _disabled = true;
            VoiceScreenLog.Warn($"Silero VAD disabled after inference failure; using RMS fallback: {ex.Message}");
            return rmsFallback;
        }
    }

    public void Reset()
    {
        Array.Clear(_window);
        Array.Clear(_context);
        Array.Clear(_input);
        Array.Clear(_state);
        _windowCount = 0;
        _speechActive = false;
    }

    private float RunWindow()
    {
        using var inputValue = OrtValue.CreateTensorValueFromMemory(_input, [1, _input.Length]);
        using var stateValue = OrtValue.CreateTensorValueFromMemory(_state, [2, 1, 128]);
        using var sampleRateValue = OrtValue.CreateTensorValueFromMemory(_sampleRate, []);
        var inputs = new Dictionary<string, OrtValue>
        {
            ["input"] = inputValue,
            ["state"] = stateValue,
            ["sr"] = sampleRateValue
        };
        using var runOptions = new RunOptions();
        using var results = _session.Run(runOptions, inputs, OutputNames);
        var outputValues = results.ToArray();
        var probability = outputValues[0].GetTensorDataAsSpan<float>()[0];
        outputValues[1].GetTensorDataAsSpan<float>().CopyTo(_state);
        return probability;
    }

    public void Dispose() => _session.Dispose();
}
