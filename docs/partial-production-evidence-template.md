# Registro de validação privada da candidata

> Preencha uma cópia por execução. Guarde-a em repositório operacional privado, não no Git do produto. As etapas abaixo têm aprovações independentes: concluir o smoke passivo não autoriza modo ativo nem encaminhamento de tráfego.

## Identificação da release

- SHA completo da candidata:
- Workflow aprovado e URL:
- ID do artefato da imagem:
- SHA-256 externo do ZIP da imagem:
- ID do pacote operacional pareado:
- SHA-256 externo do ZIP do pacote:
- Validade dos artefatos:
- Conferência de pareamento e checksums: `APROVADA` / `REPROVADA`

## Capacidade do host antes do smoke

Registre referências das medições, sem incluir dados privados. Meça a API atual e os demais serviços durante o pico normal e escolha reservas que os preservem. O launcher recusa iniciar se o orçamento declarado não couber na capacidade observada.

- Data/hora UTC e janela da medição de carga:
- CPUs disponíveis no host:
- Teto de CPU da candidata / reserva para os serviços atuais:
- Memória disponível antes da carga / teto da candidata / reserva dos serviços atuais (MiB):
- Espaço livre no Docker / tamanho descompactado estimado da imagem / reserva de disco (MiB):
- Limite simultâneo do PostgreSQL/provedor / pico de conexões da API atual / conexões reservadas a outros serviços e margem:
- Teto `--candidate-db-max-pool-size` aprovado para o pool compartilhado da candidata:
- Referência segura de `docker stats`, `MemAvailable` e espaço no Docker:
- Limites de CPU, memória e conexões PostgreSQL conferidos no container candidato: `SIM` / `NÃO`

## Etapa A — smoke privado passivo

Esta etapa mantém a API anterior como única API ativa. A candidata deve ser iniciada pelo launcher sem `--active-overlap` e sem alterar rota pública. O modo passivo não executa `DbInitializer`, não registra workers e bloqueia as rotas comerciais e WebSockets. `/health/ready` somente lê o histórico de migrations; uma falha ali não autoriza aplicar migrations neste passo.

- Início e fim em UTC:
- Nome exato do container candidato:
- Launcher confirmou `Modo passivo`: `SIM` / `NÃO`
- Porta publicada somente em loopback (`127.0.0.1`): `SIM` / `NÃO`
- Rota pública ou DNS alterado: `NÃO` / `SIM`
- API antiga saudável antes, durante e depois: `SIM` / `NÃO`
- Candidata sem conexões de máquinas: `SIM` / `NÃO`
- Workers da candidata não registrados/inativos: `SIM` / `NÃO`
- Migrations ou escrita de negócio executadas pela candidata: `NÃO` / `SIM`
- `GET /health`: status e horário UTC:
- `GET /health/ready`: status, motivo sanitizado e horário UTC:
- `GET /health/deploy-ready`: bloqueado em modo passivo: `SIM` / `NÃO`
- Uma rota comercial e `/telemetria` retornaram `503`: `SIM` / `NÃO`
- Falhas ou efeitos inesperados observados:
- Resultado da Etapa A: `APROVADA` / `REPROVADA`

**Pare a candidata pelo nome exato se ela iniciar workers, aceitar WebSockets/rotas comerciais, exigir mudança de rota ou afetar a API antiga.** Uma Etapa A aprovada comprova apenas o smoke privado; não aprova convivência funcional, acesso de usuários ou tráfego de máquinas.

## Gates antes da Etapa B — convivência ativa privada

Preencha antes de iniciar a candidata com `--active-overlap`. Pare aqui se algum item obrigatório estiver `NÃO` ou sem evidência. Não aplique migrations no banco comercial sem backup restaurável e aprovação operacional registrada.

