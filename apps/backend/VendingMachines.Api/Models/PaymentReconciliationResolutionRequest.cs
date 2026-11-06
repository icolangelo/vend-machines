using System;
using System.ComponentModel.DataAnnotations;

namespace VendingMachines.Api.Models;

public sealed class PaymentReconciliationResolutionRequest
{
    [Required]
    [StringLength(40)]
    public string Resolution { get; init; } = string.Empty;

    [Required]
    [StringLength(64, MinimumLength = 3)]
    public string EvidenceReference { get; init; } = string.Empty;

    [Required]
    [StringLength(32)]
    public string ExpectedProviderStatus { get; init; } = string.Empty;

    [StringLength(64)]
    public string? ExpectedProviderStatusDetail { get; init; }

    [StringLength(128)]
    public string? ExpectedReconciliationReason { get; init; }

    public DateTime? ExpectedLastReconciliationAt { get; init; }

    [Range(typeof(decimal), "0", "10000000")]
    public decimal? ConfirmedRefundedAmount { get; init; }
}
