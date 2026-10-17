# Inventário operacional da Fase 0

Este documento registra o estado das rotas sensíveis enquanto a telemetria continua ativa em produção.

## Rotas públicas esperadas

| Rota | Estado | Motivo |
| --- | --- | --- |
| `POST /api/auth/login` | Pública | Entrada do usuário |
| `POST /api/auth/register` | Pública, com e-mail administrativo reservado | Cadastro de empresa |
| `POST /api/auth/refresh` | Pública, protegida por cookie HttpOnly | Renovação de sessão |
| `POST /api/auth/logout` | Pública, protegida por cookie | Encerramento de sessão |
| `POST /webhooks/mercadopago` | Pública | Callback do provedor; assinatura deve ser validada |
| `GET /health` | Pública | Monitoramento de disponibilidade; não expõe dados |
| `GET /` | Pública | Identificação mínima da API |

As rotas `WeatherForecast`, `GET /api/products/debug` e `GET /WeatherForecast/run-migration` foram removidas. `POST /api/payments/simulate-webhook` retorna 404 fora de desenvolvimento.

## Rotas que exigem autenticação

Dashboard, máquinas, localizações, produtos, clientes, usuários, empresas, pagamentos e o painel/API de telemetria exigem JWT. As consultas por empresa usam `company_id`; a listagem de usuários fica limitada à empresa do administrador. A consulta de taxa global, a impersonação e a administração de empresas exigem o papel persistido `PlatformAdmin`; alterações operacionais sensíveis exigem `Admin` ou `PlatformAdmin`. O WebSocket de dispositivo `/telemetria` permanece em modo de compatibilidade até a migração de credenciais da Fase 1.

As rotas `POST /api/machines/{id}/telemetry-credential` e `DELETE /api/machines/{id}/telemetry-credential` fazem rotação e revogação da credencial individual. A credencial em texto puro só aparece na resposta da rotação; a API persiste somente o hash.

## Telemetria em produção

`/telemetria` permanece habilitado por requisito operacional. A conexão atualmente identifica a máquina pelo serial enviado no handshake. A autenticação individual do dispositivo é uma tarefa da Fase 1; não alterar essa rota sem plano de migração, credenciais provisionadas e teste de reconexão.

## Configuração por ambiente

- Produção deve fornecer `Jwt:Key` com pelo menos 32 bytes, `Cors:AllowedOrigins`, conexão PostgreSQL e segredos do Mercado Pago por secret manager ou variáveis de ambiente.
- `Telemetry:RequireDeviceCredentials` deve permanecer `false` até todas as máquinas terem uma credencial provisionada e o fluxo de reconexão ser validado; depois da migração, configurar `true` para rejeitar handshakes sem credencial válida.
- Quando essa opção estiver `true`, a API falha ao iniciar se existir máquina sem credencial ativa; isso evita publicar uma configuração que derrube todas as conexões por falta de provisionamento.
- A API falha ao iniciar fora de desenvolvimento quando encontra SQLite, localhost ou placeholders na conexão; isso evita operar acidentalmente com banco local.
- Qualquer credencial que tenha aparecido em arquivo versionado deve ser considerada exposta, rotacionada e removida da configuração versionada antes do lançamento.
- `VITE_IS_TEST_ENVIRONMENT` deve ser `false` e `VITE_API_URL` deve apontar para a API de produção no build publicado.
- O Vite falha o build de produção se o modo de teste estiver ligado ou se `VITE_API_URL` estiver ausente/local.
- Login, cadastro, renovação de sessão e emissão de tickets do painel de telemetria usam limites por IP; respostas excedentes retornam HTTP 429. O WebSocket físico `/telemetria` permanece fora do limite durante a migração.
- A tabela `AuditLogs` registra login, cadastro, logout, rotação e revogação de credenciais sem armazenar senhas, tokens ou credenciais em texto puro.
- `GET /api/admin/audit-logs` exige `PlatformAdmin` e permite consulta paginada por empresa e ação.
- `PUT /api/users/{id}/role` exige `Admin` ou `PlatformAdmin`, valida escopo da empresa, impede autoalteração e não permite que administradores de empresa atribuam `PlatformAdmin`; a ação revoga refresh sessions e fica registrada em auditoria.
- Desenvolvimento pode usar SQLite e os simuladores locais; esses valores não podem ser usados no ambiente comercial.
- Migrações devem ser executadas pelo pipeline ou procedimento operacional autenticado, nunca por rota HTTP pública.

## Verificações antes de promover

- [ ] Testar 401 sem sessão e 403 sem papel suficiente nas rotas administrativas.
- [ ] Testar uma conta da empresa A tentando consultar e alterar IDs da empresa B.
- [ ] Testar duplicação de webhook e assinatura inválida no ambiente de homologação.
- [ ] Confirmar que o frontend publicado não contém o modo de teste nem URL localhost.
- [ ] Confirmar que a telemetria continua conectada após a implantação e que o health check responde.
