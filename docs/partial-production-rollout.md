# Preflight de convivência da API

Este preflight é somente leitura. Ele não envia comandos MDB, não cria sessões, não altera configuração, não aplica migrations e não inicia publicação. Seu objetivo é confirmar que a candidata consegue atender enquanto a API anterior continua dona de conexões existentes.

## Smoke privado em modo passivo

Para apenas subir a imagem candidata e verificar o processo, o launcher inicia em modo passivo por padrão e injeta `Deployment__PassiveMode=true`, sem exigir a origem legada. Se iniciar o container diretamente, configure essa variável explicitamente. O launcher recusa um arquivo de runtime que tente definir `Deployment__PassiveMode`, para evitar conflito ou override acidental. Nesse modo a API não executa `DbInitializer`, não registra nenhum worker em background e permite somente `GET /`, `GET /health` e `GET /health/ready`; todas as rotas de aplicação, Swagger e WebSockets, incluindo `/telemetria`, retornam `503`. O healthcheck do container usa somente `GET /health` em modo passivo para confirmar que o processo responde, mesmo sem migrations aplicadas. `/health/ready` faz apenas conexão e leitura da tabela de migrations; o launcher exige `200` nesse endpoint antes de declarar o smoke passivo aprovado. Se retornar `503` por banco inacessível ou migrations pendentes, o launcher para somente a candidata e não tenta aplicar migrations. `/health/deploy-ready` fica bloqueado intencionalmente porque esse modo não significa prontidão para cutover.

Esse smoke confirma somente que o container e a conexão/readiness de banco funcionam. Não permite testar dashboard, APIs comerciais, relay ou tráfego de máquinas, e não substitui o preflight de convivência descrito abaixo. Para testar a candidata como API de aplicação, use uma cópia isolada restaurada do banco e a validação de release completa. Nunca desligue o modo passivo numa instância ligada ao banco comercial até cumprir os gates de backup, migration e operação da seção seguinte. O launcher exige a flag `--active-overlap` para iniciar workers e aceitar rotas comerciais; essa flag é somente um seletor técnico e não substitui nenhuma aprovação ou evidência operacional.

Na inicialização ativa, depois de validar o schema e antes de o host iniciar qualquer worker, a API verifica se todo socket recente tem owner/lease/rota válidos ou pertence ao conjunto legado encaminhável. Se houver máquinas ainda conectadas à API anterior, a configuração de relay privado precisa estar presente e `GET /health` da API anterior deve responder saudável. Qualquer socket recente sem rota válida, relay ausente ou API anterior indisponível faz a candidata encerrar o startup antes de iniciar workers ou aceitar tráfego; a API anterior continua ativa. Isso é uma barreira adicional, não substitui o preflight de 120 segundos nem os gates de backup, migrations e operação.

## Configuração da candidata

1. Mantenha a API anterior ativa e sem reinício enquanto ela possuir WebSockets de máquinas.
2. Na configuração de runtime somente da candidata, defina `Telemetry__LegacyApiBaseUrl` com a origem privada da API anterior, por exemplo `http://previous-api:8080`. Use apenas esquema, host e porta; não inclua caminho, query, fragmento, usuário ou senha. A rota precisa levar diretamente ao processo anterior, nunca ao endereço público que encaminha tráfego à candidata.
3. A partir da rede da candidata, confirme que `GET <Telemetry__LegacyApiBaseUrl>/health` responde HTTP 200 com `{"status":"Healthy"}`. O readiness da candidata repete essa verificação quando há sockets legados elegíveis e falha fechado se o owner ficar inacessível.
4. Não coloque token, URL privada ou segredo em arquivos versionados, argumentos de linha de comando, logs, imagem ou saída compartilhada.

## Verificação somente leitura

