﻿using BepuPhysics.Constraints;
using BepuUtilities;
using BepuUtilities.Collections;
using BepuUtilities.Memory;
using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace BepuPhysics
{
    public class Deactivator
    {
        IdPool<Buffer<int>> setIdPool;
        Bodies bodies;
        Solver solver;
        BufferPool pool;
        public int InitialIslandBodyCapacity { get; set; } = 1024;
        public int InitialIslandConstraintCapacity { get; set; } = 1024;

        /// <summary>
        /// Gets or sets the multiplier applied to the active body count used to calculate the number of deactivation traversals in a given timestep.
        /// </summary>
        public float TestedFractionPerFrame { get; set; } = 0.01f;
        /// <summary>
        /// Gets or sets the fraction of the active set to target as the number of bodies deactivated in a given frame.
        /// This is only a goal; the actual number of deactivated bodies may be more or less.
        /// </summary>
        public float TargetDeactivatedFraction { get; set; } = 0.005f;
        /// <summary>
        /// Gets or sets the fraction of the active set to target as the number of bodies traversed for deactivation in a given frame.
        /// This is only a goal; the actual number of traversed bodies may be more or less.
        /// </summary>
        public float TargetTraversedFraction { get; set; } = 0.02f;

        public Deactivator(Bodies bodies, Solver solver, BufferPool pool)
        {
            this.bodies = bodies;
            this.solver = solver;
            this.pool = pool;
            IdPool<Buffer<int>>.Create(pool.SpecializeFor<int>(), 16, out setIdPool);
            //We reserve index 0 for the active set.
            setIdPool.Take();
            findIslandsDelegate = FindIslands;
            gatherDelegate = Gather;
        }

        struct ConstraintBodyEnumerator : IForEach<int>
        {
            public QuickList<int, Buffer<int>> ConstraintBodyIndices;
            public BufferPool<int> IntPool;
            public int SourceIndex;
            public void LoopBody(int bodyIndex)
            {
                if (bodyIndex != SourceIndex)
                {
                    ConstraintBodyIndices.Add(bodyIndex, IntPool);
                }
            }
        }



        struct ForcedDeactivationPredicate : IPredicate<int>
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool Matches(ref int bodyIndex)
            {
                return true;
            }
        }
        struct DeactivationPredicate : IPredicate<int>
        {
            public Bodies Bodies;
            public IndexSet PreviouslyTraversedBodies;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool Matches(ref int bodyIndex)
            {
                //Note that we block traversals on a single thread from retreading old ground.
                if (PreviouslyTraversedBodies.Contains(bodyIndex))
                    return false;
                PreviouslyTraversedBodies.AddUnsafely(bodyIndex);
                return Bodies.ActiveSet.Activity[bodyIndex].DeactivationCandidate;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        bool PushBody<TTraversalPredicate>(int bodyIndex, ref IndexSet consideredBodies, ref QuickList<int, Buffer<int>> bodyIndices, ref QuickList<int, Buffer<int>> visitationStack,
            ref BufferPool<int> intPool, ref TTraversalPredicate predicate) where TTraversalPredicate : IPredicate<int>
        {
            if (predicate.Matches(ref bodyIndex))
            {
                if (!consideredBodies.Contains(bodyIndex))
                {
                    //This body has not yet been traversed. Push it onto the stack.
                    bodyIndices.Add(bodyIndex, intPool);
                    consideredBodies.AddUnsafely(bodyIndex);
                    visitationStack.Add(bodyIndex, intPool);

                }
                return true;
            }
            return false;
        }


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        bool EnqueueUnvisitedNeighbors<TTraversalPredicate>(int bodyHandle,
            ref QuickList<int, Buffer<int>> bodyHandles,
            ref QuickList<int, Buffer<int>> constraintHandles,
            ref IndexSet consideredBodies, ref IndexSet consideredConstraints,
            ref QuickList<int, Buffer<int>> visitationStack,
            ref ConstraintBodyEnumerator bodyEnumerator,
            ref BufferPool<int> intPool, ref TTraversalPredicate predicate) where TTraversalPredicate : IPredicate<int>
        {
            var bodyIndex = bodies.HandleToLocation[bodyHandle].Index;
            bodyEnumerator.SourceIndex = bodyIndex;
            ref var list = ref bodies.ActiveSet.Constraints[bodyIndex];
            for (int i = 0; i < list.Count; ++i)
            {
                ref var entry = ref list[i];
                if (!consideredConstraints.Contains(entry.ConnectingConstraintHandle))
                {
                    //This constraint has not yet been traversed. Follow the constraint to every other connected body.
                    constraintHandles.Add(entry.ConnectingConstraintHandle, intPool);
                    consideredConstraints.AddUnsafely(entry.ConnectingConstraintHandle);
                    solver.EnumerateConnectedBodies(entry.ConnectingConstraintHandle, ref bodyEnumerator);
                    for (int j = 0; j < bodyEnumerator.ConstraintBodyIndices.Count; ++j)
                    {
                        var connectedBodyIndex = bodyEnumerator.ConstraintBodyIndices[j];
                        if (!PushBody(connectedBodyIndex, ref consideredBodies, ref bodyHandles, ref visitationStack, ref intPool, ref predicate))
                            return false;
                    }
                    bodyEnumerator.ConstraintBodyIndices.Count = 0;
                }
            }
            return true;
        }

        void CleanUpTraversal(
            BufferPool pool,
            ref IndexSet consideredBodies, ref IndexSet consideredConstraints,
            ref QuickList<int, Buffer<int>> visitationStack)
        {
            var intPool = pool.SpecializeFor<int>();
            consideredBodies.Dispose(pool);
            consideredConstraints.Dispose(pool);
            visitationStack.Dispose(intPool);
        }

        /// <summary>
        /// Traverses the active constraint graph collecting bodies that match a predicate. If any body visited during the traversal fails to match the predicate, the traversal terminates.
        /// </summary>
        /// <typeparam name="TTraversalPredicate">Type of the predicate to test each body index with.</typeparam>
        /// <param name="pool">Pool to allocate temporary collections from.</param>
        /// <param name="startingActiveBodyIndex">Index of the active body to start the traversal at.</param>
        /// <param name="predicate">Predicate to test each traversed body with. If any body results in the predicate returning false, the traversal stops and the function returns false.</param>
        /// <param name="bodyIndices">List to fill with body indices traversed during island collection. Bodies failing the predicate will not be included.</param>
        /// <param name="constraintHandles">List to fill with constraint handles traversed during island collection.</param>
        /// <returns>True if the simulation graph was traversed without ever finding a body that made the predicate return false. False if any body failed the predicate.
        /// The bodyIndices and constraintHandles lists will contain all traversed predicate-passing bodies and constraints.</returns>
        public bool CollectIsland<TTraversalPredicate>(BufferPool pool, int startingActiveBodyIndex, ref TTraversalPredicate predicate,
            ref QuickList<int, Buffer<int>> bodyIndices, ref QuickList<int, Buffer<int>> constraintHandles) where TTraversalPredicate : IPredicate<int>
        {
            //We'll build the island by working depth-first. This means the bodies and constraints we accumulate will be stored in any inactive island by depth-first order,
            //which happens to be a pretty decent layout for cache purposes. In other words, when we wake these islands back up, bodies near each other in the graph will have 
            //a higher chance of being near each other in memory. Bodies directly connected may often end up adjacent to each other, meaning loading one body may give you the other for 'free'
            //(assuming they share a cache line).
            //The DFS order for constraints is not quite as helpful as the constraint optimizer's sort, but it's not terrible.

            //Despite being DFS, there is no guarantee that the visitation stack will be any smaller than the final island itself, and we have no way of knowing how big the island is 
            //ahead of time- except that it can't be larger than the entire active simulation.
            var intPool = pool.SpecializeFor<int>();
            var initialBodyCapacity = Math.Min(InitialIslandBodyCapacity, bodies.ActiveSet.Count);
            //Note that we track all considered bodies AND constraints. 
            //While we only need to track one of them for the purposes of traversal, tracking both allows low-overhead collection of unique bodies and constraints.
            //Note that the constraint handle set is initialized to cover the entire handle span. 
            //That's actually fine- every single object occupies only a single bit, so 131072 objects only use 16KB.
            var consideredBodies = new IndexSet(pool, bodies.ActiveSet.Count);
            var consideredConstraints = new IndexSet(pool, solver.HandlePool.HighestPossiblyClaimedId + 1);
            //The stack will store body indices.
            QuickList<int, Buffer<int>>.Create(intPool, initialBodyCapacity, out var visitationStack);

            //Start the traversal by pushing the initial body conditionally.
            if (!PushBody(startingActiveBodyIndex, ref consideredBodies, ref bodyIndices, ref visitationStack, ref intPool, ref predicate))
            {
                CleanUpTraversal(pool, ref consideredBodies, ref consideredConstraints, ref visitationStack);
                return false;
            }
            var enumerator = new ConstraintBodyEnumerator();
            enumerator.IntPool = intPool;

            while (visitationStack.TryPop(out var nextIndexToVisit))
            {
                QuickList<int, Buffer<int>>.Create(intPool, 4, out enumerator.ConstraintBodyIndices);
                if (!EnqueueUnvisitedNeighbors(nextIndexToVisit, ref bodyIndices, ref constraintHandles, ref consideredBodies, ref consideredConstraints, ref visitationStack,
                    ref enumerator, ref intPool, ref predicate))
                {
                    CleanUpTraversal(pool, ref consideredBodies, ref consideredConstraints, ref visitationStack);
                    return false;
                }
            }
            //The visitation stack was emptied without finding any traversal disqualifying bodies.
            return true;
        }

        int targetTraversedBodyCountPerThread;
        int targetDeactivatedBodyCountPerThread;
        QuickList<int, Buffer<int>> traversalTargetBodyIndices;
        IThreadDispatcher threadDispatcher;
        int jobIndex;


        struct WorkerTraversalResults
        {
            //Note that all these resources are allocated on per-worker pools. Be careful when disposing them.
            public IndexSet TraversedBodies;
            public QuickList<Island, Buffer<Island>> Islands;

            internal void Dispose(BufferPool pool)
            {
                for (int islandIndex = 0; islandIndex < Islands.Count; ++islandIndex)
                {
                    Islands[islandIndex].Dispose(pool);
                }
                Islands.Dispose(pool.SpecializeFor<Island>());
                TraversedBodies.Dispose(pool);
            }
        }

        Buffer<WorkerTraversalResults> workerTraversalResults;

        struct GatheringJob
        {
            public int TargetSetIndex;
            public int TargetBatchIndex;
            public int TargetTypeBatchIndex;
            public QuickList<int, Buffer<int>> SourceIndices;
            public int StartIndex;
            public int EndIndex;
            /// <summary>
            /// If true, this job relates to a subset of body indices. If false, this job relates to a subset of constraint handles.
            /// </summary>
            public bool IsBodyJob;
        }

        QuickList<GatheringJob, Buffer<GatheringJob>> gatheringJobs;

        void FindIslands(int workerIndex, BufferPool threadPool)
        {
            Debug.Assert(workerTraversalResults.Allocated && workerTraversalResults.Length > workerIndex);
            ref var results = ref workerTraversalResults[workerIndex];
            var intPool = pool.SpecializeFor<int>();
            var islandPool = threadPool.SpecializeFor<Island>();

            QuickList<int, Buffer<int>>.Create(intPool, Math.Min(InitialIslandBodyCapacity, bodies.ActiveSet.Count), out var bodyIndices);
            QuickList<int, Buffer<int>>.Create(intPool, Math.Min(InitialIslandConstraintCapacity, solver.HandlePool.HighestPossiblyClaimedId + 1), out var constraintHandles);

            DeactivationPredicate predicate;
            predicate.Bodies = bodies;
            predicate.PreviouslyTraversedBodies = new IndexSet(threadPool, bodies.ActiveSet.Count);
            var traversedBodies = 0;
            var deactivatedBodies = 0;

            while (traversedBodies < targetTraversedBodyCountPerThread && deactivatedBodies < targetDeactivatedBodyCountPerThread)
            {
                //This thread still has some deactivation budget, so try another traversal.
                var targetIndex = Interlocked.Increment(ref jobIndex);
                if (targetIndex >= traversalTargetBodyIndices.Count)
                    break;
                var bodyIndex = traversalTargetBodyIndices[targetIndex];
                if (CollectIsland(threadPool, bodyIndex, ref predicate, ref bodyIndices, ref constraintHandles))
                {
                    //Found an island to deactivate!
                    deactivatedBodies += bodyIndices.Count;

                    //Note that the deactivation predicate refuses to visit any body that was visited in any previous traversal on this thread. 
                    //From that we know that any newly discovered island is unique *on this thread*. It's very possible that a different thread has found the same
                    //island, but we let that happen in favor of avoiding tons of sync overhead.
                    //The gathering phase will check each worker's island against all previous workers. If it's a duplicate, it will get thrown out.
                    var island = new Island(ref bodyIndices, ref constraintHandles, solver, threadPool);
                    results.Islands.Add(ref island, islandPool);

                }
                traversedBodies += bodyIndices.Count;
                bodyIndices.Count = 0;
                constraintHandles.Count = 0;
            }
            bodyIndices.Dispose(intPool);
            constraintHandles.Dispose(intPool);
            results.TraversedBodies = predicate.PreviouslyTraversedBodies;
        }

        Action<int> findIslandsDelegate;
        void FindIslands(int workerIndex)
        {
            //The only reason we separate this out is to make it easier for the main pool to be passed in if there is only a single thread.
            FindIslands(workerIndex, threadDispatcher.GetThreadMemoryPool(workerIndex));
        }

        Action<int> gatherDelegate;
        void Gather(int workerIndex)
        {
            while(true)
            {
                var index = Interlocked.Increment(ref jobIndex);
                if(index >= gatheringJobs.Count)
                {
                    break;
                }
                ref var job = ref gatheringJobs[index];
                if(job.IsBodyJob)
                {
                    //Load a range of bodies from the active set and store them into the target inactive body set.
                    ref var sourceSet = ref bodies.ActiveSet;
                    for (int targetIndex = job.StartIndex; targetIndex < job.EndIndex; ++targetIndex)
                    {
                        var sourceIndex = job.SourceIndices[targetIndex];
                        ref var targetSet = ref bodies.Sets[job.TargetSetIndex];
                        targetSet.IndexToHandle[targetIndex] = sourceSet.IndexToHandle[sourceIndex];
                        targetSet.Activity[targetIndex] = sourceSet.Activity[sourceIndex];
                        targetSet.Collidables[targetIndex] = sourceSet.Collidables[sourceIndex];
                        //Note that we are just copying the constraint list reference; we don't have to reallocate it.
                        //Keep this in mind when removing the object from the active set. We don't want to dispose the list since we're still using it.
                        targetSet.Constraints[targetIndex] = sourceSet.Constraints[sourceIndex];
                        targetSet.LocalInertias[targetIndex] = sourceSet.LocalInertias[sourceIndex];
                        targetSet.Poses[targetIndex] = sourceSet.Poses[sourceIndex];
                        targetSet.Velocities[targetIndex] = sourceSet.Velocities[sourceIndex];
                    }
                }
                else
                {
                    //Load a range of constraints from the active set and store them into the target inactive constraint set.
                    ref var targetTypeBatch = ref solver.Sets[job.TargetSetIndex].Batches[job.TargetBatchIndex].TypeBatches[job.TargetTypeBatchIndex];
                    //We can share a single virtual dispatch over all the constraints since they are of the same type. They may, however, be in different batches.
                    solver.TypeProcessors[targetTypeBatch.TypeId].GatherActiveConstraints(bodies, solver, ref job.SourceIndices, job.StartIndex, job.EndIndex, ref targetTypeBatch);
                }
            }
        }

        struct HandleComparer : IComparerRef<int>
        {
            public Buffer<int> Handles;
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public int Compare(ref int a, ref int b)
            {
                return Handles[a].CompareTo(Handles[b]);
            }
        }

        int scheduleOffset;

        public void Update(IThreadDispatcher threadDispatcher, bool deterministic)
        {
            if (bodies.ActiveSet.Count == 0)
                return;

            //There are three phases to deactivation:
            //1) Traversing the constraint graph to identify 'simulation islands' that satisfy the deactivation conditions.
            //2) Gathering the data backing the bodies and constraints of a simulation island and placing it into an inactive storage representation (a BodySet and ConstraintSet).
            //3) Removing the deactivated bodies and constraints from the active set.
            //Separating it into these phases allows for a fairly direct parallelization.
            //Traversal proceeds in parallel, biting the bullet on the fact that different traversal starting points on separate threads may identify the same island sometimes.
            //Once all islands have been detected, the second phase is able to eliminate duplicates and gather the remaining unique islands in parallel.
            //Finally, while removal involves many sequential operations, there are some fully parallel parts and some of the locally sequential parts can be run in parallel with each other.

            //The goal here isn't necessarily to speed up the best case- using three dispatches basically guarantees ~15-40us of overhead- 
            //but rather to try to keep the worst case from dropping frames and to improve deactivation responsiveness.

            //So first, traverse.
            int candidateCount = (int)Math.Max(1, bodies.ActiveSet.Count * TestedFractionPerFrame);

            QuickList<int, Buffer<int>>.Create(pool.SpecializeFor<int>(), candidateCount, out traversalTargetBodyIndices);

            //Uniformly distribute targets across the active set. Each frame, the targets are pushed up by one slot.
            int spacing = bodies.ActiveSet.Count / candidateCount;

            //The schedule offset will gradually walk off into the sunset, and there's also a possibility that changes to the size of the active set (by, say, deactivation)
            //will put the offset so far out that a single subtraction by the active set count would be insufficient. So instead we just wrap it to zero.
            if (scheduleOffset > bodies.ActiveSet.Count)
            {
                scheduleOffset = 0;
            }

            var index = scheduleOffset;
            for (int i = 0; i < candidateCount; ++i)
            {
                if (index > bodies.ActiveSet.Count)
                {
                    index -= bodies.ActiveSet.Count;
                }
                traversalTargetBodyIndices.AllocateUnsafely() = index;
                index += spacing;
            }
            ++scheduleOffset;


            if (deterministic)
            {
                //The order in which deactivations occurs affects the result of the simulation. To ensure determinism, we need to pin the deactivation order to something
                //which is deterministic. We will use the handle associated with each active body as the order provider.
                pool.SpecializeFor<int>().Take(bodies.ActiveSet.Count, out var sortedIndices);
                for (int i = 0; i < bodies.ActiveSet.Count; ++i)
                {
                    sortedIndices[i] = i;
                }
                //Handles are guaranteed to be unique; no need for three way partitioning.
                HandleComparer comparer;
                comparer.Handles = bodies.ActiveSet.IndexToHandle;
                //TODO: This sort might end up being fairly expensive. On a very large simulation, it might even amount to 5% of the simulation time.
                //It would be nice to come up with a better solution here. Some options include other sources of determinism, hiding the sort, and possibly enumerating directly over handles.
                QuickSort.Sort(ref sortedIndices[0], 0, bodies.ActiveSet.Count - 1, ref comparer);

                //Now that we have a sorted set of indices, we have eliminated nondeterminism related to memory layout. The initial target body indices can be remapped onto the sorted list.
                for (int i = 0; i < traversalTargetBodyIndices.Count; ++i)
                {
                    traversalTargetBodyIndices[i] = sortedIndices[traversalTargetBodyIndices[i]];
                }
            }

            int threadCount = threadDispatcher == null ? 1 : threadDispatcher.ThreadCount;
            targetDeactivatedBodyCountPerThread = (int)Math.Max(1, bodies.ActiveSet.Count * TargetDeactivatedFraction / threadCount);
            targetTraversedBodyCountPerThread = (int)Math.Max(1, bodies.ActiveSet.Count * TargetTraversedFraction / threadCount);

            pool.SpecializeFor<WorkerTraversalResults>().Take(threadCount, out workerTraversalResults);
            //Note that all resources within a worker's results set are allocate on the worker's pool since the thread may need to resize things.

            this.threadDispatcher = threadDispatcher;
            jobIndex = -1;
            if (threadCount > 1)
            {
                threadDispatcher.DispatchWorkers(findIslandsDelegate);
            }
            else
            {
                FindIslands(0, pool);
            }
            this.threadDispatcher = null;

            traversalTargetBodyIndices.Dispose(pool.SpecializeFor<int>());

            //Traversal is now done. We should have a set of results for each worker in the workerTraversalResults. It's time to gather all the data for the deactivating bodies and constraints.
            //Note that we only preallocate a fixed size. It will often be an overestimate, but that's fine. Resizes are more concerning.
            //(We could precompute the exact number of jobs, but it's not really necessary.) 
            var objectsPerGatherJob = 32;
            var gatheringJobPool = pool.SpecializeFor<GatheringJob>();
            QuickList<GatheringJob, Buffer<GatheringJob>>.Create(gatheringJobPool, 512, out gatheringJobs);
            //We now create jobs only for the set of unique islands. While each worker avoided creating duplicates locally, threads did not communicate and so may have found the same islands.
            //Each worker output a set of their traversed bodies. Using it, we can efficiently check to see if a given island is a duplicate by checking all previous workers.
            //If a previous worker traversed a body, the later worker's island holding that body is considered a duplicate.
            //(Note that a body can only belong to one island. If two threads find the same body, it means they found the exact same island, just using different paths. They are fully redundant.)
            for (int workerIndex = 0; workerIndex < threadCount; ++workerIndex)
            {
                ref var workerIslands = ref workerTraversalResults[workerIndex].Islands;
                for (int j = 0; j < workerIslands.Count; ++j)
                {
                    ref var island = ref workerIslands[j];
                    bool skip = false;
                    for (int previousWorkerIndex = 0; previousWorkerIndex < workerIndex; ++previousWorkerIndex)
                    {
                        if (workerTraversalResults[workerIndex].TraversedBodies.Contains(island.BodyIndices[0]))
                        {
                            //A previous worker already reported this island. It is a duplicate; skip it.
                            skip = true;
                        }
                    }
                    if (!skip)
                    {
                        //Allocate space for a new island.
                        var setIndex = setIdPool.Take();
                        if (setIndex >= bodies.Sets.Length)
                        {
                            var necessaryCapacity = setIndex + 1;
                            EnsureSetsCapacity(necessaryCapacity);
                        }
                        bodies.Sets[setIndex] = new BodySet(island.BodyIndices.Count, pool);
                        bodies.Sets[setIndex].Count = island.BodyIndices.Count;
                        AllocateConstraintSet(ref island.Protobatches, solver, pool, out solver.Sets[setIndex]);

                        //A single island may involve multiple jobs, depending on its size.
                        //For simplicity, a job can only cover contiguous regions. In other words, if there are two type batches, there will be at least two jobs- even if the type batches
                        //only have one constraint each. 
                        //A job also only covers either bodies or constraints, not both at once.
                        //TODO: This job scheduling pattern appears frequently. Would be nice to unify it. Obvious zero overhead approach with generics abuse.
                        {
                            var jobCount = Math.Max(1, island.BodyIndices.Count / objectsPerGatherJob);
                            var bodiesPerJob = island.BodyIndices.Count / jobCount;
                            var remainder = island.BodyIndices.Count - bodiesPerJob * jobCount;
                            var previousEnd = 0;
                            gatheringJobs.EnsureCapacity(gatheringJobs.Count + jobCount, gatheringJobPool);
                            for (int i = 0; i < jobCount; ++i)
                            {
                                var bodiesInJob = i < remainder ? bodiesPerJob + 1 : bodiesPerJob;
                                ref var job = ref gatheringJobs.AllocateUnsafely();
                                job.IsBodyJob = true;
                                job.SourceIndices = island.BodyIndices;
                                job.StartIndex = previousEnd;
                                previousEnd += bodiesInJob;
                                job.EndIndex = previousEnd;
                                job.TargetSetIndex = setIndex;
                            }
                        }

                        for (int batchIndex = 0; batchIndex < island.Protobatches.Count; ++batchIndex)
                        {
                            ref var sourceBatch = ref island.Protobatches[batchIndex];
                            for (int typeBatchIndex = 0; typeBatchIndex < sourceBatch.TypeBatches.Count; ++typeBatchIndex)
                            {
                                ref var sourceTypeBatch = ref sourceBatch.TypeBatches[typeBatchIndex];
                                var jobCount = Math.Max(1, sourceTypeBatch.Handles.Count / objectsPerGatherJob);
                                var constraintsPerJob = sourceTypeBatch.Handles.Count / jobCount;
                                var remainder = sourceTypeBatch.Handles.Count - constraintsPerJob * jobCount;
                                var previousEnd = 0;
                                gatheringJobs.EnsureCapacity(gatheringJobs.Count + jobCount, gatheringJobPool);
                                for (int i = 0; i < jobCount; ++i)
                                {
                                    var constraintsInJob = i < remainder ? constraintsPerJob + 1 : constraintsPerJob;
                                    ref var job = ref gatheringJobs.AllocateUnsafely();
                                    job.IsBodyJob = false;
                                    job.SourceIndices = sourceTypeBatch.Handles;
                                    job.StartIndex = previousEnd;
                                    previousEnd += constraintsInJob;
                                    job.EndIndex = previousEnd;
                                    job.TargetSetIndex = setIndex;
                                    job.TargetBatchIndex = batchIndex;
                                    job.TargetTypeBatchIndex = typeBatchIndex;
                                }
                            }
                        }
                    }
                }
            }

            jobIndex = -1;
            if (threadCount > 1)
            {
                threadDispatcher.DispatchWorkers(gatherDelegate);
                //The source of traversal worker resources is a per-thread pool.
                for (int workerIndex = 0; workerIndex < threadCount; ++workerIndex)
                {
                    workerTraversalResults[workerIndex].Dispose(threadDispatcher.GetThreadMemoryPool(workerIndex));
                }
            }
            else
            {
                Gather(0);
                //The source of traversal worker resources was the main pool since it's running in single threaded mode.
                workerTraversalResults[0].Dispose(pool);
            }
            pool.SpecializeFor<WorkerTraversalResults>().Return(ref workerTraversalResults);
            gatheringJobs.Dispose(pool.SpecializeFor<GatheringJob>());
        }

        void AllocateConstraintSet(ref QuickList<IslandProtoConstraintBatch, Buffer<IslandProtoConstraintBatch>> batches, Solver solver, BufferPool pool, out ConstraintSet constraintSet)
        {
            constraintSet = new ConstraintSet(pool, batches.Count);
            for (int i = 0; i < batches.Count; ++i)
            {
                ref var sourceBatch = ref batches[i];
                ref var targetBatch = ref constraintSet.Batches.AllocateUnsafely();
                pool.SpecializeFor<int>().Take(sourceBatch.TypeIdToIndex.Length, out targetBatch.TypeIndexToTypeBatchIndex);
                QuickList<TypeBatch, Buffer<TypeBatch>>.Create(pool.SpecializeFor<TypeBatch>(), sourceBatch.TypeBatches.Count, out targetBatch.TypeBatches);
                sourceBatch.TypeIdToIndex.CopyTo(0, ref targetBatch.TypeIndexToTypeBatchIndex, 0, targetBatch.TypeIndexToTypeBatchIndex.Length);
                for (int j = 0; j < sourceBatch.TypeBatches.Count; ++j)
                {
                    ref var sourceTypeBatch = ref sourceBatch.TypeBatches[j];
                    ref var targetTypeBatch = ref targetBatch.GetOrCreateTypeBatch(sourceTypeBatch.TypeId, solver.TypeProcessors[sourceTypeBatch.TypeId], sourceTypeBatch.Handles.Count, pool);
                }

            }
        }

        /// <summary>
        /// Ensures that the Bodies and Solver can hold at least the given number of sets (BodySets for the Bodies collection, ConstraintSets for the Solver).
        /// </summary>
        /// <param name="setsCapacity">Number of sets to guarantee space for.</param>
        public void EnsureSetsCapacity(int setsCapacity)
        {
            var potentiallyAllocatedCount = setIdPool.HighestPossiblyClaimedId + 1;
            if (setsCapacity > bodies.Sets.Length)
            {
                bodies.ResizeSetsCapacity(setsCapacity, potentiallyAllocatedCount);
            }
            if (setsCapacity > solver.Sets.Length)
            {
                solver.ResizeSetsCapacity(setsCapacity, potentiallyAllocatedCount);
            }
        }

        /// <summary>
        /// Ensures that the Bodies and Solver can hold the given number of sets. 
        /// If the existing allocation is smaller than the requested sets capacity, the allocation will be enlarged.
        /// If the existing allocation is larger than both the existing potentially allocated set range and the requested sets capacity, the allocation will be shrunk.
        /// Shrinks will never cause an existing set to be lost.
        /// </summary>
        /// <param name="setsCapacity">Target number of sets to allocate space for.</param>
        public void ResizeSetsCapacity(int setsCapacity)
        {
            var potentiallyAllocatedCount = setIdPool.HighestPossiblyClaimedId + 1;
            setsCapacity = Math.Max(potentiallyAllocatedCount, setsCapacity);
            bodies.ResizeSetsCapacity(setsCapacity, potentiallyAllocatedCount);
            solver.ResizeSetsCapacity(setsCapacity, potentiallyAllocatedCount);
        }

        public void Clear()
        {
            setIdPool.Clear();
            //Slot 0 is reserved for the active set.
            setIdPool.Take();
        }

        public void Dispose()
        {
            setIdPool.Dispose(pool.SpecializeFor<int>());
        }



    }
}