using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Win32;
using SeewoAssistant.Core.Abstractions;
using SeewoAssistant.Core.Models;

namespace SeewoAssistant.Core.Services.Seewo;

/// <summary>
/// Finds and toggles the mechanisms that make Seewo software start with Windows.
/// </summary>
/// <remarks>
/// <para>
/// Seewo products register auto-start in more than one way depending on the product
/// and version, so a single technique would silently miss entries. All four real
/// mechanisms are covered:</para>
/// <list type="number">
/// <item><description><b>Scheduled Tasks</b> — the most common for Seewo services
/// and updaters. Driven through <c>schtasks.exe</c> because the Task Scheduler COM
/// API needs the task's XML to change its state, and the command-line tool is the
/// documented, stable interface.</description></item>
/// <item><description><b>Registry Run keys</b> — both the machine and per-user
/// hives, including the 32-bit view on a 64-bit system.</description></item>
/// <item><description><b>Startup folders</b> — shortcuts placed in the per-user and
/// all-users Startup directories.</description></item>
/// <item><description><b>Services</b> — auto-start services are reported so the user
/// can see them. Their start type is changed through the Service Control Manager,
/// which is a supported operation.</description></item>
/// </list>
/// <para>
/// Disabling a Run key value <em>renames</em> it to a suffixed name rather than
/// deleting it, so the original command is preserved and can be restored exactly.
/// This is what Windows itself does when a user disables a startup item in Task
/// Manager, and it makes the operation fully reversible.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class StartupManagerService
{
    /// <summary>Suffix appended to a disabled Run value so it can be restored.</summary>
    public const string DisabledSuffix = ".seewoassistant-disabled";

    private const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string RunKeyPath32 = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";

    private readonly IAppLogger _logger;

    public StartupManagerService(IAppLogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Finds every auto-start entry whose command mentions Seewo.</summary>
    public IReadOnlyList<StartupEntry> FindSeewoStartupEntries()
    {
        var entries = new List<StartupEntry>();

        entries.AddRange(FindScheduledTasks());
        entries.AddRange(FindRunKeyEntries());
        entries.AddRange(FindStartupFolderEntries());
        entries.AddRange(FindServices());

        return entries
            .OrderBy(e => e.Kind)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool MentionsSeewo(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string[] keywords = ["seewo", "easinote", "希沃", "easicare", "swproxy", "swupdate"];
        return keywords.Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase));
    }

    // ------------------------------------------------------------------ tasks

    private List<StartupEntry> FindScheduledTasks()
    {
        var entries = new List<StartupEntry>();

        // CSV output is used because it is stable across Windows locales, unlike the
        // table format whose column headers are localized.
        var output = RunProcess(
            "schtasks.exe",
            "/Query /FO CSV /V /NH");

        if (string.IsNullOrWhiteSpace(output))
        {
            _logger.Debug("schtasks produced no output; scheduled tasks will not be listed.");
            return entries;
        }

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = ParseCsvLine(line.Trim());
            if (fields.Count < 9)
            {
                continue;
            }

            // CSV column order: HostName, TaskName, NextRunTime, Status, LogonMode,
            // LastRunTime, LastResult, Author, TaskToRun, StartIn, Comment, ...
            var taskName = fields[1];
            var status = fields.Count > 3 ? fields[3] : string.Empty;
            var taskToRun = fields.Count > 8 ? fields[8] : string.Empty;
            var author = fields.Count > 7 ? fields[7] : string.Empty;

            if (!MentionsSeewo(taskName) && !MentionsSeewo(taskToRun) && !MentionsSeewo(author))
            {
                continue;
            }

            entries.Add(new StartupEntry
            {
                Kind = StartupEntryKind.ScheduledTask,
                Name = taskName,
                Location = @"\",
                Command = taskToRun,
                Enabled = !status.Contains("Disabled", StringComparison.OrdinalIgnoreCase)
                          && !status.Contains("已禁用", StringComparison.OrdinalIgnoreCase),
                CanToggle = true,
            });
        }

        return entries;
    }

    /// <summary>Parses one line of <c>schtasks /FO CSV</c> output, honouring quoted commas.</summary>
    private static List<string> ParseCsvLine(string line)
    {
        var fields = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];

            if (c == '"')
            {
                // A doubled quote inside a quoted field is a literal quote.
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }

                continue;
            }

            if (c == ',' && !inQuotes)
            {
                fields.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(c);
        }

        fields.Add(current.ToString());
        return fields;
    }

    // ------------------------------------------------------------------ run keys

    private List<StartupEntry> FindRunKeyEntries()
    {
        var entries = new List<StartupEntry>();

        (RegistryKey Root, string Path, string Label)[] locations =
        [
            (Registry.LocalMachine, RunKeyPath, @"HKLM\" + RunKeyPath),
            (Registry.LocalMachine, RunKeyPath32, @"HKLM\" + RunKeyPath32),
            (Registry.CurrentUser, RunKeyPath, @"HKCU\" + RunKeyPath),
            (Registry.CurrentUser, RunKeyPath32, @"HKCU\" + RunKeyPath32),
        ];

        foreach (var (root, path, label) in locations)
        {
            try
            {
                using var key = root.OpenSubKey(path, writable: false);
                if (key is null)
                {
                    continue;
                }

                foreach (var valueName in key.GetValueNames())
                {
                    var value = key.GetValue(valueName)?.ToString() ?? string.Empty;

                    // A disabled entry keeps its original command, so it is still
                    // listed, but marked as disabled.
                    var isDisabled = valueName.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase);
                    var displayName = isDisabled
                        ? valueName[..^DisabledSuffix.Length]
                        : valueName;

                    if (!MentionsSeewo(valueName) && !MentionsSeewo(value))
                    {
                        continue;
                    }

                    entries.Add(new StartupEntry
                    {
                        Kind = StartupEntryKind.RegistryRun,
                        Name = displayName,
                        Location = label,
                        Command = value,
                        Enabled = !isDisabled,
                        CanToggle = true,
                    });
                }
            }
            catch (System.Security.SecurityException)
            {
                _logger.Debug($"Access denied reading {label}.");
            }
            catch (UnauthorizedAccessException)
            {
                _logger.Debug($"Access denied reading {label}.");
            }
        }

        return entries;
    }

    // ------------------------------------------------------------------ startup folders

    private List<StartupEntry> FindStartupFolderEntries()
    {
        var entries = new List<StartupEntry>();

        var folders = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Startup),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup),
        };

        foreach (var folder in folders)
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            {
                continue;
            }

            try
            {
                foreach (var file in Directory.EnumerateFiles(folder))
                {
                    var fileName = Path.GetFileName(file);

                    // The desktop.ini that marks the folder is not a startup item.
                    if (fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var isDisabled = file.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase);
                    var displayName = isDisabled
                        ? fileName[..^DisabledSuffix.Length]
                        : fileName;

                    // A shortcut's target cannot be read without resolving the link,
                    // so the name is the only signal available here.
                    if (!MentionsSeewo(displayName) && !MentionsSeewo(file))
                    {
                        continue;
                    }

                    entries.Add(new StartupEntry
                    {
                        Kind = StartupEntryKind.StartupFolder,
                        Name = displayName,
                        Location = file,
                        Command = file,
                        Enabled = !isDisabled,
                        CanToggle = true,
                    });
                }
            }
            catch (UnauthorizedAccessException)
            {
                _logger.Debug($"Access denied reading startup folder {folder}.");
            }
            catch (DirectoryNotFoundException)
            {
                // The folder does not exist for this user.
            }
        }

        return entries;
    }

    // ------------------------------------------------------------------ services

    private List<StartupEntry> FindServices()
    {
        var entries = new List<StartupEntry>();

        // Query the registry rather than the SCM: the Start value is readable without
        // elevation, which keeps the listing complete for a non-elevated user. The
        // value is only changed through the SCM, which does require elevation.
        const string servicesKey = @"SYSTEM\CurrentControlSet\Services";

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(servicesKey, writable: false);
            if (key is null)
            {
                return entries;
            }

            foreach (var serviceName in key.GetSubKeyNames())
            {
                using var service = key.OpenSubKey(serviceName, writable: false);
                if (service is null)
                {
                    continue;
                }

                var imagePath = service.GetValue("ImagePath")?.ToString() ?? string.Empty;

                if (!MentionsSeewo(serviceName) && !MentionsSeewo(imagePath))
                {
                    continue;
                }

                var start = service.GetValue("Start") switch
                {
                    int value => value,
                    _ => -1,
                };

                // 2 = automatic, 3 = manual, 4 = disabled.
                entries.Add(new StartupEntry
                {
                    Kind = StartupEntryKind.Service,
                    Name = serviceName,
                    Location = $@"HKLM\{servicesKey}\{serviceName}",
                    Command = imagePath,
                    Enabled = start is 2 or 3,
                    CanToggle = start is 2 or 4,
                    DisabledReason = start == 3
                        ? "该服务为「手动」启动，不随系统自动运行，无需禁用。"
                        : string.Empty,
                });
            }
        }
        catch (System.Security.SecurityException)
        {
            _logger.Debug("Access denied reading the services registry key.");
        }

        return entries;
    }

    // ------------------------------------------------------------------ toggling

    /// <summary>Enables or disables a discovered startup entry.</summary>
    public ActionResult SetEnabled(StartupEntry entry, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (!entry.CanToggle)
        {
            return ActionResult.Fail(
                string.IsNullOrWhiteSpace(entry.DisabledReason)
                    ? "该启动项无法通过本程序修改。"
                    : entry.DisabledReason);
        }

        return entry.Kind switch
        {
            StartupEntryKind.ScheduledTask => SetTaskEnabled(entry, enabled),
            StartupEntryKind.RegistryRun => SetRunKeyEnabled(entry, enabled),
            StartupEntryKind.StartupFolder => SetStartupFileEnabled(entry, enabled),
            StartupEntryKind.Service => SetServiceEnabled(entry, enabled),
            _ => ActionResult.Fail("未知的启动项类型。"),
        };
    }

    private ActionResult SetTaskEnabled(StartupEntry entry, bool enabled)
    {
        var arguments = $"/Change /TN \"{entry.Name}\" /{(enabled ? "ENABLE" : "DISABLE")}";
        var (exitCode, output) = RunProcessWithExitCode("schtasks.exe", arguments);

        if (exitCode == 0)
        {
            entry.Enabled = enabled;
            _logger.Info($"{(enabled ? "Enabled" : "Disabled")} scheduled task '{entry.Name}'.");
            return ActionResult.Ok($"已{(enabled ? "启用" : "禁用")}计划任务「{entry.Name}」。");
        }

        var message = exitCode == 1 && output.Contains("Access is denied", StringComparison.OrdinalIgnoreCase)
            ? "修改计划任务需要管理员权限，请以管理员身份运行本程序。"
            : $"修改计划任务失败（退出码 {exitCode}）：{output.Trim()}";

        return ActionResult.Fail(message);
    }

    private ActionResult SetRunKeyEnabled(StartupEntry entry, bool enabled)
    {
        // Reconstruct which hive and path the entry came from.
        var isMachine = entry.Location.StartsWith("HKLM", StringComparison.OrdinalIgnoreCase);
        var isWow64 = entry.Location.Contains("WOW6432Node", StringComparison.OrdinalIgnoreCase);
        var root = isMachine ? Registry.LocalMachine : Registry.CurrentUser;
        var path = isWow64 ? RunKeyPath32 : RunKeyPath;

        var originalName = entry.Name;
        var disabledName = originalName + DisabledSuffix;

        try
        {
            using var key = root.OpenSubKey(path, writable: true);
            if (key is null)
            {
                return ActionResult.Fail($"无法打开注册表项 {entry.Location}（可能需要管理员权限）。");
            }

            if (enabled)
            {
                // Restore the disabled value under its original name.
                if (key.GetValue(disabledName) is { } value)
                {
                    key.SetValue(originalName, value);
                    key.DeleteValue(disabledName, throwOnMissingValue: false);
                }
                else if (key.GetValue(originalName) is null)
                {
                    return ActionResult.Fail(
                        $"找不到已禁用的启动项「{originalName}」，可能已被其他程序删除。");
                }
            }
            else
            {
                if (key.GetValue(originalName) is { } value)
                {
                    key.SetValue(disabledName, value);
                    key.DeleteValue(originalName, throwOnMissingValue: false);
                }
                else if (key.GetValue(disabledName) is null)
                {
                    return ActionResult.Fail($"找不到启动项「{originalName}」。");
                }
            }

            entry.Enabled = enabled;
            _logger.Info($"{(enabled ? "Enabled" : "Disabled")} Run value '{originalName}' in {entry.Location}.");
            return ActionResult.Ok($"已{(enabled ? "启用" : "禁用")}启动项「{originalName}」。");
        }
        catch (UnauthorizedAccessException)
        {
            return ActionResult.Fail($"修改 {entry.Location} 需要管理员权限，请以管理员身份运行本程序。");
        }
        catch (System.Security.SecurityException)
        {
            return ActionResult.Fail($"修改 {entry.Location} 被拒绝，请以管理员身份运行本程序。");
        }
    }

    private ActionResult SetStartupFileEnabled(StartupEntry entry, bool enabled)
    {
        try
        {
            var current = entry.Location;
            var target = enabled
                ? (current.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase)
                    ? current[..^DisabledSuffix.Length]
                    : current)
                : (current.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase)
                    ? current
                    : current + DisabledSuffix);

            if (!File.Exists(current))
            {
                return ActionResult.Fail($"启动文件夹中的文件已不存在：{current}");
            }

            if (string.Equals(current, target, StringComparison.OrdinalIgnoreCase))
            {
                entry.Enabled = enabled;
                return ActionResult.Ok($"启动项「{entry.Name}」已是目标状态。");
            }

            File.Move(current, target);
            entry.Enabled = enabled;

            _logger.Info($"{(enabled ? "Enabled" : "Disabled")} startup file '{entry.Name}'.");
            return ActionResult.Ok($"已{(enabled ? "启用" : "禁用")}启动项「{entry.Name}」。");
        }
        catch (UnauthorizedAccessException)
        {
            return ActionResult.Fail("修改启动文件夹需要管理员权限，请以管理员身份运行本程序。");
        }
        catch (IOException ex)
        {
            return ActionResult.Fail($"重命名启动项失败：{ex.Message}", ex);
        }
    }

    private ActionResult SetServiceEnabled(StartupEntry entry, bool enabled)
    {
        // "auto" and "demand" are the SCM start types; disabling uses "disabled".
        var startType = enabled ? "auto" : "disabled";
        var (exitCode, output) = RunProcessWithExitCode("sc.exe", $"config \"{entry.Name}\" start= {startType}");

        if (exitCode == 0)
        {
            entry.Enabled = enabled;
            _logger.Info($"Set service '{entry.Name}' start type to {startType}.");
            return ActionResult.Ok(
                $"已将服务「{entry.Name}」设为{(enabled ? "自动启动" : "禁用")}。" +
                (enabled ? "" : "（下次系统启动时生效，当前已运行的服务不会被停止）"));
        }

        var message = output.Contains("Access is denied", StringComparison.OrdinalIgnoreCase)
            ? "修改服务启动类型需要管理员权限，请以管理员身份运行本程序。"
            : $"修改服务失败（退出码 {exitCode}）：{output.Trim()}";

        return ActionResult.Fail(message);
    }

    // ------------------------------------------------------------------ process helper

    private static string RunProcess(string fileName, string arguments)
    {
        var (_, output) = RunProcessWithExitCode(fileName, arguments);
        return output;
    }

    /// <summary>Guards registration of the legacy code-page provider.</summary>
    private static readonly object EncodingProviderGate = new();

    private static bool _encodingProviderRegistered;

    /// <summary>
    /// The console output encoding used by schtasks.exe and sc.exe.
    /// </summary>
    /// <remarks>
    /// .NET Core only ships Unicode, ASCII and Latin-1 by default. The legacy code
    /// pages - including the OEM page these tools write in - require
    /// <see cref="CodePagesEncodingProvider"/> to be registered, and calling
    /// <c>Encoding.GetEncoding</c> before that throws
    /// <see cref="NotSupportedException"/>. That is what broke the startup scan
    /// entirely on a machine whose OEM code page is not one of the built-in few.
    /// </remarks>
    private static System.Text.Encoding OemEncoding
    {
        get
        {
            EnsureCodePagesRegistered();

            try
            {
                var codePage = System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage;
                return System.Text.Encoding.GetEncoding(codePage);
            }
            catch (Exception)
            {
                // A missing code page must not take the whole scan down. Latin-1 never
                // fails to decode, so the output is readable even if it is imperfect.
                return System.Text.Encoding.Latin1;
            }
        }
    }

    /// <summary>
    /// Registers the legacy code-page provider exactly once.
    /// </summary>
    private static void EnsureCodePagesRegistered()
    {
        if (_encodingProviderRegistered)
        {
            return;
        }

        lock (EncodingProviderGate)
        {
            if (_encodingProviderRegistered)
            {
                return;
            }

            System.Text.Encoding.RegisterProvider(
                System.Text.CodePagesEncodingProvider.Instance);

            _encodingProviderRegistered = true;
        }
    }

    private static (int ExitCode, string Output) RunProcessWithExitCode(string fileName, string arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,

                // schtasks.exe and sc.exe are console programs that write in the
                // console output code page (the OEM code page), not UTF-8. Decoding
                // their output as UTF-8 would garble every localized status word and
                // task name on a non-English Windows, so the OEM code page is used.
                //
                // .NET Core does not ship the legacy code pages: Encoding.GetEncoding
                // throws NotSupportedException for them unless the provider is
                // registered first. That exception made the whole startup scan fail
                // with "No data is available for encoding 437", so the page reported
                // nothing at all. The provider is registered once, below.
                StandardOutputEncoding = OemEncoding,
                StandardErrorEncoding = OemEncoding,
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return (-1, $"无法启动 {fileName}。");
            }

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();

            if (!process.WaitForExit(20000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // Already exited.
                }

                return (-1, $"{fileName} 执行超时。");
            }

            var combined = string.Join(
                Environment.NewLine,
                new[] { stdout, stderr }.Where(s => !string.IsNullOrWhiteSpace(s)));

            return (process.ExitCode, combined);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return (-1, $"无法启动 {fileName}：{ex.Message}");
        }
    }
}
