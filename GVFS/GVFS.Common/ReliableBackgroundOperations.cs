﻿using GVFS.Common.Tracing;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Isam.Esent.Collections.Generic;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace GVFS.Common
{
    public class ReliableBackgroundOperations<TBackgroundOperation> : IDisposable where TBackgroundOperation : IBackgroundOperation
    {
        private const int ActionRetryDelayMS = 50;
        private const int MaxCallbackAttemptsOnShutdown = 5;
        private const int LogUpdateTaskThreshold = 25000;
        private static readonly string EtwArea = "ProcessBackgroundOperations";

        private readonly ReaderWriterLockSlim acquisitionLock;
        private PersistentDictionary<Guid, TBackgroundOperation> persistence;

        private ConcurrentQueue<TBackgroundOperation> backgroundOperations;
        private AutoResetEvent wakeUpThread;
        private Task backgroundThread;
        private bool isStopping;

        private GVFSContext context;

        private Func<CallbackResult> preCallback;
        private Func<TBackgroundOperation, CallbackResult> callback;
        private Func<CallbackResult> postCallback;

        public ReliableBackgroundOperations(
            GVFSContext context,
            Func<CallbackResult> preCallback,             
            Func<TBackgroundOperation, CallbackResult> callback,
            Func<CallbackResult> postCallback,
            string databaseName)
        {
            this.acquisitionLock = new ReaderWriterLockSlim();
            this.persistence = new PersistentDictionary<Guid, TBackgroundOperation>(
                Path.Combine(context.Enlistment.DotGVFSRoot, databaseName));

            this.backgroundOperations = new ConcurrentQueue<TBackgroundOperation>();
            this.wakeUpThread = new AutoResetEvent(true);

            this.context = context;
            this.preCallback = preCallback;
            this.callback = callback;
            this.postCallback = postCallback;
        }

        private enum AcquireGitLockResult
        {
            LockAcquired,
            ShuttingDown
        }

        public int Count
        {
            get { return this.backgroundOperations.Count; }
        }

        public void Start()
        {
            this.EnqueueSavedOperations();
            this.backgroundThread = Task.Run((Action)this.ProcessBackgroundOperations);
            if (this.backgroundOperations.Count > 0)
            {
                this.wakeUpThread.Set();
            }
        }

        public void Enqueue(TBackgroundOperation backgroundOperation)
        {
            this.persistence[backgroundOperation.Id] = backgroundOperation;
            this.persistence.Flush();

            if (!this.isStopping)
            {
                this.backgroundOperations.Enqueue(backgroundOperation);
                this.wakeUpThread.Set();
            }
        }

        public void Shutdown()
        {
            this.isStopping = true;
            this.wakeUpThread.Set();
            this.backgroundThread.Wait();
        }

        public void ObtainAcquisitionLock()
        {
            this.acquisitionLock.EnterReadLock();
        }

        public void ReleaseAcquisitionLock()
        {
            if (this.acquisitionLock.IsReadLockHeld)
            {
                this.acquisitionLock.ExitReadLock();
            }
        }

        public void Dispose()
        {
            this.Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected void Dispose(bool disposing)
        {
            if (this.persistence != null)
            {
                this.persistence.Dispose();
                this.persistence = null;
            }

            if (this.backgroundThread != null)
            {
                this.backgroundThread.Dispose();
                this.backgroundThread = null;
            }
        }

        private void EnqueueSavedOperations()
        {
            foreach (Guid operationId in this.persistence.Keys)
            {
                // We are setting the Id here because there may be old operations that
                // were persisted without the Id begin set in the background operation object
                TBackgroundOperation backgroundOperation = this.persistence[operationId];
                backgroundOperation.Id = operationId;
                this.backgroundOperations.Enqueue(backgroundOperation);
            }
        }

        private AcquireGitLockResult WaitToAcquireGitLock()
        {
            while (!this.context.Repository.GVFSLock.TryAcquireLock())
            {
                if (this.isStopping)
                {
                    return AcquireGitLockResult.ShuttingDown;
                }

                Thread.Sleep(ActionRetryDelayMS);
            }

            return AcquireGitLockResult.LockAcquired;
        }

        private void ReleaseGitLockIfNecessary()
        {
            try
            {
                // Only release GVFS lock if the queue is empty. If it's not empty then another thread
                // added something to the queue, allow it to continue processing.
                while (this.backgroundOperations.IsEmpty)
                {
                    // An external caller (eg. GVFLT callback) will hold reader status while adding something to the queue.
                    // If unable to enter writer status, wait and try again if the queue is still empty.
                    if (this.acquisitionLock.TryEnterWriteLock(millisecondsTimeout: 10))
                    {
                        this.context.Repository.GVFSLock.ReleaseLock();
                        break;
                    }

                    Thread.Sleep(millisecondsTimeout: 10);
                }
            }
            catch (Exception e)
            {
                this.LogErrorAndExit("gitLock.TryReleaseLock threw Exception, shutting down", e);
            }
            finally
            {
                if (this.acquisitionLock.IsWriteLockHeld)
                {
                    this.acquisitionLock.ExitWriteLock();
                }
            }
        }
     
        private void ProcessBackgroundOperations()
        {
            TBackgroundOperation backgroundOperation;

            while (true)
            {
                AcquireGitLockResult acquireLockResult = AcquireGitLockResult.ShuttingDown;

                try
                {
                    this.wakeUpThread.WaitOne();

                    if (this.isStopping)
                    {
                        return;
                    }

                    acquireLockResult = this.WaitToAcquireGitLock();
                    switch (acquireLockResult)
                    {
                        case AcquireGitLockResult.LockAcquired:
                            break;
                        case AcquireGitLockResult.ShuttingDown:
                            return;
                        default:
                            this.LogErrorAndExit("Invalid AcquireGitLockResult result");
                            return;
                    }

                    this.RunCallbackUntilSuccess(this.preCallback, "PreCallback");

                    int tasksProcessed = 0;
                    while (this.backgroundOperations.TryPeek(out backgroundOperation))
                    {
                        if (tasksProcessed % LogUpdateTaskThreshold == 0 && 
                            (tasksProcessed >= LogUpdateTaskThreshold || this.backgroundOperations.Count >= LogUpdateTaskThreshold))
                        {
                            this.LogTaskProcessingStatus(tasksProcessed);
                        }

                        if (this.isStopping)
                        {
                            // If we are stopping, then GVFlt has already been shut down
                            // Some of the queued background tasks may require GVFlt, and so it is unsafe to
                            // proceed.  GVFS will resume any queued tasks next time it is mounted
                            this.persistence.Flush();
                            return;
                        }
                        
                        CallbackResult callbackResult = this.callback(backgroundOperation);
                        switch (callbackResult)
                        {
                            case CallbackResult.Success:                                
                                this.backgroundOperations.TryDequeue(out backgroundOperation);
                                this.persistence.Remove(backgroundOperation.Id);                                
                                ++tasksProcessed;
                                break;

                            case CallbackResult.RetryableError:
                                if (!this.isStopping)
                                {
                                    Thread.Sleep(ActionRetryDelayMS);
                                }

                                break;

                            case CallbackResult.FatalError:
                                this.LogErrorAndExit("Callback encountered fatal error, exiting process");
                                break;

                            default:
                                this.LogErrorAndExit("Invalid background operation result");
                                break;
                        }
                    }

                    this.persistence.Flush();

                    if (tasksProcessed >= LogUpdateTaskThreshold)
                    {
                        EventMetadata metadata = new EventMetadata();
                        metadata.Add("BackgroundOperations", EtwArea);
                        metadata.Add("TasksProcessed", tasksProcessed);
                        metadata.Add("Message", "Processing background tasks complete");
                        this.context.Tracer.RelatedEvent(EventLevel.Informational, "TaskProcessingStatus", metadata);
                    }

                    if (this.isStopping)
                    {
                        return;
                    }
                }
                catch (Exception e)
                {
                    this.LogErrorAndExit("ProcessBackgroundOperations caught unhandled exception, exiting process", e);
                }
                finally
                {
                    if (acquireLockResult == AcquireGitLockResult.LockAcquired)
                    {
                        this.RunCallbackUntilSuccess(this.postCallback, "PostCallback");
                        this.ReleaseGitLockIfNecessary();
                    }
                }
            }
        }

        private void RunCallbackUntilSuccess(Func<CallbackResult> callback, string errorHeader)
        {
            while (true)
            {
                CallbackResult callbackResult = callback();
                switch (callbackResult)
                {
                    case CallbackResult.Success:
                        return;

                    case CallbackResult.RetryableError:
                        if (this.isStopping)
                        {
                            return;
                        }

                        Thread.Sleep(ActionRetryDelayMS);
                        break;

                    case CallbackResult.FatalError:
                        this.LogErrorAndExit(errorHeader + " encountered fatal error, exiting process");
                        return;

                    default:
                        this.LogErrorAndExit(errorHeader + " result could not be found");
                        return;
                }
            }
        }

        private void LogWarning(string message)
        {
            EventMetadata metadata = new EventMetadata();
            metadata.Add("Area", EtwArea);
            metadata.Add("Message", message);
            this.context.Tracer.RelatedEvent(EventLevel.Warning, "Warning", metadata);
        }

        private void LogError(string message, Exception e = null)
        {
            this.LogError(message, e, exit: false);
        }

        private void LogErrorAndExit(string message, Exception e = null)
        {
            this.LogError(message, e, exit: true);
        }

        private void LogError(string message, Exception e, bool exit)
        {
            EventMetadata metadata = new EventMetadata();
            metadata.Add("Area", EtwArea);
            if (e != null)
            {
                metadata.Add("Exception", e.ToString());
            }

            metadata.Add("ErrorMessage", message);
            this.context.Tracer.RelatedError(metadata);
            if (exit)
            {
                Environment.Exit(1);
            }
        }

        private void LogTaskProcessingStatus(int tasksProcessed)
        {
            EventMetadata metadata = new EventMetadata();
            metadata.Add("BackgroundOperations", EtwArea);
            metadata.Add("TasksProcessed", tasksProcessed);
            metadata.Add("TasksRemaining", this.backgroundOperations.Count);
            this.context.Tracer.RelatedEvent(EventLevel.Informational, "TaskProcessingStatus", metadata);
        }
    }
}
