namespace AutoClicker;

/// <summary>
/// Thrown by the engine when an opt-in safety rule stops a run mid-flight — currently the
/// corner fail-safe (the cursor parked in a screen corner stops the run). Distinct from a
/// plain <see cref="InvalidOperationException"/> so callers can report it as a deliberate,
/// user-triggered safety stop rather than an error.
/// </summary>
public sealed class FailSafeException : Exception
{
    public FailSafeException(string message) : base(message) { }
}
