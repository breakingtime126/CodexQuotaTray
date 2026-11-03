using CodexQuota.Core;

internal static class AutoResetPreferenceTests
{
    public static void Register(Action<string, Action> test)
    {
        test("auto reset preference: missing defaults on and does not create files", () => InDirectory(directory =>
        {
            var path = Path.Combine(directory, "missing", "auto-reset.json");
            var result = new AutoResetPreferenceStore(path).Load();
            Check.True(result.Enabled);
            Check.True(result.Explanation.Contains("60 秒"));
            Check.Equal(AutoResetPreferenceStatus.Default, result.Status);
            Check.False(Directory.Exists(Path.GetDirectoryName(path)));
        }));
        test("auto reset preference: missing file in existing directory defaults on without writing", () => InDirectory(directory =>
        {
            var path = Path.Combine(directory, "auto-reset.json");
            var result = new AutoResetPreferenceStore(path).Load();
            Check.True(result.Enabled);
            Check.Equal(AutoResetPreferenceStatus.Default, result.Status);
            Check.False(File.Exists(path));
        }));
        test("auto reset preference: v1 explicit off and on survive upgrade unchanged", () => InDirectory(directory =>
        {
            var path = Path.Combine(directory, "auto-reset.json");
            foreach (var enabled in new[] { false, true })
            {
                var original = "{\"version\":1,\"enabled\":" + (enabled ? "true" : "false") + "}\n";
                File.WriteAllText(path, original);
                var result = new AutoResetPreferenceStore(path).Load();
                Check.Equal(enabled, result.Enabled);
                Check.Equal(AutoResetPreferenceStatus.Loaded, result.Status);
                Check.Equal(original, File.ReadAllText(path));
            }
        }));
        test("auto reset preference: unreadable directory or blocked ancestor never enables default", () => InDirectory(directory =>
        {
            var blocker = Path.Combine(directory, "blocker");
            File.WriteAllText(blocker, "not a directory");
            foreach (var path in new[] { directory, Path.Combine(blocker, "auto-reset.json"),
                Path.Combine(blocker, "missing", "auto-reset.json") })
            {
                var result = new AutoResetPreferenceStore(path).Load();
                Check.False(result.Enabled);
                Check.Equal(AutoResetPreferenceStatus.ReadFailed, result.Status);
                Check.True(result.IsWarning);
            }
        }));
        test("auto reset preference: atomic on/off roundtrip remains separate from source settings", () => InDirectory(directory =>
        {
            var sourcePath = Path.Combine(directory, "settings.json");
            File.WriteAllText(sourcePath, "source settings sentinel");
            var path = Path.Combine(directory, "auto-reset.json");
            var store = new AutoResetPreferenceStore(path);
            foreach (var enabled in new[] { true, false, true, false })
            {
                Check.Equal(AutoResetPreferenceStatus.Saved, store.Save(enabled).Status);
                var loaded = store.Load();
                Check.Equal(enabled, loaded.Enabled);
                Check.Equal(AutoResetPreferenceStatus.Loaded, loaded.Status);
                Check.True(new FileInfo(path).Length <= AutoResetPreferenceStore.MaximumBytes);
                Check.Equal(0, Directory.GetFiles(directory, ".auto-reset-*.tmp").Length);
                Check.Equal("source settings sentinel", File.ReadAllText(sourcePath));
            }
        }));
        test("auto reset preference: malformed unknown oversized and duplicate values fail closed", () => InDirectory(directory =>
        {
            var path = Path.Combine(directory, "auto-reset.json");
            var store = new AutoResetPreferenceStore(path);
            foreach (var invalid in new[]
            {
                "", "[]", "null", "true", "{", "{\"enabled\":true}",
                "{\"version\":2,\"enabled\":true}", "{\"version\":1,\"enabled\":1}",
                "{\"version\":1,\"enabled\":\"true\"}", "{\"version\":1,\"enabled\":null}",
                "{\"version\":1,\"enabled\":true,\"extra\":1}",
                "{\"version\":1,\"enabled\":false,\"enabled\":true}",
                "{\"enabled\":true,\"enabled\":true}",
                "{\"version\":1,\"enabled\":true}" + new string(' ', AutoResetPreferenceStore.MaximumBytes)
            })
            {
                File.WriteAllText(path, invalid);
                var result = store.Load();
                Check.False(result.Enabled);
                Check.Equal(AutoResetPreferenceStatus.Invalid, result.Status);
                Check.True(result.IsWarning);
            }
        }));
        test("auto reset preference: save failure is explicit and cleans temporary files", () => InDirectory(directory =>
        {
            // Destination is a directory: replacement cannot succeed on any platform.
            var path = Path.Combine(directory, "not-a-file");
            Directory.CreateDirectory(path);
            var store = new AutoResetPreferenceStore(path);
            var enable = store.Save(true);
            Check.Equal(AutoResetPreferenceStatus.SaveFailed, enable.Status);
            Check.True(enable.IsWarning);
            Check.True(enable.Explanation.Contains("仍关闭"));
            var disable = store.Save(false);
            Check.Equal(AutoResetPreferenceStatus.SaveFailed, disable.Status);
            Check.True(disable.Explanation.Contains("重启后可能重新开启"));
            Check.Equal(0, Directory.GetFiles(directory, ".auto-reset-*.tmp").Length);
            Check.False(enable.Explanation.Contains(path));
            Check.False(disable.ToSafeLogCode().Contains(path));
            Check.False(store.Load().Enabled);
        }));
        test("auto reset preference: invalid load never modifies saved contents", () => InDirectory(directory =>
        {
            var path = Path.Combine(directory, "auto-reset.json");
            var store = new AutoResetPreferenceStore(path);
            Check.Equal(AutoResetPreferenceStatus.Saved, store.Save(true).Status);
            // Reading malformed content is non-mutating: never silently repairs/enables an invalid setting.
            const string invalid = "{\"version\":1,\"enabled\":true,\"unknown\":true}";
            File.WriteAllText(path, invalid);
            Check.False(store.Load().Enabled);
            Check.Equal(invalid, File.ReadAllText(path));
        }));
    }

    private static void InDirectory(Action<string> body)
    {
        var directory = Path.Combine(Path.GetTempPath(), "cqt-auto-reset-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { body(directory); }
        finally { Directory.Delete(directory, true); }
    }
}
