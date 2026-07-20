namespace VendingMachines.Simulator;

public enum SimulatorState
{
    Disconnected,
    Connecting,
    Connected,
    AwaitingSelection,
    WaitingPix,
    WaitingPaymentResult,
    PaymentApproved,
    Closing,
    Completed,
    Failed
}

public enum ScenarioKind
{
    None,
    SaleSuccess,
    Sale,
    CancelBeforeSelection,
    CancelAfterPix,
    DeliveryFailure
}

public sealed record ScenarioPlan(
    ScenarioKind Kind,
    int ItemNumber = 0,
    int ItemPrice = 0,
    string FailureReason = "unknown",
    TimeSpan? Delay = null)
{
    public static readonly ScenarioPlan None = new(ScenarioKind.None);
}
