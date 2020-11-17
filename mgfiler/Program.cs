using CommandLine;
using mgflib;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace mgfiler
{
    class Program
    {
        private const string UNLISTED_FILE_TAG = "__unlisted_";

        [Verb("unpack", HelpText = "Unpacks files from an MGF file.")]
        class UnpackOptions
        {
            [Option("elf", HelpText = "Path to the ELF executable file (starts with SLPS, SLES, SLUS)", Required = true)]
            public string ElfPath { get; set; }

            [Option("mgf", HelpText = "Path to the MGF file", Required = true )]
            public string MgfPath { get; set; }

            [Option('o', "out", HelpText = "Path to the output directory", Required = false)]
            public string OutPath { get; set; }

            [Option("mgf-name", HelpText = "Name of the original MGF file.", Required = false)]
            public string MgfName { get; set; }
        }

        [Verb("pack", HelpText = "Packs files into an MGF file and patches the ELF executable.")]
        class PackOptions
        {
            [Option( "elf", HelpText = "Path to the ELF executable file (starts with SLPS, SLES, SLUS)", Required = true )]
            public string ElfPath { get; set; }

            [Option( "dir", HelpText = "Path to the directory containing the files to pack.", Required = true )]
            public string DirectoryPath { get; set; }

            [Option("mgf", HelpText = "Name of the original MGF file.", Required = true)]
            public string MgfName { get; set; }

            [Option( "out-elf", HelpText = "Path to the patched ELF executable file.", Required = true )]
            public string OutElfPath { get; set; }

            [Option("out-mgf", HelpText = "Path to the newly packed MGF file.", Required = true)]
            public string OutMgfPath { get; set; }
        }

        [Verb("debug", Hidden = true)]
        class DebugOptions
        {
            [Option( "elf", HelpText = "Path to the ELF executable file (starts with SLPS, SLES, SLUS)", Required = true )]
            public string ElfPath { get; set; }

            [Option( "mgf", HelpText = "Path to the MGF file", Required = true )]
            public string MgfPath { get; set; }

            [Option( 'o', "out", HelpText = "Path to the output directory", Required = true )]
            public string OutPath { get; set; }
        }

        static int Main( string[] args )
        {
            var result = Parser.Default.ParseArguments<UnpackOptions, PackOptions, DebugOptions>( args )
                .MapResult(
                    ( UnpackOptions options ) => UnpackMgf( options ),
                    ( PackOptions options ) => PackMgf( options ),
                    ( DebugOptions options ) => DebugMgf( options ),
                    errors => 2 );
            if ( result == 2 )
            {
                Console.WriteLine( "Press any key to exit." );
                Console.ReadKey();
            }

            return result;
        }

        static int UnpackMgf( UnpackOptions options )
        {
            if ( options.OutPath == null )
                options.OutPath = Path.ChangeExtension( options.MgfPath, null );

            var mgfFileName = options.MgfName ?? Path.GetFileName( options.MgfPath );
            using var mgfFileStream = File.OpenRead( options.MgfPath );

            var fileTable = new FileTable();
            fileTable.Read( File.ReadAllBytes( options.ElfPath ) );

            var fileIdx = 0;
            foreach ( var entry in fileTable.Entries
                .Where( x => x.Node.Type != FileSystemNodeType.RealArchiveFile && 
                             x.Node.RealFilePath.Contains( mgfFileName, StringComparison.OrdinalIgnoreCase ))
                .OrderBy( x => x.Node.DataOffset ) )
            {
                var parentNode = (FileSystemNode)entry.Node.Parent;
                var node = entry.Node;
                var dataOffset = node.DataOffset;
                string filePath;

                if ( mgfFileStream.Position < dataOffset )
                {
                    void UnpackGap( int gapOffset, int gapSize )
                    {
                        var gapBuf = new byte[ gapSize ];
                        lock ( mgfFileStream )
                        {
                            mgfFileStream.Read( gapBuf );
                        }

                        var dirSpans = fileTable.FindDirectorySpans( mgfFileName, gapOffset, gapSize );
                        foreach ( var dirSpan in dirSpans )
                        {
                            filePath = Path.Combine( options.OutPath, $"{dirSpan.Directory.Path}/__unlisted_{gapOffset:X8}.bin" );
                            Console.WriteLine( $"Extracting {filePath}" );
                            Directory.CreateDirectory( Path.GetDirectoryName( filePath ) );
                            File.WriteAllBytes( filePath, gapBuf.AsSpan( dirSpan.StartOffset - gapOffset, dirSpan.EndOffset - dirSpan.StartOffset ).ToArray() );
                            mgfFileStream.Position = AlignmentHelper.Align( mgfFileStream.Position, 2048 );
                        }
                    }

                    // Unlisted file(s)
                    var gapOffset = (int)mgfFileStream.Position;
                    var gapTotalSize = dataOffset - gapOffset;
                    UnpackGap( gapOffset, gapTotalSize );
                }

                Console.WriteLine( $"Extracting {entry.Path}" );
                var fileBuf = new byte[ node.DataLength ];
                lock ( mgfFileStream )
                {
                    Debug.Assert( mgfFileStream.Position == node.DataOffset );
                    mgfFileStream.Read( fileBuf );
                }

                filePath = Path.Combine( options.OutPath, entry.Path );
                var dirPath = Path.GetDirectoryName( filePath );
                Directory.CreateDirectory( dirPath );
                File.WriteAllBytes( filePath, fileBuf );
                mgfFileStream.Position = AlignmentHelper.Align( mgfFileStream.Position, 2048 );
                fileIdx++;
            }

            return 0;
        }

        interface IFsPackNode
        {
            public DirPackNode Parent { get; set; }
            public FileSystemNode Node { get; }
            public int? OriginalDataOffset { get; set; }
        }

        class DirPackNode : IFsPackNode
        {
            public DirPackNode Parent { get; set; }
            public string SourcePath { get; set; }
            public FileSystemNode Node { get; set; }
            public int? OriginalDataOffset { get; set; }
            public int StartOffset { get; set; }


            public override string ToString() => SourcePath;

            public DirPackNode()
            {
            }
        }

        class FilePackNode : IFsPackNode
        {
            public DirPackNode Parent { get; set; }
            public string SourcePath { get; set; }
            public FileTableEntry Entry { get; set; }
            public int? OriginalDataOffset { get; set; }
            public FileSystemNode Node => Entry?.Node;

            public override string ToString() => SourcePath;
        }

        class DirPackTerminatorNode : IFsPackNode
        {
            public DirPackNode Parent { get; set; }

            public FileSystemNode Node { get; set; }

            public int? OriginalDataOffset { get; set; }
        }

        static void CollectPackInfoFromDirectory( List<IFsPackNode> newFileTable, FileTable fileTable, string rootDirectoryPath, string path, DirPackNode parent )
        {
            var dirRelPath = rootDirectoryPath != path ?
                path.Substring( rootDirectoryPath.Length + 1 ).Replace( "\\", "/" ) :
                string.Empty;

            // Check if dir maps to anything
            var dirNode = fileTable.Entries.Where( x => x.Node.Type == FileSystemNodeType.VirtualFile && ( (FileSystemNode)x.Node.Parent ).Type == FileSystemNodeType.VirtualFile )
                .Select( x => (FileSystemNode)x.Node.Parent )
                .Where( x => x.Path.Equals( dirRelPath, StringComparison.OrdinalIgnoreCase ) )
                .FirstOrDefault();

            // Don't create pack node if the dir isn't listed
            DirPackNode dirInfo = null;
            if ( dirNode != null )
            {
                dirInfo = new DirPackNode();
                dirInfo.Parent = parent;
                dirInfo.SourcePath = path;
                dirInfo.Node = dirNode;
                newFileTable.Add( dirInfo );
            }

            foreach ( var file in Directory.EnumerateFiles( path, "*", SearchOption.TopDirectoryOnly ) )
            {
                var fileRelPath = file.Substring( rootDirectoryPath.Length + 1 ).Replace( "\\", "/" );
                var fileInfo = new FilePackNode();
                fileInfo.SourcePath = file;
                fileInfo.Entry = fileTable.Entries.Where( x => x.Path.Equals( fileRelPath, StringComparison.OrdinalIgnoreCase ) ).FirstOrDefault();
                fileInfo.Parent = dirInfo;
                if ( fileInfo.Entry == null )
                {
                    var fileName = Path.GetFileNameWithoutExtension( file );

                    // Check if it's an unlisted file
                    if ( fileName.StartsWith( UNLISTED_FILE_TAG ) )
                    {
                        // Get original data offset, which we'll use to sort the list
                        fileInfo.OriginalDataOffset = int.Parse( fileName.Substring( UNLISTED_FILE_TAG.Length ), System.Globalization.NumberStyles.HexNumber );
                    }
                    else
                    {
                        Console.WriteLine( $"File {file} skipped because it does not exist in the file list." );
                        continue;
                    }
                }

                newFileTable.Add( fileInfo );
            }

            foreach ( var subDir in Directory.EnumerateDirectories( path, "*", SearchOption.TopDirectoryOnly ) )
            {
                CollectPackInfoFromDirectory( newFileTable, fileTable, rootDirectoryPath, subDir, dirInfo );
            }

            if ( dirNode != null )
                newFileTable.Add( new DirPackTerminatorNode() { Parent = dirInfo, OriginalDataOffset = newFileTable.Max( x => x.OriginalDataOffset ) + 1 } );
        }

        static int PackMgf( PackOptions options )
        {
            // Load file table
            Console.WriteLine( "Loading file table" );
            var elfBuffer = File.ReadAllBytes( options.ElfPath );
            var fileTable = new FileTable();
            fileTable.Read( elfBuffer );

            // Add extension in case it is missing
            options.MgfName = Path.ChangeExtension( options.MgfName, "mgf" );

            // Gather file replacements
            var newFileTable = new List<IFsPackNode>();
            CollectPackInfoFromDirectory( newFileTable, fileTable, options.DirectoryPath, options.DirectoryPath, null );

            newFileTable.Sort( ( a, b ) =>
            {
                if ( a is DirPackTerminatorNode && b.Parent == a.Parent )
                    return 1;
                else if ( b is DirPackTerminatorNode && a.Parent == b.Parent )
                    return -1;

                var aOff = ( a.Node?.DataOffset ?? a.OriginalDataOffset ) ?? 1;
                var bOff = ( b.Node?.DataOffset ?? b.OriginalDataOffset ) ?? 1;
                if ( aOff == bOff )
                {
                    if ( a.Parent == b )
                        return 1;
                    else if ( b.Parent == a )
                        return -1;
                }

                return aOff.CompareTo( bOff );
            } );

            Console.WriteLine( $"Packing directory {options.DirectoryPath} into {options.OutMgfPath}" );
            using var mgfStream = File.Create( options.OutMgfPath );
            var dirStack = new Stack<DirPackNode>();

            //// HACK fix unlisted files coming before their directory
            var relocations = new List<(int From, int To)>();
            for ( int i = 0; i < newFileTable.Count; i++ )
            {
                if ( newFileTable[ i ].OriginalDataOffset != null )
                {
                    var parentIndex = newFileTable.FindIndex( x => x == newFileTable[ i ].Parent );
                    if ( parentIndex > i )
                        relocations.Add( (i, parentIndex + 1) );
                }
            }

            for ( int i = 0; i < relocations.Count; i++ )
            {
                var val = newFileTable[ relocations[ i ].From ];
                newFileTable.Insert( relocations[ i ].To, val );
                newFileTable.RemoveAt( relocations[ i ].From );
            }

            for ( int i = 0; i < newFileTable.Count; i++ )
            {
                var entry = newFileTable[ i ];
                var dirStartOffset = dirStack.Count > 0 ? dirStack.Peek()?.StartOffset ?? 0 : 0;
                var mgfAlignedPos = AlignmentHelper.Align( mgfStream.Position, 2048 );

                if ( entry is DirPackNode dir && dir.Node != null )
                {
                    // Directory is in file table
                    var newRelativeDataOffset = (int)mgfAlignedPos - dirStartOffset;

#if DEBUG_ENSURE_MATCHING_OUTPUT
                    //TODO investigate this
                    Debug.Assert( dir.Node.RelativeDataOffset == newRelativeDataOffset );
#endif

                    dir.Node.RelativeDataOffset = newRelativeDataOffset;
                    dir.StartOffset = (int)mgfAlignedPos;
                    dirStack.Push( dir );
                }
                else if ( entry is DirPackTerminatorNode terminator && terminator.Parent.Node != null )
                {
                    // Pop current directory off the stack
                    var curDir = dirStack.Pop();
                    var newDataLength = (int)mgfAlignedPos - curDir.StartOffset;
                    Console.WriteLine( $"Packed dir   {curDir.Node.Path.PadRight(64)} pos: 0x{curDir.StartOffset:X8} oldpos: 0x{curDir.Node.DataOffset:X8} size: 0x{newDataLength:X8} oldsize: 0x{curDir.Node.DataLength:X8}" );
#if DEBUG_ENSURE_MATCHING_OUTPUT
                    Debug.Assert( curDir.Node.DataLength == newDataLength );
#endif
                    curDir.Node.DataLength = newDataLength;
                }
                else if ( entry is FilePackNode file )
                {
                    using var fileStream = File.OpenRead( file.SourcePath );

                    if ( file.Entry != null )
                        Console.WriteLine( $"Packing file {file.SourcePath.PadRight( 64 )} pos: 0x{mgfAlignedPos:X8} oldpos: 0x{file.Entry.Node.DataOffset:X8} size: 0x{fileStream.Length:X8} oldsize: 0x{file.Entry.Node.DataLength:X8} path: {file.Entry.Path}" );
                    else
                        Console.WriteLine( $"Packing file {file.SourcePath.PadRight( 64 )} pos: 0x{mgfAlignedPos:X8} oldpos: 0x{file.OriginalDataOffset:X8} size: 0x{fileStream.Length:X8}" );

                    if ( file.Entry != null )
                    {
                        var newRelativeDataOffset = (int)mgfAlignedPos - dirStartOffset;
                        var newDataLength = (int)fileStream.Length;

#if DEBUG_ENSURE_MATCHING_OUTPUT
                        Debug.Assert( file.Entry.Node.RelativeDataOffset == newRelativeDataOffset );
                        Debug.Assert( file.Entry.Node.DataLength == newDataLength );
#endif

                        file.Entry.Node.RelativeDataOffset = newRelativeDataOffset;
                        file.Entry.Node.DataLength = newDataLength;
                    }

                    mgfStream.Position = mgfAlignedPos;
                    fileStream.CopyTo( mgfStream );

                    mgfAlignedPos = AlignmentHelper.Align( mgfStream.Position, 2048 );
                    while ( mgfStream.Position < mgfAlignedPos )
                        mgfStream.WriteByte( (byte)0 );
                }
                else
                {
                    throw new InvalidOperationException( "Unknown entry type" );
                }
            }

            // Write patched executable
            fileTable.Write( elfBuffer );
            File.WriteAllBytes( options.OutElfPath, elfBuffer );
            return 0;
        }

        static int DebugMgf( DebugOptions options )
        {
            var colors = new uint[] { 0x1BE7FFFF, 0x6EEB83FF, 0xE4FF1AFF, 0xE8AA14FF, 0xFF5714FF };
            var mgfFileName = Path.GetFileName( options.MgfPath );
            var mgfFileBaseName = Path.GetFileNameWithoutExtension( mgfFileName );

            var elfBuffer = File.ReadAllBytes( options.ElfPath );
            var fileTable = new FileTable();
            fileTable.Read( elfBuffer );

            var entries = new List<FileTableEntry>();
            for ( var i = 0; i < fileTable.Entries.Count; i++ )
            {
                var entry = (FileTableEntry)fileTable.Entries[ i ];
                var rootPath = entry.Node.RealFilePath;
                if ( entry.Node.Type == FileSystemNodeType.RealArchiveFile || !rootPath.Contains( mgfFileName, StringComparison.OrdinalIgnoreCase ) )
                {
                    continue;
                }

                entries.Add( entry );
            }


            using var writer = File.CreateText( options.OutPath );
            writer.WriteLine( 
@"int Align( int value, int alignment )
{
	return (value + (alignment - 1)) & ~(alignment - 1);
}

void FAlign( int alignment )
{
	FSeek( Align( FTell(), alignment ) );
}" );

            long curPos = 0;
            int gapCounter = 0;
            var counter = 0;

            foreach ( var entry in entries.OrderBy( x => x.Node.DataOffset ) )
            {
                var parentNode = (FileSystemNode)entry.Node.Parent;
                var node = entry.Node;
                var safeName = $"file_{entry.Path.Replace( "/", "_" ).Replace( ".", "_" )}";
                var dataOffset = node.DataOffset;

                if ( curPos < dataOffset )
                {
                    var gapEntry = fileTable.Entries.Where( x => x.Node.DataOffset == curPos );
                    if ( curPos != 0 && gapEntry.Count() > 0 )
                    {
                        Debugger.Break();
                    }

                    writer.WriteLine( $"SetBackColor( 0x0000FF );" );
                    writer.WriteLine( $"byte Gap{gapCounter++}[0x{dataOffset - curPos:X8}];" );
                    writer.WriteLine();
                    curPos = dataOffset;
                }

                writer.WriteLine( $"// {entry.Path}" );
                writer.WriteLine( $"SetBackColor( 0x{colors[ counter++ % colors.Length ]:X8} );" );
                writer.WriteLine( $"byte {safeName}[ 0x{node.DataLength:X8} ];" );
                writer.WriteLine( "FAlign( 2048 );" );
                writer.WriteLine();
                curPos = AlignmentHelper.Align( curPos + node.DataLength, 2048 );
            }

            return 0;
        }
    }
}
