using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Classless.Hasher;

namespace DepotDownloader
{
    static class Util
    {
        [DllImport( "libc" )]
        static extern int uname( IntPtr buf );

        static int _isMacOSX = -1;

        // Environment.OSVersion.Platform returns PlatformID.Unix under Mono on OS X
        // Code adapted from Mono: mcs/class/Managed.Windows.Forms/System.Windows.Forms/XplatUI.cs
        public static bool IsMacOSX()
        {
            if ( _isMacOSX != -1 )
                return _isMacOSX == 1;

            IntPtr buf = IntPtr.Zero;

            try
            {
                // The size of the utsname struct varies from system to system, but this _seems_ more than enough
                buf = Marshal.AllocHGlobal( 4096 );

                if ( uname( buf ) == 0 )
                {
                    string sys = Marshal.PtrToStringAnsi( buf );
                    if ( sys == "Darwin" )
                    {
                        _isMacOSX = 1;
                        return true;
                    }
                }
            }
            catch
            {
                // Do nothing?
            }
            finally
            {
                if ( buf != IntPtr.Zero )
                    Marshal.FreeHGlobal( buf );
            }

            _isMacOSX = 0;
            return false;
        }

        public static string ReadPassword()
        {
            ConsoleKeyInfo keyInfo;
            StringBuilder password = new StringBuilder();

            do 
            {
                keyInfo = Console.ReadKey( true );

                if ( keyInfo.Key == ConsoleKey.Backspace )
                {
                    if ( password.Length > 0 )
                        password.Remove( password.Length - 1, 1 );
                    continue;
                }

                /* Printable ASCII characters only */
                char c = keyInfo.KeyChar;
                if ( c >= ' ' && c <= '~' )
                    password.Append( c );
            } while ( keyInfo.Key != ConsoleKey.Enter );

            return password.ToString();
        }

        // Validate a file against Steam2 Checksums
        public static bool ValidateFileChecksums( FileInfo file, int [] checksums )
        {
            byte[] chunk = new byte[0x8000]; // checksums are for 32KB at a time

            FileStream fs = file.OpenRead();
            int read, cnt=0;
            while ((read = fs.Read(chunk, 0, 0x8000)) > 0)
            {
                byte[] tempchunk;
                if(read < 0x8000)
                {
                    tempchunk = new byte[read];
                    Array.Copy(chunk, 0, tempchunk, 0, read);
                }
                else
                {
                    tempchunk = chunk;
                }
                int adler = BitConverter.ToInt32(AdlerHash(tempchunk), 0);
                int crc32 = BitConverter.ToInt32(CRCHash(tempchunk), 0);
                if((adler ^ crc32) != checksums[cnt])
                {
                    fs.Close();
                    return false;
                }
                ++cnt;   
            }
            fs.Close();
            return (cnt == checksums.Length);
        }

        // Generate Steam2 Checksums for a file
        public static int [] CalculateFileChecksums( FileInfo file )
        {
            byte[] chunk = new byte[0x8000]; // checksums are for 32KB at a time
            int [] checksums = new int[((file.Length-1)/0x8000)+1];
            FileStream fs = file.OpenRead();
            int read, cnt=0;
            while ((read = fs.Read(chunk, 0, 0x8000)) > 0)
            {
                byte[] tempchunk;
                if(read < 0x8000)
                {
                    tempchunk = new byte[read];
                    Array.Copy(chunk, 0, tempchunk, 0, read);
                }
                else
                {
                    tempchunk = chunk;
                }
                int adler = BitConverter.ToInt32(AdlerHash(tempchunk), 0);
                int crc32 = BitConverter.ToInt32(CRCHash(tempchunk), 0);
                checksums[cnt++] = adler ^ crc32;
            }
            fs.Close();
            return checksums;
        }

        public static byte[] CRCHash( byte[] input )
        {
            using ( Crc crc = new Crc( CrcParameters.GetParameters( CrcStandard.Crc32Bit ) ) )
            {
                byte[] hash = crc.ComputeHash( input );
                Array.Reverse( hash );

                return hash;
            }
        }

        public static byte[] AdlerHash(byte[] input)
        {
            uint a = 0, b = 0;
            for (int i = 0; i < input.Length; i++)
            {
                a = (a + input[i]) % 65521;
                b = (b + a) % 65521;
            }
            return BitConverter.GetBytes(a | (b << 16));
        }

        public static byte[] SHAHash( byte[] input )
        {
            SHA1Managed sha = new SHA1Managed();

            byte[] output = sha.ComputeHash( input );

            sha.Clear();

            return output;
        }
    }
}