Execute de uma máquina autorizada a consultar a candidata e a API antiga. O token precisa pertencer a um administrador de plataforma. Sem variável de ambiente, o script solicita o token com entrada oculta; não o coloque no comando, em arquivo ou em histórico de shell. `--expected-commit` é obrigatório e deve receber o SHA Git completo, com 40 caracteres hexadecimais; o script compara o valor inteiro informado pelos endpoints de readiness. `--minimum-legacy-connections` também é obrigatório: informe a quantidade esperada de sockets conforme a fonte operacional da frota; não use `1` por conveniência quando houver mais máquinas conectadas:

```powershell
python ./Validate-TelemetryOverlapReadiness.py `
  --api-base-url 'http://candidate-api:8080' `
  --expected-commit '<sha da candidata>' `
  --minimum-legacy-connections '<quantidade esperada de sockets legados>' `
  --maximum-clock-skew-seconds 30 `
  --observation-seconds 120 `
  --sample-interval-seconds 15
```

O preflight compara `/health.timestamp` com o relógio UTC local e falha se a diferença exceder o padrão de 30 segundos. Isso detecta relógio local sem NTP ou diferença entre a API e a máquina operadora; confirme `w32tm /query /status` (Windows) ou `timedatectl status` (Linux) antes de iniciar. O limite deixa margem abaixo da janela de presença de 90 segundos. Cada amostra registra o desvio observado.

Com `--observation-seconds` maior que zero, o script repete todas as consultas de readiness, relay e conexões até o fim da janela e falha na primeira amostra abaixo do mínimo informado. Zero mantém uma única amostra. A janela indicada de 120 segundos cobre a expiração de presença da API (90 segundos) e a persistência do `LastSeenAt` (até 60 segundos); aumente-a caso o intervalo normal de ping do firmware seja maior. Em cada amostra, o script exige que o `LastSeenAt` de cada máquina tenha no máximo 75 segundos, abaixo do limite de presença de 90 segundos. Também exige que cada `LastSeenAt` avance ao menos uma vez durante a janela, detectando estagnação no final da observação. O intervalo entre amostras deve deixar margem suficiente para observar a expiração de presença. O script busca todas as páginas administrativas, exige IDs de máquina únicos e confirma que o conjunto permaneça idêntico. Assim, uma conexão listada com presença antiga, um ping parado ou uma reconexão mascarada por uma contagem igual não bastam para aprovar o preflight. Os IDs não são impressos; a saída registra a impressão SHA-256 do conjunto ao concluir. Cada amostra também registra SHA candidato, horário UTC e quantidade observada, sem emitir comandos MDB ou criar sessões.

O verificador consulta `/health`, `/health/ready`, `/health/deploy-ready`, `/api/admin/legacy-machine-connections` e `/api/admin/worker-health`. Não segue redirects. Só retorna sucesso quando:

- os endpoints de saúde estão prontos e informam o SHA esperado;
- a lista administrativa observa pelo menos o mínimo informado de conexões legadas;
- os mesmos IDs de máquina permanecem conectados durante toda a janela, sem substituição mascarada por uma contagem igual;
- o `LastSeenAt` persistido permanece com no máximo 75 segundos de idade em cada amostra e avança ao menos uma vez para cada máquina durante a janela;
- o relay está configurado e saudável;
- a contagem do relay coincide nos dois diagnósticos e a candidata reporta zero conexões online sem rota.

Qualquer divergência, `401/403`, readiness bloqueado, rota inacessível, redirect ou contagem abaixo do mínimo encerra o preflight com código diferente de zero. Corrija a causa e repita a leitura; não desative o gate para prosseguir. O token não é impresso pelo script.

## Imagem imutável da candidata

O workflow de verificação constrói e testa a imagem backend sem publicá-la em um registry nem iniciar um serviço. Somente depois dos gates de sobreposição e release, ele anexa `candidate-backend-image-<SHA>` à execução aprovada. O pacote contém `backend-image.tar.gz`, `image-ref.txt`, `image-id.txt`, `platform.txt`, `source-sha.txt`, `README.txt`, `Verify-BackendImageArtifact.ps1`, `start-private-backend-candidate.sh`, `verify-candidate-artifact-pair.py` e `SHA256SUMS`.

