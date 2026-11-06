using System.Runtime.InteropServices;

namespace CommandCenter.Core.Game
{
    // A graphics card as Windows reports it: its name, its maker's PCI vendor id, and whether it is one of Windows' own
    // software renderers
    public sealed record GraphicsCard(string Name, uint VendorId, bool Software);

    // The graphics cards of this PC (ADR 0010). A PC with two real cards, such as most gaming laptops, can run the game on
    // the slower built-in one; only such a PC is offered the dedicated card. Windows lists them through DXGI, which is
    // part of every Windows (WMI would need a package).
    public static class GraphicsCards
    {
        // Microsoft's own adapters (Basic Display Adapter, Basic Render Driver) are not graphics cards
        private const uint Microsoft = 0x1414;
        private const uint SoftwareAdapter = 2; // DXGI_ADAPTER_FLAG_SOFTWARE

        public static bool HasTwo(IReadOnlyList<GraphicsCard> cards) => Real(cards).Count() >= 2;

        // The cards that are hardware, in Windows' order
        public static IEnumerable<GraphicsCard> Real(IReadOnlyList<GraphicsCard> cards) =>
            cards.Where(card => !card.Software && card.VendorId != Microsoft);

        // The adapters Windows lists; none when DirectX cannot say
        public static IReadOnlyList<GraphicsCard> Read()
        {
            var cards = new List<GraphicsCard>();
            try
            {
                Guid iid = typeof(IDXGIFactory1).GUID;
                if (CreateDXGIFactory1(ref iid, out object factory) != 0 || factory is not IDXGIFactory1 dxgi)
                    return cards;
                try
                {
                    for (uint i = 0; dxgi.EnumAdapters1(i, out var adapter) == 0; i++)
                    {
                        try
                        {
                            if (adapter.GetDesc1(out var desc) == 0)
                                cards.Add(new GraphicsCard((desc.Description ?? "").Trim(), desc.VendorId, (desc.Flags & SoftwareAdapter) != 0));
                        }
                        finally
                        {
                            Marshal.ReleaseComObject(adapter);
                        }
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(dxgi);
                }
            }
            catch (Exception ex) when (ex is COMException or DllNotFoundException or EntryPointNotFoundException or InvalidCastException)
            {
                ErrorLog.Write(ex);
            }
            return cards.Where(card => card.Name.Length > 0).ToList();
        }

        [DllImport("dxgi.dll", ExactSpelling = true)]
        private static extern int CreateDXGIFactory1(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object factory);

        // DXGI_ADAPTER_DESC1; the LUID is two 32-bit halves, so the layout is the same in 32-bit and 64-bit processes
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct AdapterDescription
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string Description;
            public uint VendorId;
            public uint DeviceId;
            public uint SubSysId;
            public uint Revision;
            public nuint DedicatedVideoMemory;
            public nuint DedicatedSystemMemory;
            public nuint SharedSystemMemory;
            public uint LuidLow;
            public int LuidHigh;
            public uint Flags;
        }

        // The interfaces in their vtable order; the methods Command Center does not call only keep their slots
        [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDXGIFactory1
        {
            void SetPrivateData();
            void SetPrivateDataInterface();
            void GetPrivateData();
            void GetParent();
            void EnumAdapters();
            void MakeWindowAssociation();
            void GetWindowAssociation();
            void CreateSwapChain();
            void CreateSoftwareAdapter();

            [PreserveSig]
            int EnumAdapters1(uint index, out IDXGIAdapter1 adapter);

            [PreserveSig]
            int IsCurrent();
        }

        [ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDXGIAdapter1
        {
            void SetPrivateData();
            void SetPrivateDataInterface();
            void GetPrivateData();
            void GetParent();
            void EnumOutputs();
            void GetDesc();
            void CheckInterfaceSupport();

            [PreserveSig]
            int GetDesc1(out AdapterDescription description);
        }
    }
}
