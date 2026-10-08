// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Services.Performance;

internal sealed record PerfStartupRegistration(string PackageFullName, bool Debugger);

internal static class PerfStartupHelper
{
    public const string InternalVerb = "__perf-startup";

    public static string Command(string registrationPath, string packageFullName, bool debugger)
    {
        AtomicFile.WriteAllText(Path.Join(Path.GetDirectoryName(registrationPath)!, "startup.json"),
            JsonSerializer.Serialize(new PerfStartupRegistration(packageFullName, debugger),
                PerfJsonContext.Default.PerfStartupRegistration));
        var executable = Environment.ProcessPath ??
            throw new InvalidOperationException("Cannot identify the startup helper executable.");
        var args = new List<string> { executable };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            args.Add(Path.Join(AppContext.BaseDirectory, Assembly.GetEntryAssembly()!.GetName().Name + ".dll"));
        }
        args.AddRange([InternalVerb, registrationPath]);
        return string.Join(' ', args.Select(WindowsCommandLine.EscapeArgument));
    }

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 6 || args[2] != "-p" || args[4] != "-tid" ||
            !int.TryParse(args[3], NumberStyles.None, CultureInfo.InvariantCulture, out var pid) || pid <= 0 ||
            !uint.TryParse(args[5], NumberStyles.None, CultureInfo.InvariantCulture, out var tid) || tid == 0)
        {
            Console.Error.WriteLine("Startup performance recording failed: Startup helper requires a capture registration, package identity, PID and thread ID.");
            return 1;
        }

        Process? target = null;
        PerfControlRegistration registration;
        PerfStartupRegistration startup;
        // Package debugger settings outlive winapp if it is force-closed, so this helper can be
        // started for launches no capture is waiting for. Release those launches unrecorded
        // instead of leaving them suspended, and leave the capture untouched.
        try
        {
            registration = PerfCaptureService.ReadRegistration(args[1]);
            var capture = PerfCaptureDocument.Load(registration.Directory);
            PerfCaptureService.ValidateOwnership(registration, capture);
            startup = JsonSerializer.Deserialize(
                File.ReadAllText(Path.Join(Path.GetDirectoryName(args[1])!, "startup.json")),
                PerfJsonContext.Default.PerfStartupRegistration) ??
                throw new InvalidDataException("The startup registration is empty.");
            if (capture.State != "starting" || registration.Target is not null)
            {
                throw new InvalidOperationException("Startup helper requires a new, unbound capture.");
            }
            target = Process.GetProcessById(pid);
            var identity = PerfProcessIdentity.Read(target);
            if (identity.CreationUtc < capture.CreatedUtc ||
                !string.Equals(PackageFullName(target), startup.PackageFullName, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The activated process does not belong to this new package launch.");
            }
        }
        catch (Exception ex)
        {
            target?.Dispose();
            Console.Error.WriteLine("Startup performance recording skipped: " + ex.Message);
            return ReleaseDeclinedLaunch(pid, tid) ? 0 : 1;
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await PerfStartupGate.AttachAsync(pid, tid, async process =>
                await PerfCaptureService.BindAsync(registration, PerfProcessIdentity.Read(process),
                    PerfStartupGate.Coverage, startup.Debugger, timeout.Token), timeout.Token);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Startup performance recording failed: " + ex.Message);
            if (!target.HasExited)
            {
                target.Kill(entireProcessTree: true);
                await target.WaitForExitAsync(CancellationToken.None);
            }
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var capture = await PerfControlChannel.SendAsync(registration,
                    new(registration.Credential, "stop"), timeout.Token);
                capture.State = "failed";
                capture.Error = "Startup performance recording failed: " + ex.Message;
                capture.Save();
            }
            catch (Exception cleanupError)
            {
                Console.Error.WriteLine("Startup recording cleanup failed: " + cleanupError.Message);
            }
            return 1;
        }
        finally
        {
            target.Dispose();
        }
    }

    /// <summary>
    /// Lets a launch this helper will not record run normally, and turns off the package debugger
    /// setting that started the helper so later launches skip it.
    /// </summary>
    private static bool ReleaseDeclinedLaunch(int pid, uint threadId)
    {
        var released = true;
        string? packageFullName = null;
        try
        {
            using var process = Process.GetProcessById(pid);
            packageFullName = PackageFullName(process);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Cannot identify the declined launch's package: " + ex.Message);
            released = false;
        }
        try
        {
            PerfStartupGate.ResumeActivation(pid, threadId);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Cannot resume the declined launch: " + ex.Message);
            released = false;
        }
        if (packageFullName is not null)
        {
            try
            {
                PackageDebugSettings.CreateInstance<IPackageDebugSettings>().DisableDebugging(packageFullName);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Cannot disable the package startup debugger: " + ex.Message);
                released = false;
            }
        }
        return released;
    }

    private static unsafe string PackageFullName(Process process)
    {
        uint length = 0;
        var handle = new HANDLE(process.Handle);
        var result = PInvoke.GetPackageFullName(handle, &length, null);
        if ((uint)result != 122 || length is 0 or > 32768)
        {
            throw new InvalidOperationException($"Cannot identify the activated package (Windows error {(uint)result}).");
        }
        var buffer = new char[length];
        fixed (char* name = buffer)
        {
            result = PInvoke.GetPackageFullName(handle, &length, name);
            PrivateEtwSession.Check(result, "GetPackageFullName");
        }
        return new string(buffer).TrimEnd('\0');
    }
}
