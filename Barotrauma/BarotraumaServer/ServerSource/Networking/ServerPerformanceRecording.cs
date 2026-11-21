using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Barotrauma
{
    internal sealed class ServerPerformanceRecording
    {
        public const string DirectoryName = "PerformanceRecords";
        private const int MaxPendingSamples = 1200;

        public sealed record State(bool Enabled, bool HasArchive, string ErrorMessage);

        private readonly object gate = new object();
        private readonly string directory;
        private readonly string archivePath;
        private List<ServerPerformanceSnapshot> pending = new List<ServerPerformanceSnapshot>();
        private bool enabled;
        private bool stopped;
        private string errorMessage;
        private long committedLength;

        // TEMPORARY: expose the writer task until HTTP recording uses ThreadManager.WorkScope again.
        public Task Completion { get; }

        public ServerPerformanceRecording(string directory, CancellationToken stopping, TimeProvider timeProvider = null)
        {
            this.directory = directory;
            string startedAt = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            archivePath = Path.Combine(directory, $"performance-{startedAt}-{Guid.NewGuid():N}.csv");
            Completion = WriteSamplesAsync(timeProvider ?? TimeProvider.System, stopping);
        }

        public State GetState()
        {
            lock (gate) { return ReadState(); }
        }

        private State ReadState() => new State(enabled, Volatile.Read(ref committedLength) > 0, errorMessage);

        public State SetEnabled(bool value)
        {
            lock (gate)
            {
                if (stopped) { throw new ObjectDisposedException(nameof(ServerPerformanceRecording)); }
                if (enabled != value)
                {
                    enabled = value;
                    errorMessage = null;
                }
                return ReadState();
            }
        }

        public void Record(ServerPerformanceSnapshot sample)
        {
            lock (gate)
            {
                if (!enabled) { return; }
                if (pending.Count >= MaxPendingSamples)
                {
                    enabled = false;
                    errorMessage = "Recording stopped because the server could not write samples fast enough.";
                    return;
                }
                pending.Add(sample);
            }
        }

        public FileStream OpenArchive(out long length)
        {
            length = Volatile.Read(ref committedLength);
            return length == 0 ? null : new FileStream(archivePath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        }

        private async Task WriteSamplesAsync(TimeProvider timeProvider, CancellationToken stopping)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5), timeProvider);
            try
            {
                while (await timer.WaitForNextTickAsync(stopping).ConfigureAwait(false))
                {
                    await FlushAsync().ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                // The HTTP server waits for the final write on normal server shutdown.
            }
            lock (gate)
            {
                stopped = true;
                enabled = false;
            }
            await FlushAsync().ConfigureAwait(false);
        }

        private async Task FlushAsync()
        {
            List<ServerPerformanceSnapshot> batch;
            lock (gate)
            {
                if (pending.Count == 0) { return; }
                batch = pending;
                pending = new List<ServerPerformanceSnapshot>();
            }

            try
            {
                long length = Volatile.Read(ref committedLength);
                var csv = new StringBuilder();
                if (length == 0) { ServerPerformanceCsv.AppendHeader(csv); }
                foreach (var sample in batch)
                {
                    ServerPerformanceCsv.AppendSample(csv, sample);
                }
                byte[] bytes = Encoding.UTF8.GetBytes(csv.ToString());
                Directory.CreateDirectory(directory);
                await using (var file = new FileStream(archivePath, FileMode.OpenOrCreate, FileAccess.Write,
                    FileShare.ReadWrite, 4096, FileOptions.Asynchronous))
                {
                    // A retry starts at the end of the last completed batch.
                    if (file.Length < length) { throw new IOException("The performance recording was modified."); }
                    if (file.Length > length) { file.SetLength(length); }
                    file.Position = length;
                    await file.WriteAsync(bytes).ConfigureAwait(false);
                    await file.FlushAsync().ConfigureAwait(false);
                }
                Volatile.Write(ref committedLength, length + bytes.Length);
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                lock (gate)
                {
                    batch.AddRange(pending);
                    pending = batch;
                    enabled = false;
                    errorMessage = "Recording stopped because the server could not write the CSV file.";
                }
                Console.Error.WriteLine("Could not save performance recording ({0}).", error.GetType().Name);
            }
        }
    }
}
