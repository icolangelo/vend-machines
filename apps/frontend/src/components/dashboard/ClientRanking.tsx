import { type Client } from "@/data/mockData";
import { Trophy } from "lucide-react";

export function ClientRanking({ clients }: { clients: Client[] }) {
    const sorted = [...clients].sort((a, b) => b.revenue30d - a.revenue30d);
    const maxRevenue = sorted[0]?.revenue30d || 1;

    return (
        <section
            aria-labelledby="client-revenue-ranking-title"
            className="bg-card border rounded-lg animate-slide-up"
            style={{ animationDelay: "200ms" }}
        >
            <div className="px-5 py-4 border-b flex items-start gap-2">
                <Trophy className="h-4 w-4 text-accent" />
                <div className="min-w-0">
                    <h2 id="client-revenue-ranking-title" className="text-sm font-semibold text-foreground">
                        Faturamento por cliente
                    </h2>
                    <p className="text-xs text-muted-foreground">
                        Após estornos e antes da taxa da plataforma
                    </p>
                </div>
                <span className="text-label ml-auto whitespace-nowrap">30 dias</span>
            </div>
            <div className="p-5 space-y-4">
                {sorted.length === 0 ? (
                    <p className="py-4 text-center text-sm text-muted-foreground">
                        Nenhum cliente cadastrado para classificar.
                    </p>
                ) : sorted.map((client, i) => (
                    <div key={client.name} className="space-y-1.5">
                        <div className="flex items-center justify-between">
                            <div className="flex items-center gap-2">
                                <span className="font-mono-data text-xs text-muted-foreground w-4">
                                    {i + 1}.
                                </span>
                                <span className="text-sm font-medium text-foreground">
                                    {client.name}
                                </span>
                            </div>
                            <span className="font-mono-data text-sm text-foreground">
                                R$ {client.revenue30d.toLocaleString("pt-BR", { minimumFractionDigits: 2, maximumFractionDigits: 2 })}
                            </span>
                        </div>
                        <div className="ml-6 h-1.5 bg-muted rounded-full overflow-hidden">
                            <div
                                className="h-full bg-accent rounded-full transition-all duration-500"
                                style={{ width: `${(client.revenue30d / maxRevenue) * 100}%` }}
                            />
                        </div>
                        <div className="ml-6 flex gap-3">
                            <span className="text-xs text-muted-foreground">
                                {client.machineCount} máquinas
                            </span>
                            <span className="text-xs text-muted-foreground">
                                {client.totalSales30d.toLocaleString("pt-BR")} vendas
                            </span>
                        </div>
                    </div>
                ))}
            </div>
        </section>
    );
}
