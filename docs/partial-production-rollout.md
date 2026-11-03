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
python tools/Deployment/Validate-TelemetryOverlapReadiness.py `
  --api-base-url 'http://candidate-api:8080' `
  --expected-commit '<sha da candidata>' `
  --minimum-legacy-connections '<quantidade esperada de sockets legados>'
```

O verificador consulta `/health`, `/health/ready`, `/health/deploy-ready`, `/api/admin/legacy-machine-connections` e `/api/admin/worker-health`. Não segue redirects. Só retorna sucesso quando:

- os endpoints de saúde estão prontos e informam o SHA esperado;
- a lista administrativa observa pelo menos o mínimo informado de conexões legadas;
- o relay está configurado e saudável;
- a contagem do relay coincide nos dois diagnósticos e a candidata reporta zero conexões online sem rota.

Qualquer divergência, `401/403`, readiness bloqueado, rota inacessível, redirect ou contagem abaixo do mínimo encerra o preflight com código diferente de zero. Corrija a causa e repita a leitura; não desative o gate para prosseguir. O token não é impresso pelo script.

## Imagem imutável da candidata

O workflow de verificação constrói e testa a imagem backend sem publicá-la em um registry nem iniciar um serviço. Somente depois dos gates de sobreposição e release, ele anexa `candidate-backend-image-<SHA>` à execução aprovada. O pacote contém `backend-image.tar.gz`, `image-ref.txt`, `image-id.txt`, `platform.txt`, `source-sha.txt`, `README.txt`, o script `Verify-BackendImageArtifact.ps1` e `SHA256SUMS`.

Use o artefato apenas de uma execução concluída com sucesso. Antes de `docker load`, extraia o pacote e execute `./Verify-BackendImageArtifact.ps1 -ArtifactPath . -ExpectedSourceSha '<SHA Git completo esperado>'` em um host com Docker disponível. O script confere o SHA de origem, a referência e o ID da imagem, a integridade e cobertura exata dos checksums, a ausência de arquivos extras e a compatibilidade da plataforma com o daemon local. Ele somente valida os metadados e não carrega nem inicia a imagem. Se aprovado, carregar o arquivo somente importa a imagem ao daemon local; não inicia container nem altera o roteamento. Mantenha credenciais e demais configurações exclusivamente no runtime isolado da candidata. O artefato expira conforme a retenção configurada no workflow; gere uma nova execução aprovada se o SHA ou o checksum não corresponder.

## Operações que ficam indisponíveis para sockets legados

Enquanto uma máquina estiver conectada à API antiga, não rotacione nem revogue sua credencial pela candidata e não altere campos que exigem desconexão, como número de série ou ativação. A API antiga de `origin/develop` não oferece a operação de credencial e não conhece o protocolo de owner/dispatch da candidata. A candidata deve responder `503 machine_owner_unknown` antes de alterar a credencial; alterações de identidade/ativação online devem ser recusadas. Não contorne essas respostas editando diretamente o banco. Faça essas mudanças somente depois que o socket estiver offline ou pertencer a uma versão com rota de desconexão comprovada.

Repetir o cancelamento de uma sessão que já está em `ClosingWithoutSelection` ou fechada retorna `202 Accepted` pela candidata sem encaminhar outro pedido à API antiga. Para chamadas que passam pela candidata, o lock PostgreSQL da máquina também serializa concorrência entre réplicas; o ensaio local com duas candidatas confirmou um único `FECHAR_SESSAO` para quatro requisições simultâneas. Chamadas diretas à API antiga contornam esse lock. Uma falha antes da gravação ou um resultado incerto após timeout ainda exige consultar a sessão pelo ID antes de iniciar outra operação.

Se a API antiga responder `session_already_active`, a candidata retorna a sessão `panel` que continua aberta para o mesmo usuário, preservando o ID original. Para sessão aberta por outro usuário, mantém o conflito sem expor o registro. O ensaio local com duas candidatas, atraso sintético na gravação e quatro inícios simultâneos confirmou que todas receberam o ID da mesma sessão e que só houve um `ABRIR_SESSAO`. Isso permite retomar uma sessão que ainda está ativa após perder a resposta inicial; não é um ledger durável para retries depois do fechamento. Chamadas diretas à API antiga e múltiplas réplicas antigas não são coordenadas pela candidata.

Para cancelamentos de sockets legados, todas as chamadas da aplicação devem passar pela candidata durante a sobreposição; mantenha o endereço da API anterior acessível somente pela rota interna usada no relay. A candidata adquire o lock PostgreSQL da máquina e reavalia a sessão antes de encaminhar, mantendo o lock até o resultado do relay (timeout HTTP de 20 segundos). Réplicas candidatas compartilham esse lock; chamadas diretas ao processo antigo contornariam a serialização e não estão cobertas por este controle.

## Critério para considerar esta etapa validada

Guarde, sem token nem valores de configuração secreta, a data, SHA candidata, saída do preflight, quantidade de conexões antigas observadas e confirmação operacional de que os pings continuaram chegando à API anterior durante a leitura. Compare a contagem à fonte de operação da frota; não aceite simplesmente qualquer valor maior que zero se houver mais máquinas conectadas.

Este resultado comprova apenas prontidão, alcance de rede e presença observada no momento da consulta. Não prova o comportamento de firmware/MDB físico, entrega de pagamentos, nem tolerância a uma falha abrupta real. Não encerre nem reinicie a API anterior enquanto houver conexões, sessões, comandos, mensagens ou operações financeiras atribuídas a ela. Se a API candidata ainda não tiver passado pela validação física com os firmwares suportados, mantenha o tráfego MDB no caminho atual.
