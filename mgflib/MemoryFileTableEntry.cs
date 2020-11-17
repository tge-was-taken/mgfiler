using System.Runtime.InteropServices;

namespace mgflib
{
    [StructLayout( LayoutKind.Sequential, Pack = 1, Size = 8 )]
    public struct MemoryFileTableEntry
    {
        public const int SIZE = 8;

        public int NodeAddress { get; set; }
        public int PathAddress { get; set; }
    }
}
