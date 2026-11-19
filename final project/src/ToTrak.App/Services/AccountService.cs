using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Maui.Devices;
using Microsoft.Maui.Storage;

namespace ToTrak.App.Services;

public sealed record AccountSession(int UserId, string Username, string Token, DateTime ExpiresAtUtc);

public static class AccountService
{
    private const string SessionKey = "ToTrak.AccountSession";
    // Never force localhost in DEBUG: a single build must also work without USB.
    // Changing the connection in Settings takes effect after restarting the app.
    public static string ApiUrl => ConnectionPreferences.ApiUrl;

    public static HttpClient CreateClient(AccountSession? session = null)
    {
        var client = new HttpClient(new DevTunnelAuthHandler()) { BaseAddress = new Uri(ApiUrl), Timeout = TimeSpan.FromSeconds(15) };
        if (session is not null) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);
        return client;
    }

    public static async Task<AccountSession?> RestoreAsync()
    {
        string? json = await SecureStorage.Default.GetAsync(SessionKey);
        if (json is null) return null;
        var saved = JsonSerializer.Deserialize<SavedSession>(json);
        return saved is not null && saved.ApiUrl == ApiUrl && saved.Session.ExpiresAtUtc > DateTime.UtcNow ? saved.Session : null;
    }

    public static async Task<AccountSession> SignInAsync(string username, string password, bool register)
    {
        using var client = CreateClient();
        using var response = await client.PostAsJsonAsync(register ? "auth/register" : "auth/login", new { username, password });
        if (!response.IsSuccessStatusCode)
        {
            string message = response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                ? "The username or password is incorrect." : await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException(message);
        }
        var session = await response.Content.ReadFromJsonAsync<AccountSession>()
            ?? throw new InvalidOperationException("The server did not return an account.");
        await SecureStorage.Default.SetAsync(SessionKey, JsonSerializer.Serialize(new SavedSession(ApiUrl, session)));
        return session;
    }

    public static async Task SignOutAsync(AccountSession session)
    {
        SecureStorage.Default.Remove(SessionKey);
        try
        {
            using var client = CreateClient(session);
            using var response = await client.PostAsync("auth/logout", null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        { System.Diagnostics.Debug.WriteLine($"Local sign out completed: {ex.Message}"); }
    }

    private sealed record SavedSession(string ApiUrl, AccountSession Session);
}
