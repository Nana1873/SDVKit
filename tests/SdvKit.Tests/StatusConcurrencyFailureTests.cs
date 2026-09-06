using Xunit.Abstractions;

namespace SdvKit.Tests;

public sealed class StatusConcurrencyFailureTests(ITestOutputHelper output)
{
    [Fact]
    public void KeepsBothWorkerFailuresWhenDiagnosticOutputFails()
    {
        var writerFailure = new IOException("original writer failure");
        var observerFailure = new InvalidDataException("original observer failure");

        AggregateException actual = Assert.Throws<AggregateException>(() =>
            StatusConcurrencyFailure.ThrowIfAny(new UnavailableOutput(), "unused-status.json",
                new { Tick = 17 }, writerFailure, observerFailure));

        Assert.Collection(actual.InnerExceptions,
            exception => Assert.Same(writerFailure, exception),
            exception => Assert.Same(observerFailure, exception));
    }

    [Fact]
    public void KeepsOriginalFailureWhenContextSerializationFails()
    {
        var original = new IOException("original reader failure");

        IOException actual = Assert.Throws<IOException>(() =>
            StatusConcurrencyFailure.ThrowIfAny(output, "unused-status.json",
                new { Unsupported = typeof(StatusConcurrencyFailureTests) }, null, original));

        Assert.Same(original, actual);
    }

    private sealed class UnavailableOutput : ITestOutputHelper
    {
        public void WriteLine(string message) => throw new InvalidOperationException("Output unavailable.");

        public void WriteLine(string format, params object[] args) => WriteLine(format);
    }
}
