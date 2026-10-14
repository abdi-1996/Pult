using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace TouchDisplayMobileWeb;

internal sealed class SystemStats
{
    private long _lastRx;
    private long _lastTx;
    private long _lastTick;

    public Snapshot Read()
    {
        var mem = ReadMemory();
        var net = ReadNetwork();
        var gpu = ReadNvidia();
        return new Snapshot(
            gpu.Utilization,
            gpu.MemoryPercent,
            gpu.Name,
            mem.UsedPercent,
            mem.UsedGb,
            mem.TotalGb,
            net.DownMbps,
            net.UpMbps
        );
    }

    private (double UsedPercent, double UsedGb, double TotalGb) ReadMemory()
    {
        var m = new MEMORYSTATUSEX();
        m.dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>();
        if (!GlobalMemoryStatusEx(ref m) || m.ullTotalPhys == 0) return (0, 0, 0);
        var used = m.ullTotalPhys - m.ullAvailPhys;
        return (
            used * 100.0 / m.ullTotalPhys,
            used / 1024.0 / 1024 / 1024,
            m.ullTotalPhys / 1024.0 / 1024 / 1024
        );
    }

    private (double DownMbps, double UpMbps) ReadNetwork()
    {
        var now = Environment.TickCount64;
        long rx = 0, tx = 0;
        foreach (var n in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up))
        {
            try
            {
                var s = n.GetIPv4Statistics();
                rx += s.BytesReceived;
                tx += s.BytesSent;
            }
            catch { }
        }

        if (_lastTick == 0)
        {
            _lastTick = now; _lastRx = rx; _lastTx = tx;
            return (0, 0);
        }

        var seconds = Math.Max(.1, (now - _lastTick) / 1000.0);
        var down = Math.Max(0, rx - _lastRx) * 8.0 / seconds / 1_000_000;
        var up = Math.Max(0, tx - _lastTx) * 8.0 / seconds / 1_000_000;
        _lastTick = now; _lastRx = rx; _lastTx = tx;
        return (down, up);
    }

    private static (double Utilization, double MemoryPercent, string Name) ReadNvidia()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "nvidia-smi",
                Arguments = "--query-gpu=name,utilization.gpu,memory.used,memory.total --format=csv,noheader,nounits",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p is null) return (0, 0, "GPU");
            var line = p.StandardOutput.ReadLine();
            if (!p.WaitForExit(500) || string.IsNullOrWhiteSpace(line)) return (0, 0, "GPU");
            var parts = line.Split(',').Select(x => x.Trim()).ToArray();
            if (parts.Length < 4) return (0, 0, "GPU");
            _ = double.TryParse(parts[1], out var util);
            _ = double.TryParse(parts[2], out var used);
            _ = double.TryParse(parts[3], out var total);
            return (util, total > 0 ? used * 100 / total : 0, parts[0]);
        }
        catch { return (0, 0, "GPU"); }
    }

    public sealed record Snapshot(
        double GpuPercent,
        double GpuMemoryPercent,
        string GpuName,
        double RamPercent,
        double RamUsedGb,
        double RamTotalGb,
        double DownMbps,
        double UpMbps);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
}
