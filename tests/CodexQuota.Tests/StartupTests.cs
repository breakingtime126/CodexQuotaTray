using CodexQuota.Core;

internal static class StartupTests
{
    private const string Executable = @"C:\Apps\Codex quota\CodexQuotaTray.exe";
    private static readonly string Command = StartupCommand.Build(Executable);

    public static void Register(Action<string, Action> test)
    {
        foreach (var operation in new[] { "initialize", "inspect", "enable", "disable" })
            test("startup: administrator build blocks " + operation + " without touching preserved settings", () =>
            {
                var policy = new StartupPolicy(new ForbiddenStorage(), Command, requiresElevation: true);
                var result = operation switch
                {
                    "initialize" => policy.Initialize(),
                    "inspect" => policy.Inspect(),
                    "enable" => policy.SetEnabled(true),
                    _ => policy.SetEnabled(false)
                };
                Check.False(result.IsEnabled);
                Check.False(result.IsStateKnown);
                Check.Contains("UAC", result.Error!);
                Check.Contains("旧启动项", result.Error!);
            });

        test("startup: first run enables once and records initialization", () =>
        {
            var storage = new FakeStorage();
            var policy = new StartupPolicy(storage, Command);
            Check.True(policy.Initialize().IsEnabled);
            Check.True(storage.Settings.Initialized);
            Check.Equal(Command, storage.Entry.Command);
            Check.Equal(Command, storage.Settings.ManagedCommand);
            var writes = storage.Mutations;
            Check.True(policy.Initialize().IsEnabled);
            Check.Equal(writes, storage.Mutations);
        });

        test("startup: explicit opt-out survives process restart", () =>
        {
            var storage = new FakeStorage();
            var policy = new StartupPolicy(storage, Command);
            policy.Initialize();
            Check.False(policy.SetEnabled(false).IsEnabled);
            Check.False(storage.Entry.Exists);
            var writes = storage.Mutations;
            Check.False(new StartupPolicy(storage, Command).Initialize().IsEnabled);
            Check.Equal(writes, storage.Mutations);
            Check.True(policy.SetEnabled(true).IsEnabled);
        });

        test("startup: external removal is honored without automatic repair", () =>
        {
            var storage = new FakeStorage();
            var policy = new StartupPolicy(storage, Command);
            policy.Initialize();
            storage.Entry = new(false, null);
            var writes = storage.Mutations;
            Check.False(policy.Inspect().IsEnabled);
            Check.False(policy.Initialize().IsEnabled);
            Check.Equal(writes, storage.Mutations);
        });

        test("startup: first run never overwrites conflicting or nonstring Run values", () =>
        {
            foreach (var other in new[] { new StartupRunEntry(true, @"C:\Other\app.exe"), new StartupRunEntry(true, null) })
            {
                var storage = new FakeStorage { Entry = other };
                var policy = new StartupPolicy(storage, Command);
                Check.False(policy.Initialize().IsEnabled);
                Check.True(storage.Settings.Initialized);
                Check.Equal(other, storage.Entry);
                Check.Contains("覆盖", policy.SetEnabled(true).Error!);
                Check.Contains("覆盖", policy.SetEnabled(false).Error!);
                Check.Equal(other, storage.Entry);
            }
        });

        test("startup: existing exact registration is adopted without rewriting Run", () =>
        {
            var storage = new FakeStorage { Entry = new(true, Command) };
            Check.True(new StartupPolicy(storage, Command).Initialize().IsEnabled);
            Check.Equal(0, storage.RunMutations);
            Check.Equal(Command, storage.Settings.ManagedCommand);
        });

        test("startup: moved enabled executable automatically follows its managed path", () =>
        {
            var storage = new FakeStorage();
            new StartupPolicy(storage, Command).Initialize();
            var moved = StartupCommand.Build(@"D:\工具\CodexQuotaTray.exe");
            var policy = new StartupPolicy(storage, moved);
            var result = policy.Initialize();
            Check.True(result.IsEnabled);
            Check.Null(result.Error);
            Check.Equal(moved, storage.Entry.Command);
            Check.Equal(moved, storage.Settings.ManagedCommand);
            var writes = storage.Mutations;
            Check.True(policy.Initialize().IsEnabled);
            Check.Equal(writes, storage.Mutations);
        });

        test("startup: moving after explicit opt-out never reenables it", () =>
        {
            var storage = new FakeStorage();
            var first = new StartupPolicy(storage, Command);
            first.Initialize();
            first.SetEnabled(false);
            var writes = storage.Mutations;
            var result = new StartupPolicy(storage, StartupCommand.Build(@"D:\new\CodexQuotaTray.exe")).Initialize();
            Check.False(result.IsEnabled);
            Check.False(storage.Entry.Exists);
            Check.Equal(writes, storage.Mutations);
        });

        test("startup: moving after external Run removal preserves removal", () =>
        {
            var storage = new FakeStorage();
            new StartupPolicy(storage, Command).Initialize();
            storage.Entry = new(false, null);
            var writes = storage.Mutations;
            Check.False(new StartupPolicy(storage, StartupCommand.Build(@"D:\new\CodexQuotaTray.exe")).Initialize().IsEnabled);
            Check.Equal(writes, storage.Mutations);
        });

        foreach (var approval in new[] { StartupApproval.Disabled, StartupApproval.Unknown })
            test($"startup: moved path preserves Windows {approval} state without migration", () =>
            {
                var storage = new FakeStorage();
                new StartupPolicy(storage, Command).Initialize();
                storage.Approval = approval;
                var writes = storage.Mutations;
                var result = new StartupPolicy(storage, StartupCommand.Build(@"D:\new\CodexQuotaTray.exe")).Initialize();
                Check.False(result.IsEnabled);
                Check.Contains("Windows", result.Error!);
                Check.Equal(approval != StartupApproval.Unknown, result.IsStateKnown);
                Check.Equal(Command, storage.Entry.Command);
                Check.Equal(approval, storage.Approval);
                Check.Equal(writes, storage.Mutations);
            });

        test("startup: moved path never overwrites a foreign Run entry", () =>
        {
            var storage = new FakeStorage();
            new StartupPolicy(storage, Command).Initialize();
            storage.Entry = new(true, @"C:\Other\app.exe");
            var writes = storage.Mutations;
            var result = new StartupPolicy(storage, StartupCommand.Build(@"D:\new\CodexQuotaTray.exe")).Initialize();
            Check.False(result.IsEnabled);
            Check.Contains("覆盖", result.Error!);
            Check.Equal(writes, storage.Mutations);
            Check.Equal(@"C:\Other\app.exe", storage.Entry.Command);
        });

        test("startup: moving repeatedly follows only the latest managed enabled copy", () =>
        {
            var storage = new FakeStorage();
            new StartupPolicy(storage, Command).Initialize();
            foreach (var path in new[] { @"D:\工具\CodexQuotaTray.exe", @"E:\Portable Tray\CodexQuotaTray.exe", Executable })
            {
                var next = StartupCommand.Build(path);
                Check.True(new StartupPolicy(storage, next).Initialize().IsEnabled);
                Check.Equal(next, storage.Entry.Command);
                Check.Equal(next, storage.Settings.ManagedCommand);
            }
        });

        test("startup: moved path preserves identical explicit CLI arguments", () =>
        {
            var storage = new FakeStorage();
            const string cli = @"C:\Users\测试\npm\codex.cmd";
            new StartupPolicy(storage, StartupCommand.Build(Executable, cli)).Initialize();
            var moved = StartupCommand.Build(@"D:\new\CodexQuotaTray.exe", cli);
            Check.True(new StartupPolicy(storage, moved).Initialize().IsEnabled);
            Check.Equal(moved, storage.Entry.Command);
        });

        test("startup: move does not silently change explicit CLI arguments", () =>
        {
            var storage = new FakeStorage();
            new StartupPolicy(storage, Command).Initialize();
            var moved = StartupCommand.Build(@"D:\new\CodexQuotaTray.exe", @"C:\Custom\codex.exe");
            var result = new StartupPolicy(storage, moved).Initialize();
            Check.False(result.IsEnabled);
            Check.Equal(Command, storage.Entry.Command);
        });

        test("startup: failed migration leaves previous entry and honest unchecked state", () =>
        {
            var storage = new FakeStorage();
            new StartupPolicy(storage, Command).Initialize();
            storage.FailRunWrites = true;
            var result = new StartupPolicy(storage, StartupCommand.Build(@"D:\new\CodexQuotaTray.exe")).Initialize();
            Check.False(result.IsEnabled);
            Check.Contains("权限", result.Error!);
            Check.Equal(Command, storage.Entry.Command);
        });

        test("startup: partial migration repairs ownership without rewriting Run", () =>
        {
            var storage = new FakeStorage { FailSettingsWriteNumber = 3 };
            new StartupPolicy(storage, Command).Initialize();
            var moved = StartupCommand.Build(@"D:\new\CodexQuotaTray.exe");
            var policy = new StartupPolicy(storage, moved);
            var partial = policy.Initialize();
            Check.True(partial.IsEnabled);
            Check.Contains("权限", partial.Error!);
            Check.Equal(moved, storage.Entry.Command);
            Check.Equal(Command, storage.Settings.ManagedCommand);
            var writes = storage.RunMutations;
            Check.True(policy.Initialize().IsEnabled);
            Check.Equal(writes, storage.RunMutations);
            Check.Equal(moved, storage.Settings.ManagedCommand);
            Check.True(new StartupPolicy(storage, StartupCommand.Build(@"E:\again\CodexQuotaTray.exe")).Initialize().IsEnabled);
        });

        test("startup: executable-only comparison rejects argument changes and malformed paths", () =>
        {
            var moved = StartupCommand.Build(@"D:\new\CodexQuotaTray.exe");
            Check.True(StartupCommand.IsExecutablePathOnlyChange(Command, moved));
            Check.False(StartupCommand.IsExecutablePathOnlyChange(Command, Command));
            Check.False(StartupCommand.IsExecutablePathOnlyChange("app.exe", moved));
            Check.False(StartupCommand.IsExecutablePathOnlyChange("\"relative.exe\"", moved));
            Check.False(StartupCommand.IsExecutablePathOnlyChange("\"C:\\old.dll\"", moved));
            Check.False(StartupCommand.IsExecutablePathOnlyChange(Command + " --other", moved));
        });

        test("startup: moved executable can disable its previous owned registration", () =>
        {
            var storage = new FakeStorage();
            new StartupPolicy(storage, Command).Initialize();
            var policy = new StartupPolicy(storage, StartupCommand.Build(@"D:\new\CodexQuotaTray.exe"));
            Check.False(policy.SetEnabled(false).IsEnabled);
            Check.False(storage.Entry.Exists);
        });

        test("startup: changed CLI parameters require explicit updating", () =>
        {
            var storage = new FakeStorage();
            new StartupPolicy(storage, Command).Initialize();
            var changed = StartupCommand.Build(Executable, @"C:\Users\测试\npm\codex.cmd");
            var policy = new StartupPolicy(storage, changed);
            Check.False(policy.Initialize().IsEnabled);
            Check.Equal(Command, storage.Entry.Command);
            Check.True(policy.SetEnabled(true).IsEnabled);
            Check.Equal(changed, storage.Entry.Command);
        });

        test("startup: external replacement is never deleted or repaired", () =>
        {
            var storage = new FakeStorage();
            var policy = new StartupPolicy(storage, Command);
            policy.Initialize();
            storage.Entry = new(true, @"C:\Other\app.exe");
            var writes = storage.Mutations;
            Check.Contains("覆盖", policy.Initialize().Error!);
            Check.Contains("覆盖", policy.SetEnabled(false).Error!);
            Check.Contains("覆盖", policy.SetEnabled(true).Error!);
            Check.Equal(writes, storage.Mutations);
        });

        test("startup: Windows disabled and unknown approval are reported honestly", () =>
        {
            foreach (var approval in new[] { StartupApproval.Disabled, StartupApproval.Unknown })
            {
                var storage = new FakeStorage { Approval = approval };
                var policy = new StartupPolicy(storage, Command);
                var result = policy.Initialize();
                Check.False(result.IsEnabled);
                Check.Contains("Windows", result.Error!);
                Check.Equal(approval != StartupApproval.Unknown, result.IsStateKnown);
                Check.False(policy.SetEnabled(true).IsEnabled);
                Check.Equal(approval, storage.Approval);
                Check.False(policy.SetEnabled(false).IsEnabled);
                Check.False(storage.Entry.Exists);
            }
        });

        test("startup: registration failure never repeats the default attempt", () =>
        {
            var storage = new FakeStorage { FailRunWrites = true };
            var policy = new StartupPolicy(storage, Command);
            var result = policy.Initialize();
            Check.False(result.IsEnabled);
            Check.True(result.IsStateKnown);
            Check.Contains("权限", result.Error!);
            Check.True(storage.Settings.Initialized);
            storage.FailRunWrites = false;
            Check.False(policy.Initialize().IsEnabled);
            Check.True(policy.SetEnabled(true).IsEnabled);
        });

        test("startup: failed opt-out reports that startup is still enabled", () =>
        {
            var storage = new FakeStorage();
            var policy = new StartupPolicy(storage, Command);
            policy.Initialize();
            storage.FailRunWrites = true;
            var result = policy.SetEnabled(false);
            Check.True(result.IsEnabled);
            Check.True(result.IsStateKnown);
            Check.Contains("权限", result.Error!);
        });

        test("startup: initialization write failure never changes Run", () =>
        {
            var storage = new FakeStorage { FailSettingsWrites = true };
            var result = new StartupPolicy(storage, Command).Initialize();
            Check.False(result.IsEnabled);
            Check.False(storage.Entry.Exists);
            Check.Equal(0, storage.RunMutations);
            Check.Contains("权限", result.Error!);
        });

        test("startup: failure after registration preserves accurate enabled state", () =>
        {
            var storage = new FakeStorage { FailSettingsWriteNumber = 2 };
            var result = new StartupPolicy(storage, Command).Initialize();
            Check.True(result.IsEnabled);
            Check.Contains("权限", result.Error!);
            Check.True(storage.Settings.Initialized);
        });

        test("startup: state-read failure is unknown and does not leak exception details", () =>
        {
            var storage = new FakeStorage { FailReads = true };
            var result = new StartupPolicy(storage, Command).Initialize();
            Check.False(result.IsEnabled);
            Check.False(result.IsStateKnown);
            Check.False(result.Error!.Contains("SECRET", StringComparison.Ordinal));
            Check.Equal(0, storage.Mutations);
        });

        test("startup: concurrent replacement is preserved", () =>
        {
            var storage = new FakeStorage { ReplaceBeforeWrite = true };
            var result = new StartupPolicy(storage, Command).Initialize();
            Check.False(result.IsEnabled);
            Check.Contains("修改", result.Error!);
            Check.Equal("other registration", storage.Entry.Command);
        });

        test("startup: command quotes the exe and original explicit CLI selection", () =>
        {
            Check.Equal("\"C:\\Apps\\Codex quota\\CodexQuotaTray.exe\"", Command);
            Check.Equal("\"C:\\Apps\\Codex quota\\CodexQuotaTray.exe\" --codex-path \"C:\\Users\\测试 & team\\npm\\codex.cmd\"",
                StartupCommand.Build(Executable, @"C:\Users\测试 & team\npm\codex.cmd"));
            Check.Equal("\"\\\\server\\share\\CodexQuotaTray.exe\"", StartupCommand.Build(@"\\server\share\CodexQuotaTray.exe"));
        });

        test("startup: argument quoting handles empty quotes and trailing backslashes", () =>
        {
            Check.Equal("\"\"", StartupCommand.QuoteArgument(""));
            Check.Equal("\"a\\\"b\"", StartupCommand.QuoteArgument("a\"b"));
            Check.Equal("\"C:\\path with space\\\\\"", StartupCommand.QuoteArgument(@"C:\path with space\"));
            Check.Equal("\"a\\\\\\\"b\"", StartupCommand.QuoteArgument("a\\\"b"));
            Check.Throws<ArgumentException>(() => StartupCommand.QuoteArgument("a\0b"));
        });

        test("startup: unsafe and nonabsolute executable paths are rejected", () =>
        {
            foreach (var path in new[] { "app.exe", @"C:app.exe", @"\app.exe", "C:\\a\"b.exe", "C:\\a\nb.exe", @"C:\app.dll", @"\\server\app.exe" })
                Check.Throws<ArgumentException>(() => StartupCommand.Build(path));
            Check.Throws<ArgumentException>(() => StartupCommand.Build(Executable, @"relative\codex.exe"));
        });

        test("startup: documented 260 character command limit is enforced", () =>
        {
            var maxPath = @"C:\" + new string('a', 251) + ".exe";
            Check.Equal(StartupCommand.MaximumLength, StartupCommand.Build(maxPath).Length);
            Check.Throws<ArgumentException>(() => StartupCommand.Build(@"C:\" + new string('a', 252) + ".exe"));
            Check.Throws<ArgumentException>(() => StartupCommand.Build(Executable, @"C:\" + new string('a', 240) + ".cmd"));
        });
    }

    private sealed class ForbiddenStorage : IStartupStorage
    {
        public StartupSettings ReadSettings() => throw new InvalidOperationException("Unexpected settings read.");
        public void WriteSettings(StartupSettings settings) => throw new InvalidOperationException("Unexpected settings write.");
        public StartupRunEntry ReadRunEntry() => throw new InvalidOperationException("Unexpected Run read.");
        public StartupApproval ReadApproval() => throw new InvalidOperationException("Unexpected approval read.");
        public bool TryChangeRunEntry(StartupRunEntry expected, string? command) => throw new InvalidOperationException("Unexpected Run write.");
    }

    private sealed class FakeStorage : IStartupStorage
    {
        public StartupSettings Settings { get; private set; } = new(false, null);
        public StartupRunEntry Entry { get; set; } = new(false, null);
        public StartupApproval Approval { get; set; } = StartupApproval.Allowed;
        public int Mutations { get; private set; }
        public int RunMutations { get; private set; }
        public bool FailReads { get; init; }
        public bool FailSettingsWrites { get; init; }
        public int FailSettingsWriteNumber { get; init; }
        public bool FailRunWrites { get; set; }
        public bool ReplaceBeforeWrite { get; init; }
        private int _settingsWrites;

        public StartupSettings ReadSettings() => FailReads ? throw new IOException("SECRET") : Settings;
        public StartupRunEntry ReadRunEntry() => FailReads ? throw new IOException("SECRET") : Entry;
        public StartupApproval ReadApproval() => Approval;
        public void WriteSettings(StartupSettings settings)
        {
            _settingsWrites++;
            if (FailSettingsWrites || _settingsWrites == FailSettingsWriteNumber) throw new UnauthorizedAccessException("SECRET");
            Settings = settings;
            Mutations++;
        }
        public bool TryChangeRunEntry(StartupRunEntry expected, string? command)
        {
            if (FailRunWrites) throw new UnauthorizedAccessException("SECRET");
            if (ReplaceBeforeWrite) Entry = new(true, "other registration");
            if (Entry != expected) return false;
            Entry = new(command is not null, command);
            RunMutations++;
            Mutations++;
            return true;
        }
    }
}
