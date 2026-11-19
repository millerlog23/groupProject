using AndroidX.Work;
using Android.App;
using Android.Content.PM;
using Android.OS;

namespace ToTrak.App;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        // Run background synchronization when network is available.
        using var constraintsBuilder = new Constraints.Builder();

        var constraints = constraintsBuilder
            .SetRequiredNetworkType(NetworkType.Connected)
            .Build();

        // Schedule synchronization approximately every 15 minutes.
        var request = PeriodicWorkRequest.Builder
            .From<ToTrak.App.Platforms.Android.BackgroundSyncWorker>(
                TimeSpan.FromMinutes(15))
            .SetConstraints(constraints)
            .Build();

        // Prevent scheduling duplicate background jobs.
        WorkManager.GetInstance(this)
            .EnqueueUniquePeriodicWork(
                "ToTrakBackgroundSync",
                ExistingPeriodicWorkPolicy.Keep,
                request);
    }
}