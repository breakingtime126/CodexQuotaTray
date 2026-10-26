using System.Text.Json;
using CodexQuota.Core;

static class ThemePreferencesTests
{
    public static void Register(Action<string, Action> test)
    {
        test("theme selection: stable labels and default System policy", () =>
        {
            var selection = new ThemeSelection();
            Check.Equal(ThemePreference.System, selection.Preference);
            Check.False(selection.IsDark);
            Check.Equal("跟随系统", ThemePreference.System.DisplayName());
            Check.Equal("浅色", ThemePreference.Light.DisplayName());
            Check.Equal("深色", ThemePreference.Dark.DisplayName());
            Check.False(ThemePreferenceNames.IsValid((ThemePreference)900));
            Check.Throws<ArgumentOutOfRangeException>(() => new ThemeSelection((ThemePreference)900));
        });

        test("theme selection: only System follows OS changes", () =>
        {
            var selection = new ThemeSelection();
            Check.True(selection.SetSystemTheme(true));
            Check.True(selection.IsDark);
            Check.False(selection.SetSystemTheme(true));
            Check.True(selection.SetSystemTheme(false));
            Check.False(selection.IsDark);
            foreach (var preference in new[] { ThemePreference.Light, ThemePreference.Dark })
            {
                _ = selection.Select(preference);
                foreach (var systemDark in new[] { true, false, true, false })
                {
                    Check.False(selection.SetSystemTheme(systemDark));
                    Check.Equal(systemDark, selection.SystemIsDark);
                    Check.Equal(preference == ThemePreference.Dark, selection.IsDark);
                    Check.Equal(preference, selection.Preference);
                }
            }
        });

        test("theme selection: returning to System uses latest OS theme", () =>
        {
            var selection = new ThemeSelection(ThemePreference.Light, systemIsDark: false);
            Check.False(selection.SetSystemTheme(true));
            Check.True(selection.Select(ThemePreference.System));
            Check.True(selection.IsDark);
            Check.False(selection.Select(ThemePreference.Dark));
            Check.False(selection.SetSystemTheme(false));
            Check.True(selection.Select(ThemePreference.System));
            Check.False(selection.IsDark);
        });

        test("theme selection: rapid choices immediately apply the newest policy", () =>
        {
            var selection = new ThemeSelection(systemIsDark: true);
            foreach (var preference in new[]
            {
                ThemePreference.Light, ThemePreference.Dark, ThemePreference.System,
                ThemePreference.Light, ThemePreference.System, ThemePreference.Dark, ThemePreference.Light
            })
            {
                _ = selection.Select(preference);
                Check.Equal(preference, selection.Preference);
                Check.Equal(preference != ThemePreference.Light, selection.IsDark);
            }
            Check.Equal(ThemePreference.Light, selection.Preference);
            Check.False(selection.Select(ThemePreference.Light));
            Check.False(selection.IsDark);
        });

        test("theme selection: invalid enum cannot change active policy", () =>
        {
            var selection = new ThemeSelection(ThemePreference.Dark, systemIsDark: false);
            Check.Throws<ArgumentOutOfRangeException>(() => selection.Select((ThemePreference)900));
            Check.Equal(ThemePreference.Dark, selection.Preference);
            Check.True(selection.IsDark);
            Check.False(selection.SystemIsDark);
        });

        test("theme settings: missing file defaults to System without writing", () => WithDirectory(directory =>
        {
            var path = Path.Combine(directory, "not-created", "theme.json");
            var result = new ThemePreferencesStore(path).Load();
            Check.Equal(ThemePreference.System, result.Preference);
            Check.Equal(ThemePreferenceStatus.Default, result.Status);
            Check.False(result.IsWarning);
            Check.False(result.IsPersisted);
            Check.Contains("persistedTheme=None", result.ToSafeLogCode());
            Check.False(Directory.Exists(Path.GetDirectoryName(path)));
        }));

        test("theme settings: all selections survive restart and atomically replace settings", () => WithDirectory(directory =>
        {
            var path = Path.Combine(directory, "theme.json");
            var store = new ThemePreferencesStore(path);
            foreach (var preference in new[] { ThemePreference.System, ThemePreference.Light, ThemePreference.Dark, ThemePreference.System })
            {
                var saved = store.Save(preference);
                Check.Equal(new ThemePreferenceResult(preference, ThemePreferenceStatus.Saved), saved);
                Check.True(saved.IsPersisted);
                Check.False(saved.IsWarning);
                var loaded = new ThemePreferencesStore(path).Load();
                Check.Equal(new ThemePreferenceResult(preference, ThemePreferenceStatus.Loaded), loaded);
                Check.True(loaded.IsPersisted);
                Check.Equal(preference.DisplayName(), loaded.PersistedPreferenceDescription);
                Check.Equal(preference, new ThemeSelection(loaded.Preference).Preference);
                Check.True(new FileInfo(path).Length < ThemePreferencesStore.MaximumBytes);
                using var document = JsonDocument.Parse(File.ReadAllBytes(path));
                Check.Equal(2, document.RootElement.EnumerateObject().Count());
                Check.Equal(1, document.RootElement.GetProperty("version").GetInt32());
                Check.Equal(preference.ToString().ToLowerInvariant(), document.RootElement.GetProperty("theme").GetString());
                Check.Equal(1, Directory.GetFiles(directory).Length);
            }
        }));

        test("theme settings: an old installation retains source preferences and defaults to System", () => WithDirectory(directory =>
        {
            var sourcePath = Path.Combine(directory, "settings.json");
            const string sourceSettings = "{\"version\":2,\"dataSource\":\"desktop\"}";
            File.WriteAllText(sourcePath, sourceSettings);
            var store = new ThemePreferencesStore(Path.Combine(directory, "theme.json"));
            Check.Equal(new ThemePreferenceResult(ThemePreference.System, ThemePreferenceStatus.Default), store.Load());
            Check.Equal(sourceSettings, File.ReadAllText(sourcePath));
            Check.Equal(ThemePreferenceStatus.Saved, store.Save(ThemePreference.Dark).Status);
            Check.Equal(sourceSettings, File.ReadAllText(sourcePath));
            Check.Equal(QuotaDataSource.Desktop, new SourcePreferencesStore(sourcePath).Load().Source);
        }));

        foreach (var json in new[]
        {
            "", "{", "null", "[]", "123", "{}", "{\"version\":1}",
            "{\"version\":1,\"dataSource\":\"desktop\"}",
            "{\"version\":1,\"theme\":\"unsupported-private-value\"}",
            "{\"version\":1,\"theme\":0}", "{\"version\":1,\"theme\":null}",
            "{\"version\":1,\"theme\":\"Dark\"}", "{\"version\":0,\"theme\":\"dark\"}",
            "{\"version\":2,\"theme\":\"dark\"}", "{\"version\":\"1\",\"theme\":\"dark\"}",
            "{\"version\":null,\"theme\":\"dark\"}", "{\"version\":1.5,\"theme\":\"dark\"}",
            "{\"version\":1e99,\"theme\":\"dark\"}",
            "{\"version\":1,\"theme\":\"light\",\"theme\":\"dark\"}",
            "{\"version\":1,\"version\":1,\"theme\":\"dark\"}",
            "{\"version\":1,\"theme\":\"dark\",\"token\":\"PRIVATE_SENTINEL\"}",
            "{\"version\":1,\"theme\":{\"nested\":{\"nested\":{\"nested\":{\"nested\":1}}}}}"
        })
        {
            var invalid = json;
            test("theme settings: malformed/unsupported settings safely fall back: " + json, () => WithDirectory(directory =>
            {
                var path = Path.Combine(directory, "theme.json");
                File.WriteAllText(path, invalid);
                var result = new ThemePreferencesStore(path).Load();
                Check.Equal(ThemePreference.System, result.Preference);
                Check.Equal(ThemePreferenceStatus.Invalid, result.Status);
                Check.True(result.IsWarning);
                Check.False(result.IsPersisted);
                Check.Contains("persistedTheme=Unknown", result.ToSafeLogCode());
                Check.False(result.ToSafeLogCode().Contains("PRIVATE_SENTINEL", StringComparison.Ordinal));
                Check.False(result.Explanation.Contains(directory, StringComparison.Ordinal));
                Check.Equal(invalid, File.ReadAllText(path));
            }));
        }

        test("theme settings: oversized input is bounded and rejected", () => WithDirectory(directory =>
        {
            var path = Path.Combine(directory, "theme.json");
            File.WriteAllText(path, new string(' ', ThemePreferencesStore.MaximumBytes + 1));
            var result = new ThemePreferencesStore(path).Load();
            Check.Equal(ThemePreferenceStatus.Invalid, result.Status);
            Check.Equal(ThemePreference.System, result.Preference);
        }));

        test("theme settings: a read failure reports a safe status and follows System", () => WithDirectory(directory =>
        {
            var result = new ThemePreferencesStore(directory).Load();
            Check.Equal(ThemePreferenceStatus.ReadFailed, result.Status);
            Check.Equal(ThemePreference.System, result.Preference);
            Check.True(result.IsWarning);
            Check.False(result.IsPersisted);
            Check.Contains("ReadFailed", result.ToSafeLogCode());
            Check.False(result.ToSafeLogCode().Contains(directory, StringComparison.Ordinal));
            Check.True(new ThemeSelection(result.Preference, systemIsDark: true).IsDark);
        }));

        test("theme settings: failed save keeps session choice without claiming persistence", () => WithDirectory(directory =>
        {
            var path = Path.Combine(directory, "theme.json");
            Directory.CreateDirectory(path); // Stable failure even with elevated Unix filesystem permissions.
            var store = new ThemePreferencesStore(path);
            var selection = new ThemeSelection(systemIsDark: true);
            Check.True(selection.Select(ThemePreference.Light));
            var result = store.Save(selection.Preference);
            Check.Equal(ThemePreference.Light, result.Preference);
            Check.Equal(ThemePreferenceStatus.SaveFailed, result.Status);
            Check.True(result.IsWarning);
            Check.False(result.IsPersisted);
            Check.Equal("未知（未能确认已保存的主题）", result.PersistedPreferenceDescription);
            Check.Contains("本次选择仍有效", result.Explanation);
            Check.Contains("selectedTheme=Light", result.ToSafeLogCode());
            Check.Contains("persistedTheme=Unknown", result.ToSafeLogCode());
            Check.Equal(ThemePreference.Light, selection.Preference);
            Check.False(selection.IsDark);
            Check.False(selection.SetSystemTheme(false));
            Check.False(selection.SetSystemTheme(true));
            Check.Equal(0, Directory.GetFiles(directory).Length); // Failed save cleans its temporary file.
            Check.True(Directory.Exists(path));
            Check.Equal(ThemePreference.System, new ThemePreferencesStore(path).Load().Preference);

            Directory.Delete(path);
            Check.False(selection.Select(ThemePreference.Light)); // A same-choice retry can still save.
            var retry = store.Save(selection.Preference);
            Check.Equal(ThemePreferenceStatus.Saved, retry.Status);
            Check.True(retry.IsPersisted);
            Check.Equal(ThemePreference.Light, new ThemePreferencesStore(path).Load().Preference);
        }));

        test("theme settings: invalid enum cannot alter an existing saved selection", () => WithDirectory(directory =>
        {
            var path = Path.Combine(directory, "theme.json");
            var store = new ThemePreferencesStore(path);
            Check.Equal(ThemePreferenceStatus.Saved, store.Save(ThemePreference.Dark).Status);
            var before = File.ReadAllText(path);
            var result = store.Save((ThemePreference)900);
            Check.Equal(ThemePreferenceStatus.SaveFailed, result.Status);
            Check.False(result.IsPersisted);
            Check.Contains("selectedTheme=Unknown", result.ToSafeLogCode());
            Check.Equal(before, File.ReadAllText(path));
            Check.Equal(ThemePreference.Dark, store.Load().Preference);
        }));

        test("theme settings: only the dedicated preference file is changed", () => WithDirectory(directory =>
        {
            var unrelated = Path.Combine(directory, "synthetic-unrelated.json");
            const string sentinel = "SYNTHETIC_UNRELATED_FILE";
            File.WriteAllText(unrelated, sentinel);
            var store = new ThemePreferencesStore(Path.Combine(directory, "theme.json"));
            _ = store.Load();
            _ = store.Save(ThemePreference.Dark);
            _ = store.Load();
            Check.Equal(sentinel, File.ReadAllText(unrelated));
        }));
    }

    private static void WithDirectory(Action<string> action)
    {
        var path = Path.Combine(Path.GetTempPath(), "quota-theme-selection-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try { action(path); }
        finally { Directory.Delete(path, recursive: true); }
    }
}
