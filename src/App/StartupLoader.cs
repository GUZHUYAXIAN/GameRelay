using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;

namespace GameRelay.App
{
    internal static class StartupLoader
    {
        // This type must not reference Core: the CLR may bind it before entering a caller's try block.
        public static void LoadCore()
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GameRelay.Core.dll");
            var watch = Stopwatch.StartNew();
            while (true)
            {
                try
                {
                    // Hold read access through binding so an exclusive writer cannot race the preflight.
                    using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                        Assembly.LoadFrom(path);
                    return;
                }
                catch (IOException error)
                {
                    int code = error.HResult & 0xffff;
                    if ((code != 32 && code != 33) || watch.ElapsedMilliseconds >= 5000) throw;
                    Thread.Sleep(250);
                }
            }
        }
    }
}
