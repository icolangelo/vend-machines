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
using Npgsql;
using VendingMachines.Api.Data;
using VendingMachines.Api.Models;
using VendingMachines.Api.Services;

namespace VendingMachines.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class PaymentsController : BaseApiController
{
    private const string ManualChargebackReimbursementConfirmedReason = "manual_chargeback_reimbursement_confirmed";

    private readonly AppDbContext _context;
    private readonly SessionOrchestrator _sessionOrchestrator;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly IWebHostEnvironment _environment;
    private readonly IAuditLogService? _audit;
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly WebhookInboxAdmissionService _webhookInboxAdmission;
    private readonly LegacyTelemetryRequestRelay? _legacyTelemetryRelay;

    public PaymentsController(
        AppDbContext context,
        SessionOrchestrator sessionOrchestrator,
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        IWebHostEnvironment environment,
        IAuditLogService? audit = null,
        IHttpClientFactory? httpClientFactory = null,
        WebhookInboxAdmissionService? webhookInboxAdmission = null,
        LegacyTelemetryRequestRelay? legacyTelemetryRelay = null)
    {
        _context = context;
        _sessionOrchestrator = sessionOrchestrator;
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _environment = environment;
        _audit = audit;
        _httpClientFactory = httpClientFactory;
        _legacyTelemetryRelay = legacyTelemetryRelay;
        _webhookInboxAdmission = webhookInboxAdmission ?? new WebhookInboxAdmissionService(
            context,
            configuration,
            WebhookInboxAdmissionGate.Shared);
    }

    // ==========================================
    // 1. SETTINGS GLOBAIS (Admin do Sistema)
    // ==========================================

    [HttpGet("global-settings")]
    [Authorize(Roles = AppRoles.PlatformAdmin)]
    public async Task<IActionResult> GetGlobalSettings()
    {
        if (!User.IsInRole(AppRoles.PlatformAdmin))
        {
            return StatusCode(403, new { message = "Apenas o administrador do sistema pode consultar a taxa global." });
        }

        var settingsRows = await _context.SystemSettings
            .OrderBy(settings => settings.UpdatedAt)
            .ThenBy(settings => settings.Id)
            .Take(2)
            .ToListAsync();
        if (settingsRows.Count > 1)
            return Conflict(new { message = "Há mais de uma configuração global de taxa; solicite a revisão dos dados antes de alterar o valor." });

        if (settingsRows.Count == 0)
        {
            return Ok(new
            {
                ApplicationFeePercent = 5.0m,
                IsConfigured = false,
                IsValid = false
            });
        }

        var settings = settingsRows[0];
        return Ok(new
        {
            settings.ApplicationFeePercent,
            IsConfigured = true,
            IsValid = settings.ApplicationFeePercent is > 0m and < 100m
        });
    }

    [HttpPost("global-settings")]
    [Authorize(Roles = AppRoles.PlatformAdmin)]
    public async Task<IActionResult> UpdateGlobalSettings([FromBody] SystemSettings request)
    {
        if (!User.IsInRole(AppRoles.PlatformAdmin))
        {
            return StatusCode(403, new { message = "Apenas o administrador do sistema pode alterar a taxa global." });
        }

        if (request.ApplicationFeePercent is <= 0m or >= 100m ||
            decimal.Round(request.ApplicationFeePercent, 2) != request.ApplicationFeePercent)
        {
            return BadRequest(new
            {
                message = "A taxa da plataforma deve estar entre 0,01% e 99,99%, com no máximo duas casas decimais."
            });
        }

        await using var transaction = await _context.Database.BeginTransactionAsync();
        var settingsRows = _context.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL"
            ? await _context.SystemSettings
                .FromSqlRaw("SELECT * FROM \"SystemSettings\" ORDER BY \"Id\" FOR UPDATE")
                .ToListAsync()
            : await _context.SystemSettings
                .OrderBy(settings => settings.UpdatedAt)
                .ThenBy(settings => settings.Id)
                .Take(2)
                .ToListAsync();
        if (settingsRows.Count > 1)
            return Conflict(new { message = "Há mais de uma configuração global de taxa; solicite a revisão dos dados antes de alterar o valor." });

        var settings = settingsRows.SingleOrDefault();
        var expectedFeeHeader = Request.Headers["X-Expected-Platform-Fee"];
        if (expectedFeeHeader.Count > 0)
        {
            var expected = expectedFeeHeader.ToString();
            var matches = expected == "missing"
                ? settings == null
                : decimal.TryParse(expected, System.Globalization.NumberStyles.AllowDecimalPoint | System.Globalization.NumberStyles.AllowLeadingSign,
                    System.Globalization.CultureInfo.InvariantCulture, out var expectedFee) &&
                    settings != null && settings.ApplicationFeePercent == expectedFee;
            if (!matches)
                return Conflict(new { message = "A taxa global foi alterada. Atualize e confira o valor antes de salvar novamente." });
        }
        var previousApplicationFeePercent = settings?.ApplicationFeePercent;
        if (settings == null)
        {
            settings = new SystemSettings { Id = Guid.NewGuid() };
            _context.SystemSettings.Add(settings);
        }
        else if (settings.ApplicationFeePercent == request.ApplicationFeePercent)
        {
            await transaction.CommitAsync();
            return Ok(new { message = "A taxa da plataforma já está configurada com esse valor.", settings });
        }

        settings.ApplicationFeePercent = request.ApplicationFeePercent;
        settings.UpdatedAt = DateTime.UtcNow;

        try
        {
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateException error) when (previousApplicationFeePercent == null &&
            error.InnerException is PostgresException postgres &&
            postgres.SqlState == PostgresErrorCodes.UniqueViolation &&
            postgres.ConstraintName == "IX_SystemSettings_IsSingleton")
        {
            await transaction.RollbackAsync();
            _context.Entry(settings).State = EntityState.Detached;
            return Conflict(new { message = "A taxa global foi alterada. Atualize e confira o valor antes de salvar novamente." });
        }
        if (_audit is not null)
        {
            await _audit.RecordRequiredAsync(
                "payments.platform_fee_updated",
                "SystemSettings",
                settings.Id.ToString(),
                metadata: new
                {
                    previousApplicationFeePercent,
                    newApplicationFeePercent = settings.ApplicationFeePercent
                });
        }

        await transaction.CommitAsync();

        return Ok(new { message = "Taxa da plataforma atualizada com sucesso.", settings });
    }

