# Remediação coordenada de segredos no histórico Git

Este procedimento prepara a remoção de credenciais que apareceram em commits do repositório público. A execução exige o titular das credenciais, acesso administrativo ao GitHub e coordenação com quem mantém clones, forks ou automações. Não inclua valores de segredo em tickets, comandos salvos, evidências ou este repositório.

## Situação conhecida

A varredura local de 28/09/2026 encontrou seis ocorrências no histórico de 60 commits. Os valores não foram registrados. As credenciais envolvidas devem ser tratadas como comprometidas até que o titular confirme revogação/rotação. Uma varredura de conteúdo atual, em modo somente arquivos e com redação, não encontrou ocorrências em `appsettings.json`, `appsettings.Development.json` nem `docs/`; essa verificação não substitui a varredura de todas as referências públicas, artefatos ou forks. A branch `codex/credential-exposure-cleanup` contém arquivos atuais redigidos, mas isso não remove versões antigas dos commits.

Em 25/10/2026, executei novamente o Gitleaks fixado no workflow contra o histórico alcançável pelo checkout atual, com redação completa e sem exibir os achados. A contagem continua em seis ocorrências. Isso confirma que a exposição histórica não foi corrigida no material local; não comprova o estado de todas as refs remotas, PRs, forks, caches ou clones.

Em 26/10/2026, repeti a varredura com a mesma imagem fixada; o checkout alcançou 93 commits e manteve seis ocorrências. Para permitir a triagem pelo titular sem registrar valores: `generic-api-key` em `apps/backend/VendingMachines.Api/appsettings.json` nos commits `12686d61a138b8b9aba0c630ddc589242a803501`, `035855e651589fddd04c95933070dcb7bf659321` e `444a52969dca1c6de0383056d520c1ed7aeb9aec`; `curl-auth-header` em `docs/documentacao_checkout_transparente_split_mercado_pago.md` três vezes no commit `ab8412e20eece278934156df0801149d206b40d8`. São ocorrências do scanner, não uma contagem de credenciais únicas; trate os dados como comprometidos até o titular identificar e revogar/renovar os valores nos provedores. A verificação cobre apenas o histórico alcançável deste checkout.

Na mesma data, uma verificação redigida e restrita aos arquivos atuais de runtime (`appsettings.json`, `appsettings.Development.json`, `appsettings.Production.json`, `.env`, `.env.production.example` e `docs/backend-production-environment.example`) não encontrou achados. Os arquivos-fonte de controllers, dados, middlewares, migrations, modelos e serviços do backend, `apps/frontend/src`, E2E, `docs`, `tools/Deployment` e workflows também foram verificados por diretório e tiveram zero achados. Uma varredura indiscriminada do workspace completo, sem filtrar dependências e artefatos, retornou 1.551 correspondências sem classificação; esse total não representa credenciais confirmadas e não substitui os scans por diretório. Os resultados selecionados não incluem todo `bin/obj`, dependências e artefatos, então não permitem concluir sobre cada arquivo do workspace.

## Preparação e responsáveis

- [ ] Nomear o titular que pode renovar cada credencial e o responsável pela reescrita GitHub.
- [ ] Agendar uma janela e avisar colaboradores, automações e responsáveis por forks; solicitar que não façam push durante a operação.
- [ ] Inventariar os tipos e integrações afetados em registro restrito, sem copiar os valores. Incluir credenciais de pagamento, webhook, banco, JWT/cifra, SMTP e tokens de deploy quando aplicável.
- [ ] Identificar todas as branches, tags, PRs, forks, clones, caches de CI, artefatos e cópias locais. Tratar cópias `.tmp` e backups que contenham configuração antiga como material sensível.
- [ ] Confirmar que os novos valores podem ser provisionados no secret manager e no runtime sem imprimir ou salvar valores no repositório.
- [ ] Fazer um espelho de recuperação com acesso restrito. Ele ainda conterá os valores expostos e não pode ser compartilhado nem restaurado para desenvolvimento.
- [ ] Confirmar uma via de reversão operacional para pagamentos/autenticação e alinhá-la à janela aprovada. A rotação pode afetar integrações em execução.

## Ordem obrigatória

