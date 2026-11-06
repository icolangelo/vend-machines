# Documentação técnica — Split de pagamentos com Mercado Pago usando somente Checkout Transparente

> Projeto: VendMachine / sistema de gestão e cobrança para vending machines  
> Objetivo: permitir que uma pessoa pague uma cobrança no seu site/sistema, o cliente recebedor receba o valor da venda, e sua empresa fique apenas com uma taxa/comissão da operação.

---

## 1. Visão geral

Embora o negócio seja integrado ao ecossistema do **Mercado Livre**, o split de pagamentos é implementado tecnicamente pela API do **Mercado Pago**.

Neste documento será considerado **somente o Checkout Transparente**, ou seja:

- O comprador paga dentro do ambiente do seu site/aplicação.
- O seu backend cria o pagamento via API do Mercado Pago.
- O pagamento é criado usando o `access_token` do cliente/vendedor.
- A sua comissão é enviada no campo `application_fee`.
- O cliente/vendedor recebe o valor da cobrança, descontadas as taxas do Mercado Pago e a comissão da sua plataforma.

Não serão abordados neste documento:

- Checkout Pro.
- Checkout Bricks.
- Links de pagamento manuais.
- Transferências manuais posteriores.
- Split 1:N entre vários recebedores.

---

## 2. Modelo de negócio

O fluxo financeiro esperado é:

```text
Comprador paga uma cobrança
        ↓
Mercado Pago processa o pagamento
        ↓
Cliente/vendedor recebe o valor líquido
        ↓
Sua plataforma recebe uma comissão/taxa da operação
```

Exemplo:

```text
Valor da cobrança: R$ 100,00
Taxa da sua plataforma: R$ 5,00
Taxa Mercado Pago: descontada pelo Mercado Pago
Cliente recebe: R$ 100,00 - taxa Mercado Pago - R$ 5,00
Sua plataforma recebe: R$ 5,00
```

O modelo recomendado é o **Split de Pagamentos 1:1**, no padrão marketplace:

```text
1 pagamento
1 recebedor principal: seu cliente/vendedor
1 marketplace/plataforma: sua empresa
```

Para o seu sistema de vending machines, o cliente/vendedor pode ser, por exemplo:

```text
BR VendMachs
Hospital São Luiz
Operador regional
Franqueado
Empresa dona das máquinas
```

A cobrança pode estar vinculada a:

```text
Máquina
Cliente final
Pedido
Produto
Abastecimento
Plano recorrente
Venda avulsa
```

---

## 3. Conceitos importantes

### 3.1 Marketplace

No Mercado Pago, seu sistema atua como um **marketplace** ou plataforma intermediadora.

A sua aplicação não deve receber todo o dinheiro primeiro para depois transferir manualmente ao cliente. O ideal é que o pagamento já seja criado em nome do vendedor/cliente, com a comissão da sua plataforma embutida na própria transação.

---

### 3.2 Cliente/vendedor

É quem efetivamente receberá o dinheiro da venda.

Exemplo:

```text
Seu cliente: BR VendMachs
Conta Mercado Pago: conta da BR VendMachs
Venda: R$ 100,00
Comissão da sua plataforma: R$ 5,00
Recebedor principal: BR VendMachs
```

---

### 3.3 Sua plataforma

É a empresa dona do sistema VendMachine.

Ela recebe uma comissão por transação através do campo:

```text
application_fee
```

No Checkout Transparente, o campo correto para a comissão do marketplace é `application_fee`.

---

### 3.4 OAuth

Cada cliente/vendedor precisa autorizar sua aplicação a criar cobranças em nome dele.

Essa autorização é feita via OAuth.

Depois da autorização, seu sistema recebe:

```text
access_token
refresh_token
public_key
user_id / collector_id
scope
live_mode
expires_in
```

O token do cliente/vendedor será usado no backend para criar os pagamentos.

---

## 4. Arquitetura recomendada

### 4.1 Componentes principais

```text
Frontend Web / App
    ↓
Backend VendMachine
    ↓
Banco de Dados
    ↓
API Mercado Pago
    ↓
Webhook Mercado Pago
    ↓
Backend VendMachine
```

---

### 4.2 Responsabilidades do frontend

O frontend deve:

