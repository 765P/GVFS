﻿using FastFetch.Jobs.Data;
using GVFS.Common;
using GVFS.Common.Git;
using GVFS.Common.Tracing;
using Microsoft.Diagnostics.Tracing;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace FastFetch.Jobs
{
    /// <summary>
    /// Takes in blocks of object shas, downloads object shas as a pack or loose object, outputs pack locations (if applicable).
    /// </summary>
    public class BatchObjectDownloadJob : Job
    {
        private const string AreaPath = "BatchObjectDownloadJob";
        private const string DownloadAreaPath = "Download";
        
        private static readonly TimeSpan HeartBeatPeriod = TimeSpan.FromSeconds(20);

        private readonly BlockingAggregator<string, BlobDownloadRequest> inputQueue;

        private int activeDownloadCount;

        private ITracer tracer;
        private Enlistment enlistment;
        private HttpGitObjects httpGitObjects;
        private GitObjects gitObjects;
        private Timer heartbeat;

        private long bytesDownloaded = 0;

        public BatchObjectDownloadJob(
            int maxParallel,
            int chunkSize,
            BlockingCollection<string> inputQueue,
            BlockingCollection<string> availableBlobs,
            ITracer tracer,
            Enlistment enlistment,
            HttpGitObjects httpGitObjects,
            GitObjects gitObjects)
            : base(maxParallel)
        {
            this.tracer = tracer.StartActivity(AreaPath, EventLevel.Informational);
            
            this.inputQueue = new BlockingAggregator<string, BlobDownloadRequest>(inputQueue, chunkSize, objectIds => new BlobDownloadRequest(objectIds));

            this.enlistment = enlistment;
            this.httpGitObjects = httpGitObjects;

            this.gitObjects = gitObjects;

            this.AvailablePacks = new BlockingCollection<IndexPackRequest>();
            this.AvailableObjects = availableBlobs;
        }

        public BlockingCollection<IndexPackRequest> AvailablePacks { get; }

        public BlockingCollection<string> AvailableObjects { get; }

        protected override void DoBeforeWork()
        {
            this.heartbeat = new Timer(this.EmitHeartbeat, null, TimeSpan.Zero, HeartBeatPeriod);
            base.DoBeforeWork();
        }

        protected override void DoWork()
        {
            BlobDownloadRequest request;
            while (this.inputQueue.TryTake(out request))
            {
                Interlocked.Increment(ref this.activeDownloadCount);

                EventMetadata metadata = new EventMetadata();
                metadata.Add("PackId", request.PackId);
                metadata.Add("ActiveDownloads", this.activeDownloadCount);
                metadata.Add("NumberOfObjects", request.ObjectIds.Count);

                using (ITracer activity = this.tracer.StartActivity(DownloadAreaPath, EventLevel.Informational, metadata))
                {
                    try
                    {
                        RetryWrapper<HttpGitObjects.GitObjectTaskResult>.InvocationResult result;

                        if (request.ObjectIds.Count == 1)
                        {
                            result = this.httpGitObjects.TryDownloadLooseObject(
                                request.ObjectIds[0],
                                onSuccess: (tryCount, response) => this.WriteObjectOrPackAsync(request, tryCount, response),
                                onFailure: RetryWrapper<HttpGitObjects.GitObjectTaskResult>.StandardErrorHandler(activity, DownloadAreaPath));
                        }
                        else
                        {
                            HashSet<string> successfulDownloads = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            result = this.httpGitObjects.TryDownloadObjects(
                                () => request.ObjectIds.Except(successfulDownloads),
                                commitDepth: 1,
                                onSuccess: (tryCount, response) => this.WriteObjectOrPackAsync(request, tryCount, response, successfulDownloads),
                                onFailure: RetryWrapper<HttpGitObjects.GitObjectTaskResult>.StandardErrorHandler(activity, DownloadAreaPath),
                                preferBatchedLooseObjects: true);
                        }

                        if (!result.Succeeded)
                        {
                            this.HasFailures = true;
                        }

                        metadata.Add("Success", result.Succeeded);
                        metadata.Add("AttemptNumber", result.Attempts);
                        metadata["ActiveDownloads"] = this.activeDownloadCount - 1;
                        activity.Stop(metadata);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref this.activeDownloadCount);
                    }
                }
            }
        }

        protected override void DoAfterWork()
        {
            this.heartbeat.Dispose();
            this.heartbeat = null;

            this.AvailablePacks.CompleteAdding();
            EventMetadata metadata = new EventMetadata();
            metadata.Add("RequestCount", BlobDownloadRequest.TotalRequests);
            metadata.Add("BytesDownloaded", this.bytesDownloaded);
            this.tracer.Stop(metadata);
        }

        private RetryWrapper<HttpGitObjects.GitObjectTaskResult>.CallbackResult WriteObjectOrPackAsync(
            BlobDownloadRequest request,
            int tryCount,
            HttpGitObjects.GitEndPointResponseData response,
            HashSet<string> successfulDownloads = null)
        {
            string fileName = null;
            switch (response.ContentType)
            {
                case HttpGitObjects.ContentType.LooseObject:
                    string sha = request.ObjectIds.First();
                    fileName = this.gitObjects.WriteLooseObject(
                        this.enlistment.WorkingDirectoryRoot,
                        response.Stream,
                        sha);
                    this.AvailableObjects.Add(sha);
                    break;
                case HttpGitObjects.ContentType.PackFile:
                    fileName = this.gitObjects.WriteTempPackFile(response);
                    this.AvailablePacks.Add(new IndexPackRequest(fileName, request));
                    break;
                case HttpGitObjects.ContentType.BatchedLooseObjects:
                    // To reduce allocations, reuse the same buffer when writing objects in this batch
                    byte[] bufToCopyWith = new byte[StreamUtil.DefaultCopyBufferSize];

                    OnLooseObject onLooseObject = (objectStream, sha1) =>
                    {
                        this.gitObjects.WriteLooseObject(
                            this.enlistment.WorkingDirectoryRoot,
                            objectStream,
                            sha1,
                            bufToCopyWith);
                        this.AvailableObjects.Add(sha1);

                        if (successfulDownloads != null)
                        {
                            successfulDownloads.Add(sha1);
                        }

                        // This isn't strictly correct because we don't add object header bytes,
                        // just the actual compressed content length, but we expect the amount of
                        // header data to be negligible compared to the objects themselves.
                        Interlocked.Add(ref this.bytesDownloaded, objectStream.Length);
                    };

                    new BatchedLooseObjectDeserializer(response.Stream, onLooseObject).ProcessObjects();
                    break;
            }

            if (fileName != null)
            {
                // NOTE: If we are writing a file as part of this method, the only case
                // where it's not expected to exist is when running unit tests
                FileInfo info = new FileInfo(fileName);
                if (info.Exists)
                {
                    Interlocked.Add(ref this.bytesDownloaded, info.Length);
                }
                else
                {
                    return new RetryWrapper<HttpGitObjects.GitObjectTaskResult>.CallbackResult(
                        new HttpGitObjects.GitObjectTaskResult(false));
                }
            }

            return new RetryWrapper<HttpGitObjects.GitObjectTaskResult>.CallbackResult(
                new HttpGitObjects.GitObjectTaskResult(true));
        }

        private void EmitHeartbeat(object state)
        {
            EventMetadata metadata = new EventMetadata();
            metadata["ActiveDownloads"] = this.activeDownloadCount;
            this.tracer.RelatedEvent(EventLevel.Verbose, "DownloadHeartbeat", metadata);
        }

        private class BlockingAggregator<InputType, OutputType>
        {
            private BlockingCollection<InputType> inputQueue;
            private int chunkSize;
            private Func<List<InputType>, OutputType> factory;

            public BlockingAggregator(BlockingCollection<InputType> input, int chunkSize, Func<List<InputType>, OutputType> factory)
            {
                this.inputQueue = input;
                this.chunkSize = chunkSize;
                this.factory = factory;
            }

            public bool TryTake(out OutputType output)
            {
                List<InputType> intermediary = new List<InputType>();
                for (int i = 0; i < this.chunkSize; ++i)
                {
                    InputType data;
                    if (this.inputQueue.TryTake(out data, millisecondsTimeout: -1))
                    {
                        intermediary.Add(data);
                    }
                    else
                    {
                        break;
                    }
                }

                if (intermediary.Any())
                {
                    output = this.factory(intermediary);
                    return true;
                }

                output = default(OutputType);
                return false;
            }
        }
    }
}