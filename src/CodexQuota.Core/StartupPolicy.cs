using System.Security;
using System.Text;

namespace CodexQuota.Core;

public sealed record StartupResult(bool IsEnabled, string? Error = null, bool IsStateKnown = true);
public sealed record StartupSettings(bool Initialized, string? ManagedCommand);
public sealed record StartupRunEntry(bool Exists, string? Command);
public enum StartupApproval { Allowed, Disabled, Unknown }

/// <summary>Small storage boundary so startup policy can be tested without a registry.</summary>
public interface IStartupStorage
{
    StartupSettings ReadSettings();
    void WriteSettings(StartupSettings settings);
    StartupRunEntry ReadRunEntry();
    StartupApproval ReadApproval();
    // Recheck the expected value immediately before mutation; never replace an unrelated value.
    bool TryChangeRunEntry(StartupRunEntry expected, string? command);
}

/// <summary>Actual registration is authoritative; initialization never repairs an opt-out.</summary>
public sealed class StartupPolicy(IStartupStorage storage, string command, bool requiresElevation = false)
{
    private static StartupResult ElevatedStartupUnavailable() => new(false,
        "此管理员版本需要手动启动并确认 UAC，不能使用普通开机启动。旧启动项和开关设置未修改；若仍指向旧版，旧版可能继续随登录启动。", false);

    private const string Conflict = "已有不同内容的同名开机启动项；为避免覆盖其他程序，未作修改。";
    private const string Changed = "开机启动项刚被其他程序修改，请重新打开菜单后再试。";

    public StartupResult Initialize() => requiresElevation ? ElevatedStartupUnavailable() : Guard(() =>
    {
        var settings = storage.ReadSettings();
        var entry = storage.ReadRunEntry();
        if (settings.Initialized)
        {
            if (IsCurrent(entry))
            {
                // Recover a previous partial write: Run already targets this exact
                // command, but saving its ownership record failed. Do not rewrite Run.
                if (!string.Equals(settings.ManagedCommand, command, StringComparison.Ordinal))
                {
                    settings = settings with { ManagedCommand = command };
                    storage.WriteSettings(settings);
                }
                return Inspect(settings, entry);
            }
            // Follow a moved portable EXE only when the actual entry is still ours
            // and still enabled. A missing entry is an opt-out, not a repair request.
            // Preserve CLI argument choices; changing arguments remains explicit.
            if (entry.Exists && IsManaged(settings, entry) &&
                StartupCommand.IsExecutablePathOnlyChange(entry.Command!, command))
            {
                var approval = storage.ReadApproval();
                if (approval != StartupApproval.Allowed)
                    return new(false, approval == StartupApproval.Disabled
                        ? "Windows 已禁用此启动项，已保留关闭状态；未自动更新程序位置。"
                        : "无法确认 Windows 的启动项状态，未自动更新程序位置。", approval != StartupApproval.Unknown);
                return Enable(settings, entry);
            }
            return Inspect(settings, entry);
        }

        // Save before touching Run. A failed registration must not repeat default enablement
        // on every launch; explicit checkbox attempts can retry it later.
        settings = settings with { Initialized = true };
        storage.WriteSettings(settings);
        if (entry.Exists && !IsCurrent(entry)) return new(false, Conflict);
        return Enable(settings, entry);
    });

    public StartupResult Inspect() => requiresElevation ? ElevatedStartupUnavailable() : Guard(() => Inspect(storage.ReadSettings(), storage.ReadRunEntry()));

    public StartupResult SetEnabled(bool enabled) => requiresElevation ? ElevatedStartupUnavailable() : Guard(() =>
    {
        var settings = storage.ReadSettings();
        var entry = storage.ReadRunEntry();
        if (entry.Exists && !IsManaged(settings, entry)) return new(false, Conflict);
        if (!settings.Initialized)
        {
            settings = settings with { Initialized = true };
            storage.WriteSettings(settings);
        }

        if (enabled) return Enable(settings, entry);
        if (entry.Exists && !storage.TryChangeRunEntry(entry, null)) return InspectAfterFailure(Changed);
        // The independent Initialized marker survives removal of Run and preserves opt-out.
        return Inspect(settings, storage.ReadRunEntry());
    });

    private StartupResult Enable(StartupSettings settings, StartupRunEntry entry)
    {
        if (!IsCurrent(entry) && !storage.TryChangeRunEntry(entry, command)) return InspectAfterFailure(Changed);
        // Keep the old ownership record until Run has been successfully changed. A partial
        // failure can always recognize the exact current command on the next launch.
        storage.WriteSettings(settings with { Initialized = true, ManagedCommand = command });
        return Inspect(settings with { Initialized = true, ManagedCommand = command }, storage.ReadRunEntry());
    }