- Exibir tela de pagamento.
- Coletar dados necessários do comprador.
- Usar SDK/componentes do Mercado Pago quando necessário para tokenizar cartão.
- Nunca expor `client_secret`.
- Nunca manipular valor final da cobrança de forma confiável.
- Nunca calcular sozinho a comissão da plataforma.
- Enviar ao backend apenas dados necessários para iniciar o pagamento.

---

### 4.3 Responsabilidades do backend

O backend deve:

- Identificar qual cliente/vendedor receberá a cobrança.
- Buscar o `access_token` do cliente/vendedor.
- Validar o valor real da cobrança.
- Calcular a comissão da plataforma.
- Criar o pagamento via API do Mercado Pago.
- Informar `application_fee`.
- Registrar o pagamento no banco.
- Receber webhooks.
- Consultar o status real do pagamento no Mercado Pago.
- Atualizar o status interno da cobrança.
- Fazer conciliação financeira.

---

## 5. Pré-requisitos

### 5.1 Conta da sua plataforma

Sua empresa precisa ter uma conta Mercado Pago para criar a aplicação marketplace.

---

### 5.2 Conta do cliente/vendedor

Cada cliente que receberá pagamentos precisa ter uma conta Mercado Pago.

Recomendações:

- Conta em nome da empresa recebedora.
- Cadastro completo.
- KYC aprovado.
- Dados bancários corretos.
- Chave Pix configurada, se for aceitar Pix.

---

### 5.3 Aplicação Mercado Pago

Você precisa criar uma aplicação no painel de desenvolvedores do Mercado Pago.

Configurações necessárias:

```text
Tipo de aplicação: Pagamentos online
Modelo: Marketplace
Produto/API: Checkout Transparente / API de pagamentos
Redirect URL OAuth: URL do seu callback
Webhook URL: URL do seu endpoint de notificações
Modo sandbox: habilitado para testes
Modo produção: habilitado depois da homologação
```

---

### 5.4 URLs necessárias

Exemplo de URLs do seu sistema:

```text
OAuth callback:
https://app.seudominio.com.br/integracoes/mercadopago/oauth/callback

Webhook:
https://api.seudominio.com.br/webhooks/mercadopago

Tela de sucesso:
https://app.seudominio.com.br/pagamento/sucesso

Tela de falha:
https://app.seudominio.com.br/pagamento/falha

Tela de pendência:
https://app.seudominio.com.br/pagamento/pendente
```

Mesmo usando Checkout Transparente, é útil ter telas internas de sucesso, falha e pendência para exibir o resultado ao usuário.

---

## 6. Cadastro do cliente/vendedor no seu site

### 6.1 Tela recomendada

No sistema, crie uma área como:

```text
Configurações
→ Pagamentos
→ Mercado Pago
→ Conectar conta Mercado Pago
```

---

### 6.2 Campos do cadastro do cliente

Tabela sugerida:

```sql
Cliente
- Id
- NomeFantasia
- RazaoSocial
- CNPJ
- EmailResponsavel
- TelefoneResponsavel
- Status
- DataCriacao
- DataAtualizacao
```

---

### 6.3 Configuração de pagamento do cliente

Tabela sugerida:

```sql
ClientePagamentoConfig
- Id
- ClienteId
- ProvedorPagamento
- StatusIntegracao
- TaxaTipo
- TaxaPercentual
- TaxaFixa
- TaxaMinima
- TaxaMaxima
- PixHabilitado
- CartaoCreditoHabilitado
- CartaoDebitoHabilitado
- BoletoHabilitado
- DataCriacao
- DataAtualizacao
```

Valores possíveis:

```text
ProvedorPagamento: MercadoPago
StatusIntegracao: Pendente, Conectado, Erro, TokenExpirado, Revogado
TaxaTipo: Fixa, Percentual, Hibrida
```

---

### 6.4 Conta Mercado Pago conectada

Tabela sugerida:

```sql
ClienteMercadoPagoConta
- Id
- ClienteId
- MercadoPagoUserId
- CollectorId
- AccessTokenCriptografado
- RefreshTokenCriptografado
- PublicKey
- Scope
- LiveMode
- ExpiresAt
- DataUltimaRenovacao
- Status
- DataCriacao
- DataAtualizacao
```

Observação importante:

```text
AccessTokenCriptografado e RefreshTokenCriptografado nunca devem ser armazenados em texto puro.
```

---

## 7. Fluxo OAuth para conectar o cliente

### 7.1 Início da conexão

Quando o cliente clicar em **Conectar Mercado Pago**, o backend deve gerar uma tentativa de conexão.

