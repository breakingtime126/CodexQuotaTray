namespace CodexQuota.Core;

/// <summary>Closed signal: do not start another backend when owned cleanup was not confirmed.</summary>
public sealed class BackendCleanupUnconfirmedException : Exception
{
    public BackendCleanupUnconfirmedException() : base("Owned backend cleanup could not be confirmed.") { }
}
