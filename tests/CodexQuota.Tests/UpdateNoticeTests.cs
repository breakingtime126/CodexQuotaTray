using CodexQuota.Core;

static class UpdateNoticeTests
{
    public static void Register(Action<string, Action> test)
    {
        UpdateNoticeState Available()
        {
            var state = new UpdateNoticeState();
            Check.True(state.TryBeginCheck());
            state.CompleteCheck("0.2.36.0");
            return state;
        }
        test("update notice: checks only expose notice, never install", () =>
        {
            var s = Available();
            Check.True(s.HasAvailableUpdate); Check.False(s.Installing);
            Check.True(s.Notice!.CanInstall); Check.Equal("更新", s.Notice.ActionText);
        });
        test("update notice: check and click are mutually exclusive", () =>
        {
            var s = Available(); Check.True(s.TryBeginCheck());
            Check.False(s.TryBeginCheck()); Check.False(s.TryBeginInstall("0.2.36.0"));
            Check.False(s.Notice!.CanInstall); s.CancelCheck();
            Check.True(s.TryBeginInstall("0.2.36.0"));
            Check.False(s.TryBeginInstall("0.2.36.0")); Check.False(s.TryBeginCheck());
            Check.False(s.Notice!.CanInstall); Check.True(s.HasAvailableUpdate);
        });
        test("update notice: failed or cancelled check retains authenticated version", () =>
        {
            var s = Available(); s.TryBeginCheck(); s.CancelCheck();
            Check.Equal("0.2.36.0", s.AvailableVersion); Check.True(s.Notice!.CanInstall);
        });
        test("update notice: stale click cannot install different newly checked release", () =>
        {
            var s = Available(); s.TryBeginCheck(); s.CompleteCheck("0.2.37.0");
            Check.False(s.TryBeginInstall("0.2.36.0"));
            Check.True(s.TryBeginInstall("0.2.37.0"));
        });
        test("update notice: failed install retains badge and explicit retry gate", () =>
        {
            var s = Available(); s.TryBeginInstall("0.2.36.0"); s.InstallFailed();
            Check.True(s.HasAvailableUpdate); Check.False(s.Installing);
            Check.Equal("重试", s.Notice!.ActionText); Check.True(s.TryBeginInstall("0.2.36.0"));
        });
        test("update notice: no newer version clears notice and invalidates old click", () =>
        {
            var s = Available(); s.TryBeginCheck(); s.CompleteCheck(null);
            Check.False(s.HasAvailableUpdate); Check.Null(s.Notice);
            Check.False(s.TryBeginInstall("0.2.36.0"));
        });
        test("update notice: stop cancels pending publication and all future actions", () =>
        {
            var s = Available(); s.TryBeginCheck(); s.Stop(); s.CompleteCheck("0.2.37.0");
            Check.Equal("0.2.36.0", s.AvailableVersion);
            Check.False(s.TryBeginCheck()); Check.False(s.TryBeginInstall("0.2.36.0"));
            Check.False(s.Notice!.CanInstall);
        });
        test("update notice: unsolicited completion cannot replace available version", () =>
        {
            var s = Available(); s.CompleteCheck(null);
            Check.True(s.HasAvailableUpdate); Check.False(s.TryBeginInstall(""));
        });
    }
}