Tabela sugerida:

```sql
MercadoPagoOAuthState
- Id
- ClienteId
- State
- CodeVerifier
- Status
- DataCriacao
- DataExpiracao
- IpOrigem
- UserAgent
```

O campo `state` deve ser único e difícil de adivinhar.

---

### 7.2 Redirecionamento para autorização

Monte a URL de autorização:

```text
https://auth.mercadopago.com.br/authorization
  ?client_id=SEU_CLIENT_ID
  &response_type=code
  &platform_id=mp
  &redirect_uri=SUA_REDIRECT_URI
  &state=STATE_GERADO_PELO_BACKEND
```

Exemplo:

```text
https://auth.mercadopago.com.br/authorization?client_id=123456789&response_type=code&platform_id=mp&redirect_uri=https://app.seudominio.com.br/integracoes/mercadopago/oauth/callback&state=abc123xyz
```

---

### 7.3 Callback OAuth

Depois que o cliente autorizar, o Mercado Pago redirecionará para sua URL de callback.

Exemplo:

```text
https://app.seudominio.com.br/integracoes/mercadopago/oauth/callback?code=AUTHORIZATION_CODE&state=abc123xyz
```

O backend deve:

```text
1. Receber code e state.
2. Validar se o state existe.
3. Validar se o state não expirou.
4. Validar se o state pertence ao cliente correto.
5. Trocar o code por access_token.
6. Salvar tokens criptografados.
7. Marcar a integração como conectada.
```

---

### 7.4 Troca do authorization code por token

Requisição:

```bash
curl -X POST \
  -H 'accept: application/json' \
  -H 'content-type: application/x-www-form-urlencoded' \
  'https://api.mercadopago.com/oauth/token' \
  -d 'client_id=SEU_CLIENT_ID' \
  -d 'client_secret=SEU_CLIENT_SECRET' \
  -d 'grant_type=authorization_code' \
  -d 'code=AUTHORIZATION_CODE' \
  -d 'redirect_uri=SUA_REDIRECT_URI'
```

Resposta esperada:

```json
{
  "access_token": "APP_USR-xxxxxxxxxxxxxxxxxxxxxxxx",
  "public_key": "APP_USR-xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx",
  "refresh_token": "TG-xxxxxxxxxxxxxxxxxxxxxxxx",
  "live_mode": true,
  "user_id": 123456789,
  "token_type": "bearer",
  "expires_in": 15552000,
  "scope": "offline_access payments write"
}
```

Campos importantes:

```text
access_token: token usado para criar pagamentos em nome do cliente/vendedor
refresh_token: token usado para renovar o access_token
user_id: identificador do usuário Mercado Pago / collector_id
public_key: chave pública do vendedor
expires_in: validade do token em segundos
scope: permissões concedidas
```

---

### 7.5 Renovação do token

O access token precisa ser renovado antes de expirar.

Requisição:

```bash
curl -X POST \
  -H 'accept: application/json' \
  -H 'content-type: application/x-www-form-urlencoded' \
  'https://api.mercadopago.com/oauth/token' \
  -d 'client_id=SEU_CLIENT_ID' \
  -d 'client_secret=SEU_CLIENT_SECRET' \
  -d 'grant_type=refresh_token' \
  -d 'refresh_token=REFRESH_TOKEN_DO_CLIENTE'
```

Rotina recomendada:

```text
Executar job diário
Buscar tokens que expiram nos próximos 15 dias
Renovar automaticamente
Atualizar access_token, refresh_token e expires_at
Registrar log de sucesso ou erro
```

---

## 8. Configuração da taxa da sua plataforma

### 8.1 Modelos de cobrança possíveis

Você pode configurar a taxa por cliente.

Exemplos:

```text
Taxa fixa: R$ 1,00 por transação
Taxa percentual: 5% por transação
Taxa híbrida: 3% + R$ 0,50
Taxa mínima: mínimo R$ 1,00
Taxa máxima: máximo R$ 15,00
```

---

### 8.2 Cálculo da taxa

Exemplo em C#:

