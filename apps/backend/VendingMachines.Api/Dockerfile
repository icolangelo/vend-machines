# Estágio de compilação (Build Stage)
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copiar o arquivo de projeto e restaurar as dependências
COPY ["VendingMachines.Api.csproj", "./"]
RUN dotnet restore "VendingMachines.Api.csproj"

# Copiar todos os outros arquivos e compilar a aplicação
COPY . .
RUN dotnet publish "VendingMachines.Api.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Estágio final de execução (Runtime Stage)
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
EXPOSE 8080
COPY --from=build /app/publish .

# O proxy de produção termina TLS; o container escuta na porta interna 8080.
ENV ASPNETCORE_URLS=http://+:8080

# Coolify só pode promover o replacement depois da API e do banco estarem prontos.
HEALTHCHECK --interval=15s --timeout=5s --start-period=60s --retries=3 \
  CMD ["bash", "-ec", "exec 3<>/dev/tcp/127.0.0.1/8080; printf 'GET /health/ready HTTP/1.0\\r\\nHost: localhost\\r\\nConnection: close\\r\\n\\r\\n' >&3; IFS= read -r status <&3; [[ \"$status\" == *' 200 '* ]]"]

USER app
ENTRYPOINT ["dotnet", "VendingMachines.Api.dll"]
