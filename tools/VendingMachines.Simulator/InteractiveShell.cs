using System.Text.Json;

namespace VendingMachines.Simulator;

public sealed class InteractiveShell
{
    private readonly MachineSimulator _simulator;
    private readonly SimulatorLog _log;

    public InteractiveShell(MachineSimulator simulator, SimulatorLog log)
    {
        _simulator = simulator;
        _log = log;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        PrintWelcome();
        while (!cancellationToken.IsCancellationRequested)
        {
            Console.Write($"{_simulator.Serial}> ");
            string? line;
            try { line = await Console.In.ReadLineAsync(cancellationToken); }
            catch (OperationCanceledException) { return; }
            if (line == null) return;
            line = line.Trim();
            if (line.Length == 0) continue;

            try
            {
                if (!await ExecuteAsync(line, cancellationToken)) return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                _log.Error(_simulator.Serial, ex.Message);
            }
        }
    }

    private async Task<bool> ExecuteAsync(string line, CancellationToken cancellationToken)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var command = parts[0].ToLowerInvariant();
        switch (command)
        {
            case "help": case "ajuda": PrintHelp(); break;
            case "status": PrintStatus(); break;
            case "scenario": case "cenario": ParseScenario(parts); break;
            case "select": case "selecionar":
                Require(parts, 3, "select <item> <preço-em-centavos>");
                await _simulator.SelectProductAsync(ParseInt(parts[1], "item"), ParseInt(parts[2], "preço"), cancellationToken);
                break;
            case "deliver": case "entregar": await DeliverAsync(parts, cancellationToken); break;
            case "cancel": case "cancelar": await _simulator.CancelFromMachineAsync(cancellationToken); break;
            case "signal": case "sinal":
                Require(parts, 2, "signal <0-100>");
                _simulator.SetSignal(ParseInt(parts[1], "sinal"));
                break;
            case "mdb":
                Require(parts, 2, "mdb <inactive_state|disable_state|enabled_state|idle_state|vend_state|auto>");
                _simulator.SetMdbStatus(parts[1]);
                break;
            case "send": case "enviar": await SendCustomAsync(line, cancellationToken); break;
            case "logs": _log.PrintHistory(); break;
            case "reconnect": case "reconectar": _simulator.Reconnect(); break;
            case "clear": case "limpar": Console.Clear(); break;
            case "exit": case "quit": case "sair": return false;
            default: throw new InvalidOperationException($"Comando desconhecido: {parts[0]}. Digite 'help'.");
        }
        return true;
    }

    private void ParseScenario(string[] parts)
    {
        Require(parts, 2, "scenario <nome> [argumentos]");
        var name = parts[1].ToLowerInvariant();
        ScenarioPlan scenario = name switch
        {
            "sale-success" or "venda-sucesso" => SaleScenario(ScenarioKind.SaleSuccess, parts),
            "sale" or "venda" => SaleScenario(ScenarioKind.Sale, parts),
            "cancel-before-selection" or "cancelar-antes-selecao" => new ScenarioPlan(
                ScenarioKind.CancelBeforeSelection,
                Delay: TimeSpan.FromSeconds(parts.Length >= 3 ? ParseInt(parts[2], "atraso") : 1)),
            "cancel-after-pix" or "cancelar-apos-pix" => SaleScenario(ScenarioKind.CancelAfterPix, parts),
            "delivery-failure" or "falha-entrega" => FailureScenario(parts),
            "none" or "nenhum" => ScenarioPlan.None,
            _ => throw new InvalidOperationException("Cenário inválido. Use: sale-success, sale, cancel-before-selection, cancel-after-pix ou delivery-failure.")
        };
        _simulator.ArmScenario(scenario);
    }

    private static ScenarioPlan SaleScenario(ScenarioKind kind, string[] parts)
    {
        Require(parts, 4, $"scenario {parts[1]} <item> <preço-em-centavos> [atraso-em-segundos]");
        var delay = parts.Length >= 5 ? TimeSpan.FromSeconds(ParseInt(parts[4], "atraso")) : (TimeSpan?)null;
        return new ScenarioPlan(kind, ParseInt(parts[2], "item"), ParseInt(parts[3], "preço"), Delay: delay);
    }

    private static ScenarioPlan FailureScenario(string[] parts)
    {
        Require(parts, 5, "scenario delivery-failure <item> <preço-em-centavos> <motivo> [atraso-em-segundos]");
        var delay = parts.Length >= 6 ? TimeSpan.FromSeconds(ParseInt(parts[5], "atraso")) : (TimeSpan?)null;
        return new ScenarioPlan(
            ScenarioKind.DeliveryFailure,
            ParseInt(parts[2], "item"),
            ParseInt(parts[3], "preço"),
            parts[4],
            delay);
    }

    private async Task DeliverAsync(string[] parts, CancellationToken cancellationToken)
    {
        Require(parts, 2, "deliver success | deliver failure <motivo>");
        if (parts[1].Equals("success", StringComparison.OrdinalIgnoreCase) || parts[1].Equals("sucesso", StringComparison.OrdinalIgnoreCase))
        {
            await _simulator.DeliverSuccessAsync(cancellationToken);
            return;
        }
        if (parts[1].Equals("failure", StringComparison.OrdinalIgnoreCase) || parts[1].Equals("falha", StringComparison.OrdinalIgnoreCase))
        {
            Require(parts, 3, "deliver failure <motivo>");
            await _simulator.DeliverFailureAsync(parts[2], cancellationToken);
            return;
        }
        throw new InvalidOperationException("Use 'deliver success' ou 'deliver failure <motivo>'.");
    }

    private async Task SendCustomAsync(string line, CancellationToken cancellationToken)
    {
        var separator = line.IndexOf(' ');
        if (separator < 0) throw new InvalidOperationException("Use: send <objeto-json>");
        using var document = JsonDocument.Parse(line[(separator + 1)..]);
        await _simulator.SendCustomAsync(document.RootElement.Clone(), cancellationToken);
    }

    private void PrintStatus()
    {
        Console.WriteLine($"""
            Serial:               {_simulator.Serial}
            Conectada:            {(_simulator.IsConnected ? "sim" : "não")}
            Estado:               {_simulator.State}
            Status MDB:           {_simulator.MdbStatus}
            Cenário:              {_simulator.Scenario.Kind}
            Transação:            {_simulator.TransactionId ?? "-"}
            Sinal:                {_simulator.Signal}
            Conectada desde UTC:  {_simulator.ConnectedAt?.ToString("O") ?? "-"}
            Último pong UTC:      {_simulator.LastPongAt?.ToString("O") ?? "-"}
            Mensagens enviadas:   {_simulator.SentMessages}
            Mensagens recebidas:  {_simulator.ReceivedMessages}
            """);
    }

    private void PrintWelcome()
    {
        Console.WriteLine($"""

            Vend Machine Simulator
            Máquina: {_simulator.Serial}
            Digite 'help' para ver os comandos. Use Ctrl+C ou 'exit' para desligar a máquina.

            """);
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            Comandos:
              status
              scenario sale-success <item> <preço-centavos> [atraso-segundos]
              scenario sale <item> <preço-centavos> [atraso-segundos]
              scenario cancel-before-selection [atraso-segundos]
              scenario cancel-after-pix <item> <preço-centavos> [atraso-segundos]
              scenario delivery-failure <item> <preço-centavos> <motivo> [atraso-segundos]
              scenario none
              select <item> <preço-centavos>
              deliver success
              deliver failure <motivo>
              cancel
              signal <0-100>
              mdb <inactive_state|disable_state|enabled_state|idle_state|vend_state|auto>
              send <objeto-json-ou-string-json>
              logs
              reconnect
              clear
              exit

            O painel deve iniciar a sessão. Um cenário preparado começa quando a máquina
            receber ABRIR_SESSAO. A aprovação do Pix continua vindo do backend real.
            """);
    }

    private static void Require(string[] parts, int count, string usage)
    {
        if (parts.Length < count) throw new InvalidOperationException($"Uso: {usage}");
    }

    private static int ParseInt(string value, string field) =>
        int.TryParse(value, out var parsed) ? parsed : throw new InvalidOperationException($"Valor inválido para {field}: {value}");
}
