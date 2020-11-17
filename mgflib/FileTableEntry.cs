namespace mgflib
{
    public class FileTableEntry
    {
        public FileSystemNode Node { get; set; }
        public string Path { get; set; }

        public override string ToString() => Path;
    }
}
