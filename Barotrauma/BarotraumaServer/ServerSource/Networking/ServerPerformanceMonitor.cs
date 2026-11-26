using Barotrauma.Networking;
using System;
using System.Diagnostics;
using System.IO;
using System.Net;

namespace Barotrauma
{
    internal sealed class ServerPerformanceMonitor : IDisposable
    {
        private readonly ServerPerformanceHttpServer http;
        public ServerPerformanceSnapshot LatestSnapshot { get; private set; }
        private readonly Process process = Process.GetCurrentProcess();
        private readonly long startedAt = Stopwatch.GetTimestamp();
        private long previousSample;
        private TimeSpan? previousCpu;
        private double initialUptime;

        private ServerPerformanceMonitor(ServerPerformanceHttpServer http)
        {
            this.http = http;
            previousSample = startedAt;
            try
            {
                previousCpu = process.TotalProcessorTime;
                initialUptime = Math.Max(0, (DateTime.UtcNow - process.StartTime.ToUniversalTime()).TotalSeconds);
            }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception || error is InvalidOperationException || error is NotSupportedException)
            {
            }
        }

        public static ServerPerformanceMonitor Start(string configurationPath = null)
        {
            string path = configurationPath ?? Path.Combine(AppContext.BaseDirectory, PerformanceHttpSettings.FileName);
            string prefix = "";
            ServerPerformanceHttpServer http = null;
            try
            {
                var settings = PerformanceHttpSettings.LoadOrCreate(path);
                if (!settings.TryGetPrefix(out prefix, out string error))
                {
                    DebugConsole.AddWarning("Performance HTTP disabled: " + error);
                    return new ServerPerformanceMonitor(null);
                }
                bool enabled = settings.Enabled;
                if (enabled && settings.EnsureToken())
                {
                    settings.SaveFile(path);
                }
                http = new ServerPerformanceHttpServer(prefix, settings.Token,
                    tokenEnabled: settings.TokenEnabled, enableManagement: settings.EnableManagement,
                    recordingDirectory: Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)), ServerPerformanceRecording.DirectoryName),
                    serverName: GameMain.Server?.ServerSettings.ServerName, enabled: enabled);
                var monitor = new ServerPerformanceMonitor(http);
                if (!enabled)
                {
                    DebugConsole.NewMessage($"Performance HTTP rejecting requests at {prefix} because the panel is disabled.");
                    return monitor;
                }
                string tokenMessage = settings.TokenEnabled ? $" Access token is in {PerformanceHttpSettings.FileName}." : "";
                DebugConsole.NewMessage($"Performance HTTP listening at {prefix}.{tokenMessage}");
                return monitor;
            }
            catch (HttpListenerException error) when (OperatingSystem.IsWindows() && error.NativeErrorCode == 5)
            {
                http?.Dispose();
                DebugConsole.AddWarning($"Performance HTTP disabled: access denied. Reserve the URL for the server account from an elevated terminal: netsh http add urlacl url={prefix} user=<server-account>");
            }
            catch (Exception error)
            {
                http?.Dispose();
                // XML parse errors may contain credentials: do not print exception contents.
                DebugConsole.AddWarning($"Performance HTTP disabled ({error.GetType().Name}). Check {PerformanceHttpSettings.FileName}, the HttpContent directory, file permissions and whether its port is already in use.");
            }
            return new ServerPerformanceMonitor(null);
        }

        public void ProcessManagementRequests() => http?.ProcessManagementRequests();

        // Called by the game thread independently of the game's resettable frame stopwatch.
        public void PublishIfDue()
        {
            long now = Stopwatch.GetTimestamp();
            double seconds = (now - previousSample) / (double)Stopwatch.Frequency;
            if (seconds < 1)
            {
                return;
            }
            previousSample = now;
            double? cpuCores = null;
            long? workingSet = null;
            try
            {
                process.Refresh();
                TimeSpan cpu = process.TotalProcessorTime;
                if (previousCpu.HasValue)
                {
                    cpuCores = Math.Max(0, (cpu - previousCpu.Value).TotalSeconds / seconds);
                }
                previousCpu = cpu;
                workingSet = process.WorkingSet64;
            }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception || error is InvalidOperationException || error is NotSupportedException)
            {
                previousCpu = null;
            }
            var window = GameMain.PerformanceCounter.Drain(seconds);
            var server = GameMain.Server;
            var session = GameMain.GameSession;
            LatestSnapshot = new ServerPerformanceSnapshot(
                DateTimeOffset.UtcNow,
                initialUptime + (now - startedAt) / (double)Stopwatch.Frequency,
                server?.ServerSettings.ServerName ?? "",
                server?.ConnectedClients.Count ?? 0,
                server?.GameStarted ?? false,
                session?.GameMode?.Paused ?? false,
                server?.GameStarted == true ? session?.RoundDuration ?? 0 : 0,
                Timing.FixedUpdateRate,
                server?.ServerSettings.TickRate ?? 0,
                window.UpdatesPerSecond,
                cpuCores,
                cpuCores * 100 / Environment.ProcessorCount,
                workingSet,
                GC.GetTotalMemory(false),
                new[] { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) },
                window.Modules,
                RoundStatus: server?.RoundStatus ?? "loading");
            http?.Publish(LatestSnapshot);
        }

        public void Dispose()
        {
            http?.Dispose();
            process.Dispose();
        }
    }
}