Use os dois artefatos da mesma execução concluída com sucesso. Antes de revisar ou aplicar qualquer SQL, rode `python3 ./verify-candidate-artifact-pair.py --image-artifact-dir '<diretório da imagem>' --release-package-dir '<diretório do pacote operacional>' --expected-source-sha '<SHA Git completo>'`. O verificador valida os manifests/checksums dos dois pacotes, confirma a igualdade do SHA completo e rejeita arquivos extras na imagem, caminhos inesperados, checksums inválidos e divergência entre os pacotes. Em Windows com Docker, `Verify-BackendImageArtifact.ps1` também confere a plataforma antes de carregar a imagem. No host Linux Docker, o launcher repete a comparação dos artefatos antes de `docker load`, confere o ID da imagem carregada e exige rede/runtime privados. O arquivo de runtime precisa ficar fora do repositório, dos diretórios dos artefatos e do pacote operacional, legível somente pelo operador (`chmod 600`). Sem `--active-overlap`, o launcher sobe por padrão no modo passivo descrito acima. Só para convivência funcional, depois dos gates operacionais, passe explicitamente `--active-overlap --legacy-api-origin http://<API antiga privada>:8080`; isso desliga o modo passivo e ativa workers. O launcher inicia um container com nome exclusivo, sem restart automático, publica apenas em `127.0.0.1:<porta>:8080` e espera até três minutos pelo health check. Não altera rota pública, não remove containers existentes e não aplica migrations. Antes de usar o modo ativo, conclua o ensaio SQL isolado, obtenha backup restaurável, rode o preflight SQL e aplique apenas as migrations aprovadas do mesmo SHA; a API recusa iniciar com migrations pendentes. A origem legada precisa apontar diretamente ao processo antigo pela rede privada. Após o health check ativo, execute o preflight administrativo somente leitura contra o SHA completo antes de encaminhar tráfego.

Antes de extrair, compare o SHA-256 de **cada arquivo ZIP** com o `digest` exibido nos metadados dos artefatos da execução GitHub Actions aprovada. Confirme também que os dois artefatos vieram da mesma execução e têm o SHA completo esperado no nome. O digest deve ser obtido dos metadados do GitHub, separadamente do arquivo baixado; os manifests internos não substituem essa verificação externa.

No Linux:

```bash
printf '%s  %s\n' '<digest SHA-256 do GitHub sem o prefixo sha256:>' '<arquivo.zip>' | sha256sum --check --strict
```

No PowerShell:

```powershell
$ExpectedArtifactSha256 = '<digest SHA-256 do GitHub sem o prefixo sha256:>'
$ActualArtifactSha256 = (Get-FileHash -LiteralPath '<arquivo.zip>' -Algorithm SHA256).Hash
if ($ActualArtifactSha256 -ne $ExpectedArtifactSha256) { throw 'Digest do ZIP diverge dos metadados do GitHub.' }
```

Se qualquer digest divergir, o artefato estiver expirado, ou os nomes/execuções/SHA não coincidirem, não extraia nem use os arquivos; obtenha o par novamente ou gere uma execução aprovada para o SHA revisado. Extraia cada ZIP em diretório vazio separado e então execute o verificador de pareamento.

Antes de iniciar a candidata no mesmo host da API atual, meça o consumo da API e dos demais serviços durante o pico normal com `docker stats`, confira `nproc`, `MemAvailable` em `/proc/meminfo` e espaço livre no diretório informado por `docker info --format '{{.DockerRootDir}}'`. Defina um teto de CPU/memória para a candidata e uma reserva que cubra o pico medido da API atual, os demais serviços e o sistema operacional. Defina também uma reserva de disco para as operações normais do host. Não use valores de exemplo sem comparar com essas medições. O launcher falha antes de carregar a imagem se o teto mais a reserva exceder os CPUs disponíveis, a memória disponível for menor que teto mais reserva, ou o espaço livre do Docker não comportar o tamanho descompactado estimado da imagem mais a reserva. O container recebe `--cpus` e `--memory`, limitando quanto pode consumir; isso reduz competição, mas não substitui acompanhar a API antiga durante o smoke.

