using System.Collections;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SdvKit.Cli.LiveLab;

internal sealed record WindowsProcessLaunchResult(
    int ProcessId,
    SafeProcessHandle ProcessHandle) : IDisposable
{
    public void Dispose() => ProcessHandle.Dispose();
}

internal static partial class WindowsProcessLauncher
{
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const int StartfUseStdHandles = 0x00000100;
    private const uint HandleFlagInherit = 0x00000001;
    private static readonly nuint ProcThreadAttributeHandleList = 0x00020002;

    public static WindowsProcessLaunchResult Start(LabProcessStartSpec specification)
    {
        ArgumentNullException.ThrowIfNull(specification);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Direct persistent process logging is implemented for Windows only.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(specification.StandardOutputPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(specification.StandardErrorPath)!);
        using FileStream standardInput = new(
            "NUL",
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite);
        using FileStream standardOutput = new(
            specification.StandardOutputPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete);
        using FileStream standardError = new(
            specification.StandardErrorPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete);

        SafeFileHandle[] inheritedHandles =
        [
            standardInput.SafeFileHandle,
            standardOutput.SafeFileHandle,
            standardError.SafeFileHandle,
        ];
        foreach (SafeFileHandle handle in inheritedHandles)
        {
            SetInherit(handle, inherit: true);
        }

        byte[] environment = CreateEnvironmentBlock(specification.Environment);
        GCHandle pinnedEnvironment = GCHandle.Alloc(environment, GCHandleType.Pinned);
        GCHandle pinnedHandles = default;
        IntPtr attributeList = IntPtr.Zero;
        bool attributeListInitialized = false;
        ProcessInformation processInformation = default;
        try
        {
            nuint attributeListSize = 0;
            _ = InitializeProcThreadAttributeList(
                IntPtr.Zero,
                AttributeCount: 1,
                Flags: 0,
                ref attributeListSize);
            if (attributeListSize == 0)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Windows couldn't size the process handle allowlist.");
            }

            attributeList = Marshal.AllocHGlobal(checked((nint)attributeListSize));
            if (!InitializeProcThreadAttributeList(
                    attributeList,
                    AttributeCount: 1,
                    Flags: 0,
                    ref attributeListSize))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Windows couldn't initialize the process handle allowlist.");
            }

