using System.Text.Json;
using VrSessionMonitor.Logging;

namespace VrSessionMonitor.Modules;

/// <summary>The avatar currently worn in VRChat, from its live "/avatar/change" OSC message (and an
/// OSCQuery read at startup). The display name comes from that avatar's own OSC config file.
/// Thread-safe: updates arrive on the OSC receive thread, reads come from the UI.</summary>
public sealed class AvatarTracker
{
    private readonly string _oscRootDir;
    private readonly object _lock = new();
    private string? _id;
    private string? _name;

    public AvatarTracker(string oscRootDir) => _oscRootDir = oscRootDir;

    /// <summary>Raised (on the caller's thread) when a different avatar is announced.</summary>
    public event EventHandler? Changed;

    public string? CurrentAvatarId { get { lock (_lock) return _id; } }
    public string? CurrentAvatarName { get { lock (_lock) return _name; } }

    public static bool TryParseAvatarChange(string address, object? value, out string avatarId)
    {
        avatarId = value as string ?? "";
        return string.Equals(address, "/avatar/change", StringComparison.OrdinalIgnoreCase) && avatarId.Length > 0;
    }

    public void OnAvatarChange(string avatarId)
    {
        lock (_lock)
        {
            if (string.Equals(_id, avatarId, StringComparison.Ordinal)) return;
            _id = avatarId;
            _name = ResolveName(avatarId);
        }
        Log.Info("Avatar", $"Now wearing {CurrentAvatarName ?? "(unnamed)"} ({avatarId}).");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The avatar's OSC config file (usr_*/Avatars/&lt;id&gt;.json), or null.</summary>
    public static string? FindConfigFile(string oscRootDir, string avatarId)
    {
        try
        {
            if (!Directory.Exists(oscRootDir)) return null;
            return Directory.EnumerateFiles(oscRootDir, avatarId + ".json", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch { return null; }
    }

    private string? ResolveName(string avatarId)
    {
        var file = FindConfigFile(_oscRootDir, avatarId);
        if (file is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            return doc.RootElement.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
        }
        catch (Exception ex)
        {
            Log.Debug("Avatar", $"Could not read avatar name from {file}: {ex.Message}");
            return null;
        }
    }
}
