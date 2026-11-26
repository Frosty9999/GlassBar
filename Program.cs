using System;

namespace GlassBar
{
    internal static class Program
    {
        /// <summary>
        /// Custom entry point, so the crash watchdog copy ("--watchdog &lt;pid&gt;") can run without starting WPF.
        /// </summary>
        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length == 2 && args[0] == "--watchdog" && int.TryParse(args[1], out int pid))
            {
                return CrashGuard.RunWatchdog(pid);
            }

            App app = new App();
            app.InitializeComponent();
            return app.Run();
        }
    }
}
