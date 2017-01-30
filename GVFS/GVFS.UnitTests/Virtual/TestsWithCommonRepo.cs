﻿using NUnit.Framework;

namespace GVFS.UnitTests.Virtual
{
    [TestFixture]
    public abstract class TestsWithCommonRepo
    {
        protected CommonRepoSetup Repo { get; private set; }

        [SetUp]
        public virtual void TestSetup()
        {
            this.Repo = new CommonRepoSetup();
        }
    }
}
