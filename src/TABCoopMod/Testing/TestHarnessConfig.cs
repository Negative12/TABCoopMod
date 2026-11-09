using System;
using System.IO;
using TABCoopMod.Core;

namespace TABCoopMod.Testing
{
    /// <summary>
    /// Reads test-harness switches from environment variables (set by the PowerShell launchers).
    /// Everything is opt-in; when disabled the mod behaves exactly as before.
    /// </summary>
    public static class TestHarnessConfig
    {
        public static bool Enabled { get; private set; }
        public static bool Initialized { get; private set; }

        // Level entry / replay
        public static string AutoLoadSave { get; private set; }
        public static string ReplaySave { get; private set; }
        public static int ReplayTicks { get; private set; }
        public static string ReplayOut { get; private set; }
        public static int ReplayRun { get; private set; }       // 0 = both passes in-process, 1/2 = single pass
        public static string TickGate { get; private set; }     // "always" | "manual"
        public static bool Intercept { get; private set; }
        public static string Scenario { get; private set; }     // "default" or path to a script file
        public static string Role { get; private set; }         // "host" | "client" | ""
        public static string ChaosSpec { get; private set; }
        public static bool PosDump { get; private set; }

        /// <summary>When > 0, the in-process MCP control server listens on 127.0.0.1:ControlPort.</summary>
        public static int ControlPort { get; private set; }

        /// <summary>Auto-save a PNG after every mutating control tool (default on; TABCOOP_AUTO_SHOT=0 disables).</summary>
        public static bool AutoShot { get; private set; }

        /// <summary>Directory screenshots are written to (TABCOOP_SHOT_DIR). Set by the launcher to the workspace folder.</summary>
        public static string ShotDir { get; private set; }

        /// <summary>True when the MCP control server is enabled (TABCOOP_CONTROL_PORT set).</summary>
        public static bool ControlEnabled => ControlPort > 0;

        public static string OutputDirectory { get; private set; }

        public static void Initialize()
        {
            if (Initialized) return;
            Initialized = true;

            Enabled = Env("TABCOOP_TEST") == "1";

            AutoLoadSave = Env("TABCOOP_AUTO_LOAD_SAVE");
            ReplaySave = Env("TABCOOP_REPLAY_SAVE");
            if (string.IsNullOrEmpty(ReplaySave)) ReplaySave = AutoLoadSave;

            ReplayTicks = ParseInt(Env("TABCOOP_REPLAY_TICKS"), 600);
            ReplayRun = ParseInt(Env("TABCOOP_REPLAY_RUN"), 0);
            TickGate = Env("TABCOOP_TICK_GATE") ?? "always";
            Intercept = Env("TABCOOP_INTERCEPT") == "1";
            Scenario = Env("TABCOOP_SCENARIO") ?? "default";
            Role = Env("TABCOOP_ROLE") ?? string.Empty;
            ChaosSpec = Env("TABCOOP_CHAOS");
            PosDump = Env("TABCOOP_POS_DUMP") == "1";
            ControlPort = ParseInt(Env("TABCOOP_CONTROL_PORT"), 0);
            AutoShot = Env("TABCOOP_AUTO_SHOT") != "0";
            ShotDir = Env("TABCOOP_SHOT_DIR");

            string logDir = null;
            try { logDir = Path.GetDirectoryName(ModLogger.LogFilePath); } catch { }
            OutputDirectory = !string.IsNullOrEmpty(logDir) ? logDir : AppDomain.CurrentDomain.BaseDirectory;

            ReplayOut = Env("TABCOOP_REPLAY_OUT");
            if (string.IsNullOrEmpty(ReplayOut))
                ReplayOut = Path.Combine(OutputDirectory, "coop_replay_run" + (ReplayRun > 0 ? ReplayRun.ToString() : "0") + ".timeline");

            if (Enabled)
            {
                ModLogger.Info($"[TestHarness] enabled: replaySave={ReplaySave ?? "(none)"}, ticks={ReplayTicks}, run={ReplayRun}, gate={TickGate}, intercept={Intercept}, scenario={Scenario}, role={Role}, chaos={ChaosSpec ?? "(none)"}");
            }
        }

        public static bool HasChaos => !string.IsNullOrEmpty(ChaosSpec);

        private static string Env(string name)
        {
            string v = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        }

        private static int ParseInt(string s, int fallback)
        {
            return int.TryParse(s, out int v) ? v : fallback;
        }
    }
}
