using System.Diagnostics;
using System.IO.Pipes;
using SdvKit.Cli.LiveLab;

namespace SdvKit.Tests;

[Collection(NativeWindowsProcessGroup.Name)]
public sealed class WindowsLabProcessHostTests
{
    [Fact]
    public void StartInfoKeepsIsolatedModsPathAsOneArgument()
    {
        using TemporaryDirectory temporary = new();
        string executable = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        string isolatedModsPath = Path.Combine(
            temporary.Path,
            ".sdvkit",
            "live",
            "default",
            "Mods with spaces");
        LabProcessStartSpec specification = new(
            executable,
            temporary.Path,
            ["--mods-path", isolatedModsPath],
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["SDVKIT_TEST_MARKER"] = "isolated",
            },
            Path.Combine(temporary.Path, ".sdvkit", "runtime", "stdout.log"),
            Path.Combine(temporary.Path, ".sdvkit", "runtime", "stderr.log"));

        ProcessStartInfo startInfo = WindowsLabProcessHost.CreateStartInfo(specification);

        Assert.False(startInfo.UseShellExecute);
        Assert.Equal(Path.GetFullPath(executable), startInfo.FileName);
        Assert.Equal(Path.GetFullPath(temporary.Path), startInfo.WorkingDirectory);
        Assert.Equal(["--mods-path", isolatedModsPath], startInfo.ArgumentList);
        Assert.Equal("isolated", startInfo.Environment["SDVKIT_TEST_MARKER"]);
    }

    [Fact]
    public void StartTimeMismatchIsRejectedWithoutClosingTheChild()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory temporary = new();
        WindowsLabProcessHost host = new();
        string executable = Path.Combine(
            Environment.SystemDirectory,
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        LabProcessStartResult start = host.Start(new LabProcessStartSpec(
            executable,
            Path.GetDirectoryName(executable)!,
            [
                "-NoLogo",
                "-NoProfile",
                "-NonInteractive",
                "-WindowStyle",
                "Hidden",
                "-Command",
                "Start-Sleep -Seconds 30",
            ],
            new Dictionary<string, string>(StringComparer.Ordinal),
            Path.Combine(temporary.Path, "runtime", "stdout.log"),
            Path.Combine(temporary.Path, "runtime", "stderr.log")));

        Assert.Equal(LabProcessStartStatus.Started, start.Status);
        Assert.NotNull(start.Identity);
        using Process child = Process.GetProcessById(start.Identity.ProcessId);
        try
        {
            Assert.Equal(
                LabProcessInspectStatus.Running,
                host.Inspect(start.Identity).Status);

            OwnedProcessIdentity mismatched = start.Identity with
            {
                StartTimeUtc = start.Identity.StartTimeUtc.AddTicks(1),
            };

            LabProcessInspectResult inspect = host.Inspect(mismatched);
            LabProcessCloseResult close = host.RequestCloseAndWait(
                mismatched,
                TimeSpan.FromMilliseconds(100));
            LabProcessWaitResult wait = host.WaitForExit(
                mismatched,
                TimeSpan.FromMilliseconds(100));

            Assert.Equal(LabProcessInspectStatus.IdentityMismatch, inspect.Status);
            Assert.Equal(LabProcessCloseStatus.IdentityMismatch, close.Status);
            Assert.Equal(LabProcessWaitStatus.IdentityMismatch, wait.Status);
            child.Refresh();
            Assert.False(child.HasExited);
        }
        finally
        {
            EnsureExited(child);
        }
    }

    [Fact]
    public void WaitForExitObservesTheExactChildWithoutSendingASignal()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory temporary = new();
        WindowsLabProcessHost host = new();
        string executable = Path.Combine(Environment.SystemDirectory, "ping.exe");
        LabProcessStartResult start = host.Start(new LabProcessStartSpec(
            executable,
            Environment.SystemDirectory,
            ["-n", "2", "127.0.0.1"],
            new Dictionary<string, string>(StringComparer.Ordinal),
            Path.Combine(temporary.Path, "runtime", "stdout.log"),
            Path.Combine(temporary.Path, "runtime", "stderr.log")));

        Assert.Equal(LabProcessStartStatus.Started, start.Status);
        Assert.NotNull(start.Identity);
        Process? child = null;
        try
        {
            child = Process.GetProcessById(start.Identity.ProcessId);

            LabProcessWaitResult wait = host.WaitForExit(
                start.Identity,
                TimeSpan.FromSeconds(5));

            Assert.Equal(LabProcessWaitStatus.Exited, wait.Status);
            child.Refresh();
            Assert.True(child.HasExited);
        }
        finally
        {
            if (child is not null)
            {
                EnsureExited(child);
                child.Dispose();
            }
        }
    }

    [Fact]
    public void NativeLauncherPreservesEnvironmentAndArgumentWithSpaces()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory temporary = new();
        string script = temporary.WriteFile(
            "script with spaces.ps1",
            "param([string]$Value)\n"
            + "[Console]::Out.WriteLine($env:SDVKIT_NATIVE_ENV)\n"
            + "[Console]::Out.WriteLine($Value)\n"
            + "Start-Sleep -Seconds 1\n");
        string stdout = Path.Combine(temporary.Path, "runtime", "stdout.log");
        string stderr = Path.Combine(temporary.Path, "runtime", "stderr.log");
        string executable = Path.Combine(
            Environment.SystemDirectory,
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        WindowsLabProcessHost host = new();
        LabProcessStartResult start = host.Start(new LabProcessStartSpec(
            executable,
            temporary.Path,
            [
                "-NoLogo",
                "-NoProfile",
                "-NonInteractive",
                "-ExecutionPolicy",
                "Bypass",
                "-File",
                script,
                "argument with spaces",
            ],
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["SDVKIT_NATIVE_ENV"] = "environment with spaces",
            },
            stdout,
            stderr));

        Assert.Equal(LabProcessStartStatus.Started, start.Status);
        Assert.NotNull(start.Identity);
        Process? child = null;
        try
        {
            child = Process.GetProcessById(start.Identity.ProcessId);

            LabProcessWaitResult wait = host.WaitForExit(
                start.Identity,
                TimeSpan.FromSeconds(5));

            Assert.Equal(LabProcessWaitStatus.Exited, wait.Status);
            string[] lines = File.ReadAllLines(stdout);
            Assert.Contains("environment with spaces", lines, StringComparer.Ordinal);
            Assert.Contains("argument with spaces", lines, StringComparer.Ordinal);
            Assert.Equal(string.Empty, File.ReadAllText(stderr));
        }
        finally
        {
            if (child is not null)
            {
                EnsureExited(child);
                child.Dispose();
            }
        }
    }

    [Fact]
    public void ExactChildWritesDirectlyToProjectLocalLogs()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory temporary = new();
        string runtime = Path.Combine(
            temporary.Path,
            ".sdvkit",
            "lab",
            "single",
            "runtime");
        string stdout = Path.Combine(runtime, "smapi.stdout.log");
        string stderr = Path.Combine(runtime, "smapi.stderr.log");
        string executable = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        LabProcessStartSpec specification = new(
            executable,
            temporary.Path,
            [
                "/d",
                "/s",
                "/c",
                "echo stdout-line & echo stderr-line 1>&2 & ping -n 3 127.0.0.1 > nul",
            ],
            new Dictionary<string, string>(StringComparer.Ordinal),
            stdout,
            stderr);
        WindowsLabProcessHost host = new();

        LabProcessStartResult start = host.Start(specification);

        Assert.Equal(LabProcessStartStatus.Started, start.Status);
        Assert.NotNull(start.Identity);
        using Process child = Process.GetProcessById(start.Identity.ProcessId);
        try
        {
            Assert.True(child.WaitForExit(5000));
            Assert.Contains(
                "stdout-line",
                File.ReadAllText(stdout),
                StringComparison.Ordinal);
            Assert.Contains(
                "stderr-line",
                File.ReadAllText(stderr),
                StringComparison.Ordinal);
        }
        finally
        {
            EnsureExited(child);
        }
    }

    [Fact]
    public async Task ExactChildDoesNotInheritUnrelatedParentPipeHandle()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory temporary = new();
        string runtime = Path.Combine(temporary.Path, ".sdvkit", "runtime");
        string executable = Path.Combine(Environment.SystemDirectory, "ping.exe");
        LabProcessStartSpec specification = new(
            executable,
            Environment.SystemDirectory,
            ["-n", "30", "127.0.0.1"],
            new Dictionary<string, string>(StringComparer.Ordinal),
            Path.Combine(runtime, "stdout.log"),
            Path.Combine(runtime, "stderr.log"));
        using AnonymousPipeServerStream unrelatedPipe = new(
            PipeDirection.In,
            HandleInheritability.Inheritable);
        _ = unrelatedPipe.GetClientHandleAsString();
        WindowsProcessLaunchResult? launched = null;
        Process? child = null;
        try
        {
            launched = WindowsProcessLauncher.Start(specification);
            child = Process.GetProcessById(launched.ProcessId);
            unrelatedPipe.DisposeLocalCopyOfClientHandle();
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(2));
            byte[] buffer = new byte[1];

            int read = await unrelatedPipe.ReadAsync(buffer, timeout.Token);

            Assert.Equal(0, read);
            Assert.False(child.HasExited);
        }
        finally
        {
            if (child is { HasExited: false })
            {
                child.Kill(entireProcessTree: false);
                await child.WaitForExitAsync();
            }

            child?.Dispose();
            launched?.Dispose();
        }
    }

    private static void EnsureExited(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: false);
                process.WaitForExit(5000);
            }
        }
        catch (InvalidOperationException)
        {
            // The game-free test child already exited.
        }
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class NativeWindowsProcessGroup
{
    public const string Name = "Native Windows process tests";
}
