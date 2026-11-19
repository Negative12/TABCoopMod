using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;

namespace TABCoopMod.Core
{
    public static class ModLogger
    {
        private readonly struct LogEntry
        {
            public readonly string Line;
            public readonly bool Immediate;

            public LogEntry(string line, bool immediate)
            {
                Line = line;
                Immediate = immediate;
            }
        }

        private const int MaxArchivedLogs = 30;
        private const int FlushIntervalMs = 500;
        private const int BatchWakeThreshold = 64;

        private static readonly object _initLock = new object();
        private static readonly ConcurrentQueue<LogEntry> _queue = new ConcurrentQueue<LogEntry>();
        private static readonly AutoResetEvent _wakeEvent = new AutoResetEvent(false);

        private static volatile bool _initialized;
        private static volatile bool _stopping;
        private static int _queuedCount;
        private static Thread _writerThread;
        private static StreamWriter _mainWriter;
        private static StreamWriter _versionedWriter;

        private static string _logFilePath;
        private static string _versionedLogFilePath;

        public static bool IsVerbose { get; private set; }
        public static string LogFilePath => _logFilePath;
        public static string VersionedLogFilePath => _versionedLogFilePath;

        public static void Initialize(string customLogFileName = null)
        {
            lock (_initLock)
            {
                if (_initialized) return;

                try
                {
                    IsVerbose = string.Equals(Environment.GetEnvironmentVariable("TABCOOP_VERBOSE_LOG")?.Trim(), "1", StringComparison.Ordinal);

                    string modDir = Path.GetDirectoryName(typeof(ModLogger).Assembly.Location);
                    string baseDir = !string.IsNullOrEmpty(modDir) ? modDir : AppDomain.CurrentDomain.BaseDirectory;
                    // Allow multi-instance tests to give each process its own log file.
                    string envLogName = Environment.GetEnvironmentVariable("TABCOOP_LOG_NAME");
                    bool isCustomEnvLog = !string.IsNullOrWhiteSpace(envLogName);
                    string fileName = isCustomEnvLog
                        ? envLogName.Trim()
                        : (customLogFileName ?? "TABCoopMod.log");
                    _logFilePath = Path.Combine(baseDir, fileName);

                    int pid = Process.GetCurrentProcess().Id;
                    DateTime now = DateTime.Now;

                    if (!isCustomEnvLog && string.IsNullOrEmpty(customLogFileName))
                    {
                        string logsDir = Path.Combine(baseDir, "TABCoopMod_Logs");
                        Directory.CreateDirectory(logsDir);
                        _versionedLogFilePath = Path.Combine(
                            logsDir,
                            $"TABCoopMod_{ModInfo.Tag}_{now:yyyyMMdd_HHmmss}_p{pid}.log");
                        PruneOldArchivedLogs(logsDir);
                    }
                    else
                    {
                        _versionedLogFilePath = null;
                    }

                    string header =
                        $"================================================================\r\n" +
                        $" TABCoopMod {ModInfo.Tag} (Asm={ModInfo.AssemblyVersion}, Build={ModInfo.GetBuildTimestamp()})\r\n" +
                        $" Initialized at {now:yyyy-MM-dd HH:mm:ss.fff} (asyncWriter=True, verbose={IsVerbose})\r\n" +
                        $" Process ID: {pid} | 64-bit: {Environment.Is64BitProcess} | CLR: {Environment.Version}\r\n" +
                        $" OS: {Environment.OSVersion}\r\n" +
                        $" Base Directory: {baseDir}\r\n" +
                        (_versionedLogFilePath != null ? $" Versioned Log: {_versionedLogFilePath}\r\n" : "") +
                        $" Command Line: {Environment.CommandLine}\r\n" +
                        $"================================================================\r\n";

                    OpenWriters(header);
                    StartWriterThread();

                    AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
                    AppDomain.CurrentDomain.DomainUnload += OnProcessExit;
                    _initialized = true;
                }
                catch
                {
                    // Fallback to temp if game dir is not writable
                    _logFilePath = Path.Combine(Path.GetTempPath(), $"TABCoopMod_{ModInfo.Tag}.log");
                    _versionedLogFilePath = null;
                    try
                    {
                        OpenWriters(string.Empty);
                        StartWriterThread();
                    }
                    catch { }
                    _initialized = true;
                }
            }
        }

        private static void OpenWriters(string initialHeader)
        {
            var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            _mainWriter = new StreamWriter(
                new FileStream(_logFilePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite, 16384),
                utf8NoBom);
            if (!string.IsNullOrEmpty(initialHeader))
            {
                _mainWriter.Write(initialHeader);
                _mainWriter.Flush();
            }

            if (_versionedLogFilePath != null)
            {
                _versionedWriter = new StreamWriter(
                    new FileStream(_versionedLogFilePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite, 16384),
                    utf8NoBom);
                if (!string.IsNullOrEmpty(initialHeader))
                {
                    _versionedWriter.Write(initialHeader);
                    _versionedWriter.Flush();
                }
            }
        }

