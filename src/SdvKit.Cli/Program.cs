using System.Text;
using SdvKit.Cli;
using SdvKit.Cli.LiveLab;

if (WindowsProjectReviewConsoleInputWorker.IsInvocation(args))
{
    var utf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    using var input = new StreamReader(
        Console.OpenStandardInput(),
        utf8,
        detectEncodingFromByteOrderMarks: false,
        leaveOpen: false);
    using var error = new StreamWriter(
        Console.OpenStandardError(),
        utf8,
        leaveOpen: false)
    {
        AutoFlush = true,
    };
    return WindowsProjectReviewConsoleInputWorker.Run(
        args,
        input,
        error);
}

return CliApplication.Run(args, Console.Out, Console.Error);
