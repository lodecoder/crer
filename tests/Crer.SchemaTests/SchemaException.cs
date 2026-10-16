namespace Crer;

// Keep schema tests independent of the browser executable while compiling its exact validator source.
public sealed class CrerException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}
