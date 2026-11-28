using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Barotrauma;

// Own only threads created by EP. The CLR pool remains available for async I/O and other tasks.
internal sealed class CoreWorkers : IDisposable
{
    private sealed class Lane(PhysicalCore core)
    {
        internal readonly PhysicalCore Core = core;
        internal int Dedicated;
        private BlockingCollection<Batch>? queue;
        private Thread? thread;

        // Called under the allocator lock. Report an affinity failure before accepting a batch.
        internal void Start()
        {
            if (thread != null) { return; }
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var jobs = new BlockingCollection<Batch>();
            var worker = new Thread(() =>
            {
                try { Core.Bind(); }
                catch (Exception error) { ready.SetException(error); return; }
                onComputeWorker = true;
                ready.SetResult();
                foreach (Batch job in jobs.GetConsumingEnumerable()) { job.Execute(); }
            }) { IsBackground = true, Name = $"EP compute (core {Core.Id})" };
            try
            {
                worker.UnsafeStart();
                ready.Task.GetAwaiter().GetResult();
            }
            catch { jobs.Dispose(); throw; }
            queue = jobs;
            thread = worker;
        }

        internal void Enqueue(Batch batch) => queue!.Add(batch);

        internal void Stop()
        {
            if (thread == null) { return; }
            queue!.CompleteAdding();
            thread.Join();
            queue.Dispose();
        }
    }

    private sealed class Batch(int start, int end, int workers, Action<int> action, CancellationToken token)
    {
        private int remaining = workers;
        private readonly TaskCompletionSource completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ExecutionContext? context = ExecutionContext.Capture();
        private readonly int chunk = (int)Math.Clamp(((long)end - start) / (workers * 8L), 1, 64);
        private long next = start;
        private List<Exception>? failures;

        internal void Execute()
        {
            try
            {
                if (context == null) { Run(); }
                else { ExecutionContext.Run(context, static b => ((Batch)b!).Run(), this); }
            }
            catch (Exception error)
            {
                lock (this) { (failures ??= new List<Exception>()).Add(error); }
            }
            finally
            {
                if (Interlocked.Decrement(ref remaining) == 0) { completed.SetResult(); }
            }
        }

        private void Run()
        {
            while (Volatile.Read(ref failures) == null && !token.IsCancellationRequested)
            {
                long first = Interlocked.Add(ref next, chunk) - chunk;
                if (first >= end) { return; }
                long last = Math.Min(first + chunk, end);
                for (long i = first; i < last; i++) { action((int)i); }
            }
        }

        internal void Wait()
        {
            completed.Task.GetAwaiter().GetResult();
            if (failures != null && !(token.IsCancellationRequested &&
                failures.All(e => e is OperationCanceledException canceled && canceled.CancellationToken == token)))
            {
                throw new AggregateException(failures);
            }
            token.ThrowIfCancellationRequested();
        }
    }

    [ThreadStatic] private static bool onComputeWorker;
    private readonly object gate = new();
    private readonly Lane[] lanes;
    private readonly Lane[] dedicatedLanes;
    private readonly bool hybrid;
    private Lane[] computeLanes;
    private sealed class DedicatedThread(int id, Lane lane)
    {
        internal readonly int Id = id;
        internal Lane Lane = lane;
    }
    private readonly List<DedicatedThread> dedicated = new();

    internal CoreWorkers(PhysicalCore[] cores, bool automatic)
    {
        lanes = cores.Select(c => new Lane(c)).ToArray();
        int fastest = cores.Max(c => c.Performance);
        hybrid = automatic && cores.Any(c => c.Performance < fastest);
        computeLanes = hybrid ? lanes.Where(l => l.Core.Performance == fastest).ToArray() : lanes;
        // Keep the first selected core available for forward progress. A one-core budget shares it.
        dedicatedLanes = hybrid ? lanes.Where(l => l.Core.Performance < fastest).ToArray() :
            lanes.Length == 1 ? lanes : lanes.Skip(1).Reverse().ToArray();
    }

    internal Action EnterDedicated()
    {
        DedicatedThread registration;
        lock (gate)
        {
            var chosen = DedicatedLane(dedicated.Count);
            chosen.Core.Bind();
            registration = new DedicatedThread(PhysicalCore.CurrentNativeThreadId, chosen);
            dedicated.Add(registration);
            chosen.Dedicated++;
            RefreshComputeLanes();
        }
        return Release;

        void Release()
        {
            lock (gate)
            {
                int slot = dedicated.IndexOf(registration);
                var tail = dedicated[^1];
                dedicated[slot] = tail;
                dedicated.RemoveAt(dedicated.Count - 1);
                registration.Lane.Dedicated--;
                // Fill the vacated slot so two surviving dedicated threads still share one core.
                if (slot < dedicated.Count && tail.Lane != DedicatedLane(slot))
                {
                    var destination = DedicatedLane(slot);
                    try
                    {
                        destination.Core.Bind(tail.Id);
                        tail.Lane.Dedicated--;
                        tail.Lane = destination;
                        destination.Dedicated++;
                    }
                    catch (Win32Exception error)
                    {
                        Console.Error.WriteLine($"Could not move EP dedicated thread {tail.Id} to core {destination.Core.Id}: {error.Message}");
                    }
                }
                RefreshComputeLanes();
            }
        }
    }

    private Lane DedicatedLane(int slot)
    {
        // Fill pairs on homogeneous cores, then spread any overflow evenly.
        int lane = !hybrid && slot < dedicatedLanes.Length * 2 ? slot / 2 : slot % dedicatedLanes.Length;
        return dedicatedLanes[lane];
    }

    private void RefreshComputeLanes()
    {
        if (!hybrid) { computeLanes = lanes.Where(l => l == lanes[0] || l.Dedicated == 0).ToArray(); }
    }

    internal void Run(int start, int end, Action<int> action, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(action);
        token.ThrowIfCancellationRequested();
        if (start >= end) { return; }
        // A worker waiting for a nested batch on its own queue would deadlock.
        if (onComputeWorker)
        {
            for (int i = start; i < end; i++) { token.ThrowIfCancellationRequested(); action(i); }
            return;
        }
        Batch batch;
        lock (gate)
        {
            int count = (int)Math.Min((long)end - start, computeLanes.Length);
            for (int i = 0; i < count; i++) { computeLanes[i].Start(); }
            batch = new Batch(start, end, count, action, token);
            for (int i = 0; i < count; i++) { computeLanes[i].Enqueue(batch); }
        }
        batch.Wait();
    }

    public void Dispose()
    {
        foreach (Lane lane in lanes) { lane.Stop(); }
    }
}