```csharp
public sealed class RegraTaxaMarketplace
{
    public string Tipo { get; set; } = "Percentual";
    public decimal? Percentual { get; set; }
    public decimal? ValorFixo { get; set; }
    public decimal? ValorMinimo { get; set; }
    public decimal? ValorMaximo { get; set; }
}

public static decimal CalcularApplicationFee(decimal valorCobranca, RegraTaxaMarketplace regra)
{
    decimal taxa = 0m;

    if (regra.Tipo == "Fixa")
    {
        taxa = regra.ValorFixo ?? 0m;
    }
    else if (regra.Tipo == "Percentual")
    {
        taxa = valorCobranca * ((regra.Percentual ?? 0m) / 100m);
    }
    else if (regra.Tipo == "Hibrida")
    {
        taxa = (valorCobranca * ((regra.Percentual ?? 0m) / 100m)) + (regra.ValorFixo ?? 0m);
    }

    if (regra.ValorMinimo.HasValue && taxa < regra.ValorMinimo.Value)
        taxa = regra.ValorMinimo.Value;

    if (regra.ValorMaximo.HasValue && taxa > regra.ValorMaximo.Value)
        taxa = regra.ValorMaximo.Value;

    return Math.Round(taxa, 2, MidpointRounding.AwayFromZero);
}
```

---

### 8.3 Validações importantes

Antes de criar o pagamento:

```text
A taxa da plataforma deve ser maior ou igual a zero.
A taxa da plataforma não deve ser maior que o valor da cobrança.
A taxa deve respeitar o contrato comercial com o cliente.
O valor deve ser calculado no backend.
O frontend não deve enviar o valor final da taxa como fonte confiável.
```

---

## 9. Criação de cobrança com Checkout Transparente

### 9.1 Fluxo geral

```text
1. Comprador inicia pagamento no seu site.
2. Seu backend identifica a cobrança/pedido.
3. Seu backend identifica o cliente/vendedor recebedor.
4. Seu backend busca o access_token do cliente/vendedor.
5. Seu backend calcula a application_fee.
6. Seu backend cria o pagamento na API /v1/payments.
7. Mercado Pago retorna status inicial.
8. Seu sistema grava o payment_id.
9. Webhook confirma alterações posteriores.
```

---

### 9.2 Endpoint interno recomendado

Crie um endpoint no seu backend:

```http
POST /api/pagamentos/mercadopago/checkout-transparente
```

Payload interno sugerido:

```json
{
  "cobrancaId": "COB-123456",
  "meioPagamento": "pix",
  "comprador": {
    "email": "comprador@email.com",
    "nome": "João",
    "sobrenome": "Silva",
    "cpf": "00000000000"
  }
}
```

O backend deve buscar no banco:

```text
Valor da cobrança
Cliente recebedor
Token Mercado Pago do cliente
Regra de taxa da plataforma
Descrição da cobrança
Identificador da máquina/pedido
```

---

## 10. Pagamento via Pix

### 10.1 Payload para criar pagamento Pix

```bash
curl --location 'https://api.mercadopago.com/v1/payments' \
  --header 'accept: application/json' \
  --header 'content-type: application/json' \
  --header 'Authorization: Bearer ACCESS_TOKEN_DO_CLIENTE_VENDEDOR' \
  --header 'X-Idempotency-Key: UUID-UNICO-DA-OPERACAO' \
  --data-raw '{
    "transaction_amount": 100.00,
    "description": "Compra na vending machine VM-001",
    "payment_method_id": "pix",
    "payer": {
      "email": "comprador@email.com",
      "first_name": "João",
      "last_name": "Silva",
      "identification": {
        "type": "CPF",
        "number": "00000000000"
      }
    },
    "application_fee": 5.00,
    "external_reference": "COB-123456",
    "notification_url": "https://api.seudominio.com.br/webhooks/mercadopago"
  }'
```

Pontos importantes:

```text
Authorization: deve usar o access_token do cliente/vendedor.
application_fee: sua comissão na transação.
external_reference: identificador interno da cobrança no seu sistema.
notification_url: endpoint que receberá notificações do Mercado Pago.
X-Idempotency-Key: evita duplicidade em caso de retentativa.
```

---

### 10.2 Resposta esperada para Pix

A resposta do Mercado Pago pode trazer dados para exibir o QR Code ou copia e cola.

Estrutura comum:

```json
{
  "id": 123456789,
  "status": "pending",
  "status_detail": "pending_waiting_transfer",
  "payment_method_id": "pix",
  "payment_type_id": "bank_transfer",
  "transaction_amount": 100.00,
  "application_fee": 5.00,
  "external_reference": "COB-123456",
  "point_of_interaction": {
    "transaction_data": {
      "qr_code": "00020126580014br.gov.bcb.pix...",
      "qr_code_base64": "iVBORw0KGgoAAAANSUhEUg...",
      "ticket_url": "https://www.mercadopago.com.br/payments/..."
    }
  }
}
```