    private StartupResult Inspect(StartupSettings settings, StartupRunEntry entry)
    {
        if (!entry.Exists) return new(false);
        if (!IsManaged(settings, entry)) return new(false, Conflict);
        if (!IsCurrent(entry))
            return new(false, "开机启动仍指向上次的程序位置或参数；勾选后将更新为当前程序。");
        return storage.ReadApproval() switch
        {
            StartupApproval.Allowed => new(true),
            StartupApproval.Disabled => new(false, "Windows 已禁用此启动项，请在“设置 → 应用 → 启动”中启用 CodexQuotaTray。"),
            _ => new(false, "无法确认 Windows 对此启动项的启用状态，请检查“设置 → 应用 → 启动”。", false)
        };
    }

    private bool IsCurrent(StartupRunEntry entry) => entry.Exists && string.Equals(entry.Command, command, StringComparison.Ordinal);
    private bool IsManaged(StartupSettings settings, StartupRunEntry entry) => IsCurrent(entry) ||
        (entry.Exists && settings.ManagedCommand is not null && string.Equals(entry.Command, settings.ManagedCommand, StringComparison.Ordinal));

    private StartupResult Guard(Func<StartupResult> operation)
    {
        try { return operation(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException or ArgumentException or NotSupportedException)
        {
            // Never display exception text, registry contents, user paths, or credentials.
            var message = exception is UnauthorizedAccessException or SecurityException
                ? "没有权限读取或保存开机启动设置；请检查当前用户权限或组织策略。"
                : "无法读取或保存开机启动设置；未能确认更改，请重新打开菜单检查。";
            return InspectAfterFailure(message);
        }
    }

    private StartupResult InspectAfterFailure(string message)
    {
        try
        {
            var result = Inspect(storage.ReadSettings(), storage.ReadRunEntry());
            return result with { Error = message };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException or ArgumentException or NotSupportedException)
        {
            return new(false, message, false);
        }
    }
}

public static class StartupCommand
{
    // Microsoft documents a maximum of 260 characters for Run/RunOnce command lines.
    public const int MaximumLength = 260;

    public static bool IsExecutablePathOnlyChange(string previous, string current)
    {
        // Build emits a quoted EXE without embedded quotes; comparing the entire
        // remaining suffix avoids parsing/reinterpreting arbitrary command lines.
        static int EndOfExecutable(string value)
        {
            if (value.Length < 3 || value[0] != '"') return -1;
            var end = value.IndexOf('"', 1);
            if (end < 2) return -1;
            var path = value.Substring(1, end - 1);
            try { ValidatePath(path); }
            catch (ArgumentException) { return -1; }
            return path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? end : -1;
        }
        var oldEnd = EndOfExecutable(previous);
        var newEnd = EndOfExecutable(current);
        return oldEnd > 0 && newEnd > 0 &&
            !string.Equals(previous.Substring(0, oldEnd + 1), current.Substring(0, newEnd + 1), StringComparison.Ordinal) &&
            string.Equals(previous.Substring(oldEnd + 1), current.Substring(newEnd + 1), StringComparison.Ordinal);
    }

    public static string Build(string executablePath, string? cliPath = null)
    {
        ValidatePath(executablePath);
        if (!executablePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("An executable path is required.", nameof(executablePath));
        var command = QuoteArgument(executablePath);
        if (cliPath is not null)
        {
            ValidatePath(cliPath);
            command += " --codex-path " + QuoteArgument(cliPath);
        }
        if (command.Length > MaximumLength) throw new ArgumentException("Startup command is too long.");
        return command;
    }

    /// <summary>Win32/CRT argument quoting, including quotes and trailing backslashes. No shell.</summary>
    public static string QuoteArgument(string value)
    {
        if (value is null) throw new ArgumentNullException(nameof(value));
        if (value.IndexOf('\0') >= 0) throw new ArgumentException("NUL is not a valid argument.", nameof(value));
        var result = new StringBuilder("\"");
        var slashes = 0;
        foreach (var character in value)
        {
            if (character == '\\') { slashes++; continue; }
            result.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
            result.Append(character);
            slashes = 0;
        }
        result.Append('\\', slashes * 2);
        return result.Append('"').ToString();
    }

    private static void ValidatePath(string path)
    {
        if (path is null) throw new ArgumentNullException(nameof(path));
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A path is required.", nameof(path));
        var driveRooted = path.Length >= 3 && (path[0] is >= 'A' and <= 'Z' or >= 'a' and <= 'z') && path[1] == ':' && path[2] is '\\' or '/';
        var uncRooted = path.StartsWith(@"\\", StringComparison.Ordinal) &&
            path.Substring(2).Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries).Length >= 3;
        if ((!driveRooted && !uncRooted) || path.IndexOfAny(['"', '\0', '\r', '\n']) >= 0)
            throw new ArgumentException("A fully qualified Windows path is required.", nameof(path));
    }
}
