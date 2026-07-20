namespace VendingMachines.Simulator;

public sealed record CliOptions(
    string Serial,
    Uri ServerUri,
    int Signal,
    bool Reconnect,
    TimeSpan HeartbeatInterval)
{
    public static bool TryParse(string[] args, out CliOptions? options, out string? error)
    {
        options = null;
        error = null;

        if (args.Any(x => x is "--help" or "-h")) return false;

        string? serial = null;
        var url = "ws://localhost:5118/telemetria";
        var signal = 20;
        var reconnect = true;
        var heartbeatSeconds = 30;

        for (var i = 0; i < args.Length; i++)
        {
            var argument = args[i];
            string NextValue()
            {
                if (++i >= args.Length) throw new ArgumentException($"Faltou o valor de {argument}.");
                return args[i];
            }

            try
            {
                switch (argument)
                {
                    case "--serial": case "-s": serial = NextValue(); break;
                    case "--url": case "-u": url = NextValue(); break;
                    case "--signal": signal = int.Parse(NextValue()); break;
                    case "--heartbeat": heartbeatSeconds = int.Parse(NextValue()); break;
                    case "--no-reconnect": reconnect = false; break;
                    default: throw new ArgumentException($"Argumento desconhecido: {argument}");
                }
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
            {
                error = ex.Message;
                return false;
            }
        }

        serial = serial?.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(serial))
        {
            error = "Informe o serial com --serial.";
            return false;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("ws" or "wss"))
        {
            error = "A URL precisa ser absoluta e usar ws:// ou wss://.";
            return false;
        }

        if (signal is < 0 or > 100)
        {
            error = "O sinal deve estar entre 0 e 100.";
            return false;
        }

        if (heartbeatSeconds is < 5 or > 300)
        {
            error = "O heartbeat deve estar entre 5 e 300 segundos.";
            return false;
        }

        options = new CliOptions(serial, uri, signal, reconnect, TimeSpan.FromSeconds(heartbeatSeconds));
        return true;
    }

    public static void PrintUsage()
    {
        Console.WriteLine("""
            Vend Machine Simulator

            Uso:
              dotnet run --project tools/VendingMachines.Simulator -- --serial <SERIAL> [opções]

            Opções:
              -s, --serial <serial>      Serial cadastrado da máquina (obrigatório)
              -u, --url <url>            WebSocket (padrão: ws://localhost:5118/telemetria)
                  --signal <0-100>       Intensidade inicial do sinal (padrão: 20)
                  --heartbeat <segundos> Intervalo do ping (padrão: 30)
                  --no-reconnect         Não reconectar depois de uma queda
              -h, --help                 Exibir esta ajuda
            """);
    }
}
