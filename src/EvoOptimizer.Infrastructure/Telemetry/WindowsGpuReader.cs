using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace EvoOptimizer.Infrastructure.Telemetry;

[SupportedOSPlatform("windows")]
public sealed class WindowsGpuReader : IDisposable
{
    private IntPtr _query = IntPtr.Zero;
    private IntPtr _gpuCounter = IntPtr.Zero;
    private bool _initialized;
    private bool _hasFailed;

    [DllImport("pdh.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int PdhOpenQuery(string? szDataSource, IntPtr dwUserData, out IntPtr phQuery);

    [DllImport("pdh.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int PdhAddEnglishCounter(IntPtr hQuery, string szFullCounterPath, IntPtr dwUserData, out IntPtr phCounter);

    [DllImport("pdh.dll", SetLastError = true)]
    private static extern int PdhCollectQueryData(IntPtr hQuery);

    [StructLayout(LayoutKind.Sequential)]
    private struct PDH_FMT_COUNTERVALUE
    {
        public int CStatus;
        public double doubleValue;
    }

    private const int PdhFmtDouble = 0x00000200;

    [DllImport("pdh.dll", SetLastError = true)]
    private static extern int PdhGetFormattedCounterValue(IntPtr hCounter, int dwFormat, out int lpdwType, out PDH_FMT_COUNTERVALUE pValue);

    [DllImport("pdh.dll", SetLastError = true)]
    private static extern int PdhCloseQuery(IntPtr hQuery);

    public (double? UsagePercent, double? SharedMemoryMb) Read()
    {
        if (_hasFailed)
            return (null, null);

        try
        {
            EnsureInitialized();

            if (_query == IntPtr.Zero || _gpuCounter == IntPtr.Zero)
                return (null, null);

            int status = PdhCollectQueryData(_query);
            if (status != 0)
                return (null, null);

            int type;
            PDH_FMT_COUNTERVALUE val;
            int fmtStatus = PdhGetFormattedCounterValue(_gpuCounter, PdhFmtDouble, out type, out val);
            if (fmtStatus == 0 && val.CStatus == 0)
            {
                double usage = Math.Round(Math.Clamp(val.doubleValue, 0, 100), 1);
                return (usage, null);
            }

            return (null, null);
        }
        catch
        {
            _hasFailed = true;
            return (null, null);
        }
    }

    private void EnsureInitialized()
    {
        if (_initialized)
            return;

        _initialized = true;

        try
        {
            int status = PdhOpenQuery(null, IntPtr.Zero, out _query);
            if (status == 0)
            {
                // Contador nativo de utilización de motores 3D de GPU en Windows
                int cStatus = PdhAddEnglishCounter(_query, @"\GPU Engine(*engtype_3D*)\Utilization Percentage", IntPtr.Zero, out _gpuCounter);
                if (cStatus == 0)
                {
                    PdhCollectQueryData(_query); // Primera lectura de cebado
                }
            }
            else
            {
                _hasFailed = true;
            }
        }
        catch
        {
            _hasFailed = true;
        }
    }

    public void Dispose()
    {
        if (_query != IntPtr.Zero)
        {
            PdhCloseQuery(_query);
            _query = IntPtr.Zero;
            _gpuCounter = IntPtr.Zero;
        }
    }
}
