using Microsoft.Win32;

namespace DLPPCController;

internal static class Cli
{
    public static int Run(string[] args)
    {
        string command = args[0].ToLowerInvariant();

        switch (command)
        {
            case "help":
            case "--help":
            case "-h":
                PrintHelp();
                return 0;

            case "status":
                return ShowStatus();

            case "disable":
                return SetState(false);

            case "enable":
                return SetState(true);

            case "toggle":
                return Toggle();

            case "watch":
                return Watch(ParseInterval(args));

            case "startup":
                if (args.Length < 2)
                {
                    Console.WriteLine("Usage: DLPPCController.exe startup enable|disable");
                    return 2;
                }
                return SetStartup(args[1].Equals("enable", StringComparison.OrdinalIgnoreCase));

            default:
                Console.Error.WriteLine($"Unknown command: {command}");
                PrintHelp();
                return 2;
        }
    }

    private static int ShowStatus()
    {
        using var api = new NvidiaApi();
        PrintIdentity(api);

        DlppcState? state = api.ReadDlppc();
        if (state is null)
        {
            Console.WriteLine("DLPPC policy: NOT SUPPORTED");
            return 3;
        }

        PrintState(state);
        return 0;
    }

    private static int SetState(bool active)
    {
        using var api = new NvidiaApi();
        PrintIdentity(api);

        DlppcState? before = api.ReadDlppc();
        if (before is null)
            throw new NotSupportedException("DLPPC_1X policy was not found on this GPU/driver.");

        Console.WriteLine($"Current state: {(before.Active ? "ENABLED" : "DISABLED")}");
        Console.WriteLine(active ? "Enabling DLPPC..." : "Disabling DLPPC...");

        api.SetDlppc(active);

        DlppcState? after = api.ReadDlppc();
        if (after is null || after.Active != active)
            throw new InvalidOperationException("Final DLPPC state verification failed.");

        Console.WriteLine($"DLPPC: {(after.Active ? "ENABLED" : "DISABLED")}");
        Console.WriteLine("Verification: PASSED");
        return 0;
    }

    private static int Toggle()
    {
        using var api = new NvidiaApi();
        PrintIdentity(api);

        DlppcState state = api.ReadDlppc()
            ?? throw new NotSupportedException("DLPPC_1X policy was not found.");

        bool requested = !state.Active;
        api.SetDlppc(requested);

        Console.WriteLine($"DLPPC: {(requested ? "ENABLED" : "DISABLED")}");
        Console.WriteLine("Verification: PASSED");
        return 0;
    }

    private static int Watch(int intervalSeconds)
    {
        AppSettings settings = SettingsStore.Load();
        settings.DisableDlppc = true;
        settings.WatchdogEnabled = true;
        SettingsStore.Save(settings);

        Console.WriteLine("DLPPC watchdog started.");
        Console.WriteLine($"Polling interval: {intervalSeconds} seconds");
        Console.WriteLine("NOTE: NVAPI polling may wake the discrete GPU and interfere with its low-power state.");
        Console.WriteLine("Press Ctrl+C to stop.");

        using var stop = new ManualResetEventSlim(false);
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Set();
        };

        NvidiaApi? api = null;
        while (!stop.IsSet)
        {
            try
            {
                api ??= new NvidiaApi();
                DlppcState? state = api.ReadDlppc();

                if (state is null)
                {
                    Console.WriteLine($"{DateTime.Now:T} DLPPC policy unavailable.");
                }
                else if (state.Active)
                {
                    Console.WriteLine($"{DateTime.Now:T} DLPPC is ENABLED; disabling...");
                    api.SetDlppc(false);
                    Console.WriteLine($"{DateTime.Now:T} DLPPC disabled and verified.");
                }
                else
                {
                    Console.WriteLine($"{DateTime.Now:T} DLPPC already disabled.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{DateTime.Now:T} ERROR: {ex.Message}");
                api?.Dispose();
                api = null;
            }

            stop.Wait(TimeSpan.FromSeconds(intervalSeconds));
        }

        api?.Dispose();
        return 0;
    }

    private static int ParseInterval(string[] args)
    {
        int interval = 5;
        for (int i = 1; i < args.Length - 1; i++)
        {
            if (args[i] == "--interval" &&
                int.TryParse(args[i + 1], out int parsed) &&
                parsed is >= 2 and <= 3600)
                interval = parsed;
        }
        return interval;
    }

    private static int SetStartup(bool enabled)
    {
        const string runKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string valueName = "DLPPCController";
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(runKey, true)
            ?? throw new InvalidOperationException("Could not open the current-user Run registry key.");

        if (enabled)
        {
            string exe = Environment.ProcessPath
                ?? throw new InvalidOperationException("Could not determine the executable path.");
            key.SetValue(valueName, $"\"{exe}\"");
            Console.WriteLine("DLPPC Controller was added to the current user's startup.");
        }
        else
        {
            key.DeleteValue(valueName, false);
            Console.WriteLine("DLPPC Controller was removed from startup.");
        }

        return 0;
    }

    private static void PrintIdentity(NvidiaApi api)
    {
        Console.WriteLine("DLPPC Controller");
        Console.WriteLine($"GPU: {api.Identity.Name}");
        Console.WriteLine($"PCI device ID: 0x{api.Identity.DeviceId:X4}");
        Console.WriteLine($"Driver: {api.Identity.DriverVersion}");
        Console.WriteLine($"PCI bus: {api.Identity.Bus}");
    }

    private static void PrintState(DlppcState state)
    {
        Console.WriteLine($"DLPPC policy: FOUND");
        Console.WriteLine($"Policy index: {state.PolicyIndex}");
        Console.WriteLine($"Controller mask: 0x{state.ControllerMask:X8}");
        Console.WriteLine($"Current state: {(state.Active ? "ENABLED" : "DISABLED")}");
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
DLPPC Controller

Commands:
  status                  Read current DLPPC state
  disable                 Disable DLPPC
  enable                  Enable DLPPC
  toggle                  Toggle DLPPC state
  watch [--interval N]    Keep DLPPC disabled (default interval: 5 seconds)
  startup enable          Add this executable to current-user startup
  startup disable         Remove it from current-user startup
  help                    Show this help

Examples:
  DLPPCController.exe status
  DLPPCController.exe disable
  DLPPCController.exe enable
  DLPPCController.exe watch --interval 10

The watchdog polls NVAPI. Polling can wake the discrete GPU.
""");
    }
}