No frontend, você pode exibir:

```text
QR Code
Pix copia e cola
Status aguardando pagamento
Tempo de expiração
Botão para verificar pagamento
```

---

## 11. Pagamento via cartão

### 11.1 Tokenização do cartão

Para cartão, o frontend deve gerar um token de cartão usando as bibliotecas/client-side do Mercado Pago.

O seu backend **não deve receber nem armazenar o número completo do cartão**.

Fluxo:

```text
1. Comprador informa os dados do cartão no frontend.
2. SDK Mercado Pago tokeniza o cartão.
3. Frontend envia card_token ao backend.
4. Backend cria pagamento usando token.
```

---

### 11.2 Payload para criar pagamento com cartão

```bash
curl --location 'https://api.mercadopago.com/v1/payments' \
  --header 'accept: application/json' \
  --header 'content-type: application/json' \
  --header 'Authorization: Bearer ACCESS_TOKEN_DO_CLIENTE_VENDEDOR' \
  --header 'X-Idempotency-Key: UUID-UNICO-DA-OPERACAO' \
  --data-raw '{
    "transaction_amount": 100.00,
    "token": "CARD_TOKEN_GERADO_NO_FRONTEND",
    "description": "Compra na vending machine VM-001",
    "installments": 1,
    "payment_method_id": "visa",
    "issuer_id": "25",
    "payer": {
      "email": "comprador@email.com",
      "identification": {
        "type": "CPF",
        "number": "00000000000"
      }
    },
    "application_fee": 5.00,
    "external_reference": "COB-123456",
    "notification_url": "https://api.seudominio.com.br/webhooks/mercadopago"
  }'
```

---

### 11.3 Status comuns para cartão

```text
approved: pagamento aprovado
pending: pagamento pendente
in_process: pagamento em análise
rejected: pagamento recusado
cancelled: pagamento cancelado
refunded: pagamento reembolsado
charged_back: chargeback/contestação
```

---

## 12. Idempotência

Sempre envie o header:

```http
X-Idempotency-Key: UUID-UNICO-DA-OPERACAO
```

Exemplo:

```text
COB-123456-CRIAR-PAGAMENTO-001
```

Ou um UUID:

```text
7e9f942e-7421-4a6a-b946-ff1d87c59c31
```

Regra recomendada:

```text
Uma cobrança interna deve ter uma chave de idempotência fixa para a tentativa de criação do pagamento.
Se o backend sofrer timeout, repita a chamada com a mesma chave.
Não gere uma nova chave em retentativas da mesma operação.
```

Tabela sugerida:

```sql
PagamentoTentativa
- Id
- CobrancaId
- IdempotencyKey
- MercadoPagoPaymentId
- Status
- RequestPayload
- ResponsePayload
- DataCriacao
- DataAtualizacao
```

---

## 13. Registro da cobrança no banco

Tabela principal sugerida:

```sql
Cobranca
- Id
- ClienteId
- MaquinaId
- PedidoId
- ValorBruto
- ValorApplicationFee
- ValorLiquidoEstimadoCliente
- MeioPagamento
- StatusInterno
- ExternalReference
- MercadoPagoPaymentId
- MercadoPagoStatus
- MercadoPagoStatusDetail
- DataCriacao
- DataAprovacao
- DataCancelamento
- DataExpiracao
```

---

## 14. Status internos recomendados

```text
Criada
AguardandoPagamento
EmAnalise
Aprovada
Recusada
Cancelada
Expirada
Reembolsada
Chargeback
Erro
```

Mapeamento inicial:

```text
Mercado Pago pending → AguardandoPagamento
Mercado Pago in_process → EmAnalise
Mercado Pago approved → Aprovada
Mercado Pago rejected → Recusada
Mercado Pago cancelled → Cancelada
Mercado Pago refunded → Reembolsada
Mercado Pago charged_back → Chargeback
```

---

## 15. Webhook Mercado Pago

### 15.1 Endpoint

Crie um endpoint público HTTPS:

```http
POST /webhooks/mercadopago
```

URL completa:

```text
https://api.seudominio.com.br/webhooks/mercadopago
```

---

### 15.2 O que fazer ao receber webhook

Fluxo recomendado:

