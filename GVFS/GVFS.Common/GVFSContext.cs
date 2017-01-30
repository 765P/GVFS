﻿using GVFS.Common.Physical.FileSystem;
using GVFS.Common.Physical.Git;
using GVFS.Common.Tracing;
using System;

namespace GVFS.Common
{
    public class GVFSContext : IDisposable
    {
        private bool disposedValue = false;
        
        public GVFSContext(ITracer tracer, PhysicalFileSystem fileSystem, GitRepo repository, GVFSEnlistment enlistment)
        {
            this.Tracer = tracer;
            this.FileSystem = fileSystem;
            this.Enlistment = enlistment;
            this.Repository = repository;
        }

        public ITracer Tracer { get; private set; }
        public PhysicalFileSystem FileSystem { get; private set; }
        public GitRepo Repository { get; private set; }
        public GVFSEnlistment Enlistment { get; private set; }

        public void Dispose()
        {
            this.Dispose(true);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!this.disposedValue)
            {
                if (disposing)
                {
                    this.Repository.Dispose();
                    this.Tracer.Dispose();
                    this.Tracer = null;
                }

                this.disposedValue = true;
            }
        }
    }
}
