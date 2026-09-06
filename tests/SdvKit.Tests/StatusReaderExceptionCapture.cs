using System.Runtime.ExceptionServices;
using System.Security;
using System.Text.Json;
using SdvKit.Cli.LiveLab;

namespace SdvKit.Tests;

internal sealed class StatusReaderExceptionCapture
{
    private const int MaximumExceptions = 4;
    private readonly Exception?[] exceptions = new Exception?[MaximumExceptions];
    private int count;
    private int threadId;
    private bool active;

    public Exception[] Exceptions => exceptions.Take(count).Cast<Exception>().ToArray();

    public AlwaysOnStatusReport Read(Func<AlwaysOnStatusReport> read)
    {
        Array.Clear(exceptions);
        count = 0;
        threadId = Environment.CurrentManagedThreadId;
        AppDomain.CurrentDomain.FirstChanceException += OnException;
        active = true;
        try
        {
            return read();
        }
        finally
        {
            active = false;
            AppDomain.CurrentDomain.FirstChanceException -= OnException;
        }
    }

    public object[] Describe()
    {
        try
        {
            return Exceptions.Select(exception => (object)new
            {
                Type = exception.GetType().FullName,
                exception.HResult,
                HResultHex = $"0x{exception.HResult:X8}",
                Message = ReadDescription(() => exception.Message, 1024),
                StackTrace = ReadDescription(() => exception.StackTrace, 4096),
            }).ToArray();
        }
        catch (Exception)
        {
            // Diagnostic allocation/description must not replace either worker failure.
            return [];
        }
    }

    private static string? ReadDescription(Func<string?> read, int maximumLength)
    {
        try
        {
            string? value = read();
            return value?[..Math.Min(value.Length, maximumLength)];
        }
        catch (Exception)
        {
            return "<unavailable>";
        }
    }

    private void OnException(object? sender, FirstChanceExceptionEventArgs args)
    {
        // Keep the callback allocation-free and never invoke exception properties here.
        // Assertions run after Read returns, outside this thread-specific scope.
        if (Environment.CurrentManagedThreadId == threadId && active && count < MaximumExceptions
            && args.Exception is IOException or UnauthorizedAccessException or SecurityException or JsonException)
        {
            exceptions[count++] = args.Exception;
        }
    }
}
