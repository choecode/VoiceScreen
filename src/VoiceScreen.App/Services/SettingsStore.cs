using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VoiceScreen.App.Models;

namespace VoiceScreen.App.Services;

public sealed class SettingsStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("VoiceScreen.Settings.v1");
    private readonly string _path;

    public SettingsStore()
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoiceScreen");
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "settings.dat");
    }

    public AppSettings Load()
    {
        try
        {
            return ReadEncrypted<AppSettings>(_path) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(settings, new JsonSerializerOptions { WriteIndented = true });
        var encrypted = ProtectedData.Protect(json, Entropy, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(_path, encrypted);
    }

    private static T? ReadEncrypted<T>(string path)
    {
        if (!File.Exists(path)) return default;
        var encrypted = File.ReadAllBytes(path);
        var json = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
        return JsonSerializer.Deserialize<T>(json);
    }

}
