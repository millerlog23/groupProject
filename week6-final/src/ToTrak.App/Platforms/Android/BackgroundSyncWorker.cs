using Android.Content;
using Android.Runtime;
using AndroidX.Work;
using Microsoft.Maui.Devices;
using Microsoft.Maui.Storage;
using ToTrak.App.Data;
using ToTrak.App.Services;

namespace ToTrak.App.Platforms.Android;

[Register("com.companyname.totrak.app.BackgroundSyncWorker")]
public class BackgroundSyncWorker : Worker
{
    private const int CurrentUserId = 4005;

    public BackgroundSyncWorker(
        Context context,
        WorkerParameters workerParams)
        : base(context, workerParams)
    {
    }

    public override Result DoWork()
    {
        try
        {
            bool success = RunSyncAsync()
                .GetAwaiter()
                .GetResult();

            return success
                ? Result.InvokeSuccess()!
                : Result.InvokeRetry()!;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Background synchronization failed: {ex.Message}");

            return Result.InvokeRetry()!;
        }
    }

    private static async Task<bool> RunSyncAsync()
    {
        // Use the same API configuration as MainPage.
        string defaultApiUrl =
            DeviceInfo.Platform == DevicePlatform.Android &&
            DeviceInfo.DeviceType == DeviceType.Virtual
                ? "http://10.0.2.2:5125/"
                : "http://127.0.0.1:5125/";

        string apiUrl = Preferences.Get(
            "ApiBaseUrl",
            defaultApiUrl);

        using var httpClient = new HttpClient
        {
            BaseAddress = new Uri(apiUrl),
            Timeout = TimeSpan.FromSeconds(15)
        };

        var database = new TaskDatabase();

        var syncService = new SyncService(
            database,
            httpClient);

        // STEP 1: Upload pending local changes.
        var pushResult =
            await syncService.SendPendingChangesAsync(CurrentUserId);

        if (pushResult.Failed)
        {
            return false;
        }

        // STEP 2: Download server changes.
        var pullResult =
            await syncService.PullChangesAsync(CurrentUserId);

        // Request another attempt if pulling was paused.
        return pullResult.PauseReason is null;
    }
}