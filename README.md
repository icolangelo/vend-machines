# vend-machines

## Simulador de vending machine

Para manter uma máquina simulada conectada ao WebSocket de telemetria durante o desenvolvimento:

```powershell
dotnet run --project tools/VendingMachines.Simulator -- --serial SN-123456
```

Cada processo representa uma máquina e permanece online até o terminal ser encerrado. Consulte a [documentação do simulador](tools/VendingMachines.Simulator/README.md) para os cenários interativos.

## Como iniciar o projeto

Para iniciar a **API (.NET)** e o **Frontend (Vite/React)** juntos, você pode usar um dos scripts fornecidos na raiz do projeto:

### Opção 1: Usando o Script em Lote (Batch) - Recomendado para Windows
Dê um duplo clique no arquivo [start.bat](file:///d:/projects/vend-machines/start.bat) ou execute o comando abaixo no Prompt de Comando (`cmd`):
```cmd
start.bat
```

### Opção 2: Usando o PowerShell
Execute o script [start.ps1](file:///d:/projects/vend-machines/start.ps1) no PowerShell:
```powershell
.\start.ps1
```

*(Ambos os scripts abrirão janelas de terminal separadas para a API e o Frontend, permitindo visualizar os logs de cada serviço de forma isolada).*
