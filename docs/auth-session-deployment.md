# Configuração segura do backend de produção

O workflow atual publica somente uma imagem imutável por SHA em pushes. O deploy via Coolify é manual, controlado pelo ambiente `production` do GitHub e não aplica migrations durante o startup da API.

## Segredos obrigatórios no ambiente

Configure os valores diretamente no provedor de segredos/ambiente do serviço de produção; não os grave em `appsettings.json`, `.env` versionado, workflow ou imagem:

- `Jwt__Key`: chave aleatória com pelo menos 32 bytes. A rotação invalida os tokens de acesso existentes.
- `Encryption__Key`: chave de pelo menos 32 bytes. Na primeira release de compatibilidade, use a chave legada atual; só troque para uma nova chave na etapa 2 abaixo.
- `Encryption__LegacyKey`: chave que cifra atualmente os tokens Mercado Pago persistidos; necessária enquanto existirem valores sem prefixo `enc:v1:`.
- `Encryption__WriteLegacyFormat`: `true` na release de compatibilidade; `false` ao começar a usar a nova chave e gravar AES-GCM.
- `Encryption__RequireLegacyData`: mantenha `true` até concluir e conferir o processo em lotes; depois defina `false` para remover a chave antiga do runtime.
- `MercadoPago__ClientSecret`: segredo OAuth rotacionado no Mercado Pago.
- `MercadoPago__WebhookSecret`: segredo de assinatura rotacionado/configurado no Mercado Pago.
- `ConnectionStrings__DefaultConnection`: conexão PostgreSQL de produção.

A aplicação rejeita valores ausentes, curtos ou com marcadores comuns de placeholder. A configuração `Telemetry__RequireDeviceCredentials` deve continuar no estado operacional atual até que cada máquina seja provisionada e homologada; não a habilite como parte desta mudança.

## Compatibilidade e rotação da cifra

O formato legado era AES-CBC sem marcador de versão. Novos valores podem usar AES-GCM com prefixo `enc:v1:`; ciphertext versionado com chave inválida ou adulterado falha fechado. A aplicação aceita configuração de compatibilidade para preservar o formato antigo no primeiro deploy e lê ambos os formatos durante a migração.

Os valores antigos estiveram no histórico do Git e devem ser tratados como comprometidos. Rotacione os segredos do Mercado Pago e mantenha a chave AES legada somente no cofre de segredos enquanto durar a migração. Não copie os valores antigos para este documento.

Para desenvolvimento, use User Secrets/variáveis locais. O fallback de cifra é exclusivo para desenvolvimento e não deve ser usado para dados reais.

## Procedimento de release

1. Configure os segredos Mercado Pago rotacionados e a chave AES legada no cofre/runtime. Na primeira release, defina `Encryption__Key` e `Encryption__LegacyKey` iguais à chave usada pela versão atual, com `Encryption__WriteLegacyFormat=true` e `Encryption__RequireLegacyData=true`. Isso mantém os novos valores no formato que a versão anterior consegue ler.
2. Revise o artefato PostgreSQL idempotente do mesmo SHA da imagem e compare cada migration pendente com `__EFMigrationsHistory` em uma cópia recente da produção.
3. Faça backup e valide restauração. Meça migrations com backfill na cópia; consulte `docs/deploy-seguro.md` para os dois pontos de atenção conhecidos.
4. Aplique somente as migrations revisadas antes do deploy, confirme o histórico e `GET /health/ready` na versão atual.
5. Execute `workflow_dispatch` na branch `main` ou `master` somente com as confirmações verdadeiras. O deploy também exige o ambiente `production` e as credenciais de webhook do Coolify.
6. Homologue pagamentos, login, refresh/logout e reconexão de firmware. Mantenha a release de compatibilidade como piso de rollback; não volte à imagem anterior depois que começar a gravar `enc:v1:`.
7. Em uma segunda janela, configure `Encryption__Key` com nova chave aleatória, mantenha a antiga em `Encryption__LegacyKey`, defina `Encryption__WriteLegacyFormat=false` e continue com `Encryption__RequireLegacyData=true`. A partir daí, novas gravações usam AES-GCM.
8. Após backup e janela aprovada, execute primeiro a simulação somente leitura da ferramenta `tools/VendingMachines.EncryptionRotation`; confira a quantidade. A aplicação exige `--apply --confirm-write`, opera em lotes com comparação otimista e pode ser retomada após falha. Confirme que o total pendente é zero.
9. Só depois da verificação, defina `Encryption__RequireLegacyData=false` e remova `Encryption__LegacyKey` do runtime após encerrar a janela de rollback. Tokens JWT anteriores à rotação de `Jwt__Key` exigem novo login.

## Sessão web

O refresh token usa cookie de host da API com `HttpOnly`, `Secure` e `SameSite=Strict`. O frontend deve continuar chamando a API com `credentials: "include"`. A configuração de domínios permitidos deve ser revisada no ambiente de destino antes de publicar.