1. O titular revoga ou renova as credenciais expostas nos respectivos provedores. Para credenciais de produção do Mercado Pago, a renovação de um par substitui ambos os itens daquele par e pode afetar a integração; provisionar os novos valores no runtime e conferir pagamentos/webhooks em homologação conforme a janela e os recursos de sobreposição oferecidos pelo provedor. Renovar também, separadamente, segredos de webhook e outras credenciais expostas.
2. Confirmar com evidência redigida que os valores antigos foram revogados/renovados e que os novos segredos estão disponíveis no secret manager. Não registrar os valores nem capturas de tela com segredos.
3. Pausar integrações de escrita no repositório durante a reescrita. Usar um clone novo e completo; aplicar `git-filter-repo` às referências acordadas para remover/substituir os arquivos ou valores expostos. Revisar o mapeamento antes de publicar. A reescrita altera SHAs e invalida assinaturas de commits/tags afetados.
4. Publicar as referências reescritas de forma coordenada. A proteção contra force push pode precisar de ajuste temporário por um administrador; restaurá-la imediatamente após a publicação e verificar que as regras e revisores continuam ativos.
5. Solicitar ao GitHub a remoção de dados sensíveis de referências/caches de PR quando necessário. Reescrever a branch não apaga automaticamente clones, forks, caches ou cópias de terceiros.
6. Pedir que colaboradores descartem clones antigos e façam clone novo. Não aceitar `git pull` seguido de `git push` a partir de um clone anterior, pois isso pode reintroduzir os commits expostos. Recriar branches locais a partir das referências limpas.
7. Executar Gitleaks sobre todas as referências publicadas e confirmar que não há achados para as credenciais expostas. Fazer também uma verificação redigida nos arquivos atuais, artefatos e refs de PR acessíveis. Nunca publicar saída bruta do scanner se ela contiver contexto sensível.
8. Reativar automações e confirmar CI remoto verde, regras de branch/ambiente restauradas e integração operacional testada pelo titular. Registrar horário, responsável, refs/SHAs antigos e novos e resultados sem incluir segredos.

## Critérios de aceite

- [ ] Todas as credenciais expostas foram revogadas/renovadas pelo titular e as novas estão no runtime autorizado.
- [ ] Todas as refs públicas acordadas foram reescritas; refs de PR/cache que ainda exponham dados foram encaminhadas ao GitHub.
- [ ] Gitleaks em histórico e arquivos atuais não encontra as credenciais expostas.
- [ ] Colaboradores e automações usam clones/refs limpos; proteções e checks obrigatórios foram restaurados.
- [ ] Pagamentos, webhook e autenticação foram verificados na integração adequada sem interromper as conexões de telemetria em produção.
- [ ] Evidências redigidas foram anexadas ao registro operacional da Fase 0.

Se não for possível cumprir a janela sem impacto operacional, interrompa antes de renovar ou publicar alterações e reagende com o titular e a operação. A remoção do histórico reduz exposição futura, mas não substitui a rotação.

## Referências oficiais

- [GitHub Docs: remover dados sensíveis de um repositório](https://docs.github.com/en/authentication/keeping-your-account-and-data-secure/removing-sensitive-data-from-a-repository)
- [Mercado Pago Developers: credenciais e renovação](https://www.mercadopago.com.br/developers/pt/docs/your-integrations/credentials)

Em 28/10/2026, executei o scanner fixado no workflow com `--log-opts=--all` sobre todas as refs locais do clone completo (93 commits), `--redact` e container sem rede: continua com seis ocorrências. A leitura incluiu branches locais e refs `origin/*` disponíveis neste checkout; não cobre PR refs, forks, caches nem refs que não foram buscadas. Também escaneei somente `appsettings.Production.json` ignorado pelo Git com `--no-git` e redação: 407 bytes verificados, nenhum achado. Isso descreve apenas o arquivo local, não os segredos ativos no servidor. Não extraí valores, não revoguei credenciais e não reescrevi/publicei histórico.

Em 03/11/2026, repeti a varredura de histórico no clone/worktree atual com a mesma imagem fixada, `--log-opts=--all`, `--redact`, volume somente leitura e container sem rede. Foram examinados 93 commits e aproximadamente 17,82 MB; o scanner voltou a reportar seis ocorrências. Os valores não foram exibidos nem copiados. A árvore-fonte atual do snapshot isolado foi examinada separadamente em modo `--no-git` e não teve achados. Assim, a correção do conteúdo atual está evidenciada localmente, mas as credenciais históricas continuam sendo tratadas como comprometidas até a rotação pelo titular e a reescrita coordenada de todas as referências acordadas.


Em 03/11/2026, a reauditoria do snapshot `codex/production-readiness-snapshot` com a mesma imagem fixada e `--log-opts=--all` examinou 120 commits / 21,92 MB e manteve seis achados históricos, com redação habilitada e sem rede. A verificação `--no-git` da árvore atual examinou 12,18 MB e não encontrou achados. Isso atualiza o alcance local após os commits recentes, mas não inclui PR refs, forks, caches ou clones externos não buscados; não houve consulta a secret manager, rotação nem reescrita.


Em 04/11/2026, uma nova varredura redigida com a imagem Gitleaks fixada no workflow e `--log-opts=--all` examinou 204 commits / aproximadamente 25,36 MB das referências locais disponíveis e encontrou novamente seis ocorrências. O scanner não exibiu os valores; não revoguei credenciais, não consultei o secret manager e não reescrevi referências. A contagem é de ocorrências, não de credenciais únicas, e o resultado não cobre PR refs, forks, caches ou clones externos não buscados. A rotação coordenada pelo titular e a limpeza das referências acordadas continuam obrigatórias.
