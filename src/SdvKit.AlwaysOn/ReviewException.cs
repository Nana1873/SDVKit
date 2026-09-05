using System.Reflection;

namespace SdvKit.AlwaysOn;

internal static class ReviewException
{
    public static bool IsFatal(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        while (true)
        {
            if (exception is OutOfMemoryException
                or StackOverflowException
                or AccessViolationException)
            {
                return true;
            }

            if (exception is not TargetInvocationException { InnerException: not null } invocation)
            {
                return false;
            }

            exception = invocation.InnerException!;
        }
    }
}
