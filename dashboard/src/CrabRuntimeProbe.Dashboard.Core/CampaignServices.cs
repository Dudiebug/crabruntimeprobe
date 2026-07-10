using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CrabRuntimeProbe.Dashboard.Core;

public sealed class DashboardStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public DashboardStateStore(string? root = null)
    {
        Root = Path.GetFullPath(root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CrabRuntimeProbeDashboard"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }
    public string CampaignStatePath => Path.Combine(Root, "active_campaign.json");
    public string PreferencesPath => Path.Combine(Root, "preferences.json");
    private string MachineIdPath => Path.Combine(Root, "machine_id.txt");

    public async Task<string> GetOrCreateMachineIdAsync(CancellationToken cancellationToken = default)
    {
        if (File.Exists(MachineIdPath))
        {
            var existing = (await File.ReadAllTextAsync(MachineIdPath, cancellationToken).ConfigureAwait(false)).Trim();
            if (Guid.TryParseExact(existing, "N", out _)) return existing;
        }

        var id = Guid.NewGuid().ToString("N");
        await AtomicFile.WriteTextAsync(MachineIdPath, id, cancellationToken).ConfigureAwait(false);
        return id;
    }

    public Task SaveCampaignAsync(LocalCampaignState state, CancellationToken cancellationToken = default) =>
        AtomicFile.WriteTextAsync(CampaignStatePath, JsonSerializer.Serialize(state, JsonOptions), cancellationToken);

    public async Task<LocalCampaignState?> LoadCampaignAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(CampaignStatePath)) return null;
        await using var stream = File.OpenRead(CampaignStatePath);
        return await JsonSerializer.DeserializeAsync<LocalCampaignState>(stream, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
    }

    public Task SavePreferencesAsync(DashboardPreferences preferences, CancellationToken cancellationToken = default) =>
        AtomicFile.WriteTextAsync(PreferencesPath, JsonSerializer.Serialize(preferences, JsonOptions), cancellationToken);

    public async Task<DashboardPreferences> LoadPreferencesAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(PreferencesPath))
            return new DashboardPreferences(1, CampaignRole.Host, string.Empty, string.Empty, string.Empty, true);
        try
        {
            await using var stream = File.OpenRead(PreferencesPath);
            return await JsonSerializer.DeserializeAsync<DashboardPreferences>(stream, JsonOptions, cancellationToken)
                       .ConfigureAwait(false)
                   ?? new DashboardPreferences(1, CampaignRole.Host, string.Empty, string.Empty, string.Empty, true);
        }
        catch (JsonException)
        {
            return new DashboardPreferences(1, CampaignRole.Host, string.Empty, string.Empty, string.Empty, true);
        }
    }
}

public sealed class CampaignService
{
    private static readonly string[] TransientExtensions = { ".jsonl", ".log", ".dmp" };
    private readonly DashboardStateStore _stateStore;
    private readonly DashboardResourceLocator _resourceLocator;

    public CampaignService(DashboardStateStore stateStore, DashboardResourceLocator? resourceLocator = null)
    {
        _stateStore = stateStore;
        _resourceLocator = resourceLocator ?? new DashboardResourceLocator();
    }

