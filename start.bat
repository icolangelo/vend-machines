@echo off
title Vending Machines - Startup Script
echo =========================================
echo  Iniciando Vending Machines System
echo =========================================
echo.

:: Iniciar a API .NET
echo Iniciando a API .NET em uma nova janela...
start "Vending Machines - API" cmd /k "echo Iniciando API .NET... && dotnet run --project apps/backend/VendingMachines.Api"

:: Iniciar o Frontend
echo Iniciando o Frontend em uma nova janela...
start "Vending Machines - Frontend" cmd /k "echo Iniciando Frontend (Vite)... && cd apps/frontend && npm run dev"

echo.
echo Sucesso! Ambas as aplicacoes foram iniciadas em novas janelas do terminal.
echo Pressione qualquer tecla para fechar esta janela...
pause >nul
