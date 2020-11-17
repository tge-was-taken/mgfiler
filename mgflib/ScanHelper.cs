using System;

namespace mgflib
{
    public static class ScanHelper
    {
        public static int FindPattern( Span<byte> buffer, Span<byte> pattern )
        {
            var bufferIdx = 0;
            while ( ( bufferIdx + 4 ) < buffer.Length )
            {
                var matches = true;
                for ( int i = 0; i < pattern.Length; i++ )
                {
                    if ( buffer[ bufferIdx + i ] != pattern[ i ] )
                    {
                        matches = false;
                        break;
                    }
                }

                if ( matches )
                    return bufferIdx;

                bufferIdx += 4;
            }

            return -1;
        }
    }
}
