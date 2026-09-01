using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
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

    [Theory]
    [InlineData(false, false, 0x00000100, 0)]
    [InlineData(true, false, 0x00000101, 7)]
    [InlineData(false, true, 0, 0)]
    [InlineData(true, true, 0, 0)]
    public void NativeLauncherUsesWindowFlagsForTheSelectedLaunchMode(
        bool startMinimized,
        bool interactiveConsole,
        int expectedFlags,
        short expectedShowWindow)
    {
        using TemporaryDirectory temporary = new();
        LabProcessStartSpec specification = new(
            Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            temporary.Path,
            [],
            new Dictionary<string, string>(StringComparer.Ordinal),
            Path.Combine(temporary.Path, "stdout.log"),
            Path.Combine(temporary.Path, "stderr.log"),
            StartMinimizedWithoutActivation: startMinimized,
            InteractiveConsole: interactiveConsole);

        (int flags, short showWindow) =
            WindowsProcessLauncher.GetStartupWindowSettings(specification);

        Assert.Equal(expectedFlags, flags);
        Assert.Equal(expectedShowWindow, showWindow);
    }

    [Theory]
    [InlineData(false, true, 0x00080400)]
    [InlineData(true, false, 0x00000410)]
    public void NativeLauncherUsesANewConsoleWithoutInheritedHandlesOnlyForInteractiveStarts(
        bool interactiveConsole,
        bool expectedInheritHandles,
        int expectedCreationFlags)
    {
        using TemporaryDirectory temporary = new();
        LabProcessStartSpec specification = new(
            Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            temporary.Path,
            [],
            new Dictionary<string, string>(StringComparer.Ordinal),
            Path.Combine(temporary.Path, "stdout.log"),
            Path.Combine(temporary.Path, "stderr.log"),
            InteractiveConsole: interactiveConsole);

        (bool inheritHandles, uint creationFlags) =
            WindowsProcessLauncher.GetProcessCreationSettings(specification);

        Assert.Equal(expectedInheritHandles, inheritHandles);
        Assert.Equal(checked((uint)expectedCreationFlags), creationFlags);
    }

    [Fact]
    public void FailedInteractiveStartDoesNotCreateRedirectedLogFiles()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory temporary = new();
        string stdout = Path.Combine(temporary.Path, "runtime", "stdout.log");
        string stderr = Path.Combine(temporary.Path, "runtime", "stderr.log");
        LabProcessStartSpec specification = new(
            Path.Combine(temporary.Path, "missing.exe"),
            temporary.Path,
            [],
            new Dictionary<string, string>(StringComparer.Ordinal),
            stdout,
            stderr,
            InteractiveConsole: true);

        Assert.Throws<Win32Exception>(() => WindowsProcessLauncher.Start(specification));
        Assert.False(File.Exists(stdout));
        Assert.False(File.Exists(stderr));
    }

    [Fact]
    public void ConsoleWorkerStartInfoUsesStrictUtf8AndExactParentIdentity()
    {
        using TemporaryDirectory temporary = new();
        using Process parent = Process.GetCurrentProcess();
        string fakeAppHost = Path.Combine(temporary.Path, "sdvkit.exe");
        FieldInfo processPathField = typeof(Environment).GetField(
            "s_processPath",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "The runtime process-path cache is unavailable for this isolated test.");
        string? originalProcessPath = (string?)processPathField.GetValue(null);
        var expected = new OwnedProcessIdentity(
            12345,
            new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero),
            Path.Combine(temporary.Path, "StardewModdingAPI.exe"));

        try
        {
            processPathField.SetValue(null, fakeAppHost);

            ProcessStartInfo startInfo =
                WindowsProjectReviewConsoleInputSender.CreateWorkerStartInfo(expected);

            Assert.Equal(fakeAppHost, startInfo.FileName);
            Assert.False(startInfo.UseShellExecute);
            Assert.True(startInfo.CreateNoWindow);
            Assert.True(startInfo.RedirectStandardInput);
            Assert.True(startInfo.RedirectStandardOutput);
            Assert.True(startInfo.RedirectStandardError);
            AssertStrictUtf8(startInfo.StandardInputEncoding);
            AssertStrictUtf8(startInfo.StandardOutputEncoding);
            AssertStrictUtf8(startInfo.StandardErrorEncoding);
            Assert.Equal(
                [
                    WindowsProjectReviewConsoleInputWorker.Argument,
                    expected.ProcessId.ToString(CultureInfo.InvariantCulture),
                    expected.StartTimeUtc.UtcTicks.ToString(CultureInfo.InvariantCulture),
                    expected.ExecutablePath,
                    parent.Id.ToString(CultureInfo.InvariantCulture),
                    parent.StartTime.ToUniversalTime().Ticks.ToString(
                        CultureInfo.InvariantCulture),
                ],
                startInfo.ArgumentList.ToArray());
        }
        finally
        {
            processPathField.SetValue(null, originalProcessPath);
        }
    }

    [Fact]
    public void ConsoleInputRecordsPreserveUnicodeAndTerminateWithEnter()
    {
        const string line = "sic ü 😀";

        WindowsProjectReviewConsoleInputWorker.ConsoleInputRecord[] records =
            WindowsProjectReviewConsoleInputWorker.CreateInputRecords(line);

        Assert.Equal(checked((line.Length + 1) * 2), records.Length);
        for (var index = 0; index < line.Length; index++)
        {
            Assert.True(records[index * 2].KeyEvent.KeyDown);
            Assert.False(records[(index * 2) + 1].KeyEvent.KeyDown);
            Assert.Equal(line[index], records[index * 2].KeyEvent.UnicodeChar);
            Assert.Equal(line[index], records[(index * 2) + 1].KeyEvent.UnicodeChar);
        }

        Assert.True(records[^2].KeyEvent.KeyDown);
        Assert.False(records[^1].KeyEvent.KeyDown);
        Assert.Equal('\r', records[^2].KeyEvent.UnicodeChar);
        Assert.Equal('\r', records[^1].KeyEvent.UnicodeChar);
        Assert.Equal(checked((ushort)0x000D), records[^2].KeyEvent.VirtualKeyCode);
        Assert.Equal(checked((ushort)0x000D), records[^1].KeyEvent.VirtualKeyCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ConsoleLineRejectsMalformedUtf16(int sample)
    {
        string line = sample switch
        {
            0 => new string(checked((char)0xD800), 1),
            1 => new string(checked((char)0xDC00), 1),
            _ => string.Concat("x", checked((char)0xD800), "y"),
        };

        string? error = ProjectReviewConsoleLine.ValidationError(line);

        Assert.NotNull(error);
        Assert.Contains("well-formed UTF-16", error, StringComparison.Ordinal);
    }

    [Fact]
    public void DirectConsoleWorkerInvocationRejectsANonParentProcess()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string assemblyDirectory = Path.GetDirectoryName(
            typeof(WindowsProjectReviewConsoleInputWorker).Assembly.Location)!;
        string appHost = Path.Combine(assemblyDirectory, "sdvkit.exe");
        var strictUtf8 = new UTF8Encoding(
            encoderShouldEmitUTF8Identifier: false,
            throwOnInvalidBytes: true);
        var startInfo = new ProcessStartInfo
        {
            FileName = appHost,
            WorkingDirectory = assemblyDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = strictUtf8,
            StandardOutputEncoding = strictUtf8,
            StandardErrorEncoding = strictUtf8,
        };
        startInfo.ArgumentList.Add(WindowsProjectReviewConsoleInputWorker.Argument);
        startInfo.ArgumentList.Add("1");
        startInfo.ArgumentList.Add("1");
        startInfo.ArgumentList.Add(Path.Combine(Environment.SystemDirectory, "cmd.exe"));
        startInfo.ArgumentList.Add(int.MaxValue.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(
            DateTimeOffset.UtcNow.UtcTicks.ToString(CultureInfo.InvariantCulture));

        using Process worker = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The direct worker process did not start.");
        worker.StandardInput.Close();
        string output = worker.StandardOutput.ReadToEnd();
        string error = worker.StandardError.ReadToEnd();

        Assert.True(worker.WaitForExit(5000));
        Assert.Equal((int)ProjectReviewConsoleInputStatus.WorkerParentMismatch, worker.ExitCode);
        Assert.Equal(string.Empty, output);
        Assert.Contains("exact SDVKit parent process", error, StringComparison.Ordinal);
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
                TimeSpan.FromSeconds(15));

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

    private static void AssertStrictUtf8(Encoding? encoding)
    {
        UTF8Encoding utf8 = Assert.IsType<UTF8Encoding>(encoding);
        Assert.Empty(utf8.GetPreamble());
        Assert.IsType<EncoderExceptionFallback>(utf8.EncoderFallback);
        Assert.IsType<DecoderExceptionFallback>(utf8.DecoderFallback);
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class NativeWindowsProcessGroup
{
    public const string Name = "Native Windows process tests";
}
