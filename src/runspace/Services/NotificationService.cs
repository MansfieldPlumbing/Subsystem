using Android.App;
using Android.Content;
using Android.Service.Notification;
using System.Collections.Concurrent;
using System.Linq;
using System.Collections.Generic;

namespace Subsystem;

[Service(Label = "Subsystem Notification Listener", Permission = "android.permission.BIND_NOTIFICATION_LISTENER_SERVICE", Exported = true)]
[IntentFilter(new[] { "android.service.notification.NotificationListenerService" })]
public class NotificationService : NotificationListenerService
{
    public static System.Collections.Immutable.ImmutableDictionary<string, StatusBarNotification> Notifications { get; private set; } = System.Collections.Immutable.ImmutableDictionary<string, StatusBarNotification>.Empty;

    public override void OnNotificationPosted(StatusBarNotification? sbn)
    {
        if (sbn != null) {
            Notifications = Notifications.SetItem(sbn.Key, sbn);
        }
    }

    public override void OnNotificationRemoved(StatusBarNotification? sbn)
    {
        if (sbn != null) {
            Notifications.TryRemove(sbn.Key, out _);
        }
    }

    public override void OnListenerConnected()
    {
        base.OnListenerConnected();
        var active = GetActiveNotifications();
        if (active != null) {
            foreach (var n in active) {
                Notifications[n.Key] = n;
            }
        }
    }
}