```text
1. Receber payload.
2. Gravar payload bruto em tabela de auditoria.
3. Responder rapidamente HTTP 200.
4. Processar de forma assíncrona.
5. Identificar o payment_id ou resource informado.
6. Consultar o pagamento na API do Mercado Pago.
7. Atualizar a cobrança interna.
8. Garantir idempotência do processamento.
```

---

### 15.3 Tabela de webhook

```sql
MercadoPagoWebhookEvento
- Id
- TipoEvento
- Acao
- MercadoPagoResourceId
- PayloadBruto
- HeadersBrutos
- Processado
- DataRecebimento
- DataProcessamento
- ErroProcessamento
```

---

### 15.4 Consulta do pagamento após webhook

Nunca confie apenas no payload recebido.

Depois de receber o webhook, consulte o pagamento:

```bash
curl -G -X GET \
  -H 'accept: application/json' \
  -H 'Authorization: Bearer ACCESS_TOKEN_DO_CLIENTE_VENDEDOR' \
  'https://api.mercadopago.com/v1/payments/PAYMENT_ID'
```

O token usado deve ser o do cliente/vendedor que recebeu a cobrança.

---

## 16. Confirmação do pagamento

Regra recomendada:

```text
Só considerar a cobrança paga quando o Mercado Pago retornar status approved.
```

Para vending machine, se o pagamento libera algum produto/serviço, a ordem ideal é:

```text
Pagamento aprovado
    ↓
Atualizar cobrança como Aprovada
    ↓
Registrar evento de liberação
    ↓
Enviar comando para máquina ou liberar operação
    ↓
Registrar sucesso/falha da liberação
```

Tabela sugerida:

```sql
LiberacaoMaquina
- Id
- CobrancaId
- MaquinaId
- Status
- DataSolicitacao
- DataConfirmacao
- Erro
```

---

## 17. Reembolso

### 17.1 Regras de negócio

Defina no seu sistema:

```text
Quem pode solicitar reembolso?
Cliente/vendedor pode reembolsar sozinho?
Sua plataforma precisa aprovar?
Reembolso será total ou parcial?
A comissão da plataforma será devolvida?
Como tratar saldo insuficiente do vendedor?
Como tratar chargeback?
```

---

### 17.2 Endpoint interno recomendado

```http
POST /api/pagamentos/mercadopago/{paymentId}/reembolsar
```

Payload:

```json
{
  "motivo": "Produto não entregue pela máquina",
  "valor": 100.00,
  "tipo": "total"
}
```

---

### 17.3 Registro de reembolso

Tabela sugerida:

```sql
PagamentoReembolso
- Id
- CobrancaId
- MercadoPagoPaymentId
- MercadoPagoRefundId
- Valor
- Tipo
- Motivo
- Status
- SolicitadoPor
- DataSolicitacao
- DataConfirmacao
- Erro
```

---

## 18. Conciliação financeira

### 18.1 Objetivo

A conciliação garante que os valores registrados no seu sistema batem com o Mercado Pago.

Você deve conciliar:

```text
Valor bruto da cobrança
Taxa do Mercado Pago
Application fee da sua plataforma
Valor líquido do cliente/vendedor
Status do pagamento
Data de aprovação
Data de liberação do dinheiro
Reembolsos
Chargebacks
```

---

### 18.2 Rotina recomendada

```text
Executar diariamente
Buscar pagamentos do dia anterior
Comparar com cobranças internas
Marcar divergências
Gerar relatório para financeiro
```

---

### 18.3 Tabela sugerida

```sql
ConciliacaoPagamento
- Id
- CobrancaId
- ClienteId
- MercadoPagoPaymentId
- ValorBrutoSistema
- ValorBrutoMercadoPago
- ApplicationFeeSistema
- ApplicationFeeMercadoPago
- TaxaMercadoPago
- ValorLiquidoCliente
- StatusSistema
- StatusMercadoPago
- Divergente
- MotivoDivergencia
- DataConciliacao
```

---

## 19. Telas necessárias no seu sistema

### 19.1 Tela administrativa da plataforma

```text
Configurações Mercado Pago
- Client ID
- Client Secret
- Redirect URL OAuth
- Webhook URL
- Ambiente: Sandbox / Produção
- Taxa padrão da plataforma
- Meios de pagamento habilitados
- Status geral da integração
```

---

### 19.2 Tela do cliente/vendedor

