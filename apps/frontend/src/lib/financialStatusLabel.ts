const financialStatusLabels: Record<string, string> = {
    Settled: "Liquidada",
    PartiallyRefunded: "Parcialmente estornada",
    Refunded: "Estornada",
    Disputed: "Em contestação",
    Chargeback: "Contestada pelo emissor",
    ReconciliationRequired: "Conciliação necessária",
};

export function financialStatusLabel(status: string): string {
    return financialStatusLabels[status] ?? "Desconhecida";
}
