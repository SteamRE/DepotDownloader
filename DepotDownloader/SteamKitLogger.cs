using SteamKit2;
using System;

public class SteamKitLogger : IDebugListener
{
    public void WriteLine(string category, string msg)
    {
        Console.WriteLine("[{0}] {1}", category, msg);
    }
}

public interface ILog
{
}
