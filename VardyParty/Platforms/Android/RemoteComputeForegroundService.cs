using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using VardyParty.Ports;

namespace VardyParty.Platforms.Android;

[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeDataSync)]
public sealed class RemoteComputeForegroundService : Service
{
    private const int NotificationId = 4701;
    private const string ChannelId = "remote-compute";

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        var notification = BuildNotification();
        if (OperatingSystem.IsAndroidVersionAtLeast(29))
        {
            StartForeground(NotificationId, notification, ForegroundService.TypeDataSync);
        }
        else
        {
            StartForeground(NotificationId, notification);
        }

        return StartCommandResult.Sticky;
    }

    private Notification BuildNotification()
    {
        var manager = (NotificationManager?)GetSystemService(NotificationService);
        if (OperatingSystem.IsAndroidVersionAtLeast(26) && manager?.GetNotificationChannel(ChannelId) is null)
        {
            manager?.CreateNotificationChannel(new NotificationChannel(ChannelId, "Remote compute", NotificationImportance.Low)
            {
                Description = "Keeps a paired compute session alive while a stream is resolving."
            });
        }

        var builder = OperatingSystem.IsAndroidVersionAtLeast(26)
            ? new Notification.Builder(this, ChannelId)
            : new Notification.Builder(this);
        builder
            .SetContentTitle("VardyParty")
            .SetContentText("Resolving a stream")
            .SetSmallIcon(global::Android.Resource.Drawable.StatNotifySync)
            .SetOngoing(true);
        return builder.Build()!;
    }
}

public sealed class AndroidRemoteComputeKeepAlive : IRemoteComputeKeepAlive
{
    public void Start()
    {
        var context = global::Android.App.Application.Context;
        var intent = new Intent(context, typeof(RemoteComputeForegroundService));
        if (OperatingSystem.IsAndroidVersionAtLeast(26))
        {
            context.StartForegroundService(intent);
        }
        else
        {
            context.StartService(intent);
        }
    }

    public void Stop()
    {
        var context = global::Android.App.Application.Context;
        context.StopService(new Intent(context, typeof(RemoteComputeForegroundService)));
    }
}
