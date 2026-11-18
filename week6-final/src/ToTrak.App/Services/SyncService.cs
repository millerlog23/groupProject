using System.Net.Http.Json;
using ToTrak.App.Data;
using ToTrak.App.Models;

namespace ToTrak.App.Services;

public sealed class SyncService
{
    private readonly TaskDatabase database;
    private readonly HttpClient httpClient;

    public SyncService(TaskDatabase database, HttpClient httpClient)
    {
        this.database = database;
        this.httpClient = httpClient;
    }

    // Send locally saved changes to the server.
    public async Task<(int SentCount, bool Failed, string? Error)>
        SendPendingChangesAsync(int userId)
    {
        var pendingChanges =
            await database.GetPendingChangesAsync(userId);

        int sentCount = 0;

        foreach (var change in pendingChanges)
        {
            try
            {
                using var response =
                    await httpClient.PostAsJsonAsync("api/audit", change);

                if (!response.IsSuccessStatusCode)
                {
                    string details =
                        await response.Content.ReadAsStringAsync();

                    return (
                        sentCount,
                        true,
                        $"HTTP {(int)response.StatusCode}: {details}"
                    );
                }

                var result =
                    await response.Content.ReadFromJsonAsync<SyncResult>();

                if (result is null)
                {
                    throw new InvalidOperationException(
                        "The server returned an empty sync result.");
                }

                await database.AcknowledgeChangeAsync(change, result);

                sentCount++;
            }
            catch (Exception ex) when (
                ex is HttpRequestException
                    or TaskCanceledException
                    or System.Text.Json.JsonException
                    or InvalidOperationException
                    or SQLite.SQLiteException)
            {
                return (sentCount, true, ex.Message);
            }
        }

        return (sentCount, false, null);
    }

    // Download and apply changes from the server.
    public async Task<(int Applied, string? PauseReason)>
        PullChangesAsync(int userId)
    {
        string scope =
            $"{httpClient.BaseAddress!.AbsoluteUri}|user:{userId}";

        long lastChangeId =
            await database.GetPullCursorAsync(scope);

        var changes =
            await httpClient.GetFromJsonAsync<List<ServerChange>>(
                $"changes?userId={userId}&after={lastChangeId}");

        if (changes is null)
        {
            throw new InvalidOperationException(
                "The server returned an empty response.");
        }

        return await database.ApplyIncomingChangesAsync(
            scope,
            userId,
            changes);
    }
}