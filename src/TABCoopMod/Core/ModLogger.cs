using System;
using System.IO;
using System.Text;

namespace TABCoopMod.Core
{
    public static class ModLogger
    {
        private static readonly object _lock = new object();
        private static string _logFilePath;

        public static string LogFilePath => _logFilePath;

        public static void Initialize(string customLogFileName = null)
        {
            try
            {
                string modDir = Path.GetDirectoryName(typeof(ModLogger).Assembly.Location);
                string baseDir = !string.IsNullOrEmpty(modDir) ? modDir : AppDomain.CurrentDomain.BaseDirectory;
                string fileName = customLogFileName ?? "TABCoopMod.log";
                _logFilePath = Path.Combine(baseDir, fileName);

                lock (_lock)
                {
                    File.WriteAllText(_logFilePath,
                        $"================================================================\r\n" +
                        $" TABCoopMod v0.1.0 Initialized at {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}\r\n" +
                        $" Process ID: {System.Diagnostics.Process.GetCurrentProcess().Id}\r\n" +
                        $" Base Directory: {baseDir}\r\n" +
                        $" Command Line: {Environment.CommandLine}\r\n" +
                        $"================================================================\r\n",
                        Encoding.UTF8);
                }
            }
            catch
            {
                // Fallback to temp if game dir is not writable
                _logFilePath = Path.Combine(Path.GetTempPath(), "TABCoopMod.log");
            }
        }

        public static void Info(string message) => Write("INFO", message);
        public static void Warn(string message) => Write("WARN", message);
        public static void Error(string message, Exception ex = null)
        {
            if (ex != null)
                Write("ERROR", $"{message}\r\nException: {ex}");
            else
                Write("ERROR", message);
        }

        private static void Write(string level, string message)
        {
            try
            {
                if (_logFilePath == null) Initialize();
                string line = $"[{DateTime.Now:HH:mm:ss.fff}] [{level}] [T{Environment.CurrentManagedThreadId}] {message}\r\n";
                lock (_lock)
                {
                    File.AppendAllText(_logFilePath, line, Encoding.UTF8);
                }
            }
            catch
            {
            }
        }
    }
}
