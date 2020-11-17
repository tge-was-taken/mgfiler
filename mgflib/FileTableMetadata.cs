using System.Collections.Generic;

namespace mgflib
{
    public class FileTableMetadata
    {
        public int ElfBaseOffset { get; }
        public IReadOnlyDictionary<int, object> ObjectByAddress { get; }

        public long FileTableOffset { get; }

        public FileTableMetadata( int elfBaseOffset, Dictionary<int, object> objectByAddress, long fileTableOffset )
        {
            ElfBaseOffset = elfBaseOffset;
            ObjectByAddress = objectByAddress;
            FileTableOffset = fileTableOffset;
        }
    }
}