Meça também o limite de conexões simultâneas do PostgreSQL/provedor e o pico de conexões da API atual. Escolha `--candidate-db-max-pool-size` somente dentro do saldo que o provedor pode suportar depois de reservar a API atual, os demais serviços e a margem operacional. Esse teto é obrigatório e vale para um único pool compartilhado pela candidata entre EF Core e telemetria. A configuração só afeta a candidata iniciada pelo launcher; não altere o pool da API atual.

Para coletar uma amostra agregada no banco, execute a consulta somente de leitura abaixo durante a carga de pico e repita a amostragem ao longo dessa janela. Ela não lista usuários, bancos, clientes, consultas nem strings de conexão. Registre a maior quantidade observada de conexões de cliente e a menor folga calculada; desconte ainda a margem operacional aprovada antes de definir o pool da candidata. A consulta conta a própria conexão como cliente. Se as permissões do provedor impedirem observar todas as conexões, trate a medição como inconclusiva e obtenha o limite/consumo pelo painel ou suporte do provedor.

```sql
WITH limits AS (
    SELECT
        current_setting('max_connections')::integer AS max_connections,
        COALESCE(NULLIF(current_setting('reserved_connections', true), ''), '0')::integer AS reserved_connections,
        current_setting('superuser_reserved_connections')::integer AS superuser_reserved_connections
),
usage AS (
    SELECT
        count(*) FILTER (WHERE backend_type = 'client backend')::integer AS client_connections,
        count(*) FILTER (WHERE backend_type = 'client backend' AND state = 'active')::integer AS active_client_connections,
        count(*) FILTER (WHERE backend_type = 'client backend' AND state = 'idle')::integer AS idle_client_connections
    FROM pg_stat_activity
)
SELECT
    limits.max_connections,
    limits.reserved_connections,
    limits.superuser_reserved_connections,
    usage.client_connections,
    usage.active_client_connections,
    usage.idle_client_connections,
    GREATEST(
        limits.max_connections
        - limits.reserved_connections
        - limits.superuser_reserved_connections
        - usage.client_connections,
        0
    ) AS unreserved_slots_remaining_at_sample
FROM limits
CROSS JOIN usage;
```

`docker load` acontece antes de aplicar os limites ao container e pode consumir CPU e I/O enquanto importa a imagem. Faça o carregamento fora do pico, acompanhe a API antiga e os pings da frota, e interrompa antes de iniciar a candidata se houver degradação ou a reserva operacional não estiver comprovada. Se o host não tiver capacidade suficiente para os dois processos, não rode a candidata nele.

Com as reservas dimensionadas, execute primeiro o smoke passivo no host Linux e numa rede Docker privada. Não é necessário aplicar migrations nem configurar a origem antiga para esse smoke. Os gates de backup, migrations e preflight continuam obrigatórios antes de usar `--active-overlap`. O comando não contém segredos; o runtime é lido do arquivo privado:

```bash
bash ./start-private-backend-candidate.sh \
  --artifact-dir . \
  --release-package-dir '<diretório do pacote operacional do mesmo SHA>' \
  --expected-source-sha '<SHA Git completo>' \
  --docker-network '<rede privada existente>' \
  --runtime-env-file '<arquivo privado fora do repositório>' \
  --candidate-cpus '<CPUs máximos medidos para a candidata>' \
  --candidate-memory-mib '<memória máxima em MiB para a candidata>' \
  --candidate-db-max-pool-size '<conexões que cabem no orçamento do banco>' \
  --reserve-host-cpus '<CPUs que devem permanecer para os serviços atuais>' \
  --reserve-host-memory-mib '<memória em MiB a preservar para os serviços atuais>' \
  --reserve-docker-disk-mib '<espaço em MiB a preservar no armazenamento Docker>' \
  --host-port 18080
```

