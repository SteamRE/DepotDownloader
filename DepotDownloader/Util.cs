using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Runtime.InteropServices;

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
    }
}
