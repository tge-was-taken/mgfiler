using System.Runtime.InteropServices;

namespace mgflib
{
    [StructLayout( LayoutKind.Sequential, Pack = 1, Size = 16 )]
    public struct MemoryFileSystemNode
    {
        public const int SIZE = 16;

        public FileSystemNodeType Type { get; set; }
        public int ParentAddress { get; set; }
        public int DataOffset { get; set; }
        public int DataLength { get; set; }
    }
}
