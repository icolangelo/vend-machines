# Remediação coordenada de segredos no histórico Git

Este procedimento prepara a remoção de credenciais que apareceram em commits do repositório público. A execução exige o titular das credenciais, acesso administrativo ao GitHub e coordenação com quem mantém clones, forks ou automações. Não inclua valores de segredo em tickets, comandos salvos, evidências ou este repositório.

## Situação conhecida

A varredura local de 28/09/2026 encontrou seis ocorrências no histórico de 60 commits. Os valores não foram registrados. As credenciais envolvidas devem ser tratadas como comprometidas até que o titular confirme revogação/rotação. Uma varredura de conteúdo atual, em modo somente arquivos e com redação, não encontrou ocorrências em `appsettings.json`, `appsettings.Development.json` nem `docs/`; essa verificação não substitui a varredura de todas as referências públicas, artefatos ou forks. A branch `codex/credential-exposure-cleanup` contém arquivos atuais redigidos, mas isso não remove versões antigas dos commits.

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
