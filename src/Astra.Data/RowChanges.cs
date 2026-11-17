namespace Astra.Data;

/// <summary>
/// The kinds of change a repository's <c>Changed</c> event reports (<see cref="Repositories.ProviderRepository.Changed"/>,
/// <see cref="Repositories.ProviderAccountStore.Changed"/>). They feed the v2 plugins' <c>provider.changed</c> /
/// <c>account.changed</c> host events (design/plugin-v2 09 §4.1).
/// </summary>
public static class RowChanges
{
    public const string Created = "created";
    public const string Updated = "updated";
    public const string Deleted = "deleted";

    /// <summary>Calls every handler; a handler that throws never fails the write that raised it.</summary>
    internal static void Raise(Action<string, string>? handlers, string id, string change)
    {
        if (handlers is null) return;
        foreach (var h in handlers.GetInvocationList())
        {
            try
            {
                ((Action<string, string>)h)(id, change);
            }
            catch
            {
                // a listener's failure is its own
            }
        }
    }
}
