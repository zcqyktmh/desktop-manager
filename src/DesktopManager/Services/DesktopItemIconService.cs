using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media.Imaging;

namespace DesktopManager.Services;

/// <summary>Loads the same shell icons Windows associates with desktop files and shortcuts.</summary>
public static class DesktopItemIconService
{
    private static readonly ConcurrentDictionary<string, BitmapSource> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, string> ShortcutTargets = new(StringComparer.OrdinalIgnoreCase);

    public static BitmapSource? GetIcon(string path)
    {
        if (Cache.TryGetValue(path, out var cached)) return cached;
        var icon = LoadIcon(path);
        if (icon is not null) Cache.TryAdd(path, icon);
        return icon;
    }

    public static string? ResolveShortcutTarget(string path)
    {
        if (!Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
            return path;
        var target = ShortcutTargets.GetOrAdd(path, ReadShortcutTarget);
        return string.IsNullOrWhiteSpace(target) ? null : target;
    }

    private static string ReadShortcutTarget(string shortcutPath)
    {
        IShellLinkW? shellLink = null;
        try
        {
            shellLink = (IShellLinkW)new ShellLinkCom();
            ((IPersistFile)shellLink).Load(shortcutPath, 0);
            var targetPath = new StringBuilder(2048);
            shellLink.GetPath(targetPath, targetPath.Capacity, IntPtr.Zero, 0);
            return Environment.ExpandEnvironmentVariables(targetPath.ToString());
        }
        catch { return ""; }
        finally
        {
            if (shellLink is not null && Marshal.IsComObject(shellLink)) Marshal.FinalReleaseComObject(shellLink);
        }
    }

    private static BitmapSource? LoadIcon(string path)
    {
        IntPtr icon = IntPtr.Zero;
        try
        {
            var shellImage = LoadShellImage(path);
            if (shellImage is not null) return shellImage;

            if (Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
                icon = GetShortcutIcon(path);

            if (icon == IntPtr.Zero)
            {
                var info = new ShellFileInfo();
                if (SHGetFileInfo(path, 0, out info, (uint)Marshal.SizeOf<ShellFileInfo>(), ShgfiIcon | ShgfiLargeIcon) != IntPtr.Zero)
                    icon = info.Icon;
            }

            if (icon == IntPtr.Zero) return null;
            var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(icon, System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(192, 192));
            source.Freeze();
            return source;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (icon != IntPtr.Zero) DestroyIcon(icon);
        }
    }

    private static BitmapSource? LoadShellImage(string path)
    {
        IntPtr bitmap = IntPtr.Zero;
        IShellItemImageFactory? factory = null;
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out factory);
            var size = new NativeSize { Width = 192, Height = 192 };
            if (factory.GetImage(size, SiigbfIconOnly | SiigbfBiggerSizeOk, out bitmap) < 0 || bitmap == IntPtr.Zero)
                return null;
            var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                bitmap, IntPtr.Zero, System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (factory is not null && Marshal.IsComObject(factory)) Marshal.FinalReleaseComObject(factory);
        }
    }

    private static IntPtr GetShortcutIcon(string shortcutPath)
    {
        IShellLinkW? shellLink = null;
        try
        {
            shellLink = (IShellLinkW)new ShellLinkCom();
            ((IPersistFile)shellLink).Load(shortcutPath, 0);

            var iconPath = new StringBuilder(1024);
            shellLink.GetIconLocation(iconPath, iconPath.Capacity, out var iconIndex);
            var resolvedIconPath = Environment.ExpandEnvironmentVariables(iconPath.ToString().Trim().Trim('"'));
            if (!string.IsNullOrWhiteSpace(resolvedIconPath) && File.Exists(resolvedIconPath))
            {
                ExtractIconEx(resolvedIconPath, iconIndex, out var largeIcon, IntPtr.Zero, 1);
                if (largeIcon != IntPtr.Zero) return largeIcon;
            }

            var targetPath = new StringBuilder(2048);
            shellLink.GetPath(targetPath, targetPath.Capacity, IntPtr.Zero, 0);
            var target = Environment.ExpandEnvironmentVariables(targetPath.ToString());
            if (!string.IsNullOrWhiteSpace(target))
            {
                var info = new ShellFileInfo();
                if (SHGetFileInfo(target, 0, out info, (uint)Marshal.SizeOf<ShellFileInfo>(), ShgfiIcon | ShgfiLargeIcon) != IntPtr.Zero)
                    return info.Icon;
            }
        }
        catch { }
        finally
        {
            if (shellLink is not null && Marshal.IsComObject(shellLink)) Marshal.FinalReleaseComObject(shellLink);
        }
        return IntPtr.Zero;
    }

    private const uint ShgfiIcon = 0x000000100;
    private const uint ShgfiLargeIcon = 0x000000000;
    private const uint SiigbfBiggerSizeOk = 0x00000001;
    private const uint SiigbfIconOnly = 0x00000004;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize { public int Width, Height; }

    [ComImport, Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(NativeSize size, uint flags, out IntPtr bitmap);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public IntPtr Icon;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLinkCom { }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int maxPath, IntPtr findData, uint flags);
        void GetIDList(out IntPtr itemIdList);
        void SetIDList(IntPtr itemIdList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int maxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int maxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int maxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCommand);
        void SetShowCmd(int showCommand);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int maxPath, out int iconIndex);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr window, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }

    [ComImport, Guid("0000010B-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void GetClassID(out Guid classId);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string fileName, uint mode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string fileName, [MarshalAs(UnmanagedType.Bool)] bool remember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string fileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string fileName);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint fileAttributes, out ShellFileInfo fileInfo, uint fileInfoSize, uint flags);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid interfaceId, out IShellItemImageFactory imageFactory);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string fileName, int iconIndex, out IntPtr largeIcon, IntPtr smallIcon, uint icons);
    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr objectHandle);
}
