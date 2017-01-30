﻿using GVFS.Common;
using GVFS.Tests.Should;
using GVFS.UnitTests.Category;
using NUnit.Framework;
using System;
using System.IO;
using System.Threading.Tasks;

namespace GVFS.UnitTests.Common
{
    [TestFixture]
    public class RetryWrapperTests
    {
        [TestCase]
        [Category(CategoryContants.ExceptionExpected)]
        public void WillRetryOnIOException()
        {
            const int ExpectedTries = 5;

            RetryWrapper<bool> dut = new RetryWrapper<bool>(ExpectedTries, exponentialBackoffBase: 0);

            int actualTries = 0;
            RetryWrapper<bool>.InvocationResult output = dut.InvokeAsync(
                tryCount =>
                {
                    actualTries++;
                    throw new IOException();
                }).Result;

            output.Succeeded.ShouldEqual(false);
            actualTries.ShouldEqual(ExpectedTries);
        }

        [TestCase]
        [Category(CategoryContants.ExceptionExpected)]
        public void WillNotRetryForGenericExceptions()
        {
            const int MaxTries = 5;

            RetryWrapper<bool> dut = new RetryWrapper<bool>(MaxTries, exponentialBackoffBase: 0);

            Assert.Throws<AggregateException>(
                () =>
                {
                    RetryWrapper<bool>.InvocationResult output = dut.InvokeAsync(tryCount => { throw new Exception(); }).Result;
                });
        }

        [TestCase]
        [Category(CategoryContants.ExceptionExpected)]
        public void OnFailureIsCalledWhenEventHandlerAttached()
        {
            const int MaxTries = 5;
            const int ExpectedFailures = 5;

            RetryWrapper<bool> dut = new RetryWrapper<bool>(MaxTries, exponentialBackoffBase: 0);

            int actualFailures = 0;
            dut.OnFailure += errorArgs => actualFailures++;

            RetryWrapper<bool>.InvocationResult output = dut.InvokeAsync(
                tryCount =>
                {
                    throw new IOException();
                }).Result;

            output.Succeeded.ShouldEqual(false);
            actualFailures.ShouldEqual(ExpectedFailures);
        }

        [TestCase]
        public void OnSuccessIsOnlyCalledOnce()
        {
            const int MaxTries = 5;
            const int ExpectedFailures = 0;
            const int ExpectedTries = 1;

            RetryWrapper<bool> dut = new RetryWrapper<bool>(MaxTries, exponentialBackoffBase: 0);

            int actualFailures = 0;
            dut.OnFailure += errorArgs => actualFailures++;

            int actualTries = 0;
            RetryWrapper<bool>.InvocationResult output = dut.InvokeAsync(
                tryCount =>
                {
                    actualTries++;
                    return Task.Run(() => new RetryWrapper<bool>.CallbackResult(true));
                }).Result;

            output.Succeeded.ShouldEqual(true);
            output.Result.ShouldEqual(true);
            actualTries.ShouldEqual(ExpectedTries);
            actualFailures.ShouldEqual(ExpectedFailures);
        }

        [TestCase]
        public void WillNotRetryWhenNotRequested()
        {
            const int MaxTries = 5;
            const int ExpectedFailures = 1;
            const int ExpectedTries = 1;

            RetryWrapper<bool> dut = new RetryWrapper<bool>(MaxTries, exponentialBackoffBase: 0);

            int actualFailures = 0;
            dut.OnFailure += errorArgs => actualFailures++;

            int actualTries = 0;
            RetryWrapper<bool>.InvocationResult output = dut.InvokeAsync(
                tryCount =>
                {
                    actualTries++;
                    return Task.Run(() => new RetryWrapper<bool>.CallbackResult(new Exception("Test"), false));
                }).Result;

            output.Succeeded.ShouldEqual(false);
            output.Result.ShouldEqual(false);
            actualTries.ShouldEqual(ExpectedTries);
            actualFailures.ShouldEqual(ExpectedFailures);
        }

        [TestCase]
        public void WillRetryWhenRequested()
        {
            const int MaxTries = 5;
            const int ExpectedFailures = 5;
            const int ExpectedTries = 5;

            RetryWrapper<bool> dut = new RetryWrapper<bool>(MaxTries, exponentialBackoffBase: 0);

            int actualFailures = 0;
            dut.OnFailure += errorArgs => actualFailures++;

            int actualTries = 0;
            RetryWrapper<bool>.InvocationResult output = dut.InvokeAsync(
                tryCount =>
                {
                    actualTries++;
                    return Task.Run(() => new RetryWrapper<bool>.CallbackResult(new Exception("Test"), true));
                }).Result;

            output.Succeeded.ShouldEqual(false);
            output.Result.ShouldEqual(false);
            actualTries.ShouldEqual(ExpectedTries);
            actualFailures.ShouldEqual(ExpectedFailures);
        }
    }
}