### Conferência depois do início passivo

Guarde o nome exato `container=` impresso pelo launcher e confira no mesmo host, sem configurar rota ou DNS. A porta publicada deve mostrar somente `127.0.0.1`; a política de restart deve ser `no`. Substitua o nome do container pelo valor impresso:

```bash
candidate='vend-machines-backend-candidate-<SHA completo em minúsculas>'
docker port "$candidate" 8080/tcp
docker inspect --format 'restart={{if .HostConfig.RestartPolicy.Name}}{{.HostConfig.RestartPolicy.Name}}{{else}}no{{end}}' "$candidate"
docker inspect --format 'cpu_nano={{.HostConfig.NanoCpus}} memory_bytes={{.HostConfig.Memory}} memory_swap_bytes={{.HostConfig.MemorySwap}}' "$candidate"
docker exec "$candidate" printenv Database__MaximumPoolSize
```

Confirme que os valores de CPU/memória correspondem ao orçamento declarado, que `memory_swap_bytes` é igual ao limite de memória e que `Database__MaximumPoolSize` corresponde ao orçamento aprovado; isso confirma o teto de recursos da candidata e que ela não tem swap adicional configurado.

Faça GETs locais e registre os códigos HTTP. O modo passivo exige `200` em `/health`, `503` em `/health/deploy-ready`, `/api/machines` e `/telemetria`, e `200` em `/health/ready` para aprovar a Etapa A completa. O readiness de banco só lê a conexão e o histórico de migrations. Se retornar `503` — inclusive por indisponibilidade do banco ou migrations pendentes — registre apenas o motivo sanitizado, marque a Etapa A como não aprovada e não aplique migrations neste passo. As três rotas bloqueadas devem continuar em `503`; se alguma responder com outro código, interrompa a Etapa A e pare somente a candidata pelo nome exato.

```bash
base='http://127.0.0.1:18080'
for path in /health /health/ready /health/deploy-ready /api/machines /telemetria; do
  code=$(curl --max-time 5 --silent --show-error --output /dev/null --write-out '%{http_code}' "$base$path") || code='falha-de-conexao'
  printf '%s %s\n' "$path" "$code"
done
```

Confirme também que a API antiga permaneceu saudável antes, durante e depois. Não publique corpos de resposta, tokens, arquivos de runtime, URLs privadas ou identificadores de máquinas no registro compartilhado. O launcher já confirma modo passivo, healthcheck do container e bloqueio das três rotas; esta conferência registra também o readiness do banco e a associação da porta publicada. O comando de início passivo não precisa da origem privada da API antiga.

## Efeitos da candidata no banco compartilhado

“Sem tráfego público” significa que a porta publicada fica limitada a loopback; **não significa que o processo seja somente leitura**. A API inicia workers de inbox de telemetria, webhooks, conciliação de pagamentos, deadlines/outbox, polling MDB e alertas sobre o banco configurado. O worker de telemetria também executa a limpeza de retenção de eventos e comandos antigos. Esses workers podem reivindicar e processar trabalho pendente enquanto a API anterior continua atendendo.

Os workers usam claims/transações no PostgreSQL para coordenar réplicas; os caminhos MDB e financeiros verificam o owner/lease e encaminham ou adiam operações de sockets legados. Os ensaios sintéticos cobrem esses fluxos e a convivência com o binário anterior, mas não provam o estado nem os efeitos na base comercial. Antes de iniciar, exija backup restaurável, ensaio da cadeia de migrations em cópia recente e o `deploy-preflight.sql` aprovado para o mesmo SHA. Acompanhe `/api/admin/worker-health`, logs redigidos, contagens de inbox/outbox e pings da API antiga durante a janela. Não considere a candidata isolada um probe inerte.

