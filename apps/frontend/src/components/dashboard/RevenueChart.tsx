import { useState, useMemo } from "react";
import {
    ComposedChart,
    Bar,
    Line,
    XAxis,
    YAxis,
    CartesianGrid,
    Tooltip,
    ResponsiveContainer,
    Legend
} from "recharts";
import { BarChart3 } from "lucide-react";

const TIME_RANGES = [
    { label: "7D", value: 7 },
    { label: "30D", value: 30 },
    { label: "90D", value: 90 },
    { label: "6M", value: 180 },
    { label: "1A", value: 365 },
];

interface RevenueChartPoint {
    date: string;
    revenue: number;
    products: number;
}

export function RevenueChart({ data = [] }: { data?: RevenueChartPoint[] }) {
    const [timeRange, setTimeRange] = useState(30);

    const chartData = useMemo(() => {
        return data.slice(-timeRange);
    }, [data, timeRange]);

    const hasData = chartData.some((item) => item.revenue > 0 || item.products > 0);

    return (
        <div className="bg-card border rounded-lg animate-slide-up" style={{ animationDelay: "150ms" }}>
            <div className="px-5 py-4 border-b flex items-center justify-between gap-2 flex-wrap">
                <div className="flex items-center gap-2">
                    <BarChart3 className="h-4 w-4 text-accent" />
                    <h2 className="text-sm font-semibold text-foreground">
                        Faturamento e Vendas
                    </h2>
                </div>
                
                <div className="flex items-center gap-1 bg-muted rounded-md p-1">
                    {TIME_RANGES.map((range) => (
                        <button
                            key={range.label}
                            type="button"
                            onClick={() => setTimeRange(range.value)}
                            className={`px-2 py-1 text-xs font-medium rounded-sm transition-colors ${
                                timeRange === range.value
                                    ? "bg-background text-foreground shadow-sm"
                                    : "text-muted-foreground hover:text-foreground"
                            }`}
                        >
                            {range.label}
                        </button>
                    ))}
                </div>
            </div>
            <div className="p-5">
                {hasData ? (
                    <ResponsiveContainer width="100%" height={260}>
                        <ComposedChart data={chartData} margin={{ top: 5, right: 0, left: -20, bottom: 5 }}>
                            <CartesianGrid strokeDasharray="3 3" stroke="hsl(220 13% 91%)" vertical={false} />
                            <XAxis
                                dataKey="date"
                                tick={{ fontSize: 12, fill: "hsl(220 9% 46%)" }}
                                axisLine={false}
                                tickLine={false}
                                minTickGap={30}
                            />
                            <YAxis
                                yAxisId="left"
                                tick={{ fontSize: 11, fill: "hsl(220 9% 46%)" }}
                                axisLine={false}
                                tickLine={false}
                                tickFormatter={(v) => `R$${(v / 1000).toFixed(1)}k`}
                            />
                            <YAxis
                                yAxisId="right"
                                orientation="right"
                                tick={{ fontSize: 11, fill: "hsl(220 9% 46%)" }}
                                axisLine={false}
                                tickLine={false}
                            />
                            <Tooltip
                                contentStyle={{ borderRadius: "4px", border: "1px solid hsl(220 13% 91%)", fontSize: "12px", boxShadow: "none" }}
                                formatter={(value: number, name: string) => {
                                    if (name === "revenue") return [`R$ ${value.toLocaleString("pt-BR")}`, "Receita"];
                                    if (name === "products") return [value, "Produtos"];
                                    return [value, name];
                                }}
                                labelStyle={{ color: "hsl(220 9% 46%)", marginBottom: "4px" }}
                            />
                            <Legend wrapperStyle={{ fontSize: '12px', paddingTop: '10px' }} />
                            <Bar yAxisId="left" dataKey="revenue" name="Receita" fill="hsl(217 91% 60%)" radius={[2, 2, 0, 0]} maxBarSize={40} />
                            <Line yAxisId="right" type="monotone" dataKey="products" name="Produtos" stroke="#f97316" strokeWidth={2} dot={false} activeDot={{ r: 4 }} />
                        </ComposedChart>
                    </ResponsiveContainer>
                ) : (
                    <div className="h-[260px] flex items-center justify-center rounded-md border border-dashed">
                        <p className="text-sm text-muted-foreground text-center">
                            Nenhum faturamento ou venda aprovado para exibir.
                        </p>
                    </div>
                )}
            </div>
        </div>
    );
}