    public async Task<InstallResult> InstallPayloadAsync(
        DashboardResources resources,
        GameInstallation installation,
        CancellationToken cancellationToken = default)
    {
        if (!installation.Exists)
            throw new DirectoryNotFoundException("The selected Crab Champions installation is no longer available.");
        if (!Directory.Exists(resources.PayloadRoot))
            throw new DirectoryNotFoundException($"Payload not found: {resources.PayloadRoot}");

        var targetRoot = SteamGameLocator.ResolveGameBinaryDirectory(installation);
        Directory.CreateDirectory(targetRoot);
        var installedConfig = Path.Combine(targetRoot, "Mods", "CrabRuntimeProbe", "Scripts", "config.txt");
        if (File.Exists(installedConfig))
            await BackupConfigAsync(installedConfig, cancellationToken).ConfigureAwait(false);
        var copied = 0;
        var unchanged = 0;
        var files = new List<string>();

        foreach (var source in Directory.EnumerateFiles(resources.PayloadRoot, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(source);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            var relative = Path.GetRelativePath(resources.PayloadRoot, source);
            if (relative.StartsWith("..", StringComparison.Ordinal)
                || relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Any(segment => segment.Equals("results", StringComparison.OrdinalIgnoreCase)))
                continue;
            if (TransientExtensions.Contains(Path.GetExtension(source), StringComparer.OrdinalIgnoreCase)) continue;
            if (relative.Equals(Path.Combine("Mods", "mods.txt"), StringComparison.OrdinalIgnoreCase)) continue;

            var destination = SafeCombine(targetRoot, relative);
            files.Add(relative.Replace(Path.DirectorySeparatorChar, '/'));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (File.Exists(destination) && await ContentEqualsAsync(source, destination, cancellationToken).ConfigureAwait(false))
            {
                unchanged++;
                continue;
            }

            var temporary = destination + $".{Guid.NewGuid():N}.tmp";
            try
            {
                await using (var input = File.OpenRead(source))
                await using (var output = new FileStream(
                    temporary,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    64 * 1024,
                    FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                File.Move(temporary, destination, true);
                copied++;
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        var payloadMods = Path.Combine(resources.PayloadRoot, "Mods", "mods.txt");
        if (File.Exists(payloadMods))
        {
            var merged = await MergeRequiredModsAsync(
                payloadMods,
                Path.Combine(targetRoot, "Mods", "mods.txt"),
                cancellationToken).ConfigureAwait(false);
            if (merged) copied++; else unchanged++;
            files.Add("Mods/mods.txt (merged)");
        }

        return new InstallResult(copied, unchanged, files);
    }

    public async Task<LocalCampaignState> PrepareAsync(
        GameInstallation installation,
        CampaignRole role,
        string campaignName = "CrabSync Full Observe",
        string? resourceStartPath = null,
        string? dashboardExecutablePath = null,
        CancellationToken cancellationToken = default)
    {
        if (role == CampaignRole.Unknown) throw new ArgumentException("Select Host or Joined Client.", nameof(role));
        var resources = _resourceLocator.Locate(resourceStartPath);
        await InstallPayloadAsync(resources, installation, cancellationToken).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        var gameBinary = SteamGameLocator.ResolveGameBinaryDirectory(installation);
        var scripts = Path.Combine(gameBinary, "Mods", "CrabRuntimeProbe", "Scripts");
        if (!Directory.Exists(scripts))
            throw new DirectoryNotFoundException($"Installed RuntimeProbe scripts were not found: {scripts}");
        if (!string.IsNullOrWhiteSpace(dashboardExecutablePath))
            await ConfigureDashboardAutoStartAsync(scripts, dashboardExecutablePath, cancellationToken)
                .ConfigureAwait(false);
        // RuntimeProbe's canonical append-only evidence and completed live-status ring live together
        // under Scripts/results. Older fixture packages may still contain Scripts/status; readers can
        // be pointed there explicitly, but new requests and stop markers use the canonical directory.
        var statusDirectory = Path.Combine(scripts, "results");
        Directory.CreateDirectory(statusDirectory);
        campaignName = SanitizeCampaignName(campaignName);
        var machineId = await _stateStore.GetOrCreateMachineIdAsync(cancellationToken).ConfigureAwait(false);
        var generation = now.ToUnixTimeSeconds();
        var sessionId = $"{now:yyyyMMddTHHmmssZ}-{Guid.NewGuid().ToString("N")[..8]}";
        ArchivePriorTransientStatus(statusDirectory, now);
        await ConfigureFullObserveAsync(
            Path.Combine(scripts, "config.txt"),
            role,
            campaignName,
            generation,
            sessionId,
            machineId,
            cancellationToken).ConfigureAwait(false);

        var state = new LocalCampaignState(
            1,
            "crabsync-full-observe",
            campaignName,
            generation,
            sessionId,
            machineId,
            role,
            installation.InstallDirectory,
            installation.ExecutablePath,
            statusDirectory,
            "prepared",
            now,
            now,
            string.Empty);

        await WriteCampaignRequestAsync(statusDirectory, state, false, cancellationToken).ConfigureAwait(false);
        await _stateStore.SaveCampaignAsync(state, cancellationToken).ConfigureAwait(false);
        return state;
    }

    public async Task<LocalCampaignState?> ResumeAsync(CancellationToken cancellationToken = default)
    {
        var state = await _stateStore.LoadCampaignAsync(cancellationToken).ConfigureAwait(false);
        if (state is null || !Directory.Exists(state.GameDirectory) || !File.Exists(state.ExecutablePath)) return null;
        Directory.CreateDirectory(state.StatusDirectory);
        var resumed = state with { Phase = "monitoring", UpdatedAtUtc = DateTimeOffset.UtcNow };
        await WriteCampaignRequestAsync(state.StatusDirectory, resumed, true, cancellationToken).ConfigureAwait(false);
        await _stateStore.SaveCampaignAsync(resumed, cancellationToken).ConfigureAwait(false);
        return resumed;
    }

    public async Task<LocalCampaignState> MarkMonitoringAsync(
        LocalCampaignState state,
        CancellationToken cancellationToken = default)
    {
        var updated = state with { Phase = "monitoring", UpdatedAtUtc = DateTimeOffset.UtcNow };
        await _stateStore.SaveCampaignAsync(updated, cancellationToken).ConfigureAwait(false);
        return updated;
    }

    public async Task RequestStopAsync(LocalCampaignState state, CancellationToken cancellationToken = default)
    {
        var marker = new
        {
            schemaVersion = 1,
            command = "stop-observation",
            marker = "dashboard-stop-requested",
            state.CampaignId,
            state.Generation,
            state.SessionId,
            requestedAtUtc = DateTimeOffset.UtcNow,
            diagnosticsOnly = true,
            noWrites = true,
            noRpcs = true,
            noMutation = true
        };
        await AtomicFile.WriteJsonAsync(
            Path.Combine(state.StatusDirectory, "dashboard_stop_requested.json"),
            marker,
            cancellationToken).ConfigureAwait(false);
        await _stateStore.SaveCampaignAsync(
            state with { Phase = "stop-requested", UpdatedAtUtc = DateTimeOffset.UtcNow },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task ResetAsync(LocalCampaignState? state, CancellationToken cancellationToken = default)
    {
        if (state is not null && Directory.Exists(state.StatusDirectory))
        {
            foreach (var name in new[] { "dashboard_campaign_request.json", "dashboard_stop_requested.json" })
            {
                var path = SafeCombine(state.StatusDirectory, name);
                if (File.Exists(path)) File.Delete(path);
            }
        }

        if (File.Exists(_stateStore.CampaignStatePath)) File.Delete(_stateStore.CampaignStatePath);
        await Task.CompletedTask.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static Task WriteCampaignRequestAsync(
        string statusDirectory,
        LocalCampaignState state,
        bool resume,
        CancellationToken cancellationToken)
    {
        var request = new
        {
            schemaVersion = 1,
            command = resume ? "resume" : "prepare",
            state.CampaignId,
            state.CampaignName,
            campaignGeneration = state.Generation,
            state.MachineId,
            state.SessionId,
            selectedRole = state.Role.ToContract(),
            resume,
            preparedAtUtc = state.PreparedAtUtc,
            requestedAtUtc = DateTimeOffset.UtcNow,
            diagnosticsOnly = true,
            safety = new
            {
                writesDisabled = true,
                rpcsDisabled = true,
                mutationDisabled = true,
                hudHookDisabled = true,
                rawIdentityDisabled = true
            }
        };
        return AtomicFile.WriteJsonAsync(
            Path.Combine(statusDirectory, "dashboard_campaign_request.json"),
            request,
            cancellationToken);
    }

    internal static string SafeCombine(string root, string relative)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(fullRoot, relative));
        if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Path escapes intended root: {relative}");
        return full;
    }

    private static async Task<bool> ContentEqualsAsync(
        string left,
        string right,
        CancellationToken cancellationToken)
    {
        var leftInfo = new FileInfo(left);
        var rightInfo = new FileInfo(right);
        if (leftInfo.Length != rightInfo.Length) return false;
        await using var leftStream = File.OpenRead(left);
        await using var rightStream = File.OpenRead(right);
        var leftHash = await SHA256.HashDataAsync(leftStream, cancellationToken).ConfigureAwait(false);
        var rightHash = await SHA256.HashDataAsync(rightStream, cancellationToken).ConfigureAwait(false);
        return CryptographicOperations.FixedTimeEquals(leftHash, rightHash);
    }

    private static async Task BackupConfigAsync(string configPath, CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(configPath, cancellationToken).ConfigureAwait(false);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()[..12];
        var backupDirectory = Path.Combine(Path.GetDirectoryName(configPath)!, "config.backups");
        Directory.CreateDirectory(backupDirectory);
        var existing = Directory.EnumerateFiles(backupDirectory, $"*.{hash}.txt", SearchOption.TopDirectoryOnly)
            .FirstOrDefault();
        if (existing is not null) return;
        var path = Path.Combine(backupDirectory, $"config.{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}.{hash}.txt");
        await AtomicFile.WriteTextAsync(path, System.Text.Encoding.UTF8.GetString(bytes), cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<bool> MergeRequiredModsAsync(
        string payloadModsPath,
        string installedModsPath,
        CancellationToken cancellationToken)
    {
        var requiredNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "BPModLoaderMod", "BPML_GenericFunctions", "CrabRuntimeProbe"
        };
        var payloadLines = await File.ReadAllLinesAsync(payloadModsPath, cancellationToken).ConfigureAwait(false);
        var required = payloadLines
            .Select(TryParseModName)
            .Where(name => name is not null && requiredNames.Contains(name))
            .Select(name => name!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (!required.Contains("CrabRuntimeProbe", StringComparer.OrdinalIgnoreCase))
            required = required.Append("CrabRuntimeProbe").ToArray();

        var existing = File.Exists(installedModsPath)
            ? (await File.ReadAllLinesAsync(installedModsPath, cancellationToken).ConfigureAwait(false)).ToList()
            : new List<string>();
        foreach (var name in required)
        {
            var index = existing.FindIndex(line => string.Equals(TryParseModName(line), name, StringComparison.OrdinalIgnoreCase));
            if (index >= 0) existing[index] = $"{name} : 1";
            else existing.Add($"{name} : 1");
        }

        var output = string.Join(Environment.NewLine, existing) + Environment.NewLine;
        var before = File.Exists(installedModsPath)
            ? await File.ReadAllTextAsync(installedModsPath, cancellationToken).ConfigureAwait(false)
            : string.Empty;
        if (string.Equals(before.Replace("\r\n", "\n"), output.Replace("\r\n", "\n"), StringComparison.Ordinal))
            return false;
        await AtomicFile.WriteTextAsync(installedModsPath, output, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static string? TryParseModName(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed.StartsWith(';') || trimmed.StartsWith('#')) return null;
        var separator = trimmed.IndexOf(':');
        return separator <= 0 ? null : trimmed[..separator].Trim();
    }

    private static async Task ConfigureFullObserveAsync(
        string configPath,
        CampaignRole role,
        string campaignName,
        long campaignGeneration,
        string campaignSessionId,
        string machineId,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(configPath)) throw new FileNotFoundException("Installed RuntimeProbe config is missing.", configPath);
        await BackupConfigAsync(configPath, cancellationToken).ConfigureAwait(false);
        var required = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["enabled"] = "true",
            ["campaignName"] = campaignName,
            ["campaignId"] = "crabsync-full-observe",
            ["campaignGeneration"] = campaignGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["campaignSessionId"] = campaignSessionId,
            ["machineId"] = machineId,
            ["selectedRole"] = role.ToContract(),
            ["mode"] = "observe",
            ["tickDriver"] = "executeDelay",
            ["probeSet"] = "crabsync-full-observe",
            ["fullObserveEnabled"] = "true",
            ["statusWriterEnabled"] = "true",
            ["statusRingSize"] = "4",
            ["allowPassiveObservationHooks"] = "true",
            ["allowFullObserveInventoryStages"] = "true",
            ["allowFullObserveRuntimeDiscovery"] = "true",
            ["fullObserveHeartbeatSeconds"] = "1",
            ["fullObserveInventoryIntervalSeconds"] = "2",
            ["fullObserveInventoryHeartbeatSeconds"] = "30",
            ["fullObserveMaxInventoryItems"] = "32",
            ["fullObserveMaxEnhancements"] = "16",
            ["allowWriteProbes"] = "false",
            ["allowRpcProbes"] = "false",
            ["allowHudTickHook"] = "false",
            ["allowRawIdentityEvidence"] = "false",
            ["allowUnknownRoleProbes"] = "false",
            ["allowJoinedClientDeepProbes"] = "false",
            ["allowDeepArrayProbes"] = "false"
        };

        var lines = (await File.ReadAllLinesAsync(configPath, cancellationToken).ConfigureAwait(false)).ToList();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < lines.Count; index++)
        {
            var equals = lines[index].IndexOf('=');
            if (equals <= 0) continue;
            var key = lines[index][..equals].Trim();
            if (!required.TryGetValue(key, out var value)) continue;
            lines[index] = $"{key} = {value}";
            seen.Add(key);
        }
        foreach (var pair in required.Where(pair => !seen.Contains(pair.Key)))
            lines.Add($"{pair.Key} = {pair.Value}");

        await AtomicFile.WriteTextAsync(
            configPath,
            string.Join(Environment.NewLine, lines) + Environment.NewLine,
            cancellationToken).ConfigureAwait(false);

        var parsed = (await File.ReadAllLinesAsync(configPath, cancellationToken).ConfigureAwait(false))
            .Select(line => (Line: line, SeparatorIndex: line.IndexOf('=')))
            .Where(item => item.SeparatorIndex > 0)
            .ToDictionary(
                item => item.Line[..item.SeparatorIndex].Trim(),
                item => item.Line[(item.SeparatorIndex + 1)..].Trim(),
                StringComparer.OrdinalIgnoreCase);
        foreach (var pair in required)
        {
            if (!parsed.TryGetValue(pair.Key, out var actual) || !string.Equals(actual, pair.Value, StringComparison.Ordinal))
                throw new InvalidDataException($"Installed config validation failed for {pair.Key}.");
        }
    }

    private static Task ConfigureDashboardAutoStartAsync(
        string scriptsDirectory,
        string dashboardExecutablePath,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(dashboardExecutablePath);
        if (!Path.IsPathFullyQualified(fullPath)
            || !Path.GetExtension(fullPath).Equals(".exe", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(fullPath))
            throw new FileNotFoundException("The dashboard executable required for game autostart was not found.", fullPath);
        if (fullPath.IndexOfAny(new[] { '\r', '\n', '"', '%' }) >= 0)
            throw new InvalidDataException("The dashboard executable path contains characters that cannot be launched safely.");

        return AtomicFile.WriteTextAsync(
            Path.Combine(scriptsDirectory, "dashboard_autostart.txt"),
            fullPath + Environment.NewLine,
            cancellationToken);
    }

    private static string SanitizeCampaignName(string value)
    {
        var sanitized = new string((value ?? string.Empty)
            .Where(character => character is not '\r' and not '\n' and not '#' and not '=')
            .Take(80)
            .ToArray()).Trim();
        return sanitized.Length == 0 ? "CrabSync Full Observe" : sanitized;
    }

    private static void ArchivePriorTransientStatus(string statusDirectory, DateTimeOffset now)
    {
        var candidates = Directory.EnumerateFiles(statusDirectory, "live_status.slot*.json", SearchOption.TopDirectoryOnly)
            .Concat(new[]
            {
                Path.Combine(statusDirectory, "dashboard_campaign_request.json"),
                Path.Combine(statusDirectory, "dashboard_stop_requested.json")
            }.Where(File.Exists))
            .ToArray();
        if (candidates.Length == 0) return;
        var archive = Path.Combine(statusDirectory, "status-archive", $"{now:yyyyMMddTHHmmssZ}-{Guid.NewGuid().ToString("N")[..6]}");
        Directory.CreateDirectory(archive);
        foreach (var source in candidates)
        {
            var destination = SafeCombine(archive, Path.GetFileName(source));
            File.Move(source, destination, true);
        }
    }
}

public sealed class GameProcessExitDetector
{
    public static readonly TimeSpan DefaultStartupGrace = TimeSpan.FromSeconds(15);
    public const int DefaultRequiredConsecutiveMisses = 5;

    private readonly TimeSpan _startupGrace;
    private readonly int _requiredConsecutiveMisses;
    private DateTimeOffset? _monitoringStartedAt;
    private bool _processWasSeen;
    private int _consecutiveMisses;

    public GameProcessExitDetector(
        TimeSpan? startupGrace = null,
        int requiredConsecutiveMisses = DefaultRequiredConsecutiveMisses)
    {
        if (startupGrace < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(startupGrace));
        if (requiredConsecutiveMisses < 1)
            throw new ArgumentOutOfRangeException(nameof(requiredConsecutiveMisses));
        _startupGrace = startupGrace ?? DefaultStartupGrace;
        _requiredConsecutiveMisses = requiredConsecutiveMisses;
    }

    public void Begin(DateTimeOffset observedAt, bool processSeen)
    {
        _monitoringStartedAt = observedAt;
        _processWasSeen = processSeen;
        _consecutiveMisses = 0;
    }

    public void Reset()
    {
        _monitoringStartedAt = null;
        _processWasSeen = false;
        _consecutiveMisses = 0;
    }

    public bool Observe(bool running, DateTimeOffset observedAt)
    {
        _monitoringStartedAt ??= observedAt;
        if (running)
        {
            _processWasSeen = true;
            _consecutiveMisses = 0;
            return false;
        }

        if (!_processWasSeen || observedAt - _monitoringStartedAt.Value < _startupGrace)
        {
            _consecutiveMisses = 0;
            return false;
        }

        _consecutiveMisses++;
        return _consecutiveMisses >= _requiredConsecutiveMisses;
    }
}

public sealed class GameProcessService
{
    public bool IsRunning(GameInstallation installation) => FindRunning(installation) is not null;

    public int? ProcessId(GameInstallation installation) => FindRunning(installation)?.Id;

    public Process? FindRunning(GameInstallation installation)
    {
        var names = new[]
        {
            Path.GetFileNameWithoutExtension(installation.ExecutablePath),
            "CrabChampions",
            "CrabChampions-Win64-Shipping"
        }.Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var name in names)
        {
            var process = Process.GetProcessesByName(name).FirstOrDefault(IsLive);
            if (process is not null) return process;
        }
        return null;
    }

    public Process Launch(GameInstallation installation)
    {
        if (!installation.Exists) throw new FileNotFoundException("Game executable not found.", installation.ExecutablePath);
        var existing = FindRunning(installation);
        if (existing is not null) return existing;
        return Process.Start(new ProcessStartInfo
        {
            FileName = installation.ExecutablePath,
            WorkingDirectory = installation.InstallDirectory,
            UseShellExecute = true
        }) ?? throw new InvalidOperationException("Windows did not start Crab Champions.");
    }

    public async Task<int?> WaitForExitAsync(
        GameInstallation installation,
        TimeSpan pollInterval,
        CancellationToken cancellationToken = default)
    {
        var process = FindRunning(installation);
        if (process is null) return null;
        while (!process.HasExited)
        {
            await Task.Delay(pollInterval, cancellationToken).ConfigureAwait(false);
        }
        return process.ExitCode;
    }

    private static bool IsLive(Process process)
    {
        try
        {
            return !process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
