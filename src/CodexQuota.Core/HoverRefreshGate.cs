namespace CodexQuota.Core;

/// <summary>One refresh attempt per pointer entry; staying over the icon never loops.</summary>
public sealed class HoverRefreshGate
{
    public bool IsInside { get; private set; }
    public bool Enter()
    {
        if (IsInside) return false;
        IsInside = true;
        return true;
    }
    public void ObservePointer(bool inside)
    {
        if (!inside) IsInside = false;
    }
    public void Leave() => IsInside = false;
}
