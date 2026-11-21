using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Barotrauma.Debugging;

namespace Barotrauma;

public sealed class ThreadManager : IDisposable
{
    private static ThreadManager? current;
    public static ThreadManager? Current => Volatile.Read(ref current);
    public static ThreadManager Instance => Current ?? throw new InvalidOperationException("ThreadManager is not initialized.");

    private readonly object gate = new object();
    private readonly Queue<Action> mainQueue = new Queue<Action>();
    private readonly HashSet<WorkScope> scopes = new HashSet<WorkScope>();
    private readonly Dictionary<Type, object> services = new Dictionary<Type, object>();
    private bool stopping;
    private bool dispatchClosed;

    public Thread MainThread { get; } = Thread.CurrentThread;
    public WorkScope Process { get; }

    private ThreadManager()
    {
        Process = CreateScope("Process");
    }

    public static ThreadManager Initialize()
    {
        var manager = new ThreadManager();
        if (Interlocked.CompareExchange(ref current, manager, null) != null)
        {
            manager.Dispose();
            throw new InvalidOperationException("ThreadManager is already initialized.");
        }
        return manager;
    }

    public WorkScope CreateScope(string name)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(stopping, this);
            var scope = new WorkScope(name, this);
            scopes.Add(scope);
            return scope;
        }
    }

    public void RegisterService<T>(T service) where T : class
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(stopping, this);
            services.Add(typeof(T), service);
        }
    }

    public T? GetService<T>() where T : class
    {
        lock (gate)
        {
            return services.TryGetValue(typeof(T), out var service) ? (T)service : null;
        }
    }

    public void UnregisterService<T>(T service) where T : class
    {
        lock (gate)
        {
            if (services.TryGetValue(typeof(T), out var registered) && ReferenceEquals(registered, service))
            {
                services.Remove(typeof(T));
            }
        }
    }

    public bool TryPostToMainThread(Action action)
    {
        lock (gate)
        {
            if (dispatchClosed)
            {
                return false;
            }
            mainQueue.Enqueue(action);
            return true;
        }
    }

    public void InvokeOnMainThread(Action action)
    {
        if (Thread.CurrentThread == MainThread)
        {
            action();
            return;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!TryPostToMainThread(() =>
        {
            try
            {
                action();
                completion.SetResult();
            }
            catch (Exception error)
            {
                completion.SetException(error);
            }
        }))
        {
            throw new ObjectDisposedException(nameof(ThreadManager));
        }
        completion.Task.GetAwaiter().GetResult();
    }

    public void ProcessMainThreadTasks()
    {
        EnsureMainThread();
        while (true)
        {
            Action? action;
            lock (gate)
            {
                if (!mainQueue.TryDequeue(out action))
                {
                    return;
                }
            }
            try
            {
                action();
            }
            catch (Exception error)
            {
                DebugConsoleCore.Log($"Main-thread work failed: {error}");
            }
        }
    }

    public void Shutdown(Action? pumpServices = null)
    {
        EnsureMainThread();
        WorkScope[] activeScopes;
        lock (gate)
        {
            if (stopping)
            {
                return;
            }
            stopping = true;
            activeScopes = scopes.ToArray();
        }

        foreach (WorkScope scope in activeScopes)
        {
            scope.RequestStop();
        }
        foreach (WorkScope scope in activeScopes)
        {
            scope.FinishStop(pumpServices);
        }

        while (true)
        {
            ProcessMainThreadTasks();
            lock (gate)
            {
                if (mainQueue.Count == 0)
                {
                    dispatchClosed = true;
                    services.Clear();
                    scopes.Clear();
                    break;
                }
            }
        }
    }

    public void Dispose()
    {
        Shutdown();
        lock (gate)
        {
            if (dispatchClosed)
            {
                Interlocked.CompareExchange(ref current, null, this);
            }
        }
    }

    private void EnsureMainThread()
    {
        if (Thread.CurrentThread != MainThread)
        {
            throw new InvalidOperationException("This operation belongs to the registered main thread.");
        }
    }

    private void RemoveScope(WorkScope scope)
    {
        lock (gate)
        {
            scopes.Remove(scope);
        }
    }

    public sealed class WorkScope : IDisposable
    {
        private readonly object gate = new object();
        private readonly CancellationTokenSource stop = new CancellationTokenSource();
        private readonly Dictionary<Task, string> tasks = new Dictionary<Task, string>();
        private readonly ThreadManager? manager;
        private Task? stopCompletion;
        private int finalized;

        public string Name { get; }
        public CancellationToken Stopping { get; }

        // Standalone scheduler callers can own a scope without bootstrapping a game process.
        public WorkScope(string name) : this(name, null) { }

        internal WorkScope(string name, ThreadManager? manager)
        {
            Name = name;
            this.manager = manager;
            Stopping = stop.Token;
        }

        public Thread StartThread(string name, Action<CancellationToken> action, bool background = true)
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(stopCompletion != null, this);
                CancellationToken token = Stopping;
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var thread = new Thread(() =>
                {
                    try
                    {
                        action(token);
                        completion.SetResult();
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        completion.SetCanceled(token);
                    }
                    catch (Exception error)
                    {
                        completion.SetException(error);
                        throw;
                    }
                }) { Name = name, IsBackground = background };
                TrackLocked(name, completion.Task);
                try
                {
                    thread.Start();
                }
                catch (Exception error)
                {
                    completion.SetException(error);
                    throw;
                }
                return thread;
            }
        }

        public Task RunBackground(string name, Func<CancellationToken, Task> action, CancellationToken cancellation = default)
            => Schedule(name, () => Task.Run(() => action(Stopping), cancellation));

        public Task RunBackgroundAction(string name, Action<CancellationToken> action)
            => Schedule(name, () => Task.Run(() => action(Stopping)));

        public Task<T> RunBackground<T>(string name, Func<CancellationToken, Task<T>> action)
            => Schedule(name, () => Task.Run(() => action(Stopping)));

        public Task<T> RunBackgroundValue<T>(string name, Func<CancellationToken, T> action)
            => Schedule(name, () => Task.Run(() => action(Stopping)));

        // Keeps an already thread-affine scheduler or asynchronous API on its original executor.
        public T Schedule<T>(string name, Func<T> start) where T : Task
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(stopCompletion != null, this);
                T task = start();
                TrackLocked(name, task);
                return task;
            }
        }

        public void Track(string name, Task task)
        {
            lock (gate)
            {
                if (stopCompletion == null && !tasks.ContainsKey(task))
                {
                    // External operations may depend on a native callback that its SDK
                    // drops at shutdown. Cancel the observation; the SDK owns the operation.
                    TrackLocked(name, task.WaitAsync(Stopping));
                }
            }
        }

        public void RunParallel(string name, int start, int end, Action<int> action)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Schedule(name, () => completion.Task);
            try
            {
                Parallel.For(start, end, new ParallelOptions { CancellationToken = Stopping }, action);
                completion.SetResult();
            }
            catch (OperationCanceledException) when (Stopping.IsCancellationRequested)
            {
                completion.SetCanceled(Stopping);
                throw;
            }
            catch (Exception error)
            {
                completion.SetException(error);
                throw;
            }
        }

        private void TrackLocked(string name, Task task)
        {
            if (!tasks.TryAdd(task, name))
            {
                return;
            }
            _ = task.ContinueWith(completed =>
            {
                // Observe faults even for jobs whose caller does not consume the returned task.
                _ = completed.Exception;
                lock (gate)
                {
                    tasks.Remove(completed);
                }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        public void RequestStop()
        {
            lock (gate)
            {
                stopCompletion ??= stop.CancelAsync();
            }
        }

        internal void FinishStop(Action? pumpServices)
        {
            Task completion;
            lock (gate)
            {
                completion = Task.WhenAll(tasks.Keys.Append(stopCompletion!));
            }

            while (!completion.IsCompleted)
            {
                if (manager != null && Thread.CurrentThread == manager.MainThread)
                {
                    manager.ProcessMainThreadTasks();
                    pumpServices?.Invoke();
                }
                ((IAsyncResult)completion).AsyncWaitHandle.WaitOne(10);
            }
            _ = completion.Exception;
            completion.Dispose();
            if (Interlocked.Exchange(ref finalized, 1) == 0)
            {
                stop.Dispose();
                manager?.RemoveScope(this);
            }
        }

        public void Dispose()
        {
            RequestStop();
            FinishStop(null);
        }
    }
}