```text
Minha integração Mercado Pago
- Status da conexão
- Botão Conectar Mercado Pago
- Botão Reconectar
- Data da última renovação do token
- Meios de pagamento habilitados
- Taxa contratada da plataforma
- Histórico de cobranças
```

---

### 19.3 Tela de nova cobrança

```text
Nova cobrança
- Cliente/vendedor recebedor
- Máquina
- Pedido
- Valor
- Descrição
- Meio de pagamento
- Taxa da plataforma calculada
- Valor líquido estimado para o cliente
- Botão Gerar pagamento
```

---

### 19.4 Tela de pagamento do comprador

```text
Pagamento
- Valor da cobrança
- Descrição
- Dados do comprador
- Opção Pix
- Opção cartão
- Status do pagamento
- QR Code Pix, se aplicável
- Pix copia e cola, se aplicável
- Mensagem de pagamento aprovado/pendente/recusado
```

---

### 19.5 Tela de auditoria

```text
Auditoria Mercado Pago
- Cobrança
- Payment ID
- Cliente/vendedor
- Valor bruto
- Application fee
- Status
- Webhooks recebidos
- Requests enviados
- Responses recebidos
- Erros
```

---

## 20. Segurança

### 20.1 Regras obrigatórias

```text
Nunca expor client_secret no frontend.
Nunca armazenar cartão no seu banco.
Nunca armazenar access_token sem criptografia.
Nunca confiar em valor enviado pelo frontend.
Sempre validar o valor da cobrança no backend.
Sempre usar HTTPS.
Sempre usar state no OAuth.
Sempre usar idempotência na criação de pagamento.
Sempre registrar webhooks.
Sempre consultar a API do Mercado Pago antes de confirmar pagamento.
```

---

### 20.2 Criptografia dos tokens

Recomendado:

```text
Criptografar access_token e refresh_token com chave protegida.
Separar chave de criptografia do banco de dados.
Usar cofre de segredos quando possível.
Registrar acesso aos tokens.
Restringir acesso administrativo.
```

Exemplos de opções:

```text
Azure Key Vault
AWS KMS / Secrets Manager
Google Secret Manager
HashiCorp Vault
Criptografia própria com proteção adequada de chave
```

---

### 20.3 Logs

Não registre em log:

```text
access_token
refresh_token
client_secret
número completo de cartão
CVV
CPF completo sem necessidade
```

Pode registrar:

```text
payment_id
external_reference
status
status_detail
valor
cliente_id
correlation_id
idempotency_key
```

---

## 21. Tratamento de erros

### 21.1 Erros comuns

```text
Token expirado
Token revogado
Cliente não conectou Mercado Pago
Cliente sem Pix configurado
Pagamento recusado
Cartão inválido
Saldo insuficiente
Erro de antifraude
Timeout na API
Webhook duplicado
Pagamento criado, mas resposta não recebida
```

---

### 21.2 Estratégia para timeout

Se a chamada para criar pagamento der timeout:

```text
1. Não crie nova cobrança imediatamente.
2. Repita a chamada usando a mesma X-Idempotency-Key.
3. Se ainda assim houver dúvida, consulte pagamentos por external_reference.
4. Só permita nova tentativa quando tiver certeza de que não duplicou.
```

---

## 22. Ambiente de testes

Antes de ir para produção, teste:

```text
OAuth com conta de teste
Renovação de token
Pagamento Pix pendente
Pagamento Pix aprovado
Pagamento com cartão aprovado
Pagamento com cartão recusado
Pagamento em análise
Webhook duplicado
Webhook fora de ordem
Timeout na criação de pagamento
Reembolso total
Reembolso parcial, se aplicável
Token expirado
Token revogado
Cliente desconectado
```

---

## 23. Fluxo final recomendado para o MVP

```text
1. Criar aplicação marketplace no Mercado Pago.
2. Configurar redirect URL e webhook URL.
3. Criar cadastro do cliente/vendedor no seu sistema.
4. Cliente conecta conta Mercado Pago via OAuth.
5. Seu sistema salva tokens criptografados.
6. Configurar taxa da sua plataforma para o cliente.
7. Criar tela de cobrança.
8. Criar endpoint interno de pagamento.
9. Criar pagamento via /v1/payments usando access_token do cliente.
10. Enviar application_fee na criação do pagamento.
11. Receber webhook.
12. Consultar pagamento na API do Mercado Pago.
13. Atualizar cobrança no seu banco.
14. Liberar produto/serviço somente após status aprovado.
15. Conciliar diariamente.
```

