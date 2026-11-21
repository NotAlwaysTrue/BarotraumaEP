using System.Collections.Generic;

namespace Barotrauma
{
    public static class CrossThread
    {
        public delegate void TaskDelegate();

        public static void ProcessTasks()
        {
            ThreadManager.Current?.ProcessMainThreadTasks();
        }

        /// <summary>Queues work for the main thread without waiting for it.</summary>
        /// <returns>False if the main thread is not available.</returns>
        public static bool TryPostExecutionOnMainThread(TaskDelegate deleg)
        {
            return ThreadManager.Current?.TryPostToMainThread(() => deleg()) == true;
        }

        public static void RequestExecutionOnMainThread(TaskDelegate deleg)
        {
            if (ThreadManager.Current is { } manager)
            {
                manager.InvokeOnMainThread(() => deleg());
            }
            else
            {
                deleg();
            }
        }

        public static void AddOnMainThread<T>(this List<T> list, T element)
        {
            RequestExecutionOnMainThread(() => { list.Add(element); });
        }

        public static void AddRangeOnMainThread<T>(this List<T> list, IEnumerable<T> elements)
        {
            RequestExecutionOnMainThread(() => { list.AddRange(elements); });
        }

        public static void RemoveOnMainThread<T>(this List<T> list, T element)
        {
            RequestExecutionOnMainThread(() => { list.Remove(element); });
        }

        public static void ClearOnMainThread<T>(this List<T> list)
        {
            RequestExecutionOnMainThread(() => { list.Clear(); });
        }
    }
}