- Backup recente identificado por referência interna (sem anexar dump):
- Data/hora UTC do backup:
- Restauração concluída em cópia isolada: `SIM` / `NÃO`
- Referência e resultado do ensaio de migrations no mesmo SHA:
- Preflight SQL revisado e aprovado: `SIM` / `NÃO`
- Migrations aprovadas aplicadas e conferidas em `__EFMigrationsHistory`: `SIM` / `NÃO` / `NÃO APLICÁVEL`
- Desvio do relógio local em relação à fonte NTP (segundos):
- Desvio dentro do limite de 30 s: `SIM` / `NÃO`
- API antiga saudável antes do ensaio: `SIM` / `NÃO`
- Mínimo de máquinas conectadas confirmado pela operação:
- Rota privada direta ao owner antigo confirmada sem registrar a URL: `SIM` / `NÃO`
- Janela e responsável operacional definidos:
- Plano para parar somente a candidata revisado:

## Etapa B — convivência ativa, ainda sem tráfego público

Esta etapa ativa workers e rotas comerciais na candidata. Embora o container fique privado, workers podem processar inbox, webhooks, outbox, pagamentos, polling MDB, alertas e retenção no banco compartilhado. Monitore os efeitos; a parada do container não desfaz gravações já confirmadas.

- Início e fim em UTC:
- Nome exato do container candidato:
- SHA informado pelo `/health/ready` e `/health/deploy-ready`:
- Porta publicada somente em loopback e sem rota pública: `SIM` / `NÃO`
- Configuração de relay legado confirmada sem registrar a URL privada: `SIM` / `NÃO`
- Candidata sem conexões diretas de máquinas: `SIM` / `NÃO`
- Saúde dos workers antes/depois (somente nome, estado e contagem):
- Contagens de inbox, webhooks e outbox no início/fim; anote apenas diferenças relevantes e referências de consulta:
- Falhas ou efeitos inesperados em workers:
- API antiga saudável durante todo o ensaio: `SIM` / `NÃO`

### Continuidade da frota por 120 segundos

Faça amostras a cada 15 segundos. Não registre IDs, números de série ou nomes de clientes; use somente contagem e impressão do conjunto produzidas pelo preflight.

| Tempo relativo | UTC | Conexões na API antiga | Impressão SHA-256 do conjunto | Pings contínuos na API antiga |
| --- | --- | ---: | --- | --- |
| 0 s | | | | `SIM` / `NÃO` |
| 15 s | | | | `SIM` / `NÃO` |
| 30 s | | | | `SIM` / `NÃO` |
| 45 s | | | | `SIM` / `NÃO` |
| 60 s | | | | `SIM` / `NÃO` |
| 75 s | | | | `SIM` / `NÃO` |
| 90 s | | | | `SIM` / `NÃO` |
| 105 s | | | | `SIM` / `NÃO` |
| 120 s | | | | `SIM` / `NÃO` |

- Mínimo confirmado mantido em todas as amostras: `SIM` / `NÃO`
- Impressão idêntica em todas as amostras: `SIM` / `NÃO`
- Resultado e saída redigida do preflight:
- Resultado da Etapa B: `APROVADA` / `REPROVADA`

**Interrompa a validação, mantenha a API antiga atendendo e não encaminhe tráfego** se uma contagem ficar abaixo do mínimo, a impressão mudar, os pings pararem, a readiness falhar ou algum efeito de worker não puder ser explicado. Pare somente a candidata pelo nome exato; isso não reverte alterações já confirmadas no banco.

## Etapa C — decisão de encaminhamento de tráfego

- Resultado: `NO-GO` / `SOMENTE CANDIDATA PRIVADA APROVADA` / `PILOTO AUTORIZADO`
- Escopo exato do piloto e referência da aprovação operacional:
- Tráfego MDB continua na API antiga: `SIM` / `NÃO`
- Tráfego financeiro continua na API antiga: `SIM` / `NÃO`
- Validação física de MDB concluída para esta versão: `SIM` / `NÃO`
- Pagamento de teste concluído para esta versão: `SIM` / `NÃO`
- Plano de reversão do binário revisado; banco preservado:
- Pendências e responsável:

Sem testes físicos de firmware e de pagamento, selecione `NO-GO` para os caminhos MDB e financeiros. Não encerre a API antiga enquanto houver sockets, sessões, comandos, mensagens ou operações financeiras atribuídas a ela.

## Higiene do registro

Não inclua tokens, senhas, connection strings, chaves, conteúdo de webhook, payload MDB, IDs de máquina, dados pessoais ou dados comerciais. Registre somente referências internas seguras e valores agregados necessários para comprovar cada gate.