            attributeListInitialized = true;
            IntPtr[] rawHandles = inheritedHandles
                .Select(handle => handle.DangerousGetHandle())
                .ToArray();
            pinnedHandles = GCHandle.Alloc(rawHandles, GCHandleType.Pinned);
            if (!UpdateProcThreadAttribute(
                    attributeList,
                    Flags: 0,
                    ProcThreadAttributeHandleList,
                    pinnedHandles.AddrOfPinnedObject(),
                    checked((nuint)(rawHandles.Length * IntPtr.Size)),
                    IntPtr.Zero,
                    IntPtr.Zero))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Windows couldn't restrict inherited handles to the three lab log streams.");
            }

            StartupInfoEx startup = new()
            {
                StartupInfo = new StartupInfo
                {
                    Size = Marshal.SizeOf<StartupInfoEx>(),
                    Flags = StartfUseStdHandles,
                    StandardInput = standardInput.SafeFileHandle.DangerousGetHandle(),
                    StandardOutput = standardOutput.SafeFileHandle.DangerousGetHandle(),
                    StandardError = standardError.SafeFileHandle.DangerousGetHandle(),
                },
                AttributeList = attributeList,
            };
            char[] commandLine = (BuildCommandLine(
                specification.ExecutablePath,
                specification.Arguments) + '\0').ToCharArray();
            bool created;
            unsafe
            {
                fixed (char* commandLinePointer = commandLine)
                {
                    created = CreateProcessExtended(
                        specification.ExecutablePath,
                        commandLinePointer,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        InheritHandles: true,
                        CreateUnicodeEnvironment | ExtendedStartupInfoPresent,
                        pinnedEnvironment.AddrOfPinnedObject(),
                        specification.WorkingDirectory,
                        ref startup,
                        out processInformation);
                }
            }

            if (!created)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "CreateProcessW couldn't start the exact lab process.");
            }

            SafeProcessHandle processHandle = new(
                processInformation.ProcessHandle,
                ownsHandle: true);
            processInformation.ProcessHandle = IntPtr.Zero;
            return new WindowsProcessLaunchResult(
                checked((int)processInformation.ProcessId),
                processHandle);
        }
        finally
        {
            if (processInformation.ThreadHandle != IntPtr.Zero)
            {
                CloseHandle(processInformation.ThreadHandle);
            }

            if (processInformation.ProcessHandle != IntPtr.Zero)
            {
                CloseHandle(processInformation.ProcessHandle);
            }

            if (attributeListInitialized)
            {
                DeleteProcThreadAttributeList(attributeList);
            }

            if (attributeList != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(attributeList);
            }

            if (pinnedHandles.IsAllocated)
            {
                pinnedHandles.Free();
            }

            pinnedEnvironment.Free();
            foreach (SafeFileHandle handle in inheritedHandles)
            {
                TryClearInherit(handle);
            }
        }
    }

    private static byte[] CreateEnvironmentBlock(IReadOnlyDictionary<string, string> overrides)
    {
        SortedDictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && entry.Value is string value)
            {
                values[key] = value;
            }
        }

        foreach ((string key, string value) in overrides)
        {
            if (key.Contains('=') || key.Contains('\0') || value.Contains('\0'))
            {
                throw new ArgumentException(
                    $"Invalid process environment entry '{key}'.",
                    nameof(overrides));
            }

            values[key] = value;
        }

        string block = string.Join(
            '\0',
            values.Select(pair => $"{pair.Key}={pair.Value}")) + "\0\0";
        return Encoding.Unicode.GetBytes(block);
    }

    private static string BuildCommandLine(
        string executable,
        IReadOnlyList<string> arguments)
    {
        StringBuilder result = new(QuoteArgument(executable));
        foreach (string argument in arguments)
        {
            result.Append(' ').Append(QuoteArgument(argument));
        }

        return result.ToString();
    }

    private static string QuoteArgument(string value)
    {
        if (value.Length == 0)
        {
            return "\"\"";
        }

        if (!value.Any(character => char.IsWhiteSpace(character) || character == '"'))
        {
            return value;
        }

        StringBuilder result = new();
        result.Append('"');
        int backslashes = 0;
        foreach (char character in value)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            if (character == '"')
            {
                result.Append('\\', checked(backslashes * 2 + 1));
                result.Append('"');
                backslashes = 0;
                continue;
            }

            result.Append('\\', backslashes);
            backslashes = 0;
            result.Append(character);
        }

        result.Append('\\', checked(backslashes * 2));
        result.Append('"');
        return result.ToString();
    }

    private static void SetInherit(SafeFileHandle handle, bool inherit)
    {
        if (!SetHandleInformation(
                handle,
                HandleFlagInherit,
                inherit ? HandleFlagInherit : 0))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "A standard stream handle couldn't be configured for inheritance.");
        }
    }

    private static void TryClearInherit(SafeFileHandle handle)
    {
        if (!handle.IsClosed && !handle.IsInvalid)
        {
            _ = SetHandleInformation(handle, HandleFlagInherit, 0);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int Size;
        public IntPtr Reserved;
        public IntPtr Desktop;
        public IntPtr Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short ReservedSize;
        public IntPtr ReservedPointer;
        public IntPtr StandardInput;
        public IntPtr StandardOutput;
        public IntPtr StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr ProcessHandle;
        public IntPtr ThreadHandle;
        public uint ProcessId;
        public uint ThreadId;
    }

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "CreateProcessW",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool CreateProcessExtended(
        string? ApplicationName,
        char* CommandLine,
        IntPtr ProcessAttributes,
        IntPtr ThreadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool InheritHandles,
        uint CreationFlags,
        IntPtr Environment,
        string CurrentDirectory,
        ref StartupInfoEx StartupInfo,
        out ProcessInformation ProcessInformation);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool InitializeProcThreadAttributeList(
        IntPtr AttributeList,
        uint AttributeCount,
        uint Flags,
        ref nuint Size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UpdateProcThreadAttribute(
        IntPtr AttributeList,
        uint Flags,
        nuint Attribute,
        IntPtr Value,
        nuint Size,
        IntPtr PreviousValue,
        IntPtr ReturnSize);

    [LibraryImport("kernel32.dll")]
    private static partial void DeleteProcThreadAttributeList(IntPtr AttributeList);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetHandleInformation(
        SafeFileHandle Object,
        uint Mask,
        uint Flags);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr Object);
}
