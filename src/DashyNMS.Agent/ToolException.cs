namespace DashyNMS.Agent;

/// <summary>
/// A failure the model should hear about in plain English - no device
/// matched, a source isn't configured - as opposed to a bug.
/// </summary>
/// <param name="message">Written by us, never from device data: it is the one field the model may take at face value.</param>
/// <param name="details">Anything else worth returning (candidate devices, say); redacted and kept apart from the message like any tool data.</param>
public sealed class ToolException(string message, object? details = null) : Exception(message)
{
    public object? Details { get; } = details;
}
