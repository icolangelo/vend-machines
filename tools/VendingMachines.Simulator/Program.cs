namespace VendingMachines.Simulator;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (!CliOptions.TryParse(args, out var options, out var error))
        {
            if (!string.IsNullOrWhiteSpace(error))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Error.WriteLine(error);
                Console.ResetColor();
            }
            CliOptions.PrintUsage();
            return string.IsNullOrWhiteSpace(error) ? 0 : 2;
        }

        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        };

        var parsedOptions = options!;
        var log = new SimulatorLog();
        var simulator = new MachineSimulator(parsedOptions, log);
        var shell = new InteractiveShell(simulator, log);

        var connectionTask = simulator.RunAsync(shutdown.Token);
        if (parsedOptions.Headless)
        {
            try { await connectionTask; }
            catch (OperationCanceledException) { }
            return 0;
        }

        await shell.RunAsync(shutdown.Token);
        shutdown.Cancel();

        try { await connectionTask; }
        catch (OperationCanceledException) { }
        Console.WriteLine($"[{simulator.Serial}] Máquina desligada.");
        return 0;
    }
}