    // ==========================================
    // 2. INTEGRAÇÃO DA EMPRESA (Dono/Sócio)
    // ==========================================

    [HttpGet("integration")]
    [Authorize(Roles = AppRoles.CompanyAdminOrPlatformAdmin)]
    public async Task<IActionResult> GetIntegration()
    {
        if (!TryGetEffectiveCompanyId(out var companyId))
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
    [Authorize(Roles = AppRoles.CompanyAdminOrPlatformAdmin)]
    public IActionResult GetOauthConfig()
    {
        if (!TryGetEffectiveCompanyId(out var companyId))
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
    [Authorize(Roles = AppRoles.CompanyAdminOrPlatformAdmin)]
    public async Task<IActionResult> OAuthCallback([FromBody] OAuthCallbackRequest request)
    {
        if (!TryGetEffectiveCompanyId(out var companyId))
        {
            return BadRequest(new { message = "O usuário não está associado a nenhuma empresa." });
        }

        var role = User.FindFirst(ClaimTypes.Role)?.Value;
        if (role is not (AppRoles.Admin or AppRoles.PlatformAdmin))
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
                using var httpClient = CreateMercadoPagoHttpClient();
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
                    return BadRequest(new { message = $"Não foi possível concluir a conexão com o Mercado Pago (HTTP {(int)response.StatusCode})." });
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
            catch (Exception)
            {
                return BadRequest(new { message = "Não foi possível concluir a conexão com o Mercado Pago. Verifique as credenciais e tente novamente." });
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
    [Authorize(Roles = AppRoles.CompanyAdminOrPlatformAdmin)]
    public async Task<IActionResult> DisconnectIntegration()
    {
        if (!TryGetEffectiveCompanyId(out var companyId))
        {
            return BadRequest(new { message = "O usuário não está associado a nenhuma empresa." });
        }

        var role = User.FindFirst(ClaimTypes.Role)?.Value;
        if (role is not (AppRoles.Admin or AppRoles.PlatformAdmin))
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
    [Authorize(Roles = AppRoles.CompanyAdminOrPlatformAdmin)]
    public async Task<IActionResult> ToggleIntegration([FromBody] ToggleIntegrationRequest request)
    {
        if (!TryGetEffectiveCompanyId(out var companyId))
        {
            return BadRequest(new { message = "O usuário não está associado a nenhuma empresa." });
        }

        var role = User.FindFirst(ClaimTypes.Role)?.Value;
        if (role is not (AppRoles.Admin or AppRoles.PlatformAdmin))
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
            HasAccessToken = !string.IsNullOrWhiteSpace(integration.AccessToken),
            HasRefreshToken = !string.IsNullOrWhiteSpace(integration.RefreshToken),
            HasClientSecret = !string.IsNullOrWhiteSpace(integration.ClientSecret),
            AccessTokenExpiresAt = integration.AccessTokenExpiresAt,
            MercadoPagoUserId = integration.MercadoPagoUserId,
            MercadoPagoNickname = integration.MercadoPagoNickname,
            MercadoPagoSiteId = integration.MercadoPagoSiteId,
            LastTokenValidationAt = integration.LastTokenValidationAt,
            LastTokenValidationStatus = SanitizeTokenValidationStatus(integration.LastTokenValidationStatus),
            IsActive = integration.IsActive,
            CreatedAt = integration.CreatedAt,
            UpdatedAt = integration.UpdatedAt,
            AccessToken = string.Empty,
            ClientSecret = string.Empty
        };
    }

    public class ToggleIntegrationRequest
    {
        public bool IsActive { get; set; }
    }

    // ==========================================
    // 3. GERAÇÃO DE QR CODE PIX (Checkout Transparente)
    // ==========================================

    [Authorize(Roles = AppRoles.CompanyAdminOrPlatformAdmin)]
    [HttpPost("pix-qr")]
    public async Task<IActionResult> CreatePixQrCode([FromBody] PixChargeRequest request)
    {
        if (!request.UseRealMercadoPago && !_environment.IsDevelopment())
        {
            return BadRequest(new { message = "A simulação de cobrança Pix só está disponível no ambiente de desenvolvimento." });
        }

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

        if (!TryGetEffectiveCompanyId(out var currentCompanyId))
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
        var feePercent = await PlatformFeeSettingsReader.ReadValidFeePercentAsync(_context);
        if (!feePercent.HasValue)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { message = "A cobrança não foi criada porque a taxa global da plataforma está indisponível." });
        }

        decimal appFee = Math.Round(request.Amount * (feePercent.Value / 100m), 2);
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

        if (!request.UseRealMercadoPago)
        {
            return await CreateSimulatedPixAsync(tx);
        }

        // Comunicar com a API do Mercado Pago
        try
        {
            var decryptedToken = await EnsureValidAccessTokenAsync(integration);
            if (decryptedToken.Contains("mock") || decryptedToken.Length < 15)
            {
                const string mockCredentialsMessage = "As credenciais salvas são de simulação ou inválidas para uma cobrança real.";
                tx.Status = "Failed";
                await _context.SaveChangesAsync();
                await LogTelemetryAsync(tx.Id, "Error", mockCredentialsMessage);
                return BadRequest(new { message = mockCredentialsMessage, transactionId = tx.Id });
            }

            using var httpClient = CreateMercadoPagoHttpClient();
            httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", decryptedToken);
            httpClient.DefaultRequestHeaders.Add("X-Idempotency-Key", tx.Id.ToString());
            bool applicationFeeApplied = tx.ApplicationFee > 0;

            await LogTelemetryAsync(
                tx.Id,
                "Info",
                "Token OAuth da integração selecionada foi utilizado para solicitar a cobrança Pix."
            );

            MercadoPagoUserProfile tokenOwner;
            try
            {
                tokenOwner = await LogMercadoPagoTokenOwnerAsync(httpClient, tx.Id);
            }
            catch (Exception)
            {
                tx.Status = "Failed";
                integration.LastTokenValidationAt = DateTime.UtcNow;
                integration.LastTokenValidationStatus = "Falha ao validar token OAuth antes do Pix. Consulte o status HTTP nos logs do servidor.";
                await _context.SaveChangesAsync();
                await LogTelemetryAsync(tx.Id, "Error", "Falha ao validar a conta associada ao token OAuth antes da criação do Pix.");
                return BadRequest(new { message = "Não foi possível validar o token OAuth salvo no Mercado Pago antes de gerar o Pix.", transactionId = tx.Id });
            }

            if (!string.IsNullOrWhiteSpace(integration.MercadoPagoUserId) && tokenOwner.UserId != integration.MercadoPagoUserId)
            {
                tx.Status = "Failed";
                integration.LastTokenValidationAt = DateTime.UtcNow;
                integration.LastTokenValidationStatus = $"Token OAuth divergente. Esperado: {integration.MercadoPagoUserId}. Atual: {tokenOwner.UserId}.";
                await _context.SaveChangesAsync();
                await LogTelemetryAsync(tx.Id, "Error", "Token OAuth não pertence à conta Mercado Pago configurada para esta empresa.");
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
                await LogTelemetryAsync(tx.Id, "Info", "QR Code Pix gerado com sucesso pelo provedor.");

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
                await LogTelemetryAsync(tx.Id, "Error", $"Mercado Pago recusou a criação do Pix (HTTP {(int)mpResponse.StatusCode}).");
                return BadRequest(new
                {
                    message = "Não foi possível gerar o Pix agora. Tente novamente ou contate o suporte.",
                    transactionId = tx.Id
                });
            }
        }
        catch (Exception)
        {
            tx.Status = "Failed";
            await _context.SaveChangesAsync();
            await LogTelemetryAsync(tx.Id, "Error", "Falha ao integrar com o Mercado Pago durante a criação do Pix.");
            return BadRequest(new { message = "Não foi possível gerar o Pix agora. Tente novamente ou contate o suporte.", transactionId = tx.Id });
        }
    }

    private async Task<IActionResult> CreateSimulatedPixAsync(PaymentTransaction transaction)
    {
        transaction.MercadoPagoPaymentId = $"mock_mp_{Guid.NewGuid():N}";
        transaction.MercadoPagoStatus = "pending";
        transaction.MercadoPagoStatusDetail = "pending_waiting_transfer";
        transaction.QrCode = $"00020101021226870014br.gov.bcb.pix2572pix.example.com/qr/v2/mock-{transaction.Id}";
        transaction.QrCodeBase64 = "iVBORw0KGgoAAAANSUhEUgAAAJYAAACWAQAAAAAUekxPAAABUklEQVR4nNWWQWrDMBBFnxKD2pVyAwV6Dzvd9FQGp6SLHsu+iX0DZWeD4t+FnbSkBEpjQauV+QzzGP3RjI24PqfVNwn+uoZqgNxFb2tKwMblGY0UyCkB8FISRjChqp3a3ih6/PKMSXs3XeYftHlbKN8N7bS19fyZgRuPnzHi4S5GBsCwG+NTr2OcNZkvIZ3BLtNXUl8oAjZmFIIzZi3ufDNIkmyjg8hZS1KCvpKkHhcBp4QMoKQvlJJhR8DWSe+KQpE+7V1ZBbxGd0jCuGhVY1Y/ifuFNtdRA3akSulHPjFSei6Fkj5P6ocx3raYbr9QvqtzrgOUyo957j6O6oaXBfLdnrvTKi8kkbsEfhgpDM/K6LV53aoJCeqY9vnUtmlmyaQNO+iQyTBmk6gO2wS8rd3ea3RxBW5cliEFK6nFhKpVozjtc7sgo4bpXVz8MP/wH/gDo7EYHNztgj4AAAAASUVORK5CYII=";

        await _context.SaveChangesAsync();
        await LogTelemetryAsync(transaction.Id, "Info", $"Cobrança Pix gerada em modo Simulação/Desenvolvimento. ID: {transaction.MercadoPagoPaymentId}");

        return Ok(new
        {
            transactionId = transaction.Id,
            qrCode = transaction.QrCode,
            qrCodeBase64 = transaction.QrCodeBase64,
            status = transaction.Status,
            applicationFee = transaction.ApplicationFee,
            applicationFeeApplied = transaction.ApplicationFee > 0,
            simulated = true
        });
    }

    [AllowAnonymous]
    [HttpPost("/webhooks/mercadopago")]
    [RequestSizeLimit(16 * 1024)]
    public async Task<IActionResult> WebhookMercadoPago([FromBody] JsonElement webhookBody)
    {
        var paymentId = ReadJsonString(webhookBody, "data", "id");
        var eventType = ReadJsonString(webhookBody, "type");
        var eventAction = ReadJsonString(webhookBody, "action");
        var legacyTopic = ReadJsonString(webhookBody, "topic");
        var format = "unknown";

        if (eventType == "payment")
        {
            format = "webhook_v1";
        }
        else if (legacyTopic == "payment")
        {
            format = "legacy_ipn";
            paymentId = ReadJsonString(webhookBody, "id");
            eventType = "payment";
        }

        var queryPaymentId = Request.Query["data.id"].FirstOrDefault()
            ?? Request.Query["id"].FirstOrDefault();
        var queryType = Request.Query["type"].FirstOrDefault()
            ?? Request.Query["topic"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(paymentId) && queryType == "payment")
        {
            paymentId = queryPaymentId;
            eventType = "payment";
            format = Request.Query.ContainsKey("topic") ? "legacy_ipn" : "webhook_v1";
        }

        if (eventType != "payment")
        {
            // Mercado Pago expects an acknowledgement for unrelated notification types.
            return Ok();
        }

        if (string.IsNullOrWhiteSpace(paymentId))
        {
            return BadRequest(new { message = "A notificação de pagamento não contém o identificador." });
        }

        if (paymentId.Length > 128 || paymentId.Any(character => !char.IsAsciiDigit(character)))
        {
            return BadRequest(new { message = "Identificador de pagamento inválido." });
        }

        var webhookSecret = _configuration["MercadoPago:WebhookSecret"] ?? string.Empty;
        var signatureConfigured = !string.IsNullOrWhiteSpace(webhookSecret) && !webhookSecret.StartsWith("COLE_AQUI");
        var xSignature = Request.Headers["x-signature"].ToString();
        var xRequestId = Request.Headers["x-request-id"].ToString();
        var signatureValid = false;
        var authenticationMode = signatureConfigured ? "legacy_unsigned" : "unconfigured";

        if (signatureConfigured && string.IsNullOrWhiteSpace(xSignature) && format != "legacy_ipn")
        {
            return Unauthorized(new { message = "A notificação não contém uma assinatura válida." });
        }

        if (signatureConfigured && !string.IsNullOrWhiteSpace(xSignature))
        {
            var signatureParts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in xSignature.Split(','))
            {
                var pair = part.Split('=', 2);
                if (pair.Length != 2 || !signatureParts.TryAdd(pair[0].Trim(), pair[1].Trim()))
                {
                    return Unauthorized(new { message = "Assinatura do webhook inválida." });
                }
            }
            var timestamp = signatureParts.GetValueOrDefault("ts", string.Empty);
            var receivedHash = signatureParts.GetValueOrDefault("v1", string.Empty);
            if (string.IsNullOrWhiteSpace(timestamp) || string.IsNullOrWhiteSpace(receivedHash))
            {
                return Unauthorized(new { message = "Assinatura do webhook inválida." });
            }
            var dataId = Request.Query["data.id"].FirstOrDefault()
                ?? Request.Query["id"].FirstOrDefault()
                ?? paymentId;
            var manifestParts = new List<string>();
            if (!string.IsNullOrEmpty(dataId)) manifestParts.Add($"id:{dataId}");
            if (!string.IsNullOrEmpty(xRequestId)) manifestParts.Add($"request-id:{xRequestId}");
            if (!string.IsNullOrEmpty(timestamp)) manifestParts.Add($"ts:{timestamp}");
            var manifest = string.Join(";", manifestParts) + ";";

            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(webhookSecret));
            var expectedHash = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(manifest))).ToLowerInvariant();
            signatureValid = receivedHash.Length == expectedHash.Length &&
                CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(receivedHash.ToLowerInvariant()), Encoding.UTF8.GetBytes(expectedHash));
            if (!signatureValid)
            {
                return Unauthorized(new { message = "Assinatura do webhook inválida." });
            }

            authenticationMode = "hmac_verified";
        }

        if (await IsPaymentOwnedByLegacySocketAsync(paymentId, HttpContext.RequestAborted))
        {
            var relayResult = _legacyTelemetryRelay?.IsConfigured == true
                ? await _legacyTelemetryRelay.TryForwardWebhookAsync(
                    HttpContext,
                    webhookBody.GetRawText(),
                    HttpContext.RequestAborted)
                : null;
            if (relayResult is null)
            {
                Response.Headers["Retry-After"] = "30";
                return StatusCode(StatusCodes.Status503ServiceUnavailable,
                    new { message = "O pagamento pertence a uma máquina ainda atendida pela API anterior." });
            }

            if (relayResult.StatusCode is >= 200 and < 300)
                return Ok(new { accepted = true, routedToLegacyOwner = true });

            Response.Headers["Retry-After"] = "30";
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new { message = "Não foi possível encaminhar a notificação ao processo que mantém a máquina conectada." });
        }

        // Legacy IPN/QR notifications can omit x-signature. They are treated only as a trigger;
        // the worker independently fetches the canonical payment state from Mercado Pago.
        var eventIdentity = !string.IsNullOrWhiteSpace(xRequestId)
            ? $"{xRequestId}:{paymentId}"
            : $"{paymentId}:{eventType}:{eventAction}:{webhookBody.GetRawText()}";
        var eventKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(eventIdentity))).ToLowerInvariant();
        var inboxMessage = new WebhookInboxMessage
        {
            Id = Guid.NewGuid(),
            EventKey = eventKey,
            PaymentId = paymentId,
            EventType = eventType,
            EventAction = string.IsNullOrWhiteSpace(eventAction) ? string.Empty : eventAction[..Math.Min(eventAction.Length, 128)],
            Format = format,
            AuthenticationMode = authenticationMode,
            ReceivedAt = DateTime.UtcNow,
            Status = "Pending"
        };

        try
        {
            var duplicate = await _webhookInboxAdmission.PersistAsync(inboxMessage, HttpContext.RequestAborted);
            if (duplicate)
                return Ok(new { accepted = true, duplicate = true });
        }
        catch (WebhookInboxFullException)
        {
            Response.Headers["Retry-After"] = "30";
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { message = "A fila de notificações está temporariamente cheia. Tente novamente." });
        }
        catch (DbUpdateException)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { message = "Não foi possível persistir a notificação. Tente novamente." });
        }

        return Ok(new { accepted = true });
    }

    private Task<bool> IsPaymentOwnedByLegacySocketAsync(string paymentId, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var connectionFreshnessCutoff = now - MachineConnectionFreshness.Window;
        return _context.PaymentTransactions.AsNoTracking()
            .Where(transaction => transaction.MercadoPagoPaymentId == paymentId)
            .AnyAsync(transaction => _context.MachineSessions.AsNoTracking().Any(session =>
                session.TransactionId == transaction.Id && session.ClosedAt == null &&
                _context.MachineConnectionStates.AsNoTracking().Any(state =>
                    state.MachineId == session.MachineId && state.IsOnline &&
                    state.LastSeenAt >= connectionFreshnessCutoff &&
                    (state.OwnerNodeId == null || state.OwnerNodeId == "" ||
                     state.ConnectionId == null || state.ConnectionId == "" ||
                     state.OwnerConnectionId != state.ConnectionId ||
                     state.CommandRoutingVersion != TelemetryConnectionManager.CurrentCommandRoutingVersion ||
                     state.OwnerLeaseExpiresAt == null || state.OwnerLeaseExpiresAt <= now))),
                cancellationToken);
    }

    private static string? ReadJsonString(JsonElement element, params string[] path)
    {
        foreach (var segment in path)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(segment, out element))
            {
                return null;
            }
        }

        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetRawText(),
            _ => null
        };
    }


    // ==========================================
    // 5. SIMULADOR DE WEBHOOK (Front -> Back)
    // ==========================================

    [HttpPost("simulate-webhook")]
    [Authorize(Roles = AppRoles.CompanyAdminOrPlatformAdmin)]
    public async Task<IActionResult> SimulateWebhook([FromBody] SimulateWebhookRequest request)
    {
        if (!_environment.IsDevelopment())
        {
            return NotFound();
        }

        if (!TryGetEffectiveCompanyId(out var companyId))
        {
            return BadRequest(new { message = "O usuário não está associado a nenhuma empresa." });
        }

        var tx = await _context.PaymentTransactions
            .Include(t => t.Machine)
            .FirstOrDefaultAsync(t => t.Id == request.TransactionId && t.CompanyId == companyId);

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
    public async Task<IActionResult> GetTransactions([FromQuery] string? machineId = null, [FromQuery] string? transactionId = null)
    {
        if (!TryGetEffectiveCompanyId(out var companyId))
        {
            return BadRequest(new { message = "O usuário não está associado a nenhuma empresa." });
        }

        var query = _context.PaymentTransactions
            .AsNoTracking()
            .Where(t => t.CompanyId == companyId);

        if (!string.IsNullOrWhiteSpace(transactionId))
        {
            if (!Guid.TryParse(transactionId, out var parsedTransactionId))
                return BadRequest(new { message = "Identificador de transação inválido." });
            query = query.Where(transaction => transaction.Id == parsedTransactionId);
        }

        if (!string.IsNullOrEmpty(machineId))
        {
            query = query.Where(t => t.MachineId == machineId);
        }

        var list = await query
            .OrderByDescending(t => t.CreatedAt)
            .Take(50)
            .Select(t => new
            {
                t.Id,
                t.MachineId,
                Machine = t.Machine == null || t.Machine.CompanyId != companyId
                    ? null
                    : new { t.Machine.Id, t.Machine.Name, t.Machine.SerialNumber },
                t.Amount,
                t.ApplicationFee,
                t.Status,
                t.MercadoPagoPaymentId,
                t.MercadoPagoStatus,
                t.MercadoPagoStatusDetail,
                t.CreatedAt,
                t.CompletedAt
            })
            .ToListAsync();

        return Ok(list);
    }

    [HttpGet("transactions/history")]
    public async Task<IActionResult> GetTransactionHistory(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        [FromQuery] string? machineId = null,
        [FromQuery] string? transactionId = null,
        [FromQuery] string? status = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetEffectiveCompanyId(out var companyId))
            return BadRequest(new { message = "O usuário não está associado a nenhuma empresa." });

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var query = _context.PaymentTransactions.AsNoTracking().Where(t => t.CompanyId == companyId);

        if (!string.IsNullOrWhiteSpace(transactionId))
        {
            if (!Guid.TryParse(transactionId, out var parsedTransactionId))
                return BadRequest(new { message = "Identificador de transação inválido." });
            query = query.Where(transaction => transaction.Id == parsedTransactionId);
        }
        if (!string.IsNullOrWhiteSpace(machineId))
            query = query.Where(transaction => transaction.MachineId == machineId);
        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(transaction => transaction.Status == status);

        var total = await query.CountAsync(cancellationToken);
        var totalPages = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize));
        page = Math.Min(page, totalPages);
        var items = await query
            .OrderByDescending(transaction => transaction.CreatedAt)
            .ThenByDescending(transaction => transaction.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(transaction => new
            {
                transaction.Id,
                transaction.MachineId,
                Machine = transaction.Machine == null || transaction.Machine.CompanyId != companyId
                    ? null
                    : new { transaction.Machine.Id, transaction.Machine.Name, transaction.Machine.SerialNumber },
                transaction.Amount,
                transaction.ApplicationFee,
                transaction.Status,
                transaction.MercadoPagoPaymentId,
                transaction.MercadoPagoStatus,
                transaction.MercadoPagoStatusDetail,
                transaction.CreatedAt,
                transaction.CompletedAt
            })
            .ToListAsync(cancellationToken);

        return Ok(new { items, total, page, pageSize, totalPages });
    }

    [HttpGet("transactions/{id}/logs")]
    public async Task<IActionResult> GetTransactionLogs(
        Guid id,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetEffectiveCompanyId(out var companyId))
        {
            return BadRequest(new { message = "O usuário não está associado a nenhuma empresa." });
        }

        var transactionExists = await _context.PaymentTransactions
            .AnyAsync(t => t.Id == id && t.CompanyId == companyId, cancellationToken);
        if (!transactionExists)
        {
            return NotFound(new { message = "Transação não encontrada." });
        }

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var query = _context.TransactionTelemetryLogs.AsNoTracking()
            .Where(log => log.TransactionId == id);
        var total = await query.CountAsync(cancellationToken);
        var totalPages = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize));
        page = Math.Min(page, totalPages);
        var rows = await query
            .OrderByDescending(log => log.Timestamp)
            .ThenByDescending(log => log.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(log => new
            {
                log.Id,
                log.TransactionId,
                log.Timestamp,
                log.LogType,
                log.Message
            })
            .ToListAsync(cancellationToken);
        var logs = rows.Select(log => new TransactionTelemetryLog
        {
            Id = log.Id,
            TransactionId = log.TransactionId,
            Timestamp = log.Timestamp,
            LogType = log.LogType,
            Message = PaymentLogRedactor.RedactMessage(log.Message)
        }).ToList();

        Response.Headers["X-Total-Count"] = total.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Response.Headers["X-Page"] = page.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Response.Headers["X-Page-Size"] = pageSize.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Response.Headers["X-Total-Pages"] = totalPages.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Ok(logs);
    }

    [HttpGet("admin/logs")]
    [Authorize(Roles = AppRoles.PlatformAdmin)]
    public async Task<IActionResult> GetAdminLogs([FromQuery] int page = 1, [FromQuery] int pageSize = 20, [FromQuery] string? search = null)
    {
        if (!User.IsInRole(AppRoles.PlatformAdmin))
        {
            return StatusCode(403, new { message = "Acesso restrito ao Administrador Geral." });
        }

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);

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

        var rows = await query
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
                CompanyName = l.Transaction != null && l.Transaction.Company != null ? l.Transaction.Company.Name : "Empresa Desconhecida"
            })
            .ToListAsync();

        var items = rows.Select(l => new
        {
            l.Id,
            l.TransactionId,
            l.Timestamp,
            l.LogType,
            Message = PaymentLogRedactor.RedactMessage(l.Message),
            l.TransactionAmount,
            l.MachineName,
            l.CompanyName
        }).ToList();

        return Ok(new {
            items,
            page,
            pageSize,
            totalItems,
            totalPages
        });
    }

    [HttpGet("admin/webhook-inbox")]
    [Authorize(Roles = AppRoles.PlatformAdmin)]
    public async Task<IActionResult> GetWebhookInbox(
        [FromQuery] string? status = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        if (!User.IsInRole(AppRoles.PlatformAdmin))
        {
            return StatusCode(403, new { message = "Acesso restrito ao Administrador Geral." });
        }

        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 200);
        var query = _context.WebhookInboxMessages.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(status))
        {
            var normalizedStatus = status.Trim();
            if (normalizedStatus is not ("Pending" or "Processing" or "Retry" or "Processed" or "Failed"))
            {
                return BadRequest(new { message = "Estado de webhook inválido." });
            }

            query = query.Where(message => message.Status == normalizedStatus);
        }

        var totalItems = await query.CountAsync(cancellationToken);
        var rows = await query
            .OrderByDescending(message => message.ReceivedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(message => new
            {
                message.Id,
                message.PaymentId,
                message.EventType,
                message.EventAction,
                message.Format,
                message.AuthenticationMode,
                message.ReceivedAt,
                message.Status,
                message.Attempts,
                message.NextAttemptAt,
                message.ProcessedAt,
                message.LastError
            })
            .ToListAsync(cancellationToken);
        var items = rows.Select(message => new
        {
            message.Id,
            message.PaymentId,
            message.EventType,
            message.EventAction,
            message.Format,
            message.AuthenticationMode,
            message.ReceivedAt,
            message.Status,
            message.Attempts,
            message.NextAttemptAt,
            message.ProcessedAt,
            LastError = SanitizeWebhookInboxError(message.LastError)
        }).ToList();

        return Ok(new
        {
            items,
            page,
            pageSize,
            totalItems,
            totalPages = (int)Math.Ceiling(totalItems / (double)pageSize)
        });
    }

    [HttpGet("admin/reconciliation-queue")]
    [Authorize(Roles = AppRoles.PlatformAdmin)]
    public async Task<IActionResult> GetReconciliationQueue(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        if (!User.IsInRole(AppRoles.PlatformAdmin))
        {
            return StatusCode(403, new { message = "Acesso restrito ao Administrador Geral." });
        }

        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var query = _context.PaymentTransactions.AsNoTracking()
            .Where(transaction => transaction.Status == "Pending" || transaction.Status == "ReconciliationRequired");

        var counts = await query
            .GroupBy(transaction => transaction.Status)
            .Select(group => new { Status = group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.Status, item => item.Count, cancellationToken);
        var totalItems = counts.Values.Sum();
        var items = await query
            .OrderBy(transaction => transaction.CreatedAt)
            .ThenBy(transaction => transaction.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(transaction => new
            {
                transaction.Id,
                transaction.CompanyId,
                CompanyName = transaction.Company != null ? transaction.Company.Name : "Empresa removida",
                transaction.MachineId,
                MachineName = transaction.Machine != null ? transaction.Machine.Name : "Máquina removida",
                transaction.Amount,
                transaction.Status,
                transaction.ReconciliationReason,
                transaction.MercadoPagoStatus,
                transaction.MercadoPagoStatusDetail,
                transaction.MercadoPagoPaymentId,
                transaction.CreatedAt,
                transaction.LastReconciliationAt
            })
            .ToListAsync(cancellationToken);

        return Ok(new
        {
            counts = new
            {
                pending = counts.GetValueOrDefault("Pending"),
                reconciliationRequired = counts.GetValueOrDefault("ReconciliationRequired")
            },
            items,
            page,
            pageSize,
            totalItems,
            totalPages = (int)Math.Ceiling(totalItems / (double)pageSize)
        });
    }

    [HttpPost("admin/reconciliation-queue/{id:guid}/resolve")]
    [Authorize(Roles = AppRoles.PlatformAdmin)]
    public async Task<IActionResult> ResolvePaymentReconciliation(
        Guid id,
        [FromBody] PaymentReconciliationResolutionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!User.IsInRole(AppRoles.PlatformAdmin))
            return StatusCode(403, new { message = "Acesso restrito ao Administrador Geral." });

        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var actorUserId))
            return StatusCode(403, new { message = "Não foi possível identificar o operador para registrar a auditoria." });

        var evidenceReference = request.EvidenceReference.Trim();
        if (!IsEvidenceReferenceValid(evidenceReference))
        {
            return BadRequest(new
            {
                message = "Informe uma referência de evidência com 3 a 64 caracteres: letras, números, ponto, hífen ou sublinhado."
            });
        }

        var transaction = await _context.PaymentTransactions.AsNoTracking()
            .FirstOrDefaultAsync(payment => payment.Id == id, cancellationToken);
        if (transaction is null) return NotFound(new { message = "Transação não encontrada." });
        if (transaction.Status != "ReconciliationRequired")
            return Conflict(new { message = "Somente transações que exigem conciliação podem ser resolvidas por este fluxo." });
        if (string.IsNullOrWhiteSpace(transaction.MercadoPagoPaymentId) ||
            transaction.MercadoPagoPaymentId.StartsWith("mock_", StringComparison.OrdinalIgnoreCase))
        {
            return Conflict(new { message = "A resolução manual exige uma transação associada a um pagamento real do provedor." });
        }
        if (!string.Equals(transaction.MercadoPagoStatus, request.ExpectedProviderStatus, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(transaction.MercadoPagoStatusDetail, request.ExpectedProviderStatusDetail, StringComparison.OrdinalIgnoreCase) ||
            transaction.ReconciliationReason != request.ExpectedReconciliationReason ||
            transaction.LastReconciliationAt != request.ExpectedLastReconciliationAt)
        {
            return Conflict(new { message = "O estado da transação mudou. Atualize a fila e confira novamente a evidência." });
        }

        var sales = await _context.SaleRecords.AsNoTracking()
            .Where(sale => sale.TransactionId == transaction.Id &&
                           sale.CompanyId == transaction.CompanyId &&
                           sale.MachineId == transaction.MachineId)
            .OrderBy(sale => sale.Id)
            .Select(sale => new { sale.Id, sale.GrossAmount })
            .ToListAsync(cancellationToken);
        if (sales.Count > 1)
        {
            return Conflict(new
            {
                message = "A transação possui mais de uma linha de venda; a conciliação automática foi recusada para evitar alocação incorreta do estorno."
            });
        }

        var saleGrossAmount = sales.SingleOrDefault()?.GrossAmount;
        if (!TryMapManualResolution(transaction, request, saleGrossAmount, out var resolution, out var validationError))
            return BadRequest(new { message = validationError });

        var now = DateTime.UtcNow;
        await using var databaseTransaction = await _context.Database.BeginTransactionAsync(cancellationToken);
        var updated = await _context.PaymentTransactions
            .Where(payment => payment.Id == id &&
                              payment.Status == "ReconciliationRequired" &&
                              payment.MercadoPagoStatus == request.ExpectedProviderStatus &&
                              payment.MercadoPagoStatusDetail == request.ExpectedProviderStatusDetail &&
                              payment.ReconciliationReason == request.ExpectedReconciliationReason &&
                              payment.LastReconciliationAt == request.ExpectedLastReconciliationAt)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(payment => payment.Status, resolution.TransactionStatus)
                .SetProperty(payment => payment.ReconciliationReason, resolution.ReconciliationReason)
                .SetProperty(payment => payment.CompletedAt, payment => payment.CompletedAt ?? now)
                .SetProperty(payment => payment.LastReconciliationAt, now), cancellationToken);
        if (updated == 0)
        {
            await databaseTransaction.RollbackAsync(cancellationToken);
            return Conflict(new { message = "A transação mudou durante a resolução. Atualize a fila e confira novamente." });
        }

        if (sales.Count == 1)
        {
            var saleId = sales[0].Id;
            var saleUpdated = await _context.SaleRecords
                .Where(sale => sale.Id == saleId &&
                               sale.TransactionId == transaction.Id &&
                               sale.CompanyId == transaction.CompanyId &&
                               sale.MachineId == transaction.MachineId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(sale => sale.FinancialStatus, resolution.SaleFinancialStatus)
                    .SetProperty(sale => sale.RefundedAmount, sale => resolution.RefundedAmount ?? sale.GrossAmount), cancellationToken);
            if (saleUpdated != 1)
            {
                await databaseTransaction.RollbackAsync(cancellationToken);
                return Conflict(new { message = "A linha de venda mudou durante a resolução. Atualize a fila e tente novamente." });
            }
        }

        _context.AuditLogs.Add(new AuditLog
        {
            CompanyId = transaction.CompanyId,
            ActorUserId = actorUserId,
            Action = "payments.reconciliation_resolved",
            ResourceType = "PaymentTransaction",
            ResourceId = transaction.Id.ToString(),
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
            UserAgent = Truncate(HttpContext.Request.Headers.UserAgent.ToString(), 512),
            MetadataJson = AuditMetadataRedactor.SerializeRedacted(new
            {
                previousStatus = transaction.Status,
                resolvedStatus = resolution.TransactionStatus,
                resolution = request.Resolution.Trim(),
                providerStatus = transaction.MercadoPagoStatus,
                providerStatusDetail = transaction.MercadoPagoStatusDetail,
                previousReason = transaction.ReconciliationReason,
                refundedAmount = resolution.RefundedAmount ?? transaction.Amount,
                evidenceReference
            }),
            CreatedAt = now
        });
        await _context.SaveChangesAsync(cancellationToken);
        await databaseTransaction.CommitAsync(cancellationToken);

        return Ok(new
        {
            id = transaction.Id,
            status = resolution.TransactionStatus,
            message = "Conciliação registrada com evidência e auditoria. Nenhuma chamada financeira foi enviada ao provedor."
        });
    }

    private static bool IsEvidenceReferenceValid(string value) =>
        value.Length is >= 3 and <= 64 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_');

    private static bool TryMapManualResolution(
        PaymentTransaction transaction,
        PaymentReconciliationResolutionRequest request,
        decimal? saleGrossAmount,
        out ManualFinancialResolution resolution,
        out string error)
    {
        var resolutionCode = request.Resolution.Trim().ToLowerInvariant();
        var providerStatus = transaction.MercadoPagoStatus?.ToLowerInvariant();
        var providerDetail = transaction.MercadoPagoStatusDetail?.ToLowerInvariant();
        var refundedAmount = request.ConfirmedRefundedAmount;

        if (providerStatus == "approved" && resolutionCode == "approved")
        {
            if (providerDetail == "partially_refunded" &&
                refundedAmount is > 0m &&
                refundedAmount < transaction.Amount &&
                (!saleGrossAmount.HasValue || refundedAmount < saleGrossAmount.Value))
            {
                resolution = new ManualFinancialResolution("Approved", "PartiallyRefunded", refundedAmount.Value);
                error = string.Empty;
                return true;
            }

            if (providerDetail == "accredited" && (!refundedAmount.HasValue || refundedAmount == 0m))
            {
                resolution = new ManualFinancialResolution("Approved", "Settled", 0m);
                error = string.Empty;
                return true;
            }

            resolution = default!;
            error = "Para confirmar um pagamento aprovado, o detalhe precisa ser accredited ou partially_refunded com valor confirmado válido.";
            return false;
        }

        if (providerStatus == "rejected" && resolutionCode == "rejected" && (!refundedAmount.HasValue || refundedAmount == 0m))
        {
            resolution = new ManualFinancialResolution("Rejected", "Unpaid", 0m);
            error = string.Empty;
            return true;
        }

        if (providerStatus == "cancelled" && resolutionCode == "cancelled" && (!refundedAmount.HasValue || refundedAmount == 0m))
        {
            resolution = new ManualFinancialResolution("Cancelled", "Unpaid", 0m);
            error = string.Empty;
            return true;
        }

        if (providerStatus == "refunded" && resolutionCode == "refunded" && (!refundedAmount.HasValue || refundedAmount == 0m))
        {
                resolution = new ManualFinancialResolution("Refunded", "Refunded", null);
            error = string.Empty;
            return true;
        }

        if (providerStatus == "charged_back" && providerDetail == "settled" &&
            resolutionCode == "chargeback_settled" && (!refundedAmount.HasValue || refundedAmount == 0m))
        {
            resolution = new ManualFinancialResolution("Chargeback", "Chargeback", null);
            error = string.Empty;
            return true;
        }

        if (providerStatus == "charged_back" && providerDetail == "reimbursed" &&
            resolutionCode == "chargeback_reimbursed" &&
            refundedAmount is >= 0m &&
            refundedAmount <= transaction.Amount &&
            (!saleGrossAmount.HasValue || refundedAmount <= saleGrossAmount.Value))
        {
            resolution = new ManualFinancialResolution(
                "Approved",
                "Settled",
                refundedAmount.Value,
                ManualChargebackReimbursementConfirmedReason);
            error = string.Empty;
            return true;
        }

        resolution = default!;
        error = "A resolução deve corresponder ao estado canônico e ao detalhe atuais do provedor.";
        return false;
    }

    private sealed record ManualFinancialResolution(
        string TransactionStatus,
        string SaleFinancialStatus,
        decimal? RefundedAmount,
        string? ReconciliationReason = null);

    private static string? Truncate(string? value, int maxLength) =>
        string.IsNullOrEmpty(value) ? value : value.Length <= maxLength ? value : value[..maxLength];

    [HttpPost("admin/webhook-inbox/{id:guid}/retry")]
    [Authorize(Roles = AppRoles.PlatformAdmin)]
    public async Task<IActionResult> RetryWebhook(Guid id, CancellationToken cancellationToken = default)
    {
        if (!User.IsInRole(AppRoles.PlatformAdmin))
        {
            return StatusCode(403, new { message = "Acesso restrito ao Administrador Geral." });
        }

        int affected;
        try
        {
            affected = await _webhookInboxAdmission.RequeueFailedAsync(id, cancellationToken);
        }
        catch (WebhookInboxFullException)
        {
            return Conflict(new { message = "A fila de notificações está no limite; aguarde o processamento antes de reprocessar." });
        }

        if (affected == 0)
        {
            var exists = await _context.WebhookInboxMessages.AnyAsync(message => message.Id == id, cancellationToken);
            return exists
                ? Conflict(new { message = "Somente eventos com falha final podem ser reprocessados." })
                : NotFound(new { message = "Evento de webhook não encontrado." });
        }

        if (_audit is not null)
        {
            await _audit.RecordAsync(
                "payments.webhook_retry_requested",
                "WebhookInboxMessage",
                id.ToString(),
                metadata: new { previousStatus = "Failed" },
                cancellationToken: cancellationToken);
        }

        return Accepted(new { id, status = "Pending", message = "Evento colocado novamente na fila." });
    }

    // ==========================================================
    // MÉTODOS AUXILIARES DE TELEMETRIA E REEMBOLSO
    // ==========================================

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
                Message = PaymentLogRedactor.RedactMessage(message)
            };

            db.TransactionTelemetryLogs.Add(log);
            await db.SaveChangesAsync();
        }
        catch { }
    }

    private static string? SanitizeWebhookInboxError(string? error) => string.IsNullOrWhiteSpace(error)
        ? null
        : error switch
        {
            nameof(HttpRequestException) => "Falha na comunicação com o provedor.",
            nameof(InvalidOperationException) => "Operação do webhook não pôde ser concluída.",
            nameof(JsonException) => "Resposta inválida do provedor.",
            nameof(DbUpdateException) => "Falha ao salvar os dados do webhook.",
            _ => "Falha no processamento do webhook. Consulte os logs internos com acesso restrito."
        };

    private static string SanitizeTokenValidationStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status)) return string.Empty;

        string[] sensitivePrefixes =
        [
            "Falha ao renovar token OAuth:",
            "Falha ao validar token OAuth antes do Pix:"
        ];

        return sensitivePrefixes.Any(prefix => status.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            ? "Falha na validação do token OAuth. Detalhes sensíveis foram omitidos."
            : status;
    }

    private async Task<MercadoPagoUserProfile> LogMercadoPagoTokenOwnerAsync(HttpClient httpClient, Guid transactionId)
    {
        var profile = await GetMercadoPagoUserProfileAsync(httpClient);
        await LogTelemetryAsync(transactionId, "Info", "Conta associada ao token OAuth validada pelo provedor.");
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

        using var httpClient = CreateMercadoPagoHttpClient();
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
            integration.LastTokenValidationStatus = $"Falha ao renovar token OAuth (HTTP {(int)response.StatusCode}).";
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

        using var profileHttp = CreateMercadoPagoHttpClient();
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
        using var httpClient = CreateMercadoPagoHttpClient();
        httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        return await GetMercadoPagoUserProfileAsync(httpClient);
    }

    private static async Task<MercadoPagoUserProfile> GetMercadoPagoUserProfileAsync(HttpClient httpClient)
    {
        var response = await httpClient.GetAsync("https://api.mercadopago.com/users/me");
        var responseStr = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Não foi possível validar o token OAuth em /users/me. Status: {(int)response.StatusCode}.");
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

    private HttpClient CreateMercadoPagoHttpClient() =>
        _httpClientFactory?.CreateClient("MercadoPago") ?? new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(20)
        };

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
        var jwtKey = _configuration["Jwt:Key"]
            ?? throw new InvalidOperationException("A configuração Jwt:Key é obrigatória.");
        var key = Encoding.UTF8.GetBytes(jwtKey);
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
