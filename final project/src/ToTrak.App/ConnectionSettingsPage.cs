using System.Net.Http;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Storage;
using ToTrak.App.Services;

namespace ToTrak.App;

/// <summary>
/// Switch USB/ADB and Devtunnel connections without editing or rebuilding code.
/// Only the selected URL is in Preferences; the tunnel token remains in SecureStorage.
/// </summary>
public sealed class ConnectionSettingsPage : ContentPage
{
    public ConnectionSettingsPage()
    {
        Title = "Connection Settings";

        var modePicker = new Picker
        {
            Title = "Connection method",
            AutomationId = "ConnectionModePicker"
        };
        modePicker.Items.Add("USB cable (ADB reverse)");
        modePicker.Items.Add("Microsoft Devtunnel (Wi-Fi / internet)");

        string currentUrl = AccountService.ApiUrl;
        modePicker.SelectedIndex = currentUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? 1 : 0;

        var urlEntry = new Entry
        {
            Placeholder = "https://your-tunnel-5125.use.devtunnels.ms/",
            Text = ConnectionPreferences.GetSavedTunnelUrl(),
            Keyboard = Keyboard.Url,
            AutomationId = "DevTunnelUrlEntry"
        };
        var tokenEntry = new Entry
        {
            Placeholder = "Leave blank to keep previously saved token",
            IsPassword = true,
            AutomationId = "DevTunnelTokenEntry"
        };
        var usbHelp = new Label
        {
            Text = "USB mode: keep the cable attached and run adb reverse tcp:5125 tcp:5125 on your PC. The API must be listening on port 5125.",
            FontSize = 13
        };
        var tunnelHelp = new Label
        {
            Text = "Devtunnel mode works with or without USB while the PC API and private tunnel host are running. The token is stored in Android SecureStorage, not source code.",
            FontSize = 13
        };
        var urlCaption = new Label { Text = "Devtunnel URL", FontAttributes = FontAttributes.Bold };
        var tokenCaption = new Label { Text = "Devtunnel Connect Token", FontAttributes = FontAttributes.Bold };
        var status = new Label { AutomationId = "ConnectionTestStatus", FontSize = 13 };
        var testButton = new Button { Text = "Test Connection", AutomationId = "TestConnectionButton" };
        var saveButton = new Button { Text = "Save Connection", AutomationId = "SaveConnectionButton" };
        var cancelButton = new Button { Text = "Cancel" };

        bool IsTunnel() => modePicker.SelectedIndex == 1;
        void RefreshFields()
        {
            bool tunnel = IsTunnel();
            usbHelp.IsVisible = !tunnel;
            tunnelHelp.IsVisible = tunnel;
            urlCaption.IsVisible = tunnel;
            urlEntry.IsVisible = tunnel;
            tokenCaption.IsVisible = tunnel;
            tokenEntry.IsVisible = tunnel;
            status.Text = tunnel
                ? "Selected: Microsoft Devtunnel"
                : "Selected: USB / ADB reverse (http://127.0.0.1:5125/)";
        }
        modePicker.SelectedIndexChanged += (_, _) => RefreshFields();
        RefreshFields();

        // This tests the candidate settings directly; it does not change the active app session.
        testButton.Clicked += async (_, _) =>
        {
            testButton.IsEnabled = false;
            try
            {
                string baseUrl;
                string? token = null;
                if (IsTunnel())
                {
                    if (!ConnectionPreferences.IsValidTunnelUrl(urlEntry.Text, out baseUrl))
                    {
                        status.Text = "Enter an HTTPS URL ending in .devtunnels.ms.";
                        return;
                    }
                    token = tokenEntry.Text?.Trim();
                    if (string.IsNullOrWhiteSpace(token))
                        token = await SecureStorage.Default.GetAsync(DevTunnelAuthHandler.TokenStorageKey);
                    if (string.IsNullOrWhiteSpace(token))
                    {
                        status.Text = "Enter the private tunnel connect token first.";
                        return;
                    }
                }
                else baseUrl = ConnectionPreferences.UsbBaseUrl;

                using var handler = new HttpClientHandler { AllowAutoRedirect = false };
                using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) };
                using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(baseUrl), "health"));
                if (IsTunnel())
                    request.Headers.TryAddWithoutValidation("X-Tunnel-Authorization", $"tunnel {token}");

                using var response = await client.SendAsync(request);
                status.Text = response.IsSuccessStatusCode
                    ? $"Connected: API /health returned HTTP {(int)response.StatusCode}."
                    : $"Not connected: HTTP {(int)response.StatusCode}. Check the server, tunnel host, and connect token.";
            }
            catch (TaskCanceledException)
            {
                status.Text = "Connection timed out. Check API, USB forwarding or tunnel hosting.";
            }
            catch (Exception ex) when (ex is HttpRequestException or UriFormatException)
            {
                status.Text = $"Connection failed: {ex.Message}";
            }
            catch (Exception)
            {
                status.Text = "Connection test failed. Check connectivity and the saved token.";
            }
            finally { testButton.IsEnabled = true; }
        };

        saveButton.Clicked += async (_, _) =>
        {
            saveButton.IsEnabled = false;
            try
            {
                if (IsTunnel())
                {
                    if (!ConnectionPreferences.IsValidTunnelUrl(urlEntry.Text, out var normalizedUrl))
                    {
                        await DisplayAlertAsync("Invalid URL", "Enter an HTTPS *.devtunnels.ms URL.", "OK");
                        return;
                    }
                    string? typedToken = tokenEntry.Text?.Trim();
                    if (string.IsNullOrWhiteSpace(typedToken))
                        typedToken = await SecureStorage.Default.GetAsync(DevTunnelAuthHandler.TokenStorageKey);
                    if (string.IsNullOrWhiteSpace(typedToken))
                    {
                        await DisplayAlertAsync("Missing Token", "Enter a private Devtunnel connect token.", "OK");
                        return;
                    }
                    await SecureStorage.Default.SetAsync(DevTunnelAuthHandler.TokenStorageKey, typedToken);
                    ConnectionPreferences.UseTunnel(normalizedUrl);
                }
                else ConnectionPreferences.UseUsb();

                tokenEntry.Text = string.Empty;
                await DisplayAlertAsync(
                    "Connection Saved",
                    "Force-stop and reopen ToTrak so login and sync use the new connection. Sign in again if requested.",
                    "OK");
                await Navigation.PopModalAsync();
            }
            catch (Exception ex)
            {
                await DisplayAlertAsync("Configuration Error", ex.Message, "OK");
            }
            finally { saveButton.IsEnabled = true; }
        };

        cancelButton.Clicked += async (_, _) => await Navigation.PopModalAsync();

        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = new Thickness(20),
                Spacing = 12,
                Children =
                {
                    new Label { Text = "Connect ToTrak to the same central API using USB or Wi-Fi.", FontSize = 16 },
                    modePicker,
                    usbHelp,
                    tunnelHelp,
                    urlCaption,
                    urlEntry,
                    tokenCaption,
                    tokenEntry,
                    testButton,
                    status,
                    saveButton,
                    cancelButton
                }
            }
        };
    }
}
