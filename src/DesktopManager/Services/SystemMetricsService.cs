using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace DesktopManager.Services;

public sealed record SystemMetricsSnapshot(
    double? CpuLoadPercent,
    ulong MemoryUsedBytes,
    ulong MemoryTotalBytes,
    double? CpuTemperatureCelsius,
    double? GpuTemperatureCelsius,
    double? SystemDriveUsedPercent,
    long? SystemDriveFreeBytes);

public sealed class SystemMetricsService : IDisposable
{
    private bool _hasCpuTimes;
    private ulong _lastIdleTime;
    private ulong _lastKernelTime;
    private ulong _lastUserTime;

    public SystemMetricsSnapshot ReadSnapshot()
    {
        var cpuLoad = ReadCpuLoadFromSystemTimes();
        var memory = ReadMemory();
        var drive = ReadSystemDrive();
        var cpuTemp = ReadWindowsTemperature("\\\\ROOT\\WMI", "MSAcpi_ThermalZoneTemperature", "CurrentTemperature");
        return new SystemMetricsSnapshot(cpuLoad, memory.Used, memory.Total, cpuTemp, null, drive.UsedPercent, drive.FreeBytes);
    }

    private double? ReadCpuLoadFromSystemTimes()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return null;
        var idleValue = ToUInt64(idle);
        var kernelValue = ToUInt64(kernel);
        var userValue = ToUInt64(user);
        if (!_hasCpuTimes)
        {
            _lastIdleTime = idleValue;
            _lastKernelTime = kernelValue;
            _lastUserTime = userValue;
            _hasCpuTimes = true;
            return null;
        }

        var idleDelta = idleValue - _lastIdleTime;
        var kernelDelta = kernelValue - _lastKernelTime;
        var userDelta = userValue - _lastUserTime;
        _lastIdleTime = idleValue;
        _lastKernelTime = kernelValue;
        _lastUserTime = userValue;
        var total = kernelDelta + userDelta;
        return total == 0 ? null : Math.Clamp((total - Math.Min(idleDelta, total)) * 100d / total, 0, 100);
    }

    private static double? ReadWindowsTemperature(string scope, string className, string propertyName)
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(scope, $"SELECT {propertyName} FROM {className}");
            foreach (System.Management.ManagementObject item in searcher.Get())
            {
                if (item[propertyName] is not null && double.TryParse(item[propertyName].ToString(), out var raw))
                {
                    var celsius = (raw / 10d) - 273.15d;
                    if (celsius is >= 0 and <= 125) return celsius;
                }
            }
        }
        catch { }
        return null;
    }

    private static (ulong Used, ulong Total) ReadMemory()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref status)) return (0, 0);
        return (status.TotalPhysical - status.AvailablePhysical, status.TotalPhysical);
    }

    private static (double? UsedPercent, long? FreeBytes) ReadSystemDrive()
    {
        try
        {
            var root = Path.GetPathRoot(Environment.SystemDirectory);
            if (string.IsNullOrWhiteSpace(root)) return (null, null);
            var drive = new DriveInfo(root);
            if (!drive.IsReady || drive.TotalSize <= 0) return (null, null);
            return ((drive.TotalSize - drive.AvailableFreeSpace) * 100d / drive.TotalSize, drive.AvailableFreeSpace);
        }
        catch { return (null, null); }
    }

    private static ulong ToUInt64(NativeFileTime value) => ((ulong)(uint)value.HighDateTime << 32) | (uint)value.LowDateTime;

    public void CloseSensors() { }

    public void Dispose() { }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileTime
    {
        public int LowDateTime;
        public int HighDateTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out NativeFileTime idleTime, out NativeFileTime kernelTime, out NativeFileTime userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}

