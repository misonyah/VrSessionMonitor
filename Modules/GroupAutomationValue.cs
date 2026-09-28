using System.Globalization;

namespace VrSessionMonitor.Modules;

/// <summary>The value a group-automation row sends while you're in that group's instance, typed the
/// way VRChat avatar parameters are: bool ("true"/"false", or empty = true, which is what every row
/// meant before this column existed), int ("3") or float ("0.5", "0,5" also accepted). Leaving the
/// group sends the same type's default (false / 0 / 0.0).</summary>
public static class GroupAutomationValue
{
    /// <summary>bool, int or float; null when the text is none of those (the row is then skipped
    /// instead of sending something the avatar didn't ask for).</summary>
    public static object? Parse(string? text)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 0) return true;
        if (bool.TryParse(t, out var b)) return b;
        if (int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)) return i;
        if (float.TryParse(t.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var f)) return f;
        return null;
    }

    public static object OffValue(object onValue) => onValue switch
    {
        int => 0,
        float => 0f,
        _ => false,
    };
}

/// <summary>What to re-send after an avatar change: a freshly loaded avatar starts every parameter at
/// its default, so the active watched group's value would otherwise be lost until you rejoin.</summary>
public static class GroupAutomationResend
{
    public static (string Param, object Value)? For(VrSessionMonitor.Config.MonitorConfig config, string? activeGroupId)
    {
        if (activeGroupId is null) return null;
        var entry = config.VrChatGroupAutomation.Groups.FirstOrDefault(g => g.GroupId == activeGroupId);
        if (entry is null || string.IsNullOrWhiteSpace(entry.ParamName)) return null;
        return GroupAutomationValue.Parse(entry.Value) is { } on ? (entry.ParamName, on) : null;
    }
}
