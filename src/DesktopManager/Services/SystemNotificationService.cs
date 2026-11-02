using System.Runtime.InteropServices;
using Listener = Windows.UI.Notifications.Management.UserNotificationListener;
using AccessStatus = Windows.UI.Notifications.Management.UserNotificationListenerAccessStatus;
using NotificationKinds = Windows.UI.Notifications.NotificationKinds;

namespace DesktopManager.Services;

public sealed record DesktopNotification(uint Id, string AppName, string Title, string Body, DateTimeOffset Time, string AppId);

public static class SystemNotificationService
{
    public static async Task<IReadOnlyList<DesktopNotification>?> ReadAsync()
    {
        try
        {
            var listener = Listener.Current;
            var access = await listener.RequestAccessAsync();
            if (access != AccessStatus.Allowed)
            {
                return null;
            }

            var notifications = await listener.GetNotificationsAsync(NotificationKinds.Toast);
            return notifications
                .OrderByDescending(notification => notification.CreationTime)
                .Take(30)
                .Select(notification =>
                {
                    var binding = notification.Notification.Visual.Bindings?.FirstOrDefault();
                    var texts = binding?.GetTextElements();
                    var title = texts is { Count: > 0 } ? texts[0]?.Text : null;
                    var body = texts is { Count: > 1 }
                        ? string.Join("\n", texts.Skip(1).Select(text => text.Text).Where(text => !string.IsNullOrWhiteSpace(text)))
                        : null;
                    return new DesktopNotification(
                        notification.Id,
                        notification.AppInfo.DisplayInfo.DisplayName,
                        string.IsNullOrWhiteSpace(title) ? notification.AppInfo.DisplayInfo.DisplayName : title,
                        body ?? string.Empty,
                        notification.CreationTime,
                        notification.AppInfo.AppUserModelId);
                })
                .ToList();
        }
        catch
        {
            return null;
        }
    }

    public static void Activate(string appId)
    {
        if (string.IsNullOrWhiteSpace(appId)) return;
        try
        {
            var comType = Type.GetTypeFromCLSID(new Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C"));
            if (comType is null || Activator.CreateInstance(comType) is not { } activationObject) return;
            var unknown = Marshal.GetIUnknownForObject(activationObject);
            try
            {
                var interfaceId = new Guid("2e941141-7f97-4756-ba1d-9decde894a3d");
                if (Marshal.QueryInterface(unknown, in interfaceId, out var interfacePointer) != 0) return;
                try
                {
                    var manager = (IApplicationActivationManager)Marshal.GetObjectForIUnknown(interfacePointer);
                    manager.ActivateApplication(appId, string.Empty, ActivateApplicationOptions.None, out _);
                }
                finally
                {
                    Marshal.Release(interfacePointer);
                }
            }
            finally
            {
                Marshal.Release(unknown);
            }
        }
        catch
        {
            // Some apps register notifications without an activatable AUMID.
        }
    }

    public static async Task<bool> RemoveAsync(uint id)
    {
        try
        {
            var listener = Listener.Current;
            var access = await listener.RequestAccessAsync();
            if (access != AccessStatus.Allowed) return false;
            listener.RemoveNotification(id);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static async Task<bool> ClearAllAsync()
    {
        try
        {
            var listener = Listener.Current;
            var access = await listener.RequestAccessAsync();
            if (access != AccessStatus.Allowed) return false;
            listener.ClearNotifications();
            return true;
        }
        catch
        {
            return false;
        }
    }

    [ComImport]
    [Guid("2e941141-7f97-4756-ba1d-9decde894a3d")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager
    {
        void ActivateApplication(string appUserModelId, string arguments, ActivateApplicationOptions options, out uint processId);
    }

    private enum ActivateApplicationOptions
    {
        None = 0,
        NoErrorUi = 1,
        NoSplashScreen = 2
    }
}
