# Preflight de convivência da API

Este preflight é somente leitura. Ele não envia comandos MDB, não cria sessões, não altera configuração, não aplica migrations e não inicia publicação. Seu objetivo é confirmar que a candidata consegue atender enquanto a API anterior continua dona de conexões existentes.

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

Com `--observation-seconds` maior que zero, o script repete todas as consultas de readiness, relay e conexões até o fim da janela e falha na primeira amostra abaixo do mínimo informado. Zero mantém uma única amostra. A janela indicada de 120 segundos cobre a expiração de presença da API (90 segundos) e a persistência do `LastSeenAt` (até 60 segundos); aumente-a caso o intervalo normal de ping do firmware seja maior. O script busca todas as páginas administrativas, exige IDs de máquina únicos e confirma que o conjunto de máquinas conectadas permaneceu idêntico entre as amostras, mesmo quando a contagem total não muda. Os IDs não são impressos; a saída registra a impressão SHA-256 do conjunto ao concluir. Cada amostra também registra SHA candidato, horário UTC e quantidade observada, sem emitir comandos MDB ou criar sessões.

O verificador consulta `/health`, `/health/ready`, `/health/deploy-ready`, `/api/admin/legacy-machine-connections` e `/api/admin/worker-health`. Não segue redirects. Só retorna sucesso quando:

- os endpoints de saúde estão prontos e informam o SHA esperado;
- a lista administrativa observa pelo menos o mínimo informado de conexões legadas;
- os mesmos IDs de máquina permanecem conectados durante toda a janela, sem substituição mascarada por uma contagem igual;
- o relay está configurado e saudável;
- a contagem do relay coincide nos dois diagnósticos e a candidata reporta zero conexões online sem rota.

Qualquer divergência, `401/403`, readiness bloqueado, rota inacessível, redirect ou contagem abaixo do mínimo encerra o preflight com código diferente de zero. Corrija a causa e repita a leitura; não desative o gate para prosseguir. O token não é impresso pelo script.

## Imagem imutável da candidata

O workflow de verificação constrói e testa a imagem backend sem publicá-la em um registry nem iniciar um serviço. Somente depois dos gates de sobreposição e release, ele anexa `candidate-backend-image-<SHA>` à execução aprovada. O pacote contém `backend-image.tar.gz`, `image-ref.txt`, `image-id.txt`, `platform.txt`, `source-sha.txt`, `README.txt`, `Verify-BackendImageArtifact.ps1`, `start-private-backend-candidate.sh`, `verify-candidate-artifact-pair.py` e `SHA256SUMS`.

Use os dois artefatos da mesma execução concluída com sucesso. Antes de revisar ou aplicar qualquer SQL, rode `python3 ./verify-candidate-artifact-pair.py --image-artifact-dir '<diretório da imagem>' --release-package-dir '<diretório do pacote operacional>' --expected-source-sha '<SHA Git completo>'`. O verificador valida os manifests/checksums dos dois pacotes, confirma a igualdade do SHA completo e rejeita arquivos extras na imagem, caminhos inesperados, checksums inválidos e divergência entre os pacotes. Em Windows com Docker, `Verify-BackendImageArtifact.ps1` também confere a plataforma antes de carregar a imagem. No host Linux Docker, o launcher repete a comparação dos artefatos antes de `docker load`, confere o ID da imagem carregada e exige rede/runtime privados. O arquivo de runtime precisa ficar fora do repositório, dos diretórios dos artefatos e do pacote operacional, legível somente pelo operador (`chmod 600`). O launcher inicia um container com nome exclusivo, sem restart automático, publica apenas em `127.0.0.1:<porta>:8080` e espera até três minutos pelo health check. Não altera rota pública, não remove containers existentes e não aplica migrations. Antes de usá-lo, conclua o ensaio SQL isolado, obtenha backup restaurável, rode o preflight SQL e aplique apenas as migrations aprovadas do mesmo SHA; a API recusa iniciar com migrations pendentes. A origem legada precisa apontar diretamente ao processo antigo pela rede privada. Após o health check, execute o preflight administrativo somente leitura contra o SHA completo antes de encaminhar tráfego.

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

Com backup, migrations e preflight SQL aprovados, execute no host Linux e numa rede Docker que alcance a API antiga. O comando não contém segredos; o runtime é lido do arquivo privado:

```bash
bash ./start-private-backend-candidate.sh \
  --artifact-dir . \
  --release-package-dir '<diretório do pacote operacional do mesmo SHA>' \
  --expected-source-sha '<SHA Git completo>' \
  --docker-network '<rede privada existente>' \
  --runtime-env-file '<arquivo privado fora do repositório>' \
  --legacy-api-origin 'http://<API antiga privada>:8080' \
  --host-port 18080
```

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

Este resultado comprova apenas prontidão, alcance de rede e presença observada no momento da consulta. Não prova o comportamento de firmware/MDB físico, entrega de pagamentos, nem tolerância a uma falha abrupta real. Não encerre nem reinicie a API anterior enquanto houver conexões, sessões, comandos, mensagens ou operações financeiras atribuídas a ela. Se a API candidata ainda não tiver passado pela validação física com os firmwares suportados, mantenha o tráfego MDB no caminho atual.
