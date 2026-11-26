using System;
using System.Collections.Generic;
using System.Threading;
using FarseerPhysics.Collision;
using Microsoft.Xna.Framework;

namespace FarseerPhysics.Dynamics
{
    /// <summary>A ray and its hit callback. Give each request an independent result slot.</summary>
    public readonly struct RayCastRequest
    {
        public RayCastRequest(Vector2 point1, Vector2 point2, Category collisionCategory,
            Func<Fixture, Vector2, Vector2, float, float> callback)
        {
            Point1 = point1;
            Point2 = point2;
            CollisionCategory = collisionCategory;
            Callback = callback ?? throw new ArgumentNullException(nameof(callback));
        }

        public Vector2 Point1 { get; }
        public Vector2 Point2 { get; }
        public Category CollisionCategory { get; }
        public Func<Fixture, Vector2, Vector2, float, float> Callback { get; }
    }

    /// <summary>
    /// Runs every index in [0, count) exactly once on success. On failure or cancellation,
    /// stops scheduling and waits for all started actions to exit before propagating the error.
    /// No action may execute after the dispatcher returns or throws.
    /// </summary>
    public delegate void RayCastBatchDispatcher(int count, Action<int> run);

    public partial class World
    {
        /// <summary>
        /// Runs indexed requests during a synchronous read-only batch. Call from the simulation
        /// thread outside physics steps and mutations. The request list and all data read by the
        /// callbacks must remain stable through dispatch. The built-in dynamic tree uses the given
        /// dispatcher. Other broad phases run the requests serially in index order.
        /// </summary>
        public void RayCastBatch(IReadOnlyList<RayCastRequest> requests, RayCastBatchDispatcher dispatch,
            CancellationToken cancellationToken = default)
        {
            if (requests == null)
                throw new ArgumentNullException(nameof(requests));
            if (dispatch == null)
                throw new ArgumentNullException(nameof(dispatch));
            if (_isStepping || IsLocked || Interlocked.CompareExchange(ref _rayCastBatchActive, 1, 0) != 0)
                throw new WorldLockedException("Cannot start a ray cast batch during a physics step or another batch.");

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                int count = requests.Count;
                if (count == 0)
                    return;

                if (ContactManager.BroadPhase is DynamicTreeBroadPhase)
                {
                    dispatch(count, Run);
                }
                else
                {
                    for (int index = 0; index < count; index++)
                    {
                        Run(index);
                    }
                }
                cancellationToken.ThrowIfCancellationRequested();
            }
            finally
            {
                Volatile.Write(ref _rayCastBatchActive, 0);
            }

            void Run(int index)
            {
                cancellationToken.ThrowIfCancellationRequested();
                RayCastRequest request = requests[index];
                if (request.Callback == null)
                    throw new ArgumentException("Each ray cast request must have a hit callback.", nameof(requests));
                RayCast(request.Callback, request.Point1, request.Point2, request.CollisionCategory);
            }
        }

        internal void ThrowIfRayCastBatchActive()
        {
            if (Volatile.Read(ref _rayCastBatchActive) != 0)
                throw new WorldLockedException("Cannot modify the world during a ray cast batch.");
        }

        /// <summary>
        /// Applies one blocker's enabled state, then its optional transform without finding new
        /// contacts. The simulation thread observes the usual synchronous body callbacks.
        /// </summary>
        public void CommitBlocker(Body body, bool enabled, (Vector2 Position, float Rotation)? transform = null)
        {
            ThrowIfRayCastBatchActive();
            if (_isStepping || IsLocked)
                throw new WorldLockedException("Cannot commit a blocker during a physics step.");
            if (body == null)
                throw new ArgumentNullException(nameof(body));
            if (body.World != this)
                throw new ArgumentException("The blocker body must belong to this world.", nameof(body));

            body.Enabled = enabled;
            if (transform.HasValue)
            {
                var (position, rotation) = transform.Value;
                body.SetTransformIgnoreContacts(ref position, rotation);
            }
        }
    }
}
