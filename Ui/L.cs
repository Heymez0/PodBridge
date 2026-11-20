using System.Runtime.InteropServices;

namespace PodBridge.Ui;

public enum UiLanguage
{
    English,
    Turkish,
}

/// <summary>User-facing text. Each string is written in both languages where it is used,
/// so the translation sits next to the English original. Logs and code stay in English.</summary>
public static class L
{
    private const ushort TurkishPrimaryLanguage = 0x1F;

    public static UiLanguage Current { get; set; } = SystemDefault();

    public static string T(string english, string turkish) => Current == UiLanguage.Turkish ? turkish : english;

    /// <summary>Turkish when Windows itself is in Turkish, English otherwise. Read through Win32 because
    /// the app runs in invariant globalization mode, where CultureInfo does not reflect the user's language.</summary>
    public static UiLanguage SystemDefault() =>
        (GetUserDefaultUILanguage() & 0x3FF) == TurkishPrimaryLanguage ? UiLanguage.Turkish : UiLanguage.English;

    [DllImport("kernel32.dll")]
    private static extern ushort GetUserDefaultUILanguage();
}
