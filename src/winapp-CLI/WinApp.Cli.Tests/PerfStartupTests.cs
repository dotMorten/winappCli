// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection.PortableExecutable;
using Microsoft.Extensions.Logging.Abstractions;
using WinApp.Cli.Commands;
using WinApp.Cli.Services;
using WinApp.Cli.Services.Performance;

namespace WinApp.Cli.Tests;

[TestClass]
[DoNotParallelize]
public sealed class PerfStartupTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PrivateSessionBecomesReadyBeforeTheApplicationEntryPointRuns(bool wow64)
    {
        var directory = Directory.CreateTempSubdirectory("WinApp-Perf-Startup-");
        PrivateEtwSession? session = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(40));
        var marker = Path.Join(directory.FullName, "main-ran");
        try
        {
            var launcher = new AppLauncherService(NullLogger<AppLauncherService>.Instance);
            using var process = await launcher.LaunchExecutableForProfilingAsync(
                Path.Join(wow64 ? Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SysWOW64") :
                    Environment.SystemDirectory, "cmd.exe"), $"/d /c echo started>\"{marker}\" & exit /b 7",
                directory.FullName, LaunchStdioMode.Suppress, pid =>
                {
                    Assert.IsFalse(File.Exists(marker), "The application must not run before recording is ready.");
                    session = new("WinApp-Perf-Startup-" + Guid.NewGuid().ToString("N"),
                        Guid.NewGuid(), checked((int)pid), Path.Join(directory.FullName, "trace.etl"), 16);
                    Assert.IsTrue(session.CanEnable);
                    session.Enable(PerfProviders.Xaml, ulong.MaxValue);
                    Assert.IsFalse(File.Exists(marker), "Provider enablement must precede the application entry point.");
                    return Task.CompletedTask;
                }, timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            Assert.AreEqual(7, process.ExitCode, "The restored entry-point instruction must run correctly.");
            Assert.IsTrue(File.Exists(marker));
        }
        finally
        {
            session?.Dispose();
            directory.Delete(recursive: true);
        }
    }

    [TestMethod]
    public async Task ReadinessFailureDoesNotLeaveAnApplicationSuspendedOrRunning()
    {
        var directory = Directory.CreateTempSubdirectory("WinApp-Perf-Startup-Fail-");
        int? pid = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(40));
        try
        {
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => PerfStartupGate.LaunchAsync(
                Path.Join(Environment.SystemDirectory, "cmd.exe"), "/d /c exit /b 0",
                directory.FullName, LaunchStdioMode.Suppress, value =>
                {
                    pid = checked((int)value);
                    throw new InvalidOperationException("Deliberate readiness failure.");
                }, timeout.Token));
            Assert.IsNotNull(pid);
            Assert.Throws<ArgumentException>(() => Process.GetProcessById(pid.Value));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [TestMethod]
    public async Task CancellationBeforeResumeDoesNotExecuteOrLeaveTheApplication()
    {
        var directory = Directory.CreateTempSubdirectory("WinApp-Perf-Startup-Cancel-");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(40));
        int? pid = null;
        var marker = Path.Join(directory.FullName, "main-ran");
        try
        {
            await Assert.ThrowsAsync<OperationCanceledException>(() => PerfStartupGate.LaunchAsync(
                Path.Join(Environment.SystemDirectory, "cmd.exe"), $"/d /c echo started>\"{marker}\"",
                directory.FullName, LaunchStdioMode.Suppress, value =>
                {
                    pid = checked((int)value);
                    timeout.Cancel();
                    return Task.CompletedTask;
                }, timeout.Token));
            Assert.IsNotNull(pid);
            Assert.IsFalse(File.Exists(marker));
            Assert.Throws<ArgumentException>(() => Process.GetProcessById(pid.Value));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [TestMethod]
    public async Task PackagedReadinessUsesWorkerStatusInsteadOfRacingManifestWrites()
    {
        var root = Directory.CreateTempSubdirectory("WinApp-Perf-Startup-Status-");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        Task? controller = null;
        try
        {
            var directory = Path.Join(root.FullName, "capture");
            var service = new PerfCaptureService(new FakeWinappDirectoryService(root), new FakeAppLauncherService());
            var run = new PerfRunCapture(service, directory, 30, 128, false);
            var preparing = run.PrepareAsync(timeout.Token);
            var path = Directory.GetFiles(Path.Join(root.FullName, "perf-control"), "control.json",
                SearchOption.AllDirectories).Single();
            var registration = PerfCaptureService.ReadRegistration(path);
            var capture = PerfCaptureDocument.Load(directory);
            using var current = Process.GetCurrentProcess();
            var identity = PerfProcessIdentity.Read(current);
            using var pipe = new NamedPipeServerStream(PerfControlChannel.PipeName(registration.Id),
                PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            controller = Task.Run(async () =>
            {
                for (var i = 0; i < 3; i++)
                {
                    await pipe.WaitForConnectionAsync(timeout.Token);
                    var request = await PerfControlChannel.ReadAsync(pipe, PerfJsonContext.Default.PerfControlRequest,
                        4096, timeout.Token);
                    Assert.AreEqual("status", request.Operation);
                    Assert.AreEqual(registration.Credential, request.Credential);
                    if (i == 2)
                    {
                        capture.Target = identity;
                        capture.State = "recording";
                        capture.StartupCoverage = PerfStartupGate.Coverage;
                    }
                    await PerfControlChannel.WriteAsync(pipe, new PerfControlResponse(capture),
                        PerfJsonContext.Default.PerfControlResponse, timeout.Token);
                    pipe.Disconnect();
                }
            }, timeout.Token);
            await preparing;
            File.Delete(Path.Join(directory, "capture.json"));

            await run.BindAsync(checked((uint)current.Id), DateTime.UnixEpoch, timeout.Token);

            await controller;
            Assert.IsNull(run.Error);
            Assert.AreEqual("recording", run.Result.State);
            Assert.AreEqual(PerfStartupGate.Coverage, run.Result.StartupCoverage);
        }
        finally
        {
            timeout.Cancel();
            if (controller is not null)
            {
                try { await controller; }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
            }
            root.Delete(recursive: true);
        }
    }

    [TestMethod]
    public void StartupBreakpointsMatchSupportedInstructionSets()
    {
        CollectionAssert.AreEqual(new byte[] { 0xcc }, PerfStartupGate.Breakpoint(Machine.Amd64));
        CollectionAssert.AreEqual(new byte[] { 0xcc }, PerfStartupGate.Breakpoint(Machine.I386));
        CollectionAssert.AreEqual(new byte[] { 0, 0, 0x3e, 0xd4 }, PerfStartupGate.Breakpoint(Machine.Arm64));
        Assert.Throws<NotSupportedException>(() => PerfStartupGate.Breakpoint(Machine.Arm));
    }
}
