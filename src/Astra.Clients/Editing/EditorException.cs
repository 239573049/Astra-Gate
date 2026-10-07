namespace Astra.Clients.Editing;

/// <summary>Raised when an editor self-check (re-parse and compare) fails, or the document is malformed.</summary>
public sealed class EditorException : InvalidOperationException
{
    public EditorException(string message) : base(message) { }
}
