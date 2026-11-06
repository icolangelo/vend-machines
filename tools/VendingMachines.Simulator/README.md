# Vend Machine Simulator

CLI de desenvolvimento que simula uma vending machine/ESP32 conectada ao WebSocket `/telemetria`.

Cada processo representa uma máquina. Enquanto o processo estiver aberto, a máquina permanece conectada; ao sair ou pressionar `Ctrl+C`, ela fica offline.

## Executar

Com a API disponível na porta padrão do projeto:

```powershell
dotnet run --project tools/VendingMachines.Simulator -- --serial SN-123456
```

Informando outro servidor:

```powershell
dotnet run --project tools/VendingMachines.Simulator -- --serial SN-123456 --url ws://localhost:5118/telemetria
```

Para simular várias máquinas, abra um terminal para cada serial cadastrado:

```powershell
dotnet run --project tools/VendingMachines.Simulator -- --serial SN-123456
dotnet run --project tools/VendingMachines.Simulator -- --serial SN-987654
```

## Fluxo

O simulador identifica a máquina, mantém heartbeat e reconecta automaticamente. O painel web continua responsável por iniciar a sessão. Antes de iniciá-la, prepare um cenário no terminal:

```text
scenario sale-success 2 2200
```

Quando chegar `ABRIR_SESSAO`, o CLI executa `begin_session`, solicita o produto, exibe o Pix recebido e aguarda a aprovação real do backend. Após `VENDA_APROVADA`, conclui a entrega e encerra a sessão.

O simulador também responde ao comando `MDB_STATUS` diretamente com uma mensagem
`type: "msg"`, `command: "status"` e o estado MDB atual, sem enviar um ACK separado.
Por padrão, o estado acompanha o ciclo da máquina. Para fixar um estado durante os
testes, use:

```text
mdb inactive_state
mdb disable_state
mdb enabled_state
mdb idle_state
mdb vend_state
mdb auto
```

Outros exemplos:

```text
scenario sale 2 2200
scenario cancel-before-selection 10
scenario cancel-after-pix 1 1400
scenario delivery-failure 2 2200 out_of_cup
```

No cenário `sale`, a entrega é decidida manualmente:

```text
deliver success
deliver failure out_of_product
```

Use `help` dentro do CLI para consultar todos os comandos.
