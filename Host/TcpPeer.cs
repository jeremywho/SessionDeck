using System.Net;
using System.Runtime.InteropServices;

namespace SessionDeck.Host;

/// <summary>
/// Who is on the other end of a loopback connection: the process that owns the client's port, from
/// the system's TCP table. Looked up while the request is still being served, so the socket is open.
/// </summary>
internal static class TcpPeer
{
    const int AF_INET = 2;
    const int TCP_TABLE_OWNER_PID_ALL = 5;

    [StructLayout(LayoutKind.Sequential)]
    struct MIB_TCPROW_OWNER_PID
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
        public uint OwningPid;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int pdwSize, bool bOrder, int ulAf, int tableClass, uint reserved);

    /// <summary>The pid owning the loopback socket whose local port is <paramref name="port"/>, or 0 when none is found.</summary>
    public static int OwningPid(int port)
    {
        int size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
        if (size <= 0) return 0;
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buffer, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0) != 0) return 0;
            int count = Marshal.ReadInt32(buffer);
            IntPtr row = buffer + 4;
            int rowSize = Marshal.SizeOf<MIB_TCPROW_OWNER_PID>();
            for (int i = 0; i < count; i++, row += rowSize)
            {
                var r = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(row);
                int localPort = (int)IPAddress.NetworkToHostOrder((short)(r.LocalPort & 0xFFFF)) & 0xFFFF;
                if (localPort == port && r.LocalAddr == 0x0100007F) return (int)r.OwningPid;
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
        return 0;
    }
}
