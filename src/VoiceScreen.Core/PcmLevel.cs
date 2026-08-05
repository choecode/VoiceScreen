namespace VoiceScreen.Core;

/// <summary>16bit 小端单声道 PCM 的电平计算，用于语音活动检测。</summary>
public static class PcmLevel
{
    /// <summary>
    /// 计算一帧 PCM 的均方根电平。
    /// 用 <see cref="ReadOnlySpan{T}"/> 而不是 byte[]，调用方可以直接传入
    /// 池化缓冲区的一段，不必为每一帧单独分配数组。
    /// </summary>
    public static double CalculateRms(ReadOnlySpan<byte> pcm16Mono)
    {
        var sampleCount = pcm16Mono.Length / 2;
        if (sampleCount == 0) return 0;

        double sum = 0;
        for (var index = 0; index + 1 < pcm16Mono.Length; index += 2)
        {
            var sample = (short)(pcm16Mono[index] | (pcm16Mono[index + 1] << 8));
            sum += (double)sample * sample;
        }

        return Math.Sqrt(sum / sampleCount);
    }
}