        private static void StartWriterThread()
        {
            if (_writerThread != null) return;
            _stopping = false;
            _writerThread = new Thread(WriterLoop)
            {
                IsBackground = true,
                Name = "TABCoopMod.Logger",
                Priority = ThreadPriority.BelowNormal
            };
            _writerThread.Start();
        }

        private static void WriterLoop()
        {
            while (true)
            {
                bool immediateFlush = DrainQueue();
                if (_stopping && _queue.IsEmpty)
                    break;

                // Regular INFO/WARN entries batch on a 500ms cadence; critical entries set _wakeEvent immediately.
                if (!immediateFlush && _queue.IsEmpty)
                    _wakeEvent.WaitOne(FlushIntervalMs);
            }

            DrainQueue();
            CloseWriters();
        }

        private static bool DrainQueue()
        {
            bool wroteAny = false;
            bool needImmediateFlush = false;

            while (_queue.TryDequeue(out LogEntry entry))
            {
                Interlocked.Decrement(ref _queuedCount);
                wroteAny = true;
                if (entry.Immediate) needImmediateFlush = true;

                try { _mainWriter?.Write(entry.Line); } catch { }
                try { _versionedWriter?.Write(entry.Line); } catch { }
            }

            if (wroteAny)
            {
                try { _mainWriter?.Flush(); } catch { }
                try { _versionedWriter?.Flush(); } catch { }
            }

            return needImmediateFlush;
        }

        private static void CloseWriters()
        {
            try { _mainWriter?.Flush(); _mainWriter?.Dispose(); } catch { }
            _mainWriter = null;
            try { _versionedWriter?.Flush(); _versionedWriter?.Dispose(); } catch { }
            _versionedWriter = null;
        }

        private static void OnProcessExit(object sender, EventArgs e)
        {
            ShutdownAndFlush(800);
        }

        public static void ShutdownAndFlush(int timeoutMs = 800)
        {
            if (!_initialized) return;
            _stopping = true;
            try { _wakeEvent.Set(); } catch { }
            try
            {
                if (_writerThread != null && _writerThread.IsAlive && Thread.CurrentThread != _writerThread)
                    _writerThread.Join(timeoutMs);
            }
            catch { }
        }

        private static void PruneOldArchivedLogs(string logsDir)
        {
            try
            {
                var files = new DirectoryInfo(logsDir).GetFiles("TABCoopMod_*.log");
                if (files.Length < MaxArchivedLogs) return;
                Array.Sort(files, (a, b) => a.CreationTimeUtc.CompareTo(b.CreationTimeUtc));
                int toDelete = files.Length - (MaxArchivedLogs - 1);
                for (int i = 0; i < toDelete; i++)
                {
                    try { files[i].Delete(); } catch { }
                }
            }
            catch { }
        }

        public static void LogGameVersionInfo(Assembly gameAssembly)
        {
            try
            {
                if (gameAssembly == null) return;
                string loc = gameAssembly.Location;
                string fileVer = (!string.IsNullOrEmpty(loc) && File.Exists(loc))
                    ? FileVersionInfo.GetVersionInfo(loc).FileVersion
                    : "unknown";
                Info($"[Environment] Mod={ModInfo.Tag} GameAsm={gameAssembly.GetName().Version} GameFileVer={fileVer}");
            }
            catch { }
        }

        public static void Verbose(string message)
        {
            if (IsVerbose) Write("INFO", message, immediate: false);
        }

        public static void Info(string message) => Write("INFO", message, immediate: false);
        public static void Warn(string message) => Write("WARN", message, immediate: false);
        public static void InfoImmediate(string message) => Write("INFO", message, immediate: true);
        public static void WarnImmediate(string message) => Write("WARN", message, immediate: true);

        public static void Error(string message, Exception ex = null)
        {
            if (ex != null)
                Write("ERROR", $"{message}\r\nException: {ex}", immediate: true);
            else
                Write("ERROR", message, immediate: true);
        }

        private static void Write(string level, string message, bool immediate)
        {
            try
            {
                if (!_initialized) Initialize();
                string line = $"[{DateTime.Now:HH:mm:ss.fff}] [{level}] [{ModInfo.Tag}] [T{Environment.CurrentManagedThreadId}] {message}\r\n";

                if (_stopping || _writerThread == null)
                {
                    try { File.AppendAllText(_logFilePath, line, Encoding.UTF8); } catch { }
                    return;
                }

                _queue.Enqueue(new LogEntry(line, immediate));
                int count = Interlocked.Increment(ref _queuedCount);
                if (immediate || count >= BatchWakeThreshold)
                {
                    _wakeEvent.Set();
                }
            }
            catch
            {
            }
        }
    }
}
