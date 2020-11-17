namespace mgflib
{
    public class FileSystemNode
    {
        public FileSystemNodeType Type { get; set; }
        public object Parent { get; set; }
        public int RelativeDataOffset { get; set; }
        public int DataLength { get; set; }

        public FileTableEntry Entry { get; }
        public string Path { get; }
        public string RealFilePath
        {
            get
            {
                var parent = Parent as FileSystemNode;
                if ( parent != null )
                    return parent.RealFilePath;
                else
                    return (string)Parent;
            }
        }
        public FileSystemNode RealFileNode
        {
            get
            {
                var parent = Parent as FileSystemNode;
                if ( parent != null )
                    return parent.RealFileNode;
                else
                    return this;
            }
        }
        public int DataOffset
        {
            get
            {
                var dataOffset = RelativeDataOffset;
                var parent = Parent as FileSystemNode;
                if ( parent != null )
                    dataOffset += parent.DataOffset;

                return dataOffset;
            }
        }

        public FileSystemNode( FileTableEntry entry, string path )
        {
            Entry = entry;
            Path = path;
        }

        public override string ToString() => Path;
    }
}
