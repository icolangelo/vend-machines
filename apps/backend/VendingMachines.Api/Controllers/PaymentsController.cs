using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
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
    private readonly SessionOrchestrator _sessionOrchestrator;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly IWebHostEnvironment _environment;

    public PaymentsController(AppDbContext context, SessionOrchestrator sessionOrchestrator, IServiceScopeFactory scopeFactory, IConfiguration configuration, IWebHostEnvironment environment)
    {
        _context = context;
        _sessionOrchestrator = sessionOrchestrator;
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _environment = environment;
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
        var companyIdStr = User.FindFirst("company_id")?.Value;
        if (string.IsNullOrEmpty(companyIdStr) || !Guid.TryParse(companyIdStr, out var companyId))
        {
            return BadRequest(new { message = "O usuário não está associado a nenhuma empresa." });
        }

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
            redirectUri = redirectUri,
            state = CreateOAuthState(companyId)
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
        string refreshToken = "";
        string publicKey = "";
        string mpUserId = "";
        string mpNickname = "";
        string mpSiteId = "";
        DateTime? accessTokenExpiresAt = null;

        bool isMock = string.IsNullOrEmpty(clientSecret) || 
                     clientSecret == "YOUR_MERCADO_PAGO_PLATFORM_SECRET" || 
                     request.Code.StartsWith("dummy_") || 
                     request.Code.Contains("mock");

        if (isMock && !_environment.IsDevelopment())
        {
            return BadRequest(new { message = "Callback OAuth simulado não é permitido em produção." });
        }

        if (!isMock && !ValidateOAuthState(request.State, companyId, out var stateError))
        {
            return BadRequest(new { message = stateError });
        }

        if (isMock)
        {
            accessToken = $"APP_USR-DUMMY-OAUTH-{Guid.NewGuid().ToString().Replace("-", "").ToUpper()}";
            refreshToken = $"REFRESH-DUMMY-{Guid.NewGuid().ToString().Replace("-", "").ToUpper()}";
            publicKey = $"APP_USR-{Guid.NewGuid().ToString().Replace("-", "").Substring(0, 16).ToUpper()}";
            mpUserId = "123456789";
            mpNickname = "mock-seller";
            mpSiteId = "MLB";
            accessTokenExpiresAt = DateTime.UtcNow.AddHours(6);
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
                refreshToken = root.TryGetProperty("refresh_token", out var refreshProp) ? refreshProp.GetString() ?? "" : "";
                publicKey = root.GetProperty("public_key").GetString() ?? "";
                if (root.TryGetProperty("expires_in", out var expiresProp) && expiresProp.TryGetInt32(out var expiresIn))
                {
                    accessTokenExpiresAt = DateTime.UtcNow.AddSeconds(expiresIn);
                }
                if (root.TryGetProperty("user_id", out var userIdProp))
                {
                    mpUserId = userIdProp.ValueKind == JsonValueKind.Number ? userIdProp.GetInt64().ToString() : userIdProp.GetString() ?? "";
                }

                var profile = await GetMercadoPagoUserProfileAsync(accessToken);
                if (profile != null)
                {
                    mpUserId = profile.UserId;
                    mpNickname = profile.Nickname;
                    mpSiteId = profile.SiteId;
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
        integration.RefreshToken = EncryptionService.Encrypt(refreshToken);
        integration.AccessTokenExpiresAt = accessTokenExpiresAt;
        integration.PublicKey = publicKey;
        integration.ClientId = clientId;
        integration.ClientSecret = EncryptionService.Encrypt(clientSecret);
        integration.MercadoPagoUserId = mpUserId;
        integration.MercadoPagoNickname = mpNickname;
        integration.MercadoPagoSiteId = mpSiteId;
        integration.LastTokenValidationAt = DateTime.UtcNow;
        integration.LastTokenValidationStatus = string.IsNullOrWhiteSpace(mpUserId) ? "Token OAuth salvo sem confirmação de /users/me." : "Token OAuth validado em /users/me.";
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
            HasRefreshToken = !string.IsNullOrWhiteSpace(integration.RefreshToken),
            AccessTokenExpiresAt = integration.AccessTokenExpiresAt,
            MercadoPagoUserId = integration.MercadoPagoUserId,
            MercadoPagoNickname = integration.MercadoPagoNickname,
            MercadoPagoSiteId = integration.MercadoPagoSiteId,
            LastTokenValidationAt = integration.LastTokenValidationAt,
            LastTokenValidationStatus = integration.LastTokenValidationStatus,
            TokenFingerprint = GetTokenFingerprint(decryptedAccessToken),
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

        var currentCompanyIdStr = User.FindFirst("company_id")?.Value;
        if (string.IsNullOrEmpty(currentCompanyIdStr) || !Guid.TryParse(currentCompanyIdStr, out var currentCompanyId))
        {
            return BadRequest(new { message = "O usuário não está associado a nenhuma empresa." });
        }

        if (machine.CompanyId.Value != currentCompanyId)
        {
            return StatusCode(403, new { message = "A máquina selecionada pertence a outra empresa. Selecione uma máquina vinculada à empresa logada." });
        }

        var integration = await _context.MercadoPagoIntegrations
            .FirstOrDefaultAsync(i => i.CompanyId == currentCompanyId && i.IsActive);

        if (integration == null)
        {
            return BadRequest(new { message = "A empresa dessa máquina não possui uma integração ativa com o Mercado Pago." });
        }

        // Taxa da plataforma
        var settings = await _context.SystemSettings.FirstOrDefaultAsync();
        decimal feePercent = settings?.ApplicationFeePercent ?? 5.0m;
        decimal appFee = Math.Round(request.Amount * (feePercent / 100m), 2);
        if (appFee <= 0)
        {
            return BadRequest(new { message = "A taxa da plataforma precisa ser maior que R$ 0,00 para gerar uma cobrança Pix." });
        }

        if (appFee >= request.Amount)
        {
            return BadRequest(new { message = "A taxa da plataforma precisa ser menor que o valor total da cobrança Pix." });
        }

        string? requestDocType = null;
        string? requestDocNumber = null;
        if (!string.IsNullOrWhiteSpace(request.PayerCpf))
        {
            var cleanedDoc = CleanDocument(request.PayerCpf);
            if (IsCnpjValido(cleanedDoc))
            {
                requestDocType = "CNPJ";
                requestDocNumber = cleanedDoc;
            }
            else if (IsCpfValido(cleanedDoc))
            {
                requestDocType = "CPF";
                requestDocNumber = cleanedDoc;
            }
        }




        // Criar transação interna
        var tx = new PaymentTransaction
        {
            Id = Guid.NewGuid(),
            MachineId = machine.Id,
            CompanyId = machine.CompanyId.Value,
            Amount = request.Amount,
            ApplicationFee = appFee,
            Status = "Pending",
            SendTelemetryToMachine = request.SendTelemetryToMachine,
            CreatedAt = DateTime.UtcNow
        };

        _context.PaymentTransactions.Add(tx);
        await _context.SaveChangesAsync();

        // Registrar log inicial
        await LogTelemetryAsync(tx.Id, "Info", $"Solicitada cobrança Pix de R$ {tx.Amount:N2} na máquina {machine.Name} (Taxa do site: R$ {tx.ApplicationFee:N2}).");

        // Comunicar com a API do Mercado Pago
        try
        {
            var decryptedToken = await EnsureValidAccessTokenAsync(integration);
            var tokenFingerprint = GetTokenFingerprint(decryptedToken);
            if (decryptedToken.Contains("mock") || decryptedToken.Length < 15)
            {
                if (request.UseRealMercadoPago)
                {
                    const string mockCredentialsMessage = "Você solicitou a integração real, mas as credenciais salvas para a empresa são de simulação (contêm 'mock' ou são muito curtas).";
                    tx.Status = "Failed";
                    tx.RawResponse = JsonSerializer.Serialize(new { error = "mock_credentials", message = mockCredentialsMessage });
                    await _context.SaveChangesAsync();
                    await LogTelemetryAsync(tx.Id, "Error", mockCredentialsMessage);
                    return BadRequest(new { message = mockCredentialsMessage, transactionId = tx.Id });
                }
                throw new Exception("Modo simulação local ativo: chaves de teste detectadas.");
            }

            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", decryptedToken);
            httpClient.DefaultRequestHeaders.Add("X-Idempotency-Key", tx.Id.ToString());
            bool applicationFeeApplied = tx.ApplicationFee > 0;

            await LogTelemetryAsync(
                tx.Id,
                "Info",
                $"Usando token OAuth salvo na integração {integration.Id} da empresa {integration.CompanyId}. Fingerprint do token: {tokenFingerprint}. Integração atualizada em: {integration.UpdatedAt:O}."
            );

            MercadoPagoUserProfile tokenOwner;
            try
            {
                tokenOwner = await LogMercadoPagoTokenOwnerAsync(httpClient, tx.Id);
            }
            catch (Exception ex)
            {
                tx.Status = "Failed";
                tx.RawResponse = JsonSerializer.Serialize(new
                {
                    status = "failed",
                    message = "Não foi possível validar o token OAuth salvo antes da criação do Pix.",
                    details = ex.Message
                });
                integration.LastTokenValidationAt = DateTime.UtcNow;
                integration.LastTokenValidationStatus = $"Falha ao validar token OAuth antes do Pix: {ex.Message}";
                await _context.SaveChangesAsync();
                await LogTelemetryAsync(tx.Id, "Error", $"Falha ao validar o dono do token OAuth em /users/me: {ex.Message}");
                return BadRequest(new { message = "Não foi possível validar o token OAuth salvo no Mercado Pago antes de gerar o Pix.", transactionId = tx.Id });
            }

            if (!string.IsNullOrWhiteSpace(integration.MercadoPagoUserId) && tokenOwner.UserId != integration.MercadoPagoUserId)
            {
                tx.Status = "Failed";
                tx.RawResponse = JsonSerializer.Serialize(new
                {
                    status = "failed",
                    message = "O token OAuth usado na cobrança pertence a outra conta Mercado Pago.",
                    expectedUserId = integration.MercadoPagoUserId,
                    actualUserId = tokenOwner.UserId
                });
                integration.LastTokenValidationAt = DateTime.UtcNow;
                integration.LastTokenValidationStatus = $"Token OAuth divergente. Esperado: {integration.MercadoPagoUserId}. Atual: {tokenOwner.UserId}.";
                await _context.SaveChangesAsync();
                await LogTelemetryAsync(tx.Id, "Error", $"Token OAuth divergente. Integração esperava seller {integration.MercadoPagoUserId}, mas /users/me retornou {tokenOwner.UserId}.");
                return BadRequest(new { message = "O token OAuth salvo na integração não pertence à conta Mercado Pago esperada para esta empresa.", transactionId = tx.Id });
            }

            integration.MercadoPagoUserId = tokenOwner.UserId;
            integration.MercadoPagoNickname = tokenOwner.Nickname;
            integration.MercadoPagoSiteId = tokenOwner.SiteId;
            integration.LastTokenValidationAt = DateTime.UtcNow;
            integration.LastTokenValidationStatus = "Token OAuth validado em /users/me antes da criação do Pix.";
            await _context.SaveChangesAsync();

            // Determina os dados do comprador a partir da integração ativa se o frontend não enviá-los
            string payerEmail = !string.IsNullOrWhiteSpace(request.PayerEmail) 
                ? request.PayerEmail 
                : "comprador@vendmachine.com.br";
            
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

            string? docType = requestDocType;
            string? docNumber = requestDocNumber;

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
            var notificationUrl = GetMercadoPagoNotificationUrl();
            if (tx.ApplicationFee > 0)
            {
                payload = new
                {
                    transaction_amount = tx.Amount,
                    description = string.IsNullOrWhiteSpace(request.Description) ? $"Venda maquina {machine.Name}" : request.Description,
                    payment_method_id = "pix",
                    payer = payerPayload,
                    application_fee = tx.ApplicationFee,
                    notification_url = notificationUrl,
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
                    notification_url = notificationUrl,
                    external_reference = tx.Id.ToString()
                };
            }

            var mpResponse = await httpClient.PostAsJsonAsync("https://api.mercadopago.com/v1/payments", payload);
            var responseStr = await mpResponse.Content.ReadAsStringAsync();

            tx.RawResponse = responseStr;

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
                    status = tx.Status,
                    applicationFee = tx.ApplicationFee,
                    applicationFeeApplied
                });
            }
            else
            {
                tx.Status = "Failed";
                await _context.SaveChangesAsync();
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
                tx.Status = "Failed";
                if (string.IsNullOrWhiteSpace(tx.RawResponse))
                {
                    tx.RawResponse = JsonSerializer.Serialize(new { status = "failed", message = ex.Message });
                }
                await _context.SaveChangesAsync();
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
                applicationFee = tx.ApplicationFee,
                applicationFeeApplied = tx.ApplicationFee > 0,
                simulated = true
            });
        }
    }

    [AllowAnonymous]
    [HttpPost("/webhooks/mercadopago")]
    public IActionResult WebhookMercadoPago([FromBody] JsonElement webhookBody)
    {
        // ── 1. CAPTURA DE CONTEXTO ──────────────────────────────────────────────
        string xSignature  = Request.Headers["x-signature"].ToString();
        string xRequestId  = Request.Headers["x-request-id"].ToString();
        string userAgent   = Request.Headers["User-Agent"].ToString();
        string queryString = Request.QueryString.ToString();

        Console.WriteLine($"[WEBHOOK] Notificação recebida. UA: {userAgent}. Query: {queryString}. x-signature: {(string.IsNullOrEmpty(xSignature) ? "(ausente)" : xSignature[..Math.Min(40, xSignature.Length)] + "...")}");

        // ── 2. VALIDAÇÃO DA ASSINATURA SECRETA (x-signature) ───────────────────
        // Algoritmo: HMAC-SHA256 sobre "id:<dataId>;request-id:<xRequestId>;ts:<ts>;"
        // Documentação: https://www.mercadopago.com.br/developers/pt/docs/your-integrations/notifications/webhooks
        string webhookSecret = _configuration["MercadoPago:WebhookSecret"] ?? "";
        bool signatureConfigured = !string.IsNullOrWhiteSpace(webhookSecret)
                                   && !webhookSecret.StartsWith("COLE_AQUI");

        bool signatureValid = false;
        string signatureLog = "";

        if (signatureConfigured && !string.IsNullOrEmpty(xSignature))
        {
            try
            {
                // Extrair ts e v1 do header: "ts=1704908010,v1=618c85..."
                var sigParts = xSignature.Split(',')
                    .Select(p => p.Split('=', 2))
                    .Where(p => p.Length == 2)
                    .ToDictionary(p => p[0].Trim(), p => p[1].Trim());

                string ts = sigParts.GetValueOrDefault("ts", "");
                string v1 = sigParts.GetValueOrDefault("v1", "");

                // Obter o dataId para construir o manifesto (query string tem prioridade)
                string dataId = Request.Query["data.id"].FirstOrDefault()
                             ?? Request.Query["id"].FirstOrDefault()
                             ?? "";

                // Construir manifesto — omitir campos vazios conforme documentação
                var manifestParts = new List<string>();
                if (!string.IsNullOrEmpty(dataId))    manifestParts.Add($"id:{dataId}");
                if (!string.IsNullOrEmpty(xRequestId)) manifestParts.Add($"request-id:{xRequestId}");
                if (!string.IsNullOrEmpty(ts))         manifestParts.Add($"ts:{ts}");
                string manifest = string.Join(";", manifestParts) + ";";

                // Computar HMAC-SHA256
                using var hmac = new System.Security.Cryptography.HMACSHA256(
                    System.Text.Encoding.UTF8.GetBytes(webhookSecret));
                byte[] hashBytes = hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(manifest));
                string computedHash = BitConverter.ToString(hashBytes).Replace("-", "").ToLower();

                // Comparação em tempo constante (evita timing attack)
                signatureValid = v1.Length == computedHash.Length &&
                    System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                        System.Text.Encoding.UTF8.GetBytes(v1),
                        System.Text.Encoding.UTF8.GetBytes(computedHash));

                signatureLog = signatureValid
                    ? $"Assinatura x-signature VÁLIDA. Manifesto: '{manifest}'. ts={ts}."
                    : $"Assinatura x-signature INVÁLIDA. Manifesto: '{manifest}'. v1 recebido: '{v1}'. Hash calculado: '{computedHash}'.";

                Console.WriteLine($"[WEBHOOK] {signatureLog}");
            }
            catch (Exception ex)
            {
                signatureLog = $"Erro ao validar x-signature: {ex.Message}. Header recebido: '{xSignature}'.";
                Console.Error.WriteLine($"[WEBHOOK] {signatureLog}");
                signatureValid = false;
            }

            if (!signatureValid)
            {
                Console.Error.WriteLine("[WEBHOOK] Requisição rejeitada: assinatura inválida.");
                return Unauthorized(new { message = "Assinatura do webhook inválida." });
            }
        }
        else if (signatureConfigured && string.IsNullOrEmpty(xSignature))
        {
            // Secret configurado mas MP não enviou o header — possível IPN legado
            signatureLog = "Assinatura secreta configurada, mas header x-signature ausente na notificação (possível IPN legado). Processando sem validação.";
            Console.WriteLine($"[WEBHOOK] {signatureLog}");
        }
        else
        {
            // Secret não configurado — modo sem validação (aviso)
            signatureLog = "AVISO: Assinatura secreta do webhook não configurada (MercadoPago:WebhookSecret). Validação de autenticidade desativada.";
            Console.WriteLine($"[WEBHOOK] {signatureLog}");
        }

        // Responder 200 OK rapidamente ao Mercado Pago
        string? paymentId = null;

        try
        {
            // ─────────────────────────────────────────────────────────────────
            // FORMATO 1 — Webhook API novo (User-Agent: MercadoPago WebHook v1.0)
            // Body: {"action":"payment.updated","type":"payment","data":{"id":"123"}, ...}
            // URL:  ?data.id=123&type=payment
            // ─────────────────────────────────────────────────────────────────
            string webhookFormat;
            if (webhookBody.TryGetProperty("type", out var typeProp) && typeProp.GetString() == "payment")
            {
                webhookFormat = "WebHook v1.0 (novo)";
                if (webhookBody.TryGetProperty("data", out var dataProp) && dataProp.TryGetProperty("id", out var idProp))
                {
                    paymentId = idProp.ValueKind == JsonValueKind.Number
                        ? idProp.GetInt64().ToString()
                        : (idProp.GetString() ?? "");
                    Console.WriteLine($"[WEBHOOK] Payment ID extraído do body (formato novo): {paymentId}");
                }
            }
            // ─────────────────────────────────────────────────────────────────
            // FORMATO 2 — IPN antigo (User-Agent: MercadoPago Feed v2.0)
            // Body: {"id":"123","topic":"payment"}
            // URL:  ?id=123&topic=payment
            // ─────────────────────────────────────────────────────────────────
            else if (webhookBody.TryGetProperty("topic", out var topicProp) && topicProp.GetString() == "payment")
            {
                webhookFormat = "IPN Feed v2.0 (antigo)";
                if (webhookBody.TryGetProperty("id", out var idProp))
                {
                    paymentId = idProp.ValueKind == JsonValueKind.Number
                        ? idProp.GetInt64().ToString()
                        : (idProp.GetString() ?? "");
                    Console.WriteLine($"[WEBHOOK] Payment ID extraído do body (formato IPN antigo): {paymentId}");
                }
            }
            else
            {
                webhookFormat = "desconhecido";
            }

            // ─────────────────────────────────────────────────────────────────
            // FALLBACK — Extrair da query string se body não contiver o ID
            // Cobre: ?data.id=xxx&type=payment  e  ?id=xxx&topic=payment
            // ─────────────────────────────────────────────────────────────────
            if (string.IsNullOrEmpty(paymentId))
            {
                var qsId = Request.Query["data.id"].FirstOrDefault()
                        ?? Request.Query["id"].FirstOrDefault();
                var qsType = Request.Query["type"].FirstOrDefault()
                          ?? Request.Query["topic"].FirstOrDefault();

                if (!string.IsNullOrEmpty(qsId) && qsType == "payment")
                {
                    paymentId = qsId;
                    Console.WriteLine($"[WEBHOOK] Payment ID extraído da query string (fallback): {paymentId}");
                }
            }

            if (!string.IsNullOrEmpty(paymentId))
            {
                Console.WriteLine($"[WEBHOOK] Iniciando processamento do pagamento ID: {paymentId}");
                var capturedFormat    = webhookFormat;
                var capturedQuery     = queryString;
                var capturedUserAgent = userAgent;
                var capturedSigLog    = signatureLog;
                var capturedSigValid  = signatureValid;
                var capturedSigConf   = signatureConfigured;
                _ = Task.Run(() => ProcessRealWebhookAsync(
                    paymentId, capturedFormat, capturedQuery, capturedUserAgent,
                    capturedSigLog, capturedSigValid, capturedSigConf));
            }
            else
            {
                var tipoLog = webhookBody.TryGetProperty("type", out var t) ? t.GetString()
                            : webhookBody.TryGetProperty("topic", out var top) ? top.GetString()
                            : Request.Query["type"].FirstOrDefault() ?? Request.Query["topic"].FirstOrDefault() ?? "(sem tipo)";
                Console.WriteLine($"[WEBHOOK] Notificação ignorada — tipo: {tipoLog}");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WEBHOOK] Erro ao processar body da notificação: {ex.Message}");

            // Fallback de emergência: tentar extrair da query string mesmo com erro no body
            var fallbackId   = Request.Query["data.id"].FirstOrDefault() ?? Request.Query["id"].FirstOrDefault();
            var fallbackType = Request.Query["type"].FirstOrDefault() ?? Request.Query["topic"].FirstOrDefault();
            if (!string.IsNullOrEmpty(fallbackId) && fallbackType == "payment")
            {
                Console.WriteLine($"[WEBHOOK] Fallback de emergência: processando PaymentId {fallbackId} da query string.");
                _ = Task.Run(() => ProcessRealWebhookAsync(
                    fallbackId, "fallback-querystring", queryString, userAgent,
                    signatureLog, signatureValid, signatureConfigured));
            }
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

        if (request.SendTelemetryToMachine)
        {
            await _sessionOrchestrator.HandlePaymentResultAsync(tx.Id, request.Approved, "simulated_webhook", HttpContext.RequestAborted);
            return Ok(new { message = "Status atualizado e resultado encaminhado ao orquestrador da sessão.", transaction = tx });
        }

        await LogTelemetryAsync(tx.Id, "Info", "Simulador configurado para não enviar retorno MDB para a máquina.");
        return Ok(new { message = "Status atualizado. Retorno MDB para a máquina desativado por configuração.", transaction = tx });
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

    private async Task ProcessRealWebhookAsync(
        string paymentId,
        string webhookFormat = "desconhecido",
        string queryString = "",
        string userAgent = "",
        string signatureLog = "",
        bool signatureValid = false,
        bool signatureConfigured = false)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Console.WriteLine($"[WEBHOOK] Buscando transação para PaymentId: {paymentId} (formato: {webhookFormat})");

        // Retry até 10s para cobrir race condition:
        // O webhook pode chegar antes do SaveChanges do endpoint pix-qr salvar o MercadoPagoPaymentId.
        PaymentTransaction? tx = null;
        int attemptsUsed = 0;
        for (int attempt = 1; attempt <= 5; attempt++)
        {
            tx = await db.PaymentTransactions
                .Include(t => t.Machine)
                .FirstOrDefaultAsync(t => t.MercadoPagoPaymentId == paymentId);

            if (tx != null) break;

            Console.WriteLine($"[WEBHOOK] Transação não encontrada (tentativa {attempt}/5). Aguardando 2s...");
            await Task.Delay(2000);

            // Recarregar o contexto para pegar dados frescos do banco
            db.ChangeTracker.Clear();
        }

        if (tx == null)
        {
            Console.Error.WriteLine($"[WEBHOOK] Transação não encontrada para PaymentId '{paymentId}' após 5 tentativas (10s). Abortando.");
            return;
        }

        if (tx.Status is "Approved" or "RefundPending" or "Refunded")
        {
            Console.WriteLine($"[WEBHOOK] Transação {tx.Id} já possui status '{tx.Status}'. Ignorando webhook duplicado.");
            await LogTelemetryAsync(tx.Id, "Info", $"Webhook duplicado ignorado (PaymentId: {paymentId}). Transação já possui status '{tx.Status}'.");
            return;
        }

        // ── Primeiro log no banco: confirma recebimento e localização da transação ──
        var receiptMsg = attemptsUsed == 1
            ? $"Webhook recebido do Mercado Pago e transação localizada. Formato: {webhookFormat}. PaymentId MP: {paymentId}. Query: {queryString}. User-Agent: {userAgent}."
            : $"Webhook recebido do Mercado Pago. Transação localizada após {attemptsUsed} tentativas (race condition detectada). Formato: {webhookFormat}. PaymentId MP: {paymentId}.";
        await LogTelemetryAsync(tx.Id, "Webhook", receiptMsg);

        // ── Log de validação da assinatura secreta ──
        if (signatureConfigured)
        {
            string sigLogType = signatureValid ? "Info" : "Error";
            await LogTelemetryAsync(tx.Id, sigLogType,
                signatureValid
                    ? $"✅ Assinatura x-signature VÁLIDA. Autenticidade confirmada. Detalhe: {signatureLog}"
                    : $"❌ {signatureLog}");
        }
        else if (!string.IsNullOrEmpty(signatureLog))
        {
            await LogTelemetryAsync(tx.Id, "Info", signatureLog);
        }

        Console.WriteLine($"[WEBHOOK] Transação {tx.Id} encontrada. Consultando status no Mercado Pago...");
        await LogTelemetryAsync(tx.Id, "Webhook", $"Consultando status do pagamento {paymentId} na API do Mercado Pago...");

        var integration = await db.MercadoPagoIntegrations
            .FirstOrDefaultAsync(i => i.CompanyId == tx.CompanyId && i.IsActive);

        if (integration == null)
        {
            Console.Error.WriteLine($"[WEBHOOK] Integração ativa não encontrada para empresa {tx.CompanyId}.");
            await LogTelemetryAsync(tx.Id, "Error", "Webhook recebido, mas nenhuma integração Mercado Pago ativa foi encontrada para a empresa.");
            return;
        }

        try
        {
            var decryptedToken = await EnsureValidAccessTokenAsync(integration, db);
            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", decryptedToken);

            var mpResponse = await httpClient.GetAsync($"https://api.mercadopago.com/v1/payments/{paymentId}");
            var responseStr = await mpResponse.Content.ReadAsStringAsync();

            Console.WriteLine($"[WEBHOOK] Resposta do MP para PaymentId {paymentId}: HTTP {(int)mpResponse.StatusCode} — {responseStr[..Math.Min(200, responseStr.Length)]}");

            if (mpResponse.IsSuccessStatusCode)
            {
                using var jsonDoc = JsonDocument.Parse(responseStr);
                var root = jsonDoc.RootElement;

                string status = root.GetProperty("status").GetString() ?? "pending";
                tx.MercadoPagoStatus = status;
                tx.MercadoPagoStatusDetail = root.TryGetProperty("status_detail", out var detailProp) ? detailProp.GetString() : null;
                tx.RawResponse = responseStr;

                bool approved = status == "approved";
                bool rejected = status == "rejected" || status == "cancelled";

                if (approved)
                {
                    tx.Status = "Approved";
                    tx.CompletedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync();
                    Console.WriteLine($"[WEBHOOK] Pagamento {paymentId} APROVADO. Transação {tx.Id} atualizada.");
                    await LogTelemetryAsync(tx.Id, "Info", "Webhook real confirmado: Pagamento APROVADO pelo Mercado Pago.");
                    if (tx.SendTelemetryToMachine)
                    {
                        var orchestrator = scope.ServiceProvider.GetRequiredService<SessionOrchestrator>();
                        await orchestrator.HandlePaymentResultAsync(tx.Id, true, "mercado_pago_webhook", CancellationToken.None);
                    }
                    else
                    {
                        await LogTelemetryAsync(tx.Id, "Info", "Transação configurada pelo Simulador Pix para não enviar retorno MDB para a máquina.");
                    }
                }
                else if (rejected)
                {
                    tx.Status = "Rejected";
                    tx.CompletedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync();
                    Console.WriteLine($"[WEBHOOK] Pagamento {paymentId} RECUSADO/CANCELADO. Transação {tx.Id} atualizada.");
                    await LogTelemetryAsync(tx.Id, "Info", $"Webhook real confirmado: Pagamento RECUSADO/CANCELADO pelo Mercado Pago (status: {status}).");
                    if (tx.SendTelemetryToMachine)
                    {
                        var orchestrator = scope.ServiceProvider.GetRequiredService<SessionOrchestrator>();
                        await orchestrator.HandlePaymentResultAsync(tx.Id, false, $"mercado_pago:{status}", CancellationToken.None);
                    }
                    else
                    {
                        await LogTelemetryAsync(tx.Id, "Info", "Transação configurada pelo Simulador Pix para não enviar retorno MDB para a máquina.");
                    }
                }
                else
                {
                    // Status intermediário (ex: pending, in_process) — não atualizar ainda
                    await db.SaveChangesAsync();
                    Console.WriteLine($"[WEBHOOK] Pagamento {paymentId} com status intermediário: '{status}'. Aguardando próxima notificação.");
                    await LogTelemetryAsync(tx.Id, "Info", $"Webhook recebido do Mercado Pago com status intermediário: '{status}'. Aguardando confirmação final.");
                }
            }
            else
            {
                Console.Error.WriteLine($"[WEBHOOK] Falha ao consultar PaymentId {paymentId} na API do MP: HTTP {(int)mpResponse.StatusCode}");
                await LogTelemetryAsync(tx.Id, "Error", $"Webhook recebido, mas falha ao consultar o pagamento {paymentId} no Mercado Pago: HTTP {(int)mpResponse.StatusCode}. Resposta: {responseStr[..Math.Min(300, responseStr.Length)]}");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WEBHOOK] Erro ao processar webhook para PaymentId {paymentId}: {ex.Message}");
            try
            {
                await LogTelemetryAsync(tx.Id, "Error", $"Erro interno ao processar webhook do Mercado Pago: {ex.Message}");
            }
            catch { }
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
            var decryptedToken = await EnsureValidAccessTokenAsync(integration, db);
            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", decryptedToken);

            httpClient.DefaultRequestHeaders.Add("X-Idempotency-Key", $"refund-{tx.Id}");

            var body = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
            var response = await httpClient.PostAsync(
                $"https://api.mercadopago.com/v1/payments/{tx.MercadoPagoPaymentId}/refunds", body);

            var responseBody = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                Console.WriteLine($"[REFUND] Estorno aprovado pelo MP. PaymentId: {tx.MercadoPagoPaymentId}.");
                await LogTelemetryAsync(tx.Id, "Info",
                    $"Estorno processado com sucesso na API do Mercado Pago. PaymentId: {tx.MercadoPagoPaymentId}.");
                return true;
            }
            else
            {
                Console.Error.WriteLine($"[REFUND] Falha no estorno MP: HTTP {(int)response.StatusCode} — {responseBody}");
                await LogTelemetryAsync(tx.Id, "Error",
                    $"Falha ao solicitar estorno na API do Mercado Pago: HTTP {(int)response.StatusCode}. " +
                    $"PaymentId: {tx.MercadoPagoPaymentId}. " +
                    $"Resposta: {responseBody[..Math.Min(400, responseBody.Length)]}");
                return false;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[REFUND] Erro HTTP ao reembolsar: {ex.Message}");
            await LogTelemetryAsync(tx.Id, "Error",
                $"Erro interno ao tentar estorno no Mercado Pago: {ex.Message}");
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

    private static string GetTokenFingerprint(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return "empty";
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(hash)[..12];
    }

    private async Task<MercadoPagoUserProfile> LogMercadoPagoTokenOwnerAsync(HttpClient httpClient, Guid transactionId)
    {
        var profile = await GetMercadoPagoUserProfileAsync(httpClient);
        await LogTelemetryAsync(transactionId, "Info", $"Token OAuth validado no Mercado Pago. Seller/User ID: {profile.UserId}. Nickname: {profile.Nickname}. Site: {profile.SiteId}.");
        return profile;
    }

    private async Task<string> EnsureValidAccessTokenAsync(MercadoPagoIntegration integration, AppDbContext? dbContext = null)
    {
        var db = dbContext ?? _context;
        var decryptedToken = EncryptionService.Decrypt(integration.AccessToken);
        var expiresAt = integration.AccessTokenExpiresAt;

        if (!expiresAt.HasValue || expiresAt.Value > DateTime.UtcNow.AddMinutes(5))
        {
            return decryptedToken;
        }

        var refreshToken = EncryptionService.Decrypt(integration.RefreshToken);
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return decryptedToken;
        }

        var clientId = _configuration["MercadoPago:ClientId"] ?? integration.ClientId;
        var clientSecret = _configuration["MercadoPago:ClientSecret"] ?? EncryptionService.Decrypt(integration.ClientSecret);

        using var httpClient = new HttpClient();
        var parameters = new Dictionary<string, string>
        {
            { "client_secret", clientSecret },
            { "client_id", clientId },
            { "grant_type", "refresh_token" },
            { "refresh_token", refreshToken }
        };

        var response = await httpClient.PostAsync("https://api.mercadopago.com/oauth/token", new FormUrlEncodedContent(parameters));
        var responseStr = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            integration.LastTokenValidationAt = DateTime.UtcNow;
            integration.LastTokenValidationStatus = $"Falha ao renovar token OAuth: {responseStr}";
            await db.SaveChangesAsync();
            return decryptedToken;
        }

        using var doc = JsonDocument.Parse(responseStr);
        var root = doc.RootElement;
        var newAccessToken = root.GetProperty("access_token").GetString() ?? "";
        var newRefreshToken = root.TryGetProperty("refresh_token", out var refreshProp) ? refreshProp.GetString() ?? refreshToken : refreshToken;
        var expiresIn = root.TryGetProperty("expires_in", out var expiresProp) && expiresProp.TryGetInt32(out var seconds) ? seconds : 21600;

        integration.AccessToken = EncryptionService.Encrypt(newAccessToken);
        integration.RefreshToken = EncryptionService.Encrypt(newRefreshToken);
        integration.AccessTokenExpiresAt = DateTime.UtcNow.AddSeconds(expiresIn);
        integration.UpdatedAt = DateTime.UtcNow;

        using var profileHttp = new HttpClient();
        profileHttp.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", newAccessToken);
        var profile = await GetMercadoPagoUserProfileAsync(profileHttp);
        integration.MercadoPagoUserId = profile.UserId;
        integration.MercadoPagoNickname = profile.Nickname;
        integration.MercadoPagoSiteId = profile.SiteId;
        integration.LastTokenValidationAt = DateTime.UtcNow;
        integration.LastTokenValidationStatus = "Token OAuth renovado e validado em /users/me.";

        await db.SaveChangesAsync();
        return newAccessToken;
    }

    private async Task<MercadoPagoUserProfile> GetMercadoPagoUserProfileAsync(string accessToken)
    {
        using var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        return await GetMercadoPagoUserProfileAsync(httpClient);
    }

    private static async Task<MercadoPagoUserProfile> GetMercadoPagoUserProfileAsync(HttpClient httpClient)
    {
        var response = await httpClient.GetAsync("https://api.mercadopago.com/users/me");
        var responseStr = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Não foi possível validar o token OAuth em /users/me. Status: {(int)response.StatusCode}. Resposta: {responseStr}");
        }

        using var doc = JsonDocument.Parse(responseStr);
        var root = doc.RootElement;
        var userId = root.TryGetProperty("id", out var idProp) ? idProp.GetRawText().Trim('"') : "";
        var nickname = root.TryGetProperty("nickname", out var nickProp) ? nickProp.GetString() ?? "" : "";
        var siteId = root.TryGetProperty("site_id", out var siteProp) ? siteProp.GetString() ?? "" : "";
        return new MercadoPagoUserProfile(userId, nickname, siteId);
    }

    private string CreateOAuthState(Guid companyId)
    {
        var payload = JsonSerializer.Serialize(new OAuthStatePayload
        {
            CompanyId = companyId,
            Nonce = Guid.NewGuid().ToString("N"),
            IssuedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        });
        var encodedPayload = Base64UrlEncode(Encoding.UTF8.GetBytes(payload));
        var signature = SignOAuthState(encodedPayload);
        return $"{encodedPayload}.{signature}";
    }

    private bool ValidateOAuthState(string? state, Guid expectedCompanyId, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(state))
        {
            error = "State OAuth ausente. Inicie a conexão novamente pela tela de integrações.";
            return false;
        }

        var parts = state.Split('.', 2);
        if (parts.Length != 2 || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(SignOAuthState(parts[0])), Encoding.UTF8.GetBytes(parts[1])))
        {
            error = "State OAuth inválido. Inicie a conexão novamente pela tela de integrações.";
            return false;
        }

        try
        {
            var json = Encoding.UTF8.GetString(Base64UrlDecode(parts[0]));
            var payload = JsonSerializer.Deserialize<OAuthStatePayload>(json);
            if (payload == null || payload.CompanyId != expectedCompanyId)
            {
                error = "State OAuth não pertence à empresa logada.";
                return false;
            }

            var issuedAt = DateTimeOffset.FromUnixTimeSeconds(payload.IssuedAt);
            if (issuedAt < DateTimeOffset.UtcNow.AddMinutes(-30))
            {
                error = "State OAuth expirado. Inicie a conexão novamente pela tela de integrações.";
                return false;
            }

            return true;
        }
        catch
        {
            error = "State OAuth malformado. Inicie a conexão novamente pela tela de integrações.";
            return false;
        }
    }

    private string SignOAuthState(string encodedPayload)
    {
        var key = Encoding.UTF8.GetBytes(_configuration["Jwt:Key"] ?? "SuperSecretKeyForVendingMachinesManager2026!");
        using var hmac = new HMACSHA256(key);
        return Base64UrlEncode(hmac.ComputeHash(Encoding.UTF8.GetBytes(encodedPayload)));
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
        return Convert.FromBase64String(padded);
    }

    private string GetMercadoPagoNotificationUrl()
    {
        var configuredUrl = _configuration["MercadoPago:WebhookUrl"];
        if (!string.IsNullOrWhiteSpace(configuredUrl))
        {
            return configuredUrl;
        }

        var scheme = Request.Headers["X-Forwarded-Proto"].FirstOrDefault() ?? Request.Scheme;
        var host = Request.Headers["X-Forwarded-Host"].FirstOrDefault() ?? Request.Host.Value;
        return $"{scheme}://{host}/webhooks/mercadopago";
    }

    private static string CleanDocument(string document)
    {
        return document.Replace(".", "").Replace("-", "").Replace("/", "").Replace(" ", "");
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

public record MercadoPagoUserProfile(string UserId, string Nickname, string SiteId);

public class OAuthStatePayload
{
    public Guid CompanyId { get; set; }
    public string Nonce { get; set; } = string.Empty;
    public long IssuedAt { get; set; }
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
    public bool SendTelemetryToMachine { get; set; } = true;
}

public class SimulateWebhookRequest
{
    public Guid TransactionId { get; set; }
    public bool Approved { get; set; }
    public bool SendTelemetryToMachine { get; set; } = true;
}

public class OAuthCallbackRequest
{
    public string Code { get; set; } = string.Empty;
    public string RedirectUri { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
}