---

## 24. Checklist de implementação

### 24.1 Mercado Pago

```text
[ ] Criar conta Mercado Pago da sua empresa
[ ] Criar aplicação no modelo Marketplace
[ ] Configurar Redirect URL
[ ] Configurar Webhook URL
[ ] Obter Client ID
[ ] Obter Client Secret
[ ] Criar contas de teste
[ ] Validar OAuth
[ ] Validar criação de pagamento Pix
[ ] Validar criação de pagamento cartão
[ ] Validar application_fee
[ ] Validar webhook
[ ] Validar reembolso
[ ] Migrar para produção
```

---

### 24.2 Backend

```text
[ ] Criar cadastro de cliente/vendedor
[ ] Criar tabela ClientePagamentoConfig
[ ] Criar tabela ClienteMercadoPagoConta
[ ] Criar fluxo OAuth
[ ] Criar callback OAuth
[ ] Criptografar tokens
[ ] Criar job de renovação de token
[ ] Criar cálculo de application_fee
[ ] Criar endpoint de pagamento Pix
[ ] Criar endpoint de pagamento cartão
[ ] Criar tabela Cobranca
[ ] Criar tabela PagamentoTentativa
[ ] Criar webhook Mercado Pago
[ ] Criar processamento idempotente de webhook
[ ] Criar consulta de pagamento por ID
[ ] Criar rotina de conciliação
[ ] Criar rotina de reembolso
[ ] Criar logs e auditoria
```

---

### 24.3 Frontend

```text
[ ] Criar tela de conexão Mercado Pago para o cliente/vendedor
[ ] Criar tela de configuração de taxa
[ ] Criar tela de nova cobrança
[ ] Criar tela de pagamento Pix
[ ] Exibir QR Code Pix
[ ] Exibir Pix copia e cola
[ ] Criar tela de pagamento cartão
[ ] Integrar tokenização de cartão
[ ] Criar tela de status do pagamento
[ ] Criar tela de histórico de cobranças
[ ] Criar tela de auditoria para administradores
```

---

## 25. Modelo de entidades resumido

```text
Cliente
ClientePagamentoConfig
ClienteMercadoPagoConta
MercadoPagoOAuthState
Cobranca
PagamentoTentativa
MercadoPagoWebhookEvento
PagamentoReembolso
ConciliacaoPagamento
LiberacaoMaquina
```

---

## 26. Pontos de atenção jurídica e operacional

Este modelo envolve intermediação de pagamentos. Antes de operar em produção, valide com jurídico/contábil:

```text
Contrato com os clientes/vendedores
Descrição clara da taxa da plataforma
Responsabilidade sobre chargeback
Responsabilidade sobre reembolso
Emissão de nota fiscal da sua comissão
Tratamento de dados pessoais do comprador
LGPD
Conciliação e prestação de contas
Prazo de repasse e disponibilidade dos valores
```

---

## 27. Referências oficiais Mercado Pago

- Split de Pagamentos 1:1 — visão geral: https://www.mercadopago.com.br/developers/pt/docs/split-payments/split-1-1/overview
- Pré-requisitos do Split 1:1: https://www.mercadopago.com.br/developers/pt/docs/split-payments/split-1-1/prerequisites
- Integração marketplace com Split 1:1: https://www.mercadopago.com.br/developers/pt/docs/split-payments/split-1-1/integration-configuration/integrate-marketplace
- OAuth — obter access token: https://www.mercadopago.com.br/developers/pt/docs/security/oauth/creation
- OAuth — renovar access token: https://www.mercadopago.com.br/developers/pt/docs/security/oauth/renewal
- Checkout Transparente via Orders — visão geral: https://www.mercadopago.com.br/developers/pt/docs/checkout-api-orders/overview
- Webhooks: https://www.mercadopago.com.br/developers/pt/docs/checkout-pro/additional-content/notifications/webhooks
- API OAuth token: https://www.mercadopago.com.br/developers/pt/reference/authentication/oauth/_oauth_token/post

---

## 28. Observação final

Para o seu projeto, a decisão técnica recomendada é:

```text
Checkout: somente Checkout Transparente/API
Split: Split de Pagamentos 1:1
Comissão da plataforma: application_fee
Autorização dos clientes: OAuth
Token usado na cobrança: access_token do cliente/vendedor
Confirmação de pagamento: webhook + consulta na API
Conciliação: rotina diária
```
