namespace BookSpace.Application.Abstractions;

// Injected rather than calling DateTime.UtcNow inline, so token expiry and
// rotation are testable without waiting for wall-clock time to pass.
public interface IClock
{
    DateTime UtcNow { get; }
}
