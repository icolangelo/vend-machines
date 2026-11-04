# Registro de validação privada da candidata

> Preencha uma cópia por execução. Guarde-a em repositório operacional privado, não no Git do produto. Este registro organiza evidências; sozinho, não autoriza encaminhar tráfego.

## Identificação da release

- SHA completo da candidata:
- Workflow aprovado e URL:
- ID do artefato da imagem:
- SHA-256 externo do ZIP da imagem:
- ID do pacote operacional pareado:
- SHA-256 externo do ZIP do pacote:
- Validade dos artefatos:
- Conferência de pareamento e checksums: `APROVADA` / `REPROVADA`

## Gates antes de iniciar

- Backup recente identificado por referência interna (sem anexar dump):
- Data/hora UTC do backup:
- Restauração concluída em cópia isolada: `SIM` / `NÃO`
- Referência e resultado do ensaio de migrations no mesmo SHA:
- Preflight SQL revisado e aprovado: `SIM` / `NÃO`
- Migrations aplicadas antes da candidata e conferidas em `__EFMigrationsHistory`: `SIM` / `NÃO` / `NÃO APLICÁVEL`
- Desvio do relógio local em relação à fonte NTP (segundos):
- Desvio dentro do limite de 30 s: `SIM` / `NÃO`
- API antiga saudável antes do ensaio: `SIM` / `NÃO`
- Mínimo de máquinas conectadas confirmado pela operação:

**Pare aqui se algum gate obrigatório estiver `NÃO` ou sem evidência.** Não preencha valores secretos ou conteúdo de clientes.

## Execução privada

- Início e fim em UTC:
- Nome exato do container candidato:
- Porta publicada somente em loopback:
- Health `/health`: status e horário UTC:
- Readiness `/health/ready`: status, campo `sourceCommit` e horário UTC:
- Configuração de relay legado confirmada sem registrar a URL privada: `SIM` / `NÃO`
- Candidata sem conexões de máquinas: `SIM` / `NÃO`
- Saúde dos workers antes/depois (somente nome, estado e contagem):
- Contagens de inbox, webhooks e outbox no início/fim; anote apenas diferenças relevantes e referências de consulta:
- Falhas ou efeitos inesperados em workers:
- API antiga saudável durante todo o ensaio: `SIM` / `NÃO`

## Continuidade da frota por 120 segundos

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

**Interrompa o ensaio e mantenha a API antiga atendendo se uma contagem ficar abaixo do mínimo, a impressão mudar, os pings pararem ou qualquer efeito de worker não puder ser explicado.** Se a readiness/preflight falhar, pare somente o container candidato pelo nome exato, conforme o runbook; a parada não desfaz gravações já confirmadas no banco.

## Decisão e limites

- Resultado: `NO-GO` / `SOMENTE CANDIDATA PRIVADA APROVADA` / `PILOTO AUTORIZADO`
- Tráfego MDB continua na API antiga: `SIM` / `NÃO`
- Tráfego financeiro continua na API antiga: `SIM` / `NÃO`
- Validação física de MDB e pagamento de teste concluída para esta versão: `SIM` / `NÃO`
- Referência da aprovação operacional e do escopo autorizado:
- Plano de reversão do binário revisado; banco preservado:
- Pendências e responsável:

Sem testes físicos de firmware e de pagamento, selecione `NO-GO` para os caminhos MDB e financeiros. Não encerre a API antiga enquanto houver sockets, sessões, comandos, mensagens ou operações financeiras atribuídas a ela.

## Higiene do registro

Não inclua tokens, senhas, connection strings, chaves, conteúdo de webhook, payload MDB, IDs de máquina, dados pessoais ou dados comerciais. Registre somente referências internas seguras e valores agregados necessários para comprovar o gate.
