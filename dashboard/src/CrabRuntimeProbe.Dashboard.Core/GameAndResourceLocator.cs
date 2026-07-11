using Microsoft.Win32;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace CrabRuntimeProbe.Dashboard.Core;

public sealed partial class SteamGameLocator
{
    public const string SteamAppId = "774801";

    public IReadOnlyList<GameInstallation> Detect()
    {
        var steamRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (OperatingSystem.IsWindows())
        {
            AddIfDirectory(steamRoots, ReadRegistry(Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath"));
            AddIfDirectory(steamRoots, ReadRegistry(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"));
            AddIfDirectory(steamRoots, ReadRegistry(Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath"));
        }
        AddIfDirectory(steamRoots, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));
        AddIfDirectory(steamRoots, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam"));

        var libraryRoots = new HashSet<string>(steamRoots, StringComparer.OrdinalIgnoreCase);
        foreach (var steamRoot in steamRoots)
        {
            var libraryFile = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(libraryFile)) continue;
            try
            {
                foreach (Match match in VdfPathRegex().Matches(File.ReadAllText(libraryFile)))
                {
                    AddIfDirectory(libraryRoots, match.Groups[1].Value.Replace("\\\\", "\\"));
                }
            }
            catch (IOException)
            {
                // Steam can rewrite this file while the dashboard is open; the next detection retries it.
            }
        }

        var found = new Dictionary<string, GameInstallation>(StringComparer.OrdinalIgnoreCase);
        foreach (var libraryRoot in libraryRoots)
        {
            var steamApps = Path.Combine(libraryRoot, "steamapps");
            var manifest = Path.Combine(steamApps, $"appmanifest_{SteamAppId}.acf");
            if (!File.Exists(manifest)) continue;
            try
            {
                var text = File.ReadAllText(manifest);
                var match = InstallDirRegex().Match(text);
                if (!match.Success) continue;
                var installDirectory = Path.GetFullPath(Path.Combine(steamApps, "common", match.Groups[1].Value));
                var executable = FindExecutable(installDirectory);
                if (executable is null) continue;
                found[installDirectory] = new GameInstallation(installDirectory, executable, "Steam app 774801 manifest");
            }
            catch (IOException)
            {
                // Treat a temporarily unreadable manifest as not detected, without caching personal Steam state.
            }
        }

        return found.Values.OrderBy(item => item.InstallDirectory, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public GameInstallation? ValidateSelectedDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return null;
        var full = Path.GetFullPath(directory);
        var executable = FindExecutable(full);
        if (executable is null)
        {
            // The user may select the Win64 directory rather than the Steam install root.
            executable = Directory.EnumerateFiles(full, "*.exe", SearchOption.TopDirectoryOnly)
                .FirstOrDefault(path => Path.GetFileName(path).Contains("Crab", StringComparison.OrdinalIgnoreCase));
        }

        return executable is null ? null : new GameInstallation(full, executable, "User selected");
    }

    public static string ResolveGameBinaryDirectory(GameInstallation installation)
    {
        var nested = Path.Combine(installation.InstallDirectory, "CrabChampions", "Binaries", "Win64");
        if (Directory.Exists(nested)) return Path.GetFullPath(nested);
        var executableDirectory = Path.GetDirectoryName(installation.ExecutablePath);
        return Path.GetFullPath(executableDirectory ?? installation.InstallDirectory);
    }

    private static string? FindExecutable(string installDirectory)
    {
        var candidates = new[]
        {
            Path.Combine(installDirectory, "CrabChampions.exe"),
            Path.Combine(installDirectory, "Crab Champions.exe"),
            Path.Combine(installDirectory, "CrabChampions", "Binaries", "Win64", "CrabChampions-Win64-Shipping.exe")
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    [SupportedOSPlatform("windows")]
    private static string? ReadRegistry(RegistryKey root, string path, string name)
    {
        try
        {
            using var key = root.OpenSubKey(path, false);
            return key?.GetValue(name) as string;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    private static void AddIfDirectory(ISet<string> paths, string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;
        paths.Add(Path.GetFullPath(path));
    }

    [GeneratedRegex("\\\"path\\\"\\s+\\\"([^\\\"]+)\\\"", RegexOptions.IgnoreCase)]
    private static partial Regex VdfPathRegex();

    [GeneratedRegex("\\\"installdir\\\"\\s+\\\"([^\\\"]+)\\\"", RegexOptions.IgnoreCase)]
    private static partial Regex InstallDirRegex();
}

public sealed record DashboardResources(
    string Root,
    string PayloadRoot,
    string CampaignRoot,
    string SchemasRoot,
    bool IsPackaged);

public sealed class DashboardResourceLocator
{
    public DashboardResources Locate(string? startPath = null)
    {
        var start = Path.GetFullPath(startPath ?? AppContext.BaseDirectory);
        if (File.Exists(start)) start = Path.GetDirectoryName(start)!;

        for (var current = new DirectoryInfo(start); current is not null; current = current.Parent)
        {
            var payload = Path.Combine(current.FullName, "Payload");
            var campaign = Path.Combine(current.FullName, "campaign");
            var schemas = Path.Combine(current.FullName, "schemas");
            if (Directory.Exists(payload) && Directory.Exists(campaign))
            {
                return new DashboardResources(current.FullName, payload, campaign, schemas, true);
            }

            var repoClient = Path.Combine(current.FullName, "client");
            var repoMod = Path.Combine(repoClient, "Mods", "CrabRuntimeProbe");
            if (Directory.Exists(repoMod) && Directory.Exists(campaign))
            {
                return new DashboardResources(current.FullName, repoClient, campaign, schemas, false);
            }
        }

        throw new DirectoryNotFoundException(
            "Could not locate a packaged Payload/campaign root or a CrabRuntimeProbe repository root.");
    }
}
