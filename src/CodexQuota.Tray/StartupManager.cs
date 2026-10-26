using System.Security;
using System.Text.Json;
using CodexQuota.Core;
using Microsoft.Win32;

namespace CodexQuota.Tray;

/// <summary>Current-user Run registration; explicit opt-out and Windows startup decisions are preserved.</summary>
internal sealed class StartupManager
{
    // Keep aligned with app.manifest: ordinary Run startup cannot satisfy elevation.
    private const bool RequiresElevation = false;
    public bool IsAvailable => !RequiresElevation;
    private readonly StartupPolicy? _policy;
    private readonly string? _commandError;

    public StartupManager(string? cliPath = null)
    {
        try
        {
            var executable = Application.ExecutablePath;
            if (string.IsNullOrWhiteSpace(executable) ||
                string.Equals(Path.GetFileName(executable), "dotnet.exe", StringComparison.OrdinalIgnoreCase))
            {
                _commandError = "请直接运行 CodexQuotaTray.exe 后设置开机启动。";
                return;
            }
            // Login does not use this process's working directory. Preserve an explicit CLI
            // selection as its fully qualified path, including npm .cmd and .js entry points.
            var resolvedCli = string.IsNullOrWhiteSpace(cliPath) ? null : Path.GetFullPath(cliPath);
            var command = StartupCommand.Build(Path.GetFullPath(executable), resolvedCli);
            _policy = new StartupPolicy(new RegistryStartupStorage(), command, requiresElevation: RequiresElevation);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException or ArgumentException or NotSupportedException)
        {
            _commandError = "程序或 Codex 路径无法用于开机启动，或启动命令超过 Windows 的 260 字符限制；请使用较短的固定路径。";
        }
    }

    public bool IsEnabled => Inspect().IsEnabled;
    public StartupResult Initialize() => _policy?.Initialize() ?? new(false, _commandError, false);
    public StartupResult Inspect() => _policy?.Inspect() ?? new(false, _commandError, false);
    public StartupResult SetEnabled(bool enabled) => _policy?.SetEnabled(enabled) ?? new(false, _commandError, false);

    private sealed class RegistryStartupStorage : IStartupStorage
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ApprovalKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
        private const string SettingsKey = @"Software\CodexQuotaTray";
        private const string EntryName = "CodexQuotaTray";
        private const string SettingsName = "StartupState";
        private sealed record StoredSettings(int Version, bool Initialized, string? ManagedCommand);

        public StartupSettings ReadSettings()
        {
            using var key = Registry.CurrentUser.OpenSubKey(SettingsKey, writable: false);
            var value = key?.GetValue(SettingsName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (value is null) return new(false, null);
            if (value is not string json) throw new InvalidDataException("Invalid startup state.");
            try
            {
                var state = JsonSerializer.Deserialize<StoredSettings>(json);
                if (state is null || state.Version != 1 || !state.Initialized)
                    throw new InvalidDataException("Unsupported startup state.");
                return new(true, state.ManagedCommand);
            }
            catch (JsonException) { throw new InvalidDataException("Invalid startup state."); }
        }

        public void WriteSettings(StartupSettings settings)
        {
            using var key = Registry.CurrentUser.CreateSubKey(SettingsKey, writable: true)
                ?? throw new IOException("Cannot open startup settings.");
            // One registry value avoids a torn pair of initialized/ownership fields.
            key.SetValue(SettingsName, JsonSerializer.Serialize(new StoredSettings(1, settings.Initialized, settings.ManagedCommand)), RegistryValueKind.String);
            key.Flush();
        }

        public StartupRunEntry ReadRunEntry()
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            return ReadRunEntry(key);
        }

        private static StartupRunEntry ReadRunEntry(RegistryKey? key)
        {
            var value = key?.GetValue(EntryName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (value is null) return new(false, null);
            // Unsupported value kinds are conflicts, even if GetValue could coerce their text.
            return new(true, key!.GetValueKind(EntryName) == RegistryValueKind.String ? value as string : null);
        }

        public StartupApproval ReadApproval()
        {
            using var key = Registry.CurrentUser.OpenSubKey(ApprovalKey, writable: false);
            var value = key?.GetValue(EntryName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (value is null) return StartupApproval.Allowed;
            // This Windows-owned format is not a stable public API. Read conservatively;
            // never alter it or bypass a Task Manager / Windows Startup setting.
            if (value is not byte[] { Length: >= 12 } bytes || bytes[1] != 0 || bytes[2] != 0 || bytes[3] != 0)
                return StartupApproval.Unknown;
            return bytes[0] switch
            {
                2 or 6 => StartupApproval.Allowed,
                3 or 7 => StartupApproval.Disabled,
                _ => StartupApproval.Unknown
            };
        }

        public bool TryChangeRunEntry(StartupRunEntry expected, string? command)
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true)
                ?? throw new IOException("Cannot open startup registration.");
            if (ReadRunEntry(key) != expected) return false;
            if (command is null) key.DeleteValue(EntryName, throwOnMissingValue: false);
            else key.SetValue(EntryName, command, RegistryValueKind.String);
            key.Flush();
            return ReadRunEntry(key) == new StartupRunEntry(command is not null, command);
        }
    }
}
