using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Barotrauma
{
    internal enum ServerPerformanceModule
    {
        Update,
        GameSession,
        Level,
        Characters,
        StatusEffects,
        MapEntities,
        AnimationAndRagdolls,
        Submarines,
        Physics,
        Networking
    }

    internal sealed record ServerModuleTiming(string Module, long Samples, double AverageMilliseconds, double MaxMilliseconds);
    internal sealed record ServerRollingModuleTiming(double SpikeMilliseconds, double MeanMilliseconds);
    internal sealed record ServerPerformanceWindow(double UpdatesPerSecond, ServerModuleTiming[] Modules);

    internal sealed record ServerPerformanceSnapshot(
        DateTimeOffset SampledAtUtc,
        double UptimeSeconds,
        string ServerName,
        int OnlinePlayers,
        bool RoundStarted,
        bool Paused,
        double RoundSeconds,
        int TargetUpdateRate,
        int NetworkTickRate,
        double UpdatesPerSecond,
        double? CpuCores,
        double? CpuPercent,
        long? WorkingSetBytes,
        long ManagedHeapBytes,
        int[] GcCollections,
        ServerModuleTiming[] Modules,
        double? Spike5mMilliseconds = null,
        double? Mean5mMilliseconds = null,
        IReadOnlyDictionary<string, ServerRollingModuleTiming> ModuleTimings5m = null,
        string RoundStatus = null);

    // Written and drained only by the game thread. No per-character probes or locks.
    internal sealed class ServerPerformanceMetrics
    {
        private readonly long[] totals = new long[Enum.GetValues<ServerPerformanceModule>().Length];
        private readonly long[] maxima = new long[Enum.GetValues<ServerPerformanceModule>().Length];
        private readonly long[] counts = new long[Enum.GetValues<ServerPerformanceModule>().Length];

        public readonly struct Measurement : IDisposable
        {
            private readonly ServerPerformanceMetrics owner;
            private readonly ServerPerformanceModule module;
            private readonly long start;

            internal Measurement(ServerPerformanceMetrics owner, ServerPerformanceModule module)
            {
                this.owner = owner;
                this.module = module;
                start = Stopwatch.GetTimestamp();
            }

            public void Dispose() => owner?.Record(module, Math.Max(0, Stopwatch.GetTimestamp() - start));
        }

        public Measurement Measure(ServerPerformanceModule module) => new Measurement(this, module);

        // Same entry point as the client's PerformanceCounter, used by shared update code.
        public void AddElapsedTicks(string identifier, long elapsedTicks)
        {
            ServerPerformanceModule? module = identifier switch
            {
                "Update:GameSession" => ServerPerformanceModule.GameSession,
                "Update:Level" => ServerPerformanceModule.Level,
                "Update:Character" => ServerPerformanceModule.Characters,
                "Update:StatusEffects" => ServerPerformanceModule.StatusEffects,
                "Update:MapEntity" => ServerPerformanceModule.MapEntities,
                "Update:Ragdolls" => ServerPerformanceModule.AnimationAndRagdolls,
                "Update:Submarine" => ServerPerformanceModule.Submarines,
                "Update:Physics" => ServerPerformanceModule.Physics,
                _ => null
            };
            if (module.HasValue)
            {
                Record(module.Value, elapsedTicks);
            }
        }

        public void Record(ServerPerformanceModule module, long elapsedTicks)
        {
            int index = (int)module;
            elapsedTicks = Math.Max(0, elapsedTicks);
            totals[index] += elapsedTicks;
            maxima[index] = Math.Max(maxima[index], elapsedTicks);
            counts[index]++;
        }

        public ServerPerformanceWindow Drain(double elapsedSeconds)
        {
            if (!double.IsFinite(elapsedSeconds) || elapsedSeconds <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(elapsedSeconds));
            }
            double rate = counts[(int)ServerPerformanceModule.Update] / elapsedSeconds;
            var modules = new List<ServerModuleTiming>();
            double millisecondsPerTick = 1000.0 / Stopwatch.Frequency;
            foreach (var module in Enum.GetValues<ServerPerformanceModule>())
            {
                int index = (int)module;
                if (counts[index] > 0)
                {
                    modules.Add(new ServerModuleTiming(module.ToString(), counts[index],
                        totals[index] / (double)counts[index] * millisecondsPerTick,
                        maxima[index] * millisecondsPerTick));
                }
                totals[index] = maxima[index] = counts[index] = 0;
            }
            return new ServerPerformanceWindow(rate, modules.ToArray());
        }
    }
}
