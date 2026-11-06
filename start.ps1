# Script para iniciar a API (.NET) e o Frontend (Vite) juntos
Write-Host "=========================================" -ForegroundColor Cyan
Write-Host " Iniciando Vending Machines System " -ForegroundColor Cyan
Write-Host "=========================================" -ForegroundColor Cyan

# 1. Iniciar o Backend (API .NET)
Write-Host "Iniciando a API .NET em uma nova janela..." -ForegroundColor Yellow
Start-Process powershell -ArgumentList "-NoExit", "-Command", "Write-Host 'Iniciando API .NET...' -ForegroundColor Yellow; dotnet run --project apps/backend/VendingMachines.Api"

# 2. Iniciar o Frontend (Vite/React)
Write-Host "Iniciando o Frontend em uma nova janela..." -ForegroundColor Yellow
Start-Process powershell -ArgumentList "-NoExit", "-Command", "Write-Host 'Iniciando Frontend (Vite)...' -ForegroundColor Yellow; cd apps/frontend; npm.cmd run dev"

Write-Host ""
Write-Host "Sucesso! Ambas as aplicações foram iniciadas em novas janelas do PowerShell." -ForegroundColor Green
Write-Host "Você pode fechar esta janela agora." -ForegroundColor Gray
