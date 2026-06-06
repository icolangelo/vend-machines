# Estágio de compilação (Build Stage)
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copiar o arquivo de projeto e restaurar as dependências
COPY ["VendingMachines.Api.csproj", "./"]
RUN dotnet restore "VendingMachines.Api.csproj"

# Copiar todos os outros arquivos e compilar a aplicação
COPY . .
RUN dotnet publish "VendingMachines.Api.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Estágio final de execução (Runtime Stage)
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app
EXPOSE 8080
COPY --from=build /app/publish .

# Variável de ambiente para garantir que a aplicação escute na porta correta (padrão do .NET 8/9 em container é 8080)
ENV ASPNETCORE_URLS=http://+:8080

ENTRYPOINT ["dotnet", "VendingMachines.Api.dll"]
