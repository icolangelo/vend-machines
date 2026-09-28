# Proteção do deploy em produção

## Escopo deste patch

O workflow de backend executa os testes, constrói a imagem e publica uma tag imutável derivada do SHA completo do commit. Ele não promove a tag `latest`, não chama Coolify e não aplica migrations automaticamente.

Esse patch é uma proteção intermediária. Ele não habilita deploy de produção. A publicação da imagem no GHCR, por si só, não altera os containers que já estão rodando.

## Critérios antes de habilitar deploy

- Validar o procedimento de atualização e rollback em homologação.
- Verificar que firmware e máquinas reconectam sem perda de telemetria em uma atualização real controlada.
- Definir execução e verificação de backup antes de qualquer migration.
- Confirmar os secrets e as proteções de ambiente no GitHub e no Coolify.
- Confirmar readiness da API e monitoramento pós-deploy.
- Revisar o workflow remoto e aprovar explicitamente a publicação da alteração.

Enquanto esses critérios não forem atendidos, o pipeline apenas publica uma imagem identificada pelo commit. Não há etapa de deploy neste workflow.
