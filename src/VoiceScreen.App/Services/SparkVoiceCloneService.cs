using System.Net.Http;
using System.Text;
using System.Text.Json;
using VoiceScreen.App.Audio;
using VoiceScreen.App.Models;

namespace VoiceScreen.App.Services;

/// <summary>调用 Spark 上私有的 Qwen3-TTS 声音克隆服务。</summary>
public sealed class SparkVoiceCloneService : IDisposable
{
    public const string VoiceId = "spark-clone:my-voice";
    public const int ServicePort = 18766;

    public static SpeechVoiceOption VoiceOption { get; } =
        new(VoiceId, "我的声音 · Spark 私有克隆");

    private readonly HttpClient _client;

    public SparkVoiceCloneService(string modelServiceUrl, string modelServiceToken)
    {
        if (string.IsNullOrWhiteSpace(modelServiceToken))
            throw new InvalidOperationException("Spark 模型服务访问令牌为空。");

        _client = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = Timeout.InfiniteTimeSpan,
            PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan,
            MaxConnectionsPerServer = 2,
            UseProxy = false,
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(5)
        })
        {
            BaseAddress = BuildServiceUrl(modelServiceUrl),
            Timeout = TimeSpan.FromSeconds(120)
        };
        _client.DefaultRequestHeaders.TryAddWithoutValidation(
            "X-VoiceScreen-Token", modelServiceToken.Trim());
    }

    public static bool IsVoiceId(string? voiceId) =>
        string.Equals(voiceId, VoiceId, StringComparison.OrdinalIgnoreCase);

    public async Task<bool> IsHealthyAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            using var response = await _client.GetAsync("health", timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return false;
            var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            using var json = JsonDocument.Parse(body);
            return json.RootElement.TryGetProperty("ready", out var ready) && ready.GetBoolean();
        }
        catch when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    public async Task<byte[]> SynthesizeEnglishAsync(string text, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new { text, language = "English" });
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await _client.PostAsync("synthesize", content, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException($"Spark 克隆音色合成失败：{error}");
        }

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var wave = new MemoryStream();
        await input.CopyToAsync(wave, cancellationToken).ConfigureAwait(false);
        return AudioTranscoder.WaveToPcm16Mono16Khz(wave);
    }

    private static Uri BuildServiceUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var source)
            || (source.Scheme != Uri.UriSchemeHttp && source.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException("Spark 模型服务地址无效。");
        var builder = new UriBuilder(source)
        {
            Port = ServicePort,
            Path = "/",
            Query = string.Empty,
            Fragment = string.Empty
        };
        return builder.Uri;
    }

    public void Dispose() => _client.Dispose();
}