Se readiness ou preflight falhar, não encaminhe rota. Para parar apenas a candidata privada e manter a API antiga, use o nome exato `container=` impresso pelo launcher:

```bash
docker stop --time 45 '<nome exato do container candidato>'
```

Essa parada não reverte alterações de workers já confirmadas no banco; preserve os registros e investigue antes de qualquer recuperação. Não pare nem remova o container antigo e não restaure backup sobre o banco de produção como rollback.

## Operações que ficam indisponíveis para sockets legados

Enquanto uma máquina estiver conectada à API antiga, não rotacione nem revogue sua credencial pela candidata e não altere campos que exigem desconexão, como número de série ou ativação. A API antiga de `origin/develop` não oferece a operação de credencial e não conhece o protocolo de owner/dispatch da candidata. A candidata deve responder `503 machine_owner_unknown` antes de alterar a credencial; alterações de identidade/ativação online devem ser recusadas. Não contorne essas respostas editando diretamente o banco. Faça essas mudanças somente depois que o socket estiver offline ou pertencer a uma versão com rota de desconexão comprovada.

Repetir o cancelamento de uma sessão que já está em `ClosingWithoutSelection` ou fechada retorna `202 Accepted` pela candidata sem encaminhar outro pedido à API antiga. Para chamadas que passam pela candidata, o lock PostgreSQL da máquina também serializa concorrência entre réplicas; o ensaio local com duas candidatas confirmou um único `FECHAR_SESSAO` para quatro requisições simultâneas. Chamadas diretas à API antiga contornam esse lock. Uma falha antes da gravação ou um resultado incerto após timeout ainda exige consultar a sessão pelo ID antes de iniciar outra operação.

Se a API antiga responder `session_already_active`, a candidata retorna a sessão `panel` que continua aberta para o mesmo usuário, preservando o ID original. Para sessão aberta por outro usuário, mantém o conflito sem expor o registro. O ensaio local com duas candidatas, atraso sintético na gravação e quatro inícios simultâneos confirmou que todas receberam o ID da mesma sessão e que só houve um `ABRIR_SESSAO`. Isso permite retomar uma sessão que ainda está ativa após perder a resposta inicial; não é um ledger durável para retries depois do fechamento. Chamadas diretas à API antiga e múltiplas réplicas antigas não são coordenadas pela candidata.

Para cancelamentos de sockets legados, todas as chamadas da aplicação devem passar pela candidata durante a sobreposição; mantenha o endereço da API anterior acessível somente pela rota interna usada no relay. A candidata adquire o lock PostgreSQL da máquina e reavalia a sessão antes de encaminhar, mantendo o lock até o resultado do relay (timeout HTTP de 20 segundos). Réplicas candidatas compartilham esse lock; chamadas diretas ao processo antigo contornariam a serialização e não estão cobertas por este controle.

## Critério para considerar esta etapa validada

Guarde, sem token nem valores de configuração secreta, a data, SHA candidata, saída do preflight, quantidade de conexões antigas observadas e confirmação operacional de que os pings continuaram chegando à API anterior durante a leitura. Compare a contagem à fonte de operação da frota; não aceite simplesmente qualquer valor maior que zero se houver mais máquinas conectadas.

Use a cópia de [registro de validação privada da candidata](partial-production-evidence-template.md) para preservar evidências agregadas da execução. Não registre tokens, URLs privadas, IDs de máquinas, payload MDB ou dados de clientes. O registro organiza resultados; ele não autoriza sozinho a troca de rota.

Este resultado comprova apenas prontidão, alcance de rede e presença observada no momento da consulta. Não prova o comportamento de firmware/MDB físico, entrega de pagamentos, nem tolerância a uma falha abrupta real. Não encerre nem reinicie a API anterior enquanto houver conexões, sessões, comandos, mensagens ou operações financeiras atribuídas a ela. Se a API candidata ainda não tiver passado pela validação física com os firmwares suportados, mantenha o tráfego MDB no caminho atual.
