# Estágio de compilação (Build Stage)
FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:de4fe90a98845d75910b9769662465c84de1a934665c431279d1382b2bddc02b AS build
WORKDIR /src

# Copiar o arquivo de projeto e restaurar as dependências
COPY ["VendingMachines.Api.csproj", "./"]
RUN dotnet restore "VendingMachines.Api.csproj"

# Copiar todos os outros arquivos e compilar a aplicação
COPY . .
# Configuração específica de ambiente e segredos vêm do runtime, nunca da imagem.
RUN dotnet publish "VendingMachines.Api.csproj" -c Release -o /app/publish /p:UseAppHost=false \
    && rm -f /app/publish/appsettings.*.json

# Estágio final de execução (Runtime Stage)
FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:de0ee358cdd672f7017a003bed21e9ac9ed48a27c922a59b18175b6e82fa7fee AS final
WORKDIR /app
ARG APP_REVISION=unknown
ENV APP_REVISION=${APP_REVISION}
ENV VendingMachines__RejectDevelopmentEnvironment=true
EXPOSE 8080
COPY --from=build /app/publish .

# O proxy de produção termina TLS; o container escuta na porta interna 8080.
ENV ASPNETCORE_URLS=http://+:8080

# Modo ativo exige readiness da API e do banco; smoke passivo valida somente que o processo atende.
HEALTHCHECK --interval=15s --timeout=5s --start-period=60s --retries=3 \
  CMD ["bash", "-ec", "if [[ \"${Deployment__PassiveMode:-false}\" == \"true\" ]]; then health_path=/health; else health_path=/health/ready; fi; exec 3<>/dev/tcp/127.0.0.1/8080; printf 'GET %s HTTP/1.0\\r\\nHost: localhost\\r\\nConnection: close\\r\\n\\r\\n' \"$health_path\" >&3; IFS= read -r status <&3; [[ \"$status\" == *' 200 '* ]]"]

USER app
ENTRYPOINT ["dotnet", "VendingMachines.Api.dll"]
