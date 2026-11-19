using Android.Content;
using Android.Runtime;
using AndroidX.Work;
using ToTrak.App.Data;
using ToTrak.App.Services;

namespace ToTrak.App.Platforms.Android;

[Register("com.companyname.totrak.app.BackgroundSyncWorker")]
public class BackgroundSyncWorker : Worker
{

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
        var session = await AccountService.RestoreAsync();
        if (session is null) return true;
        using var httpClient = AccountService.CreateClient(session);

        var database = new TaskDatabase();

        var syncService = new SyncService(
            database,
            httpClient);

        // STEP 1: Upload pending local changes.
        var pushResult =
            await syncService.SendPendingChangesAsync(session.UserId);

        if (pushResult.Failed)
        {
            return false;
        }

        // STEP 2: Download server changes.
        var pullResult =
            await syncService.PullChangesAsync(session.UserId);

        // Request another attempt if pulling was paused.
        return pullResult.PauseReason is null;
    }
}
