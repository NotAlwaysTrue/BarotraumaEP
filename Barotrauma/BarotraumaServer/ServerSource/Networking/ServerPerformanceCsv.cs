using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Barotrauma
{
    internal static class ServerPerformanceCsv
    {
        private static readonly string[] Modules = Enum.GetNames<ServerPerformanceModule>();

        public static void AppendHeader(StringBuilder csv)
        {
            csv.Append("TimestampUtc,UptimeSeconds,TPS,TargetTPS,NetworkTickRate,CpuPercent,CpuCores,WorkingSetBytes,ManagedHeapBytes,Players,Spike5mMs,Mean5mMs");
            foreach (string module in Modules)
            {
                csv.Append($",{module}AverageMs,{module}MaxMs,{module}Calls,{module}Spike5mMs,{module}Mean5mMs");
            }
            csv.Append("\r\n");
        }

        public static void AppendSample(StringBuilder csv, ServerPerformanceSnapshot sample)
        {
            csv.Append(sample.SampledAtUtc.ToString("O", CultureInfo.InvariantCulture))
                .Append(FormattableString.Invariant($",{sample.UptimeSeconds},{sample.UpdatesPerSecond},{sample.TargetUpdateRate},{sample.NetworkTickRate},{sample.CpuPercent},{sample.CpuCores},{sample.WorkingSetBytes},{sample.ManagedHeapBytes},{sample.OnlinePlayers},{sample.Spike5mMilliseconds},{sample.Mean5mMilliseconds}"));
            var timings = sample.Modules.ToDictionary(timing => timing.Module);
            foreach (string module in Modules)
            {
                if (timings.TryGetValue(module, out var timing))
                {
                    csv.Append(FormattableString.Invariant($",{timing.AverageMilliseconds},{timing.MaxMilliseconds},{timing.Samples}"));
                }
                else
                {
                    csv.Append(",,,");
                }
                var rolling = sample.ModuleTimings5m?.GetValueOrDefault(module);
                csv.Append(FormattableString.Invariant($",{rolling?.SpikeMilliseconds},{rolling?.MeanMilliseconds}"));
            }
            csv.Append("\r\n");
        }
    }
}
