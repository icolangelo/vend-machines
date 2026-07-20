using System.Collections.Concurrent;

namespace VendingMachines.Simulator;

public sealed class SimulatorLog
{
    private readonly object _consoleLock = new();
    private readonly ConcurrentQueue<string> _history = new();

    public void Info(string serial, string message) => Write(serial, "INFO", message, ConsoleColor.Cyan);
    public void Received(string serial, string message) => Write(serial, "RECV", message, ConsoleColor.Green);
    public void Sent(string serial, string message) => Write(serial, "SEND", message, ConsoleColor.Yellow);
    public void Warning(string serial, string message) => Write(serial, "WARN", message, ConsoleColor.DarkYellow);
    public void Error(string serial, string message) => Write(serial, "ERRO", message, ConsoleColor.Red);

    public void PrintHistory(int take = 50)
    {
        foreach (var line in _history.Reverse().Take(take).Reverse()) Console.WriteLine(line);
    }

    private void Write(string serial, string level, string message, ConsoleColor color)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{serial}] [{level}] {message}";
        _history.Enqueue(line);
        while (_history.Count > 200) _history.TryDequeue(out _);

        lock (_consoleLock)
        {
            var previous = Console.ForegroundColor;
            Console.ForegroundColor = color;
            Console.WriteLine(line);
            Console.ForegroundColor = previous;
        }
    }
}
