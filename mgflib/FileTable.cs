using BinaryTools.Elf;
using BinaryTools.Elf.Io;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace mgflib
{
    public unsafe class FileTable
    {
        private List<FileSystemNode> mNodes;

        public FileTableMetadata Metadata { get; private set; }
        public List<FileTableEntry> Entries { get; }
        public IReadOnlyList<FileSystemNode> Nodes => mNodes;

        public FileTable()
        {
            Metadata = null;
            Entries = new List<FileTableEntry>();
            mNodes = new List<FileSystemNode>();
        }

        public void Read( Span<byte> buffer )
        {
            var elfBaseOffset = GetBaseOffsetFromElfFile( buffer );
            var objectByAddress = new Dictionary<int, object>() { { 0, null } };
            var fileTableOffset = FindFileTableOffset( buffer, elfBaseOffset );

            while ( true )
            {
                var fileEntryOff = fileTableOffset + ( Entries.Count * MemoryFileTableEntry.SIZE );
                ref var memFileEntry = ref MemoryMarshal.AsRef<MemoryFileTableEntry>( buffer.Slice( fileEntryOff ) );
                if ( memFileEntry.NodeAddress == 0 && memFileEntry.PathAddress == 0 )
                    break;

                var fileEntry = new FileTableEntry();
                fileEntry.Path = ReadString( buffer, memFileEntry.PathAddress, objectByAddress, elfBaseOffset );
                fileEntry.Node = ReadFileNode( buffer, memFileEntry.NodeAddress, objectByAddress, elfBaseOffset, fileEntry, fileEntry.Path );
                Entries.Add( fileEntry );
            }

            Metadata = new FileTableMetadata( elfBaseOffset, objectByAddress, fileTableOffset );
        }

        public void Write( Span<byte> buffer )
        {
            // Patches the data offset & length for each entry node
            var objectToAddress = new Dictionary<object, int>();
            foreach ( var kvp in Metadata.ObjectByAddress )
            {
                if ( kvp.Value != null )
                    objectToAddress[ kvp.Value ] = kvp.Key;
            }

            foreach ( var entry in Entries )
                WriteNodeRecursive( entry.Node, buffer, objectToAddress );
        }

        private void WriteNodeRecursive( FileSystemNode node, Span<byte> buffer, Dictionary<object, int> objectToAddress )
        {
            var nodeAddress = objectToAddress[ node ];
            var nodeOffset = nodeAddress - Metadata.ElfBaseOffset;

#if DEBUG_ENSURE_MATCHING_OUTPUT
            var origRelativeDataOffset = BinaryPrimitives.ReadInt32LittleEndian( buffer.Slice( nodeOffset + 8 ) );
            var origDataLength = BinaryPrimitives.ReadInt32LittleEndian( buffer.Slice( nodeOffset + 12 ) );
            Debug.Assert( origRelativeDataOffset == node.RelativeDataOffset );
            Debug.Assert( origDataLength == node.DataLength );
#endif
            BinaryPrimitives.WriteInt32LittleEndian( buffer.Slice( nodeOffset + 8 ), node.RelativeDataOffset );
            BinaryPrimitives.WriteInt32LittleEndian( buffer.Slice( nodeOffset + 12 ), node.DataLength );

            if ( node.Parent is FileSystemNode parentNode )
            {
                // Write parent node values as well
                WriteNodeRecursive( parentNode, buffer, objectToAddress );
            }
        }

        public IEnumerable<FileSystemNode> EnumerateDirectories( string mgfName )
        {
            var processedDirectories = new HashSet<FileSystemNode>();
            foreach ( var node in mNodes.Where( x => x.Type == FileSystemNodeType.VirtualFile && x.Entry == null && x.RealFilePath.Contains( mgfName, StringComparison.OrdinalIgnoreCase ) ) )
            {
                if ( processedDirectories.Add( node ) )
                    yield return node;
            }
        }

        public struct DirectorySpan
        {
            public readonly FileSystemNode Directory;
            public readonly int StartOffset;
            public readonly int EndOffset;
            public DirectorySpan(FileSystemNode directory, int startOffset, int endOffset)
            {
                Directory = directory;
                StartOffset = startOffset;
                EndOffset = endOffset;
            }
        }

        public List<DirectorySpan> FindDirectorySpans( string mgfName, int offset, int size )
        {
            var dirs = new List<DirectorySpan>();
            var endOffset = offset + size;
            var searchStartOffset = offset;
            while ( searchStartOffset < endOffset )
            {
                var dir = FindFirstDirectory( mgfName, searchStartOffset, endOffset - searchStartOffset );
                if ( !dir.HasValue ) break;
                searchStartOffset = dir.Value.EndOffset;
                dirs.Add( dir.Value );
            }

            return dirs;
        }

        private DirectorySpan? FindFirstDirectory( string mgfName, int offset, int size )
        {
            var endOffset = offset + size;
            foreach ( var dir in EnumerateDirectories( mgfName ) )
            {
                var dirEndOffset = dir.DataOffset + dir.DataLength;
                if ( offset >= dir.DataOffset && offset < dirEndOffset )
                {
                    return new DirectorySpan(dir, offset, Math.Min( endOffset, dirEndOffset ));
                }
            }

            return null;
        }

        private FileSystemNode ReadFileNode( Span<byte> buffer, int address, Dictionary<int, object> objectByAddress, int baseOffset, FileTableEntry entry, string path )
        {
            if ( !objectByAddress.TryGetValue( address, out var obj ) )
            {
                var offset = address - baseOffset;
                ref var memNode = ref MemoryMarshal.AsRef<MemoryFileSystemNode>( buffer.Slice( offset ) );
                var node = new FileSystemNode( entry, path );
                node.Type = memNode.Type;
                mNodes.Add( node );

                if ( memNode.DataOffset == 0 && memNode.DataLength == 0 )
                {
                    node.Parent = ReadString( buffer, memNode.ParentAddress, objectByAddress, baseOffset );
                }
                else
                {
                    node.Parent = ReadFileNode( buffer, memNode.ParentAddress, objectByAddress, baseOffset, null,
                        node.Path != null ? Path.GetDirectoryName( node.Path ).Replace( "\\", "/" ) : null );
                }
                node.RelativeDataOffset = memNode.DataOffset;
                node.DataLength = memNode.DataLength;
                objectByAddress[ address ] = obj = node;
            }

            return (FileSystemNode)obj;
        }

        private static string ReadString( Span<byte> buffer, int address, Dictionary<int, object> objectByAddress, int baseOffset )
        {
            if ( !objectByAddress.TryGetValue( address, out var obj ) )
            {
                var offset = address - baseOffset;
                var slice = buffer.Slice( offset );
                var len = NativeStringHelper.GetLength( slice );
                obj = Encoding.ASCII.GetString( slice.Slice( 0, len ) );
                objectByAddress[ address ] = obj;
            }

            return (string)obj;
        }

        private static int FindFileTableOffset( Span<byte> buffer, int baseOffset )
        {
            var firstFileName = ScanHelper.FindPattern( buffer, Encoding.ASCII.GetBytes( "./null.dev" ) );
            if ( firstFileName == -1 )
                throw new InvalidDataException( "Can't find file table" );

            var firstFileNameAddr = firstFileName + baseOffset;
            var firstFileNameRef = ScanHelper.FindPattern( buffer, MemoryMarshal.Cast<int, byte>( MemoryMarshal.CreateSpan( ref firstFileNameAddr, 1 ) ) );
            return firstFileNameRef - 4;
        }

        private static int GetBaseOffsetFromElfFile( Span<byte> buffer )
        {
            fixed ( byte* p = buffer )
            {
                using var streamWrapper = new UnmanagedMemoryStream( p, buffer.Length );
                using var reader = new EndianBinaryReader( streamWrapper, Endianness.LittleEndian );
                var elf = ElfFile.ReadElfFile( reader );
                var firstExecSegment = elf.Segments.FirstOrDefault( x => x.Flags.HasFlag( ElfSegmentFlags.Exec ) );

                if ( firstExecSegment == null )
                {
                    return -1;
                }
                else
                {
                    return (int)( firstExecSegment.VirtualAddress - firstExecSegment.Offset );
                }
            }
        }
    }
}
