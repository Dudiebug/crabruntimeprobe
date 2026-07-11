using CrabRuntimeProbe.Dashboard.Core;
using System.Text.Json;

return await ProgramMain.RunAsync(args);

internal static class ProgramMain
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (args.Length == 0 || Has(args, "--help") || Has(args, "-h"))
            {
                Help();
                return 0;
            }
            if (Has(args, "--self-test"))
            {
                foreach (var result in await CoreSelfTest.RunAsync()) Console.WriteLine(result);
                Console.WriteLine("Core self-test passed.");
                return 0;
            }
            if (Has(args, "--demo"))
            {
                var snapshot = new LiveStatusReader().Parse(DemoStatus.Json, "embedded-demo");
                Console.WriteLine(JsonSerializer.Serialize(snapshot, JsonOptions));
                return 0;
            }
            var fixture = Value(args, "--fixture");
            if (fixture is not null)
            {
                var snapshot = await new LiveStatusReader().ParseFileAsync(fixture);
                Console.WriteLine(JsonSerializer.Serialize(snapshot, JsonOptions));
                return 0;
            }

            var command = args[0].ToLowerInvariant();
            var store = new DashboardStateStore(Value(args, "--state-root"));
            switch (command)
            {
                case "detect":
                    Console.WriteLine(JsonSerializer.Serialize(new SteamGameLocator().Detect(), JsonOptions));
                    return 0;
                case "status":
                {
                    var directory = Value(args, "--dir") ?? (await store.LoadCampaignAsync())?.StatusDirectory
                        ?? throw new ArgumentException("Use --dir or prepare a campaign first.");
                    var result = await new LiveStatusReader().ReadLatestAsync(directory);
                    Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
                    return result.HasSnapshot ? 0 : 2;
                }
                case "prepare":
                {
                    var installation = Installation(args);
                    var role = CampaignRoleNames.Parse(Value(args, "--role"));
                    var name = Value(args, "--name") ?? "CrabSync Full Observe";
                    var state = await new CampaignService(store).PrepareAsync(
                        installation,
                        role,
                        name,
                        Value(args, "--resource-root"));
                    Console.WriteLine(JsonSerializer.Serialize(state, JsonOptions));
                    return 0;
                }
                case "resume":
                {
                    var state = await new CampaignService(store).ResumeAsync();
                    Console.WriteLine(JsonSerializer.Serialize(state, JsonOptions));
                    return state is null ? 2 : 0;
                }
                case "launch":
                {
                    var installation = Installation(args);
                    var process = new GameProcessService().Launch(installation);
                    Console.WriteLine($"Started or found Crab Champions process {process.Id}.");
                    return 0;
                }
                case "stop":
                {
                    var state = await store.LoadCampaignAsync() ?? throw new InvalidOperationException("No active campaign.");
                    await new CampaignService(store).RequestStopAsync(state);
                    Console.WriteLine("Diagnostic stop marker written.");
                    return 0;
                }
                case "collect":
                {
                    var state = await store.LoadCampaignAsync() ?? throw new InvalidOperationException("No active campaign.");
                    var output = Value(args, "--out") ?? Path.Combine(Environment.CurrentDirectory, "exports");
                    var result = await new EvidenceCollector().CollectAsync(state, output, Has(args, "--abnormal-exit"));
                    Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
                    return 0;
                }
                case "combine":
                {
                    var output = Value(args, "--out") ?? Path.Combine(Environment.CurrentDirectory, "combined");
                    var zips = PositionalAfterOptions(args.Skip(1).ToArray()).ToArray();
                    var result = await new BundleCorrelationService().CombineAsync(zips, output);
                    Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
                    return result.CorrelationEstablished ? 0 : 3;
                }
                case "support-summary":
                {
                    var state = await store.LoadCampaignAsync();
                    var status = state is null
                        ? new LiveStatusReadResult(LiveStatusSnapshot.Empty, false, true, false, "No campaign", DateTimeOffset.UtcNow)
                        : await new LiveStatusReader().ReadLatestAsync(state.StatusDirectory);
                    Console.WriteLine(SupportSummary.Create(state, status));
                    return 0;
                }
                default:
                    throw new ArgumentException($"Unknown command: {command}");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            return 1;
        }
    }

    private static GameInstallation Installation(string[] args)
    {
        var game = Value(args, "--game") ?? throw new ArgumentException("Use --game <Crab Champions install directory>.");
        return new SteamGameLocator().ValidateSelectedDirectory(game)
            ?? throw new DirectoryNotFoundException("The selected directory is not a Crab Champions installation.");
    }

    private static bool Has(IEnumerable<string> args, string name) =>
        args.Any(value => value.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static string? Value(IReadOnlyList<string> args, string name)
    {
        for (var index = 0; index < args.Count - 1; index++)
            if (args[index].Equals(name, StringComparison.OrdinalIgnoreCase)) return args[index + 1];
        return null;
    }

    private static IEnumerable<string> PositionalAfterOptions(IReadOnlyList<string> args)
    {
        for (var index = 0; index < args.Count; index++)
        {
            if (args[index].StartsWith("--", StringComparison.Ordinal))
            {
                if (index + 1 < args.Count && !args[index + 1].StartsWith("--", StringComparison.Ordinal)) index++;
                continue;
            }
            yield return args[index];
        }
    }

    private static void Help()
    {
        Console.WriteLine("""
        CrabRuntimeProbe Dashboard CLI

          detect
          prepare --game <install-dir> --role host|joined-client [--name <campaign>] [--resource-root <bundle-or-repo>]
          resume
          launch --game <install-dir>
          status [--dir <Scripts/results>]
          stop
          collect [--out <directory>] [--abnormal-exit]
          combine [--out <directory>] <host.zip> <joined.zip> [...]
          support-summary
          --fixture <live-status.json>
          --demo
          --self-test
        """);
    }
}
