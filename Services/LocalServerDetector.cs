#if DEBUG
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;

namespace Lyrictified.Services;

internal static class LocalServerDetector
{
    public static string? TryAutoDetect()
    {
        var processes = Process.GetProcessesByName("Lyrictified.Server");
        if (processes.Length == 0)
            return null;

        foreach (var process in processes)
        {
            try
            {
                var ports = GetListeningPorts(process.Id);
                foreach (var port in ports)
                {
                    var url = $"http://127.0.0.1:{port}/";
                    if (CanConnect(url))
                        return url;
                }
            }
            catch
            {
                // ignore inaccessible processes
            }
        }

        return null;
    }

    private static bool CanConnect(string url)
    {
        try
        {
            using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var response = client.GetAsync($"{url}health").GetAwaiter().GetResult();
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static List<int> GetListeningPorts(int processId)
    {
        var ports = new List<int>();
        const uint afInet = 2;
        const uint tableClass = 3; // TCP_TABLE_OWNER_PID_LISTENERS
        uint size = 0;
        _ = GetExtendedTcpTable(IntPtr.Zero, ref size, true, afInet, tableClass, 0);

        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            uint result = GetExtendedTcpTable(buffer, ref size, true, afInet, tableClass, 0);
            if (result != 0)
                return ports;

            int numEntries = Marshal.ReadInt32(buffer);
            IntPtr rowPtr = IntPtr.Add(buffer, 4);
            int rowSize = Marshal.SizeOf<MIB_TCPROW_OWNER_PID>();
            for (int i = 0; i < numEntries; i++)
            {
                var row = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(rowPtr);
                if (row.dwOwningPid == (uint)processId && row.dwState == 2) // MIB_TCP_STATE_LISTEN
                {
                    ushort portNetwork = (ushort)(row.dwLocalPort & 0xFFFF);
                    ushort portHost = (ushort)IPAddress.NetworkToHostOrder((short)portNetwork);
                    ports.Add(portHost);
                }
                rowPtr = IntPtr.Add(rowPtr, rowSize);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return ports;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref uint pdwSize, bool bOrder, uint ulAf, uint TableClass, uint Reserved);

    [StructLayout(LayoutKind.Sequential)]
    struct MIB_TCPROW_OWNER_PID
    {
        public uint dwState;
        public uint dwLocalAddr;
        public uint dwLocalPort;
        public uint dwRemoteAddr;
        public uint dwRemotePort;
        public uint dwOwningPid;
    }
}
#endif
