# Implantação da sessão autenticada

## Configurações obrigatórias no Cloud Run

Antes de implantar a API, disponibilize os seguintes valores por variáveis de ambiente ligadas ao Secret Manager:

- `Jwt__Key`
- `MercadoPago__ClientSecret`
- `MercadoPago__WebhookSecret`
- `ConnectionStrings__DefaultConnection`

`Jwt__Key` deve ser uma chave aleatória com pelo menos 32 bytes. A rotação dessa chave invalida imediatamente todos os access tokens existentes.

Os segredos não devem voltar para `appsettings.json`. Como valores antigos já estiveram no histórico do Git, eles precisam ser rotacionados no Mercado Pago e no Cloud Run.

## Domínios

- Aplicação: `https://app.vendmachine.com.br`
- API: `https://api.vendmachine.com.br`

O refresh token usa cookie de host da API com `HttpOnly`, `Secure` e `SameSite=Strict`. O frontend precisa continuar chamando a API com `credentials: "include"`.

## Ordem de implantação

1. Criar ou atualizar os segredos no Secret Manager e vinculá-los ao Cloud Run.
2. Implantar a API. A migration `AddRefreshSessions` será aplicada na inicialização.
3. Validar `POST /api/auth/login`, `POST /api/auth/refresh` e `POST /api/auth/logout`.
4. Implantar o frontend, já configurado para `https://api.vendmachine.com.br/api`.
5. Orientar os usuários a entrar novamente uma vez; tokens antigos do Web Storage são removidos pelo novo frontend.
6. Monitorar respostas `401`, eventos `IDX10223` e alertas de reutilização de refresh token.
