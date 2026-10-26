using CodexQuota.Tray;

internal static class PopupTests
{
    // Portable lifecycle tests only. Actual foreground ownership, outside mouse clicks,
    // and Escape still require the Windows tray-menu acceptance checks.
    public static void Register(Action<string, Action> test)
    {
        test("popup: first open accepts a dismissal for its own activation", () =>
        {
            var popup = new TrayPopupSession();
            Check.False(popup.IsActive);
            Check.False(popup.IsCurrent(0));
            popup.Begin();
            Check.True(popup.IsActive);
            Check.True(popup.IsCurrent(popup.Generation));
            Check.False(popup.IsCurrent(0));
        });

        test("popup: close cleanup completes each activation once", () =>
        {
            var popup = new TrayPopupSession();
            for (var i = 0; i < 20; i++)
            {
                popup.Begin();
                var generation = popup.Generation;
                Check.True(popup.Complete());
                Check.False(popup.IsActive);
                Check.False(popup.IsCurrent(generation));
                Check.False(popup.Complete());
            }
        });

        test("popup: late deactivation cannot close a reopened menu", () =>
        {
            var popup = new TrayPopupSession();
            popup.Begin();
            var first = popup.Generation;
            popup.Complete();
            popup.Begin();
            Check.False(popup.IsCurrent(first));
            Check.True(popup.IsCurrent(popup.Generation));
        });

        test("popup: repeated activation invalidates pending close without requiring a second Show", () =>
        {
            var popup = new TrayPopupSession();
            popup.Begin();
            var queuedClose = popup.Generation;
            popup.Begin(); // Existing visible menu is foregrounded again.
            Check.False(popup.IsCurrent(queuedClose));
            Check.True(popup.IsCurrent(popup.Generation));
            Check.True(popup.Complete());
            Check.False(popup.Complete());
        });

        test("popup: cancelled opening can be cleaned up without a Closed event", () =>
        {
            var popup = new TrayPopupSession();
            popup.Begin();
            var cancelled = popup.Generation;
            Check.True(popup.Complete());
            Check.False(popup.IsCurrent(cancelled));
            popup.Begin();
            Check.True(popup.IsActive);
            Check.False(popup.IsCurrent(cancelled));
        });

        test("popup: dispose, replacement and removal ignore duplicate close notifications", () =>
        {
            var popup = new TrayPopupSession();
            Check.False(popup.Complete());
            popup.Begin();
            Check.True(popup.Complete());
            Check.False(popup.Complete());
            Check.False(popup.Complete());
            Check.False(popup.IsActive);
        });
    }
}
