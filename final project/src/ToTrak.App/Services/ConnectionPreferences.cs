using Microsoft.Maui.Devices;
using Microsoft.Maui.Storage;

namespace ToTrak.App.Services;

/// <summary>
/// Selects the same central API through either ADB reverse (USB) or a private
/// Microsoft Devtunnel (Wi-Fi / mobile network). No credentials are stored here.
/// </summary>
public static class ConnectionPreferences
{
    public const string UsbBaseUrl = "http://127.0.0.1:5125/";
    public const string ModeKey = "ToTrak.ConnectionMode";
    public const string TunnelUrlKey = "ToTrak.TunnelUrl";
    private const string LegacyApiUrlKey = "ApiBaseUrl";
    public const string UsbMode = "usb";
    public const string TunnelMode = "devtunnel";

    public static string ApiUrl
    {
        get
        {
            var mode = Preferences.Get(ModeKey, "");
            if (mode == UsbMode) return UsbBaseUrl;
            if (mode == TunnelMode) return GetSavedTunnelUrl();

            // Upgrade existing installations without discarding their selected URL.
            var defaultUrl = DeviceInfo.Platform == DevicePlatform.Android &&
                             DeviceInfo.DeviceType == DeviceType.Virtual
                ? "http://10.0.2.2:5125/"
                : UsbBaseUrl;
            return Preferences.Get(LegacyApiUrlKey, defaultUrl);
        }
    }

    public static string GetSavedTunnelUrl()
    {
        string saved = Preferences.Get(TunnelUrlKey, "");
        if (!string.IsNullOrWhiteSpace(saved)) return saved;

        // Previous ToTrak versions saved the tunnel URL in ApiBaseUrl only.
        string legacy = Preferences.Get(LegacyApiUrlKey, "");
        return IsValidTunnelUrl(legacy, out var normalized) ? normalized : "";
    }

    public static bool IsValidTunnelUrl(string? candidate, out string normalized)
    {
        normalized = "";
        if (!Uri.TryCreate(candidate?.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !uri.Host.EndsWith(".devtunnels.ms", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(uri.UserInfo))
            return false;

        normalized = uri.GetLeftPart(UriPartial.Authority) + "/";
        return true;
    }

    public static void UseUsb()
    {
        Preferences.Set(ModeKey, UsbMode);
        Preferences.Set(LegacyApiUrlKey, UsbBaseUrl);
        // Deliberately retain the tunnel URL and secure token for later switching.
    }

    public static void UseTunnel(string normalizedTunnelUrl)
    {
        Preferences.Set(TunnelUrlKey, normalizedTunnelUrl);
        Preferences.Set(LegacyApiUrlKey, normalizedTunnelUrl);
        Preferences.Set(ModeKey, TunnelMode);
    }
}
