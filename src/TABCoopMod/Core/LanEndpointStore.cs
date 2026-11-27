using System;
using System.IO;
using System.Text;

namespace TABCoopMod.Core
{
    /// <summary>
    /// Small persistent store for the last manually entered LAN endpoint.
    /// Stored next to the game's save files so it survives restarts without registry changes.
    /// </summary>
    public static class LanEndpointStore
    {
        private static bool _loaded;
        private static string _lastJoinAddress = string.Empty;
        private static int _lastHostPort;

        public static string LastJoinAddress
        {
            get { EnsureLoaded(); return _lastJoinAddress; }
        }

        public static int LastHostPort
        {
            get { EnsureLoaded(); return _lastHostPort; }
        }

        public static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;

            try
            {
                string path = GetFilePath();
                if (!File.Exists(path)) return;

                foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
                {
                    string line = (raw ?? string.Empty).Trim();
                    if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                        continue;

                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;

                    string key = line.Substring(0, eq).Trim();
                    string value = line.Substring(eq + 1).Trim();
                    if (key.Equals("last_join", StringComparison.OrdinalIgnoreCase))
                    {
                        _lastJoinAddress = value;
                    }
                    else if (key.Equals("last_host_port", StringComparison.OrdinalIgnoreCase))
                    {
                        if (int.TryParse(value, out int port) && port > 0 && port <= 65535)
                            _lastHostPort = port;
                    }
                }
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[LanEndpointStore] Load failed: {ex.Message}");
            }
        }

        public static void SaveLastJoin(string address)
        {
            EnsureLoaded();
            string value = (address ?? string.Empty).Trim();
            if (string.Equals(_lastJoinAddress, value, StringComparison.Ordinal))
                return;
            _lastJoinAddress = value;
            Save();
        }

        public static void SaveLastHostPort(int port)
        {
            EnsureLoaded();
            if (port <= 0 || port > 65535 || _lastHostPort == port)
                return;
            _lastHostPort = port;
            Save();
        }

        private static void Save()
        {
            try
            {
                string path = GetFilePath();
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                var sb = new StringBuilder();
                sb.AppendLine("# TABCoopMod LAN endpoint cache");
                sb.AppendLine("last_join=" + (_lastJoinAddress ?? string.Empty));
                sb.AppendLine("last_host_port=" + (_lastHostPort > 0 ? _lastHostPort.ToString() : string.Empty));
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[LanEndpointStore] Save failed: {ex.Message}");
            }
        }

        private static string GetFilePath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "My Games",
                "They Are Billions",
                "TABCoopMod_LAN.txt");
        }
    }
}
