﻿using GVFS.Tests.Should;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace GVFS.FunctionalTests.Tools
{
    public static class GitHelpers
    {
        public const string ExcludeFilePath = @".git\info\exclude";
        private const int MaxRetries = 10;
        private const int ThreadSleepMS = 1500;

        public static void CheckGitCommand(string virtualRepoRoot, string command, params string[] expectedLinesInResult)
        {
            ProcessResult result = GitProcess.InvokeProcess(virtualRepoRoot, command);

            foreach (string line in expectedLinesInResult)
            {
                result.Output.ShouldContain(line);
            }

            result.Errors.ShouldBeEmpty();
        }

        public static void CheckNotInGitCommand(string virtualRepoRoot, string command, params string[] unexpectedLinesInResult)
        {
            ProcessResult result = GitProcess.InvokeProcess(virtualRepoRoot, command);

            foreach (string line in unexpectedLinesInResult)
            {
                result.Output.ShouldNotContain(line);
            }

            result.Errors.ShouldBeEmpty();
        }

        public static ProcessResult InvokeGitAgainstGVFSRepo(string gvfsRepoRoot, string command, bool cleanOutput = true)
        {
            ProcessResult result = GitProcess.InvokeProcess(gvfsRepoRoot, command);

            string output = result.Output;
            if (cleanOutput)
            {
                string[] lines = output.Split(new string[] { "\r\n" }, StringSplitOptions.None);
                output = string.Join("\r\n", lines.Where(line => !line.StartsWith("Waiting for ")));
            }

            return new ProcessResult(
                output,
                result.Errors,
                result.ExitCode);
        }

        public static void ValidateGitCommand(
            GVFSFunctionalTestEnlistment enlistment,
            ControlGitRepo controlGitRepo,
            string command,
            params object[] args)
        {
            string controlRepoRoot = controlGitRepo.RootPath;
            string gvfsRepoRoot = enlistment.RepoRoot;

            command = string.Format(command, args);
            ProcessResult expectedResult = GitProcess.InvokeProcess(controlRepoRoot, command);
            ProcessResult actualResult = GitHelpers.InvokeGitAgainstGVFSRepo(gvfsRepoRoot, command);
            actualResult.Output.ShouldEqual(expectedResult.Output);
            actualResult.Errors.ShouldEqual(expectedResult.Errors);

            if (command != "status")
            {
                ValidateGitCommand(enlistment, controlGitRepo, "status");
            }
        }

        public static ManualResetEventSlim AcquireGVFSLock(GVFSFunctionalTestEnlistment enlistment, int resetTimeout = Timeout.Infinite)
        {
            ManualResetEventSlim resetEvent = new ManualResetEventSlim(initialState: false);

            ProcessStartInfo processInfo = new ProcessStartInfo(Properties.Settings.Default.PathToGit);
            processInfo.WorkingDirectory = enlistment.RepoRoot;
            processInfo.UseShellExecute = false;
            processInfo.RedirectStandardOutput = true;
            processInfo.RedirectStandardError = true;
            processInfo.RedirectStandardInput = true;
            processInfo.Arguments = "hash-object --stdin";

            Process holdingProcess = Process.Start(processInfo);
            StreamWriter stdin = holdingProcess.StandardInput;

            enlistment.WaitForLock("git hash-object --stdin");

            Task.Run(
                () =>
                {
                    resetEvent.Wait(resetTimeout);

                    // Make sure to let the holding process end.
                    if (stdin != null)
                    {
                        stdin.WriteLine("dummy");
                        stdin.Close();
                    }

                    if (holdingProcess != null)
                    {
                        if (!holdingProcess.HasExited)
                        {
                            holdingProcess.Kill();
                        }

                        holdingProcess.Dispose();
                    }
                });

            return resetEvent;
        }
    }
}
