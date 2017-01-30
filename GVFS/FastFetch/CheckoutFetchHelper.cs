﻿using FastFetch.Git;
using FastFetch.Jobs;
using FastFetch.Jobs.Data;
using GVFS.Common;
using GVFS.Common.Git;
using GVFS.Common.Tracing;
using Microsoft.Diagnostics.Tracing;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FastFetch
{
    public class CheckoutFetchHelper : FetchHelper
    {
        private const string AreaPath = nameof(CheckoutFetchHelper);

        private int checkoutThreadCount;

        public CheckoutFetchHelper(
            ITracer tracer,
            Enlistment enlistment,
            int chunkSize,
            int searchThreadCount,
            int downloadThreadCount,
            int indexThreadCount,
            int checkoutThreadCount) : base(tracer, enlistment, chunkSize, searchThreadCount, downloadThreadCount, indexThreadCount)
        {
            this.checkoutThreadCount = checkoutThreadCount;
        }

        /// <param name="branchOrCommit">A specific branch to filter for, or null for all branches returned from info/refs</param>
        public override void FastFetch(string branchOrCommit, bool isBranch)
        {
            if (string.IsNullOrWhiteSpace(branchOrCommit))
            {
                throw new FetchException("Must specify branch or commit to fetch");
            }

            GitRefs refs = null;
            string commitToFetch;
            if (isBranch)
            {
                refs = this.HttpGitObjects.QueryInfoRefs(branchOrCommit);
                if (refs == null)
                {
                    throw new FetchException("Could not query info/refs from: {0}", this.Enlistment.RepoUrl);
                }
                else if (refs.Count == 0)
                {
                    throw new FetchException("Could not find branch {0} in info/refs from: {1}", branchOrCommit, this.Enlistment.RepoUrl);
                }

                commitToFetch = refs.GetTipCommitIds().Single();
            }
            else
            {
                commitToFetch = branchOrCommit;
            }

            this.DownloadMissingCommit(commitToFetch, this.GitObjects);
            
            // Configure pipeline
            // Checkout uses DiffHelper when running checkout.Start(), which we use instead of LsTreeHelper like in FetchHelper.cs
            // Checkout diff output => FindMissingBlobs => BatchDownload => IndexPack => Checkout available blobs
            CheckoutJob checkout = new CheckoutJob(this.checkoutThreadCount, this.PathWhitelist, commitToFetch, this.Tracer, this.Enlistment);
            FindMissingBlobsJob blobFinder = new FindMissingBlobsJob(this.SearchThreadCount, checkout.RequiredBlobs, checkout.AvailableBlobShas, this.Tracer, this.Enlistment);            
            BatchObjectDownloadJob downloader = new BatchObjectDownloadJob(this.DownloadThreadCount, this.ChunkSize, blobFinder.DownloadQueue, checkout.AvailableBlobShas, this.Tracer, this.Enlistment, this.HttpGitObjects, this.GitObjects);
            IndexPackJob packIndexer = new IndexPackJob(this.IndexThreadCount, downloader.AvailablePacks, checkout.AvailableBlobShas, this.Tracer, this.GitObjects);

            // Start pipeline
            downloader.Start();
            blobFinder.Start();
            checkout.Start();
            
            blobFinder.WaitForCompletion();
            this.HasFailures |= blobFinder.HasFailures;

            // Delay indexing. It interferes with FindMissingBlobs, and doesn't help Bootstrapping.
            packIndexer.Start();

            downloader.WaitForCompletion();
            this.HasFailures |= downloader.HasFailures;

            packIndexer.WaitForCompletion();
            this.HasFailures |= packIndexer.HasFailures;

            // Since pack indexer is the last to finish before checkout finishes, it should propagate completion.
            // This prevents availableObjects from completing before packIndexer can push its objects through this link.
            checkout.AvailableBlobShas.CompleteAdding();
            checkout.WaitForCompletion();
            this.HasFailures |= checkout.HasFailures;

            if (!this.SkipConfigUpdate)
            {
                this.UpdateRefs(branchOrCommit, isBranch, refs);

                if (!this.HasFailures)
                {
                    if (isBranch)
                    {
                        // Update the refspec before setting the upstream or git will complain the remote branch doesn't exist
                        this.HasFailures |= !RefSpecHelpers.UpdateRefSpec(this.Tracer, this.Enlistment, branchOrCommit, refs);

                        using (ITracer activity = this.Tracer.StartActivity("SetUpstream", EventLevel.Informational))
                        {
                            string remoteBranch = refs.GetBranchRefPairs().Single().Key;
                            GitProcess git = new GitProcess(this.Enlistment);
                            GitProcess.Result result = git.SetUpstream(branchOrCommit, remoteBranch);
                            if (result.HasErrors)
                            {
                                activity.RelatedError("Could not set upstream for {0} to {1}: {2}", branchOrCommit, remoteBranch, result.Errors);
                                this.HasFailures = true;
                            }
                        }
                    }

                    // Update the index
                    using (ITracer activity = this.Tracer.StartActivity("UpdateIndex", EventLevel.Informational))
                    {
                        GitProcess git = new GitProcess(this.Enlistment);
                        GitProcess.Result result = git.ReadTree("HEAD");
                        if (result.HasErrors)
                        {
                            activity.RelatedError("Could not read HEAD tree to update index: " + result.Errors);
                            this.HasFailures = true;
                        }
                    }
                }
            }
        }
        
        /// <summary>
        /// * Updates local branch (N/A for checkout to detached HEAD)
        /// * Updates HEAD
        /// * Calls base to update shallow file and remote branch.
        /// </summary>
        protected override void UpdateRefs(string branchOrCommit, bool isBranch, GitRefs refs)
        {
            UpdateRefsHelper refHelper = new UpdateRefsHelper(this.Enlistment);

            if (isBranch)
            {
                KeyValuePair<string, string> remoteRef = refs.GetBranchRefPairs().Single();
                string remoteBranch = remoteRef.Key;

                string fullLocalBranchName = branchOrCommit.StartsWith("refs/heads/") ? branchOrCommit : ("refs/heads/" + branchOrCommit);
                this.HasFailures |= !refHelper.UpdateRef(this.Tracer, fullLocalBranchName, remoteRef.Value);
                this.HasFailures |= !refHelper.UpdateRef(this.Tracer, "HEAD", fullLocalBranchName);
            }
            else
            {
                this.HasFailures |= !refHelper.UpdateRef(this.Tracer, "HEAD", branchOrCommit);
            }

            base.UpdateRefs(branchOrCommit, isBranch, refs);
        }
    }
}
