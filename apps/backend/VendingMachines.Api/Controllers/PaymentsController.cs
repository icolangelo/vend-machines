using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using VendingMachines.Api.Data;
using VendingMachines.Api.Models;
using VendingMachines.Api.Services;

namespace VendingMachines.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class PaymentsController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly TelemetryManager _telemetry;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;

    public PaymentsController(AppDbContext context, TelemetryManager telemetry, IServiceScopeFactory scopeFactory, IConfiguration configuration)
    {
        _context = context;
        _telemetry = telemetry;
        _scopeFactory = scopeFactory;
        _configuration = configuration;
    }

    // ==========================================
    // 1. SETTINGS GLOBAIS (Admin do Sistema)
    // ==========================================

    [HttpGet("global-settings")]
    public async Task<IActionResult> GetGlobalSettings()
    {
        var settings = await _context.SystemSettings.FirstOrDefaultAsync();
        if (settings == null)
        {
            settings = new SystemSettings
            {
                Id = Guid.NewGuid(),
                ApplicationFeePercent = 5.0m,
                UpdatedAt = DateTime.UtcNow
            };
            _context.SystemSettings.Add(settings);
            await _context.SaveChangesAsync();
        }
        return Ok(settings);
    }

    [HttpPost("global-settings")]
    public async Task<IActionResult> UpdateGlobalSettings([FromBody] SystemSettings request)
    {
        var email = User.FindFirst(ClaimTypes.Email)?.Value;
        if (email != "dev.ivan@gmail.com")
        {
            return StatusCode(403, new { message = "Apenas o administrador do sistema pode alterar a taxa global." });
        }

        var settings = await _context.SystemSettings.FirstOrDefaultAsync();
        if (settings == null)
        {
            settings = new SystemSettings { Id = Guid.NewGuid() };
            _context.SystemSettings.Add(settings);
        }

        settings.ApplicationFeePercent = request.ApplicationFeePercent;
        settings.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();
        return Ok(new { message = "Taxa da plataforma atualizada com sucesso.", settings });
    }

    // ==========================================
    // 2. INTEGRAÇÃO DA EMPRESA (Dono/Sócio)
    // ==========================================

    [HttpGet("integration")]
    public async Task<IActionResult> GetIntegration()
    {
        var companyIdStr = User.FindFirst("company_id")?.Value;
        if (string.IsNullOrEmpty(companyIdStr) || !Guid.TryParse(companyIdStr, out var companyId))
        {
            return BadRequest(new { message = "O usuário não está associado a nenhuma empresa." });
        }

        var integration = await _context.MercadoPagoIntegrations
            .FirstOrDefaultAsync(i => i.CompanyId == companyId);

        if (integration == null)
        {
            return Ok(new MercadoPagoIntegrationDto
            {
                CompanyId = companyId,
                IsActive = false
            });
        }

        return Ok(MapToDto(integration));
    }

    [HttpGet("oauth/config")]
    public IActionResult GetOauthConfig()
    {
        var clientId = _configuration["MercadoPago:ClientId"] ?? "";
        
        string redirectUri = "https://app.vendmachine.com.br/";
        var referer = Request.Headers["Referer"].ToString();
        if (!string.IsNullOrEmpty(referer))
        {
            try
            {
                var uri = new Uri(referer);
                redirectUri = $"{uri.Scheme}://{uri.Authority}/";
            }
            catch { }
        }
        
        return Ok(new
        {
            clientId = clientId,
            redirectUri = redirectUri
        });
    }

    [HttpPost("oauth/callback")]
    public async Task<IActionResult> OAuthCallback([FromBody] OAuthCallbackRequest request)
    {
        var companyIdStr = User.FindFirst("company_id")?.Value;
        if (string.IsNullOrEmpty(companyIdStr) || !Guid.TryParse(companyIdStr, out var companyId))
        {
            return BadRequest(new { message = "O usuário não está associado a nenhuma empresa." });
        }

        var role = User.FindFirst(ClaimTypes.Role)?.Value;
        if (role != "Admin")
        {
            return StatusCode(403, new { message = "Apenas administradores podem gerenciar integrações." });
        }

        if (string.IsNullOrWhiteSpace(request.Code))
        {
            return BadRequest(new { message = "O código de autorização é obrigatório." });
        }

        var clientSecret = _configuration["MercadoPago:ClientSecret"] ?? "";
        var clientId = _configuration["MercadoPago:ClientId"] ?? "";

        string accessToken = "";
        string publicKey = "";
        string mpUserId = "";

        bool isMock = string.IsNullOrEmpty(clientSecret) || 
                     clientSecret == "YOUR_MERCADO_PAGO_PLATFORM_SECRET" || 
                     request.Code.StartsWith("dummy_") || 
                     request.Code.Contains("mock");

        if (isMock)
        {
            accessToken = $"APP_USR-DUMMY-OAUTH-{Guid.NewGuid().ToString().Replace("-", "").ToUpper()}";
            publicKey = $"APP_USR-{Guid.NewGuid().ToString().Replace("-", "").Substring(0, 16).ToUpper()}";
            mpUserId = "123456789";
        }
        else
        {
            try
            {
                using var httpClient = new HttpClient();
                var parameters = new Dictionary<string, string>
                {
                    { "client_secret", clientSecret },
                    { "client_id", clientId },
                    { "grant_type", "authorization_code" },
                    { "code", request.Code },
                    { "redirect_uri", request.RedirectUri }
                };
                
                var response = await httpClient.PostAsync("https://api.mercadopago.com/oauth/token", new FormUrlEncodedContent(parameters));
                var responseStr = await response.Content.ReadAsStringAsync();
                
                if (!response.IsSuccessStatusCode)
                {
                    return BadRequest(new { message = $"Erro ao obter token do Mercado Pago: {responseStr}" });
                }
                
                using var doc = JsonDocument.Parse(responseStr);
                var root = doc.RootElement;
                accessToken = root.GetProperty("access_token").GetString() ?? "";
                publicKey = root.GetProperty("public_key").GetString() ?? "";
                if (root.TryGetProperty("user_id", out var userIdProp))
                {
                    mpUserId = userIdProp.ValueKind == JsonValueKind.Number ? userIdProp.GetInt64().ToString() : userIdProp.GetString() ?? "";
                }
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = $"Erro na comunicação com o Mercado Pago: {ex.Message}" });
            }
        }

        var email = User.FindFirst(ClaimTypes.Email)?.Value;
        var dbUser = await _context.Users
            .Include(u => u.Company)
            .FirstOrDefaultAsync(u => u.Email == email);

        var integration = await _context.MercadoPagoIntegrations
            .FirstOrDefaultAsync(i => i.CompanyId == companyId);

        bool isNew = integration == null;
        if (isNew)
        {
            integration = new MercadoPagoIntegration
            {
                Id = Guid.NewGuid(),
                CompanyId = companyId,
                CreatedAt = DateTime.UtcNow
            };
            _context.MercadoPagoIntegrations.Add(integration);
        }

        integration!.OwnerName = dbUser?.Name ?? "Sócio da Empresa";
        integration.OwnerCpf = dbUser?.Cpf ?? "123.456.789-00";
        integration.OwnerEmail = dbUser?.Email ?? "socio@empresa.com";
        integration.OwnerPhone = "11999999999";

        integration.BusinessName = dbUser?.Company?.Name ?? "Empresa Parceira";
        integration.TradeName = dbUser?.Company?.Name ?? "Empresa Parceira";
        integration.Cnpj = dbUser?.Company?.Cnpj ?? "12.345.678/0001-99";
        integration.BusinessEmail = dbUser?.Email ?? "contato@empresa.com";
        integration.BusinessPhone = "11999999999";

        integration.AccessToken = EncryptionService.Encrypt(accessToken);
        integration.PublicKey = publicKey;
        integration.ClientId = clientId;
        integration.ClientSecret = EncryptionService.Encrypt(clientSecret);
        integration.IsActive = true;
        integration.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();

        return Ok(new { message = "Integração via OAuth realizada com sucesso.", integration = MapToDto(integration) });
    }

    [HttpPost("integration/disconnect")]
    public async Task<IActionResult> DisconnectIntegration()
    {
        var companyIdStr = User.FindFirst("company_id")?.Value;
        if (string.IsNullOrEmpty(companyIdStr) || !Guid.TryParse(companyIdStr, out var companyId))
        {
            return BadRequest(new { message = "O usuário não está associado a nenhuma empresa." });
        }

        var role = User.FindFirst(ClaimTypes.Role)?.Value;
        if (role != "Admin")
        {
            return StatusCode(403, new { message = "Apenas administradores podem gerenciar integrações." });
        }

        var integration = await _context.MercadoPagoIntegrations
            .FirstOrDefaultAsync(i => i.CompanyId == companyId);

        if (integration != null)
        {
            _context.MercadoPagoIntegrations.Remove(integration);
            await _context.SaveChangesAsync();
        }

        return Ok(new { message = "Integração Mercado Pago desconectada com sucesso." });
    }

    [HttpPost("integration/toggle")]
    public async Task<IActionResult> ToggleIntegration([FromBody] ToggleIntegrationRequest request)
    {
        var companyIdStr = User.FindFirst("company_id")?.Value;
        if (string.IsNullOrEmpty(companyIdStr) || !Guid.TryParse(companyIdStr, out var companyId))
        {
            return BadRequest(new { message = "O usuário não está associado a nenhuma empresa." });
        }

        var role = User.FindFirst(ClaimTypes.Role)?.Value;
        if (role != "Admin")
        {
            return StatusCode(403, new { message = "Apenas administradores podem gerenciar integrações." });
        }

        var integration = await _context.MercadoPagoIntegrations
            .FirstOrDefaultAsync(i => i.CompanyId == companyId);

        if (integration == null)
        {
            return BadRequest(new { message = "Nenhuma integração configurada para esta empresa." });
        }

        integration.IsActive = request.IsActive;
        integration.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();
        return Ok(new { message = $"Integração {(request.IsActive ? "habilitada" : "desabilitada")} com sucesso.", isActive = integration.IsActive });
    }

    private MercadoPagoIntegrationDto MapToDto(MercadoPagoIntegration integration)
    {
        var decryptedAccessToken = EncryptionService.Decrypt(integration.AccessToken);
        var decryptedClientSecret = EncryptionService.Decrypt(integration.ClientSecret);

        return new MercadoPagoIntegrationDto
        {
            Id = integration.Id,
            CompanyId = integration.CompanyId,
            OwnerName = integration.OwnerName,
            OwnerCpf = integration.OwnerCpf,
            OwnerEmail = integration.OwnerEmail,
            OwnerPhone = integration.OwnerPhone,
            BusinessName = integration.BusinessName,
            TradeName = integration.TradeName,
            Cnpj = integration.Cnpj,
            BusinessEmail = integration.BusinessEmail,
            BusinessPhone = integration.BusinessPhone,
            PublicKey = integration.PublicKey,
            ClientId = integration.ClientId,
            IsActive = integration.IsActive,
            CreatedAt = integration.CreatedAt,
            UpdatedAt = integration.UpdatedAt,
            AccessToken = MaskValue(decryptedAccessToken),
            ClientSecret = MaskValue(decryptedClientSecret)
        };
    }

    private static string MaskValue(string value, int visibleStart = 8, int visibleEnd = 4)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        if (value.Length <= visibleStart + visibleEnd)
        {
            return new string('•', value.Length);
        }
        return value[..visibleStart] + new string('•', 12) + value[^visibleEnd..];
    }

    public class ToggleIntegrationRequest
    {
        public bool IsActive { get; set; }
    }

    // ==========================================
    // 3. GERAÇÃO DE QR CODE PIX (Checkout Transparente)
    // ==========================================

    [HttpPost("pix-qr")]
    public async Task<IActionResult> CreatePixQrCode([FromBody] PixChargeRequest request)
    {
        if (request.Amount <= 0)
        {
            return BadRequest(new { message = "O valor da cobrança deve ser maior que zero." });
        }

        var machine = await _context.Machines
            .FirstOrDefaultAsync(m => m.Id == request.MachineId);

        if (machine == null)
        {
            return NotFound(new { message = "Máquina não encontrada." });
        }

        if (!machine.MercadoPagoEnabled)
        {
            return BadRequest(new { message = "A cobrança via Mercado Pago não está habilitada para esta máquina." });
        }

        if (!machine.CompanyId.HasValue)
        {
            return BadRequest(new { message = "A máquina selecionada não está vinculada a nenhuma empresa." });
        }

        var integration = await _context.MercadoPagoIntegrations
            .FirstOrDefaultAsync(i => i.CompanyId == machine.CompanyId.Value && i.IsActive);

        if (integration == null)
        {
            return BadRequest(new { message = "A empresa dessa máquina não possui uma integração ativa com o Mercado Pago." });
        }

        // Taxa da plataforma
        var settings = await _context.SystemSettings.FirstOrDefaultAsync();
        decimal feePercent = settings?.ApplicationFeePercent ?? 5.0m;
        decimal appFee = Math.Round(request.Amount * (feePercent / 100m), 2);

        // Criar transação interna
        var tx = new PaymentTransaction
        {
            Id = Guid.NewGuid(),
            MachineId = machine.Id,
            CompanyId = machine.CompanyId.Value,
            Amount = request.Amount,
            ApplicationFee = appFee,
            Status = "Pending",
            CreatedAt = DateTime.UtcNow
        };

        _context.PaymentTransactions.Add(tx);
        await _context.SaveChangesAsync();

        // Registrar log inicial
        await LogTelemetryAsync(tx.Id, "Info", $"Solicitada cobrança Pix de R$ {tx.Amount:N2} na máquina {machine.Name} (Taxa do site: R$ {tx.ApplicationFee:N2}).");

        // Comunicar com a API do Mercado Pago
        try
        {
            var decryptedToken = EncryptionService.Decrypt(integration.AccessToken);
            if (decryptedToken.Contains("mock") || decryptedToken.Length < 15)
            {
                if (request.UseRealMercadoPago)
                {
                    return BadRequest(new { message = "Você solicitou a integração real, mas as credenciais salvas para a empresa são de simulação (contêm 'mock' ou são muito curtas)." });
                }
                throw new Exception("Modo simulação local ativo: chaves de teste detectadas.");
            }

            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", decryptedToken);
            httpClient.DefaultRequestHeaders.Add("X-Idempotency-Key", tx.Id.ToString());

            // Determina os dados do comprador a partir da integração ativa se o frontend não enviá-los
            string payerEmail = !string.IsNullOrWhiteSpace(request.PayerEmail) 
                ? request.PayerEmail 
                : "cliente-vending@seudominio.com.br";
            
            string payerName = !string.IsNullOrWhiteSpace(request.PayerFirstName)
                ? $"{request.PayerFirstName} {request.PayerLastName}".Trim()
                : "Cliente Vending";

            string firstName = "Cliente";
            string lastName = "Vending";
            if (!string.IsNullOrWhiteSpace(payerName))
            {
                var parts = payerName.Trim().Split(' ', 2);
                firstName = parts[0];
                if (parts.Length > 1)
                {
                    lastName = parts[1];
                }
            }

            string? docType = null;
            string? docNumber = null;

            if (!string.IsNullOrWhiteSpace(request.PayerCpf))
            {
                string cleanedDoc = request.PayerCpf.Replace(".", "").Replace("-", "").Replace("/", "").Replace(" ", "");
                if (IsCnpjValido(cleanedDoc))
                {
                    docType = "CNPJ";
                    docNumber = cleanedDoc;
                }
                else if (IsCpfValido(cleanedDoc))
                {
                    docType = "CPF";
                    docNumber = cleanedDoc;
                }
            }

            // Payloads dynamically built depending on whether document is provided and whether application_fee is charged
            object payerPayload;
            if (!string.IsNullOrEmpty(docNumber))
            {
                payerPayload = new
                {
                    email = payerEmail,
                    first_name = firstName,
                    last_name = lastName,
                    identification = new
                    {
                        type = docType,
                        number = docNumber
                    }
                };
            }
            else
            {
                payerPayload = new
                {
                    email = payerEmail,
                    first_name = firstName,
                    last_name = lastName
                };
            }

            object payload;
            if (tx.ApplicationFee > 0)
            {
                payload = new
                {
                    transaction_amount = tx.Amount,
                    description = string.IsNullOrWhiteSpace(request.Description) ? $"Venda maquina {machine.Name}" : request.Description,
                    payment_method_id = "pix",
                    payer = payerPayload,
                    application_fee = tx.ApplicationFee,
                    external_reference = tx.Id.ToString()
                };
            }
            else
            {
                payload = new
                {
                    transaction_amount = tx.Amount,
                    description = string.IsNullOrWhiteSpace(request.Description) ? $"Venda maquina {machine.Name}" : request.Description,
                    payment_method_id = "pix",
                    payer = payerPayload,
                    external_reference = tx.Id.ToString()
                };
            }

            var mpResponse = await httpClient.PostAsJsonAsync("https://api.mercadopago.com/v1/payments", payload);
            var responseStr = await mpResponse.Content.ReadAsStringAsync();

            tx.RawResponse = responseStr;

            // Retentar sem comissão (application_fee) se a resposta indicar erro na taxa
            if (!mpResponse.IsSuccessStatusCode && tx.ApplicationFee > 0)
            {
                bool isFeeError = false;
                try
                {
                    using var errDoc = JsonDocument.Parse(responseStr);
                    var errRoot = errDoc.RootElement;
                    string? msg = errRoot.TryGetProperty("message", out var msgProp) ? msgProp.GetString() : null;
                    
                    if (msg != null && (msg.Contains("application_fee") || msg.Contains("2030") || msg.Contains("2059")))
                    {
                        isFeeError = true;
                    }
                    else if (errRoot.TryGetProperty("cause", out var causeProp) && causeProp.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var cause in causeProp.EnumerateArray())
                        {
                            if (cause.TryGetProperty("description", out var descProp) && descProp.GetString()?.Contains("application_fee") == true)
                            {
                                isFeeError = true;
                                break;
                            }
                            if (cause.TryGetProperty("code", out var codeProp) && 
                                (codeProp.ValueKind == JsonValueKind.Number && (codeProp.GetInt32() == 2030 || codeProp.GetInt32() == 2059) ||
                                 codeProp.ValueKind == JsonValueKind.String && (codeProp.GetString() == "2030" || codeProp.GetString() == "2059")))
                            {
                                isFeeError = true;
                                break;
                            }
                        }
                    }
                }
                catch { }

                if (isFeeError)
                {
                    await LogTelemetryAsync(tx.Id, "Warning", "O Mercado Pago recusou a comissão (application_fee). Retentando gerar cobrança Pix sem taxa da plataforma...");
                    
                    var retryPayload = new
                    {
                        transaction_amount = tx.Amount,
                        description = string.IsNullOrWhiteSpace(request.Description) ? $"Venda maquina {machine.Name}" : request.Description,
                        payment_method_id = "pix",
                        payer = payerPayload,
                        external_reference = tx.Id.ToString()
                    };

                    mpResponse = await httpClient.PostAsJsonAsync("https://api.mercadopago.com/v1/payments", retryPayload);
                    responseStr = await mpResponse.Content.ReadAsStringAsync();
                    tx.RawResponse = responseStr;

                    if (mpResponse.IsSuccessStatusCode)
                    {
                        tx.ApplicationFee = 0; // Taxa cancelada pois não pôde ser cobrada
                        await LogTelemetryAsync(tx.Id, "Info", "Cobrança Pix gerada com sucesso sem taxa da plataforma (fallback).");
                    }
                }
            }

            if (mpResponse.IsSuccessStatusCode)
            {
                using var jsonDoc = JsonDocument.Parse(responseStr);
                var root = jsonDoc.RootElement;

                tx.MercadoPagoPaymentId = root.GetProperty("id").GetRawText();
                tx.MercadoPagoStatus = root.GetProperty("status").GetString();
                tx.MercadoPagoStatusDetail = root.GetProperty("status_detail").GetString();

                var pointOfInteraction = root.GetProperty("point_of_interaction");
                var transactionData = pointOfInteraction.GetProperty("transaction_data");

                tx.QrCode = transactionData.GetProperty("qr_code").GetString() ?? "";
                tx.QrCodeBase64 = transactionData.GetProperty("qr_code_base64").GetString() ?? "";

                await _context.SaveChangesAsync();
                await LogTelemetryAsync(tx.Id, "Info", $"QR Code Pix gerado com sucesso pelo Mercado Pago. Payment ID: {tx.MercadoPagoPaymentId}");

                return Ok(new
                {
                    transactionId = tx.Id,
                    qrCode = tx.QrCode,
                    qrCodeBase64 = tx.QrCodeBase64,
                    status = tx.Status
                });
            }
            else
            {
                await LogTelemetryAsync(tx.Id, "Error", $"Erro retornado pelo Mercado Pago: {responseStr}");
                
                string errorDetail = responseStr;
                try
                {
                    using var errDoc = JsonDocument.Parse(responseStr);
                    var errRoot = errDoc.RootElement;
                    if (errRoot.TryGetProperty("message", out var msgProp))
                    {
                        errorDetail = msgProp.GetString() ?? responseStr;
                    }
                    else if (errRoot.TryGetProperty("cause", out var causeProp) && causeProp.ValueKind == JsonValueKind.Array && causeProp.GetArrayLength() > 0)
                    {
                        var firstCause = causeProp[0];
                        if (firstCause.TryGetProperty("description", out var descProp))
                        {
                            errorDetail = descProp.GetString() ?? responseStr;
                        }
                    }
                }
                catch { }

                throw new Exception($"Erro da API Mercado Pago ({mpResponse.StatusCode}): {errorDetail}");
            }
        }
        catch (Exception ex)
        {
            if (request.UseRealMercadoPago)
            {
                await LogTelemetryAsync(tx.Id, "Error", $"Falha na integração real do Mercado Pago: {ex.Message}");
                return BadRequest(new { message = $"Erro retornado pelo Mercado Pago: {ex.Message}", transactionId = tx.Id });
            }

            // Fallback para simulação em desenvolvimento
            tx.MercadoPagoPaymentId = $"mock_mp_{new Random().Next(10000000, 99999999)}";
            tx.MercadoPagoStatus = "pending";
            tx.MercadoPagoStatusDetail = "pending_waiting_transfer";
            tx.QrCode = $"00020101021226870014br.gov.bcb.pix2572pix.example.com/qr/v2/mock-{tx.Id}";
            // Imagem de QR Code genérico para demonstração visual
            tx.QrCodeBase64 = "iVBORw0KGgoAAAANSUhEUgAAAJYAAACWAQAAAAAUekxPAAABUklEQVR4nNWWQWrDMBBFnxKD2pVyAwV6Dzvd9FQGp6SLHsu+iX0DZWeD4t+FnbSkBEpjQauV+QzzGP3RjI24PqfVNwn+uoZqgNxFb2tKwMblGY0UyCkB8FISRjChqp3a3ih6/PKMSXs3XeYftHlbKN8N7bS19fyZgRuPnzHi4S5GBsCwG+NTr2OcNZkvIZ3BLtNXUl8oAjZmFIIzZi3ufDNIkmyjg8hZS1KCvpKkHhcBp4QMoKQvlJJhR8DWSe+KQpE+7V1ZBbxGd0jCuGhVY1Y/ifuFNtdRA3akSulHPjFSei6Fkj5P6ocx3raYbr9QvqtzrgOUyo957j6O6oaXBfLdnrvTKi8kkbsEfhgpDM/K6LV53aoJCeqY9vnUtmlmyaQNO+iQyTBmk6gO2wS8rd3ea3RxBW5cliEFK6nFhKpVozjtc7sgo4bpXVz8MP/wH/gDo7EYHNztgj4AAAAASUVORK5CYII=";
            tx.RawResponse = JsonSerializer.Serialize(new { status = "pending", message = ex.Message });

            await _context.SaveChangesAsync();
            await LogTelemetryAsync(tx.Id, "Info", $"Cobrança Pix gerada em modo Simulação/Desenvolvimento. ID: {tx.MercadoPagoPaymentId}");

            return Ok(new
            {
                transactionId = tx.Id,
                qrCode = tx.QrCode,
                qrCodeBase64 = tx.QrCodeBase64,
                status = tx.Status,
                simulated = true
            });
        }
    }

    // ==========================================
    // 4. WEBHOOK DO MERCADO PAGO
    // ==========================================

    [AllowAnonymous]
    [HttpPost("/webhooks/mercadopago")]
    public IActionResult WebhookMercadoPago([FromBody] JsonElement webhookBody)
    {
        // Responder 200 OK rapidamente
        System.Diagnostics.Debug.WriteLine($"Webhook recebido: {webhookBody.GetRawText()}");
        
        try
        {
            if (webhookBody.TryGetProperty("type", out var typeProp) && typeProp.GetString() == "payment")
            {
                if (webhookBody.TryGetProperty("data", out var dataProp) && dataProp.TryGetProperty("id", out var idProp))
                {
                    string paymentId = idProp.GetString() ?? idProp.GetInt64().ToString();
                    _ = Task.Run(() => ProcessRealWebhookAsync(paymentId));
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Erro no parsing do webhook: {ex.Message}");
        }

        return Ok();
    }

    // ==========================================
    // 5. SIMULADOR DE WEBHOOK (Front -> Back)
    // ==========================================

    [HttpPost("simulate-webhook")]
    public async Task<IActionResult> SimulateWebhook([FromBody] SimulateWebhookRequest request)
    {
        var tx = await _context.PaymentTransactions
            .Include(t => t.Machine)
            .FirstOrDefaultAsync(t => t.Id == request.TransactionId);

        if (tx == null)
        {
            return NotFound(new { message = "Transação não encontrada." });
        }

        if (tx.Status != "Pending")
        {
            return BadRequest(new { message = "Esta transação já foi processada anteriormente." });
        }

        tx.Status = request.Approved ? "Approved" : "Rejected";
        tx.MercadoPagoStatus = request.Approved ? "approved" : "rejected";
        tx.MercadoPagoStatusDetail = request.Approved ? "accredited" : "cc_rejected_bad_filled_other";
        tx.CompletedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        await LogTelemetryAsync(tx.Id, "Info", $"Webhook Simulado recebido. Status do Pagamento: {(request.Approved ? "APROVADO" : "RECUSADO")}");

        // Inicia a telemetria resiliente na máquina em segundo plano
        _ = Task.Run(() => RunResilientTelemetrySequence(tx.Id, request.Approved));

        return Ok(new { message = "Status atualizado. Sequência de telemetria disparada em background.", transaction = tx });
    }

    // ==========================================
    // 6. HISTÓRICO E LOGS DE TRANSAÇÕES
    // ==========================================

    [HttpGet("transactions")]
    public async Task<IActionResult> GetTransactions([FromQuery] string? machineId = null)
    {
        var companyIdStr = User.FindFirst("company_id")?.Value;
        if (string.IsNullOrEmpty(companyIdStr) || !Guid.TryParse(companyIdStr, out var companyId))
        {
            return BadRequest(new { message = "O usuário não está associado a nenhuma empresa." });
        }

        var query = _context.PaymentTransactions
            .Include(t => t.Machine)
            .Where(t => t.CompanyId == companyId);

        if (!string.IsNullOrEmpty(machineId))
        {
            query = query.Where(t => t.MachineId == machineId);
        }

        var list = await query
            .OrderByDescending(t => t.CreatedAt)
            .Take(50)
            .ToListAsync();

        return Ok(list);
    }

    [HttpGet("transactions/{id}/logs")]
    public async Task<IActionResult> GetTransactionLogs(Guid id)
    {
        var logs = await _context.TransactionTelemetryLogs
            .Where(l => l.TransactionId == id)
            .OrderBy(l => l.Timestamp)
            .ToListAsync();

        return Ok(logs);
    }

    [HttpGet("admin/logs")]
    public async Task<IActionResult> GetAdminLogs([FromQuery] int page = 1, [FromQuery] int pageSize = 20, [FromQuery] string? search = null)
    {
        var email = User.FindFirst(ClaimTypes.Email)?.Value;
        if (email != "dev.ivan@gmail.com")
        {
            return StatusCode(403, new { message = "Acesso restrito ao Administrador Geral." });
        }

        var query = _context.TransactionTelemetryLogs
            .Include(l => l.Transaction)
            .ThenInclude(t => t!.Machine)
            .Include(l => l.Transaction)
            .ThenInclude(t => t!.Company)
            .AsQueryable();

        if (!string.IsNullOrEmpty(search))
        {
            query = query.Where(l => l.Message.Contains(search) || 
                                     l.LogType.Contains(search) || 
                                     l.TransactionId.ToString().Contains(search));
        }

        var totalItems = await query.CountAsync();
        var totalPages = (int)Math.Ceiling(totalItems / (double)pageSize);

        var items = await query
            .OrderByDescending(l => l.Timestamp)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(l => new {
                l.Id,
                l.TransactionId,
                l.Timestamp,
                l.LogType,
                l.Message,
                TransactionAmount = l.Transaction != null ? l.Transaction.Amount : 0,
                MachineName = l.Transaction != null && l.Transaction.Machine != null ? l.Transaction.Machine.Name : "Máquina Desconhecida",
                CompanyName = l.Transaction != null && l.Transaction.Company != null ? l.Transaction.Company.Name : "Empresa Desconhecida",
                RawResponse = l.Transaction != null ? l.Transaction.RawResponse : null
            })
            .ToListAsync();

        return Ok(new {
            items,
            page,
            pageSize,
            totalItems,
            totalPages
        });
    }

    // ==========================================================
    // MÉTODOS AUXILIARES DE TELEMETRIA E REEMBOLSO
    // ==========================================

    private async Task ProcessRealWebhookAsync(string paymentId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var tx = await db.PaymentTransactions
            .Include(t => t.Machine)
            .FirstOrDefaultAsync(t => t.MercadoPagoPaymentId == paymentId);

        if (tx == null || tx.Status != "Pending") return;

        var integration = await db.MercadoPagoIntegrations
            .FirstOrDefaultAsync(i => i.CompanyId == tx.CompanyId && i.IsActive);

        if (integration == null) return;

        try
        {
            var decryptedToken = EncryptionService.Decrypt(integration.AccessToken);
            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", decryptedToken);

            var mpResponse = await httpClient.GetAsync($"https://api.mercadopago.com/v1/payments/{paymentId}");
            if (mpResponse.IsSuccessStatusCode)
            {
                var responseStr = await mpResponse.Content.ReadAsStringAsync();
                using var jsonDoc = JsonDocument.Parse(responseStr);
                var root = jsonDoc.RootElement;

                string status = root.GetProperty("status").GetString() ?? "pending";
                tx.MercadoPagoStatus = status;
                tx.MercadoPagoStatusDetail = root.GetProperty("status_detail").GetString();
                tx.RawResponse = responseStr;

                bool approved = status == "approved";
                bool rejected = status == "rejected" || status == "cancelled";

                if (approved)
                {
                    tx.Status = "Approved";
                    tx.CompletedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync();
                    await LogTelemetryAsync(tx.Id, "Info", "Webhook real confirmado: Aprovado pelo Mercado Pago.");
                    _ = Task.Run(() => RunResilientTelemetrySequence(tx.Id, true));
                }
                else if (rejected)
                {
                    tx.Status = "Rejected";
                    tx.CompletedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync();
                    await LogTelemetryAsync(tx.Id, "Info", "Webhook real confirmado: Recusado/Cancelado pelo Mercado Pago.");
                    _ = Task.Run(() => RunResilientTelemetrySequence(tx.Id, false));
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Erro ao conciliar webhook real: {ex.Message}");
        }
    }

    private async Task RunResilientTelemetrySequence(Guid transactionId, bool approved)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var tx = await db.PaymentTransactions
            .Include(t => t.Machine)
            .FirstOrDefaultAsync(t => t.Id == transactionId);

        if (tx == null || tx.Machine == null) return;

        string serialNumber = tx.Machine.SerialNumber;
        if (string.IsNullOrEmpty(serialNumber))
        {
            await LogTelemetryAsync(transactionId, "Error", "Falha: Máquina não possui Serial Number configurado.");
            return;
        }

        await LogTelemetryAsync(transactionId, "Info", $"Iniciando sequência resiliente de telemetria MDB para {tx.Machine.Name} (Serial: {serialNumber}).");

        bool connected = false;

        for (int attempt = 1; attempt <= 3; attempt++)
        {
            await LogTelemetryAsync(transactionId, "Info", $"Verificando conexão da máquina (Tentativa {attempt}/3)...");
            
            var clientConnection = _telemetry.ClientIdentifiers.FirstOrDefault(x => x.Value == serialNumber);
            if (clientConnection.Key != null && _telemetry.EspClients.TryGetValue(clientConnection.Key, out var ws) && ws.State == System.Net.WebSockets.WebSocketState.Open)
            {
                connected = true;
                break;
            }

            if (attempt < 3)
            {
                await LogTelemetryAsync(transactionId, "Info", "Máquina não respondeu. Nova verificação em 5 segundos.");
                await Task.Delay(5000);
            }
        }

        if (!connected)
        {
            await LogTelemetryAsync(transactionId, "Error", "A máquina permaneceu offline após 3 tentativas de conexão.");
            
            if (approved)
            {
                await LogTelemetryAsync(transactionId, "RefundTriggered", "Iniciando estorno automático da transação por falha técnica de comunicação.");
                
                tx.Status = "Failed";
                await db.SaveChangesAsync();

                bool refundSuccess = await RefundTransactionAsync(db, tx);
                if (refundSuccess)
                {
                    tx.Status = "Refunded";
                    await db.SaveChangesAsync();
                    await LogTelemetryAsync(transactionId, "Info", "Estorno Pix concluído com sucesso e saldo devolvido ao comprador.");
                }
                else
                {
                    await LogTelemetryAsync(transactionId, "Error", "O estorno no Mercado Pago falhou. Requer conciliação manual do operador.");
                }
            }
            return;
        }

        try
        {
            await LogTelemetryAsync(transactionId, "CommandSent", "Enviando comando: ABRIR_SESSAO");
            _telemetry.EnqueueRawMessageToEsp(serialNumber, "ABRIR_SESSAO");
            await Task.Delay(2000);

            if (approved)
            {
                await LogTelemetryAsync(transactionId, "CommandSent", "Enviando comando: VENDA_APROVADA");
                _telemetry.EnqueueRawMessageToEsp(serialNumber, "VENDA_APROVADA");
            }
            else
            {
                await LogTelemetryAsync(transactionId, "CommandSent", "Enviando comando: VENDA_NEGADA");
                _telemetry.EnqueueRawMessageToEsp(serialNumber, "VENDA_NEGADA");
            }
            await Task.Delay(2000);

            await LogTelemetryAsync(transactionId, "CommandSent", "Enviando comando: FECHAR_SESSAO");
            _telemetry.EnqueueRawMessageToEsp(serialNumber, "FECHAR_SESSAO");
            
            await LogTelemetryAsync(transactionId, "Info", "Sequência de telemetria MDB remota concluída com sucesso.");
        }
        catch (Exception ex)
        {
            await LogTelemetryAsync(transactionId, "Error", $"Erro na transmissão MDB: {ex.Message}");
        }
    }

    private async Task<bool> RefundTransactionAsync(AppDbContext db, PaymentTransaction tx)
    {
        var integration = await db.MercadoPagoIntegrations
            .FirstOrDefaultAsync(i => i.CompanyId == tx.CompanyId && i.IsActive);

        if (integration == null || string.IsNullOrEmpty(tx.MercadoPagoPaymentId) || tx.MercadoPagoPaymentId.Contains("mock"))
        {
            await Task.Delay(1500);
            return true;
        }

        try
        {
            var decryptedToken = EncryptionService.Decrypt(integration.AccessToken);
            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", decryptedToken);

            var response = await httpClient.PostAsync($"https://api.mercadopago.com/v1/payments/{tx.MercadoPagoPaymentId}/refunds", null);
            if (response.IsSuccessStatusCode)
            {
                return true;
            }
            else
            {
                var err = await response.Content.ReadAsStringAsync();
                System.Diagnostics.Debug.WriteLine($"Falha no reembolso Mercado Pago: {err}");
                return false;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Erro HTTP ao reembolsar: {ex.Message}");
            return false;
        }
    }

    private async Task LogTelemetryAsync(Guid transactionId, string type, string message)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var log = new TransactionTelemetryLog
            {
                Id = Guid.NewGuid(),
                TransactionId = transactionId,
                Timestamp = DateTime.UtcNow,
                LogType = type,
                Message = message
            };

            db.TransactionTelemetryLogs.Add(log);
            await db.SaveChangesAsync();
        }
        catch { }
    }

    private static bool IsCpfValido(string cpf)
    {
        if (string.IsNullOrWhiteSpace(cpf)) return false;
        cpf = cpf.Replace(".", "").Replace("-", "").Replace(" ", "");
        if (cpf.Length != 11) return false;
        
        string[] invalidos = {
            "00000000000", "11111111111", "22222222222", "33333333333",
            "44444444444", "55555555555", "66666666666", "77777777777",
            "88888888888", "99999999999"
        };
        if (invalidos.Contains(cpf)) return false;

        int[] multiplicador1 = { 10, 9, 8, 7, 6, 5, 4, 3, 2 };
        int[] multiplicador2 = { 11, 10, 9, 8, 7, 6, 5, 4, 3, 2 };
        
        string tempCpf = cpf.Substring(0, 9);
        int soma = 0;

        for (int i = 0; i < 9; i++)
            soma += int.Parse(tempCpf[i].ToString()) * multiplicador1[i];

        int resto = soma % 11;
        if (resto < 2)
            resto = 0;
        else
            resto = 11 - resto;

        string semente = tempCpf + resto;
        soma = 0;
        for (int i = 0; i < 10; i++)
            soma += int.Parse(semente[i].ToString()) * multiplicador2[i];

        resto = soma % 11;
        if (resto < 2)
            resto = 0;
        else
            resto = 11 - resto;

        semente = semente + resto;
        return cpf.EndsWith(semente.Substring(9));
    }

    private static bool IsCnpjValido(string cnpj)
    {
        if (string.IsNullOrWhiteSpace(cnpj)) return false;
        cnpj = cnpj.Replace(".", "").Replace("-", "").Replace("/", "").Replace(" ", "");
        if (cnpj.Length != 14) return false;

        string[] invalidos = {
            "00000000000000", "11111111111111", "22222222222222", "33333333333333",
            "44444444444444", "55555555555555", "66666666666666", "77777777777777",
            "88888888888888", "99999999999999"
        };
        if (invalidos.Contains(cnpj)) return false;

        int[] multiplicador1 = { 5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2 };
        int[] multiplicador2 = { 6, 5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2 };

        string tempCnpj = cnpj.Substring(0, 12);
        int soma = 0;

        for (int i = 0; i < 12; i++)
            soma += int.Parse(tempCnpj[i].ToString()) * multiplicador1[i];

        int resto = (soma % 11);
        if (resto < 2)
            resto = 0;
        else
            resto = 11 - resto;

        string semente = tempCnpj + resto;
        soma = 0;
        for (int i = 0; i < 13; i++)
            soma += int.Parse(semente[i].ToString()) * multiplicador2[i];

        resto = (soma % 11);
        if (resto < 2)
            resto = 0;
        else
            resto = 11 - resto;

        semente = semente + resto;
        return cnpj.EndsWith(semente.Substring(12));
    }
}

public class PixChargeRequest
{
    public string MachineId { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string Description { get; set; } = string.Empty;
    public string? PayerEmail { get; set; }
    public string? PayerFirstName { get; set; }
    public string? PayerLastName { get; set; }
    public string? PayerCpf { get; set; }
    public bool UseRealMercadoPago { get; set; } = false;
}

public class SimulateWebhookRequest
{
    public Guid TransactionId { get; set; }
    public bool Approved { get; set; }
}

public class OAuthCallbackRequest
{
    public string Code { get; set; } = string.Empty;
    public string RedirectUri { get; set; } = string.Empty;
}
