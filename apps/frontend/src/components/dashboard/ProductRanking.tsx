import { type Product } from "@/data/mockData";
import { TrendingUp, TrendingDown, ArrowUpRight, ArrowDownRight } from "lucide-react";

function ProductList({
    products,
    type,
}: {
    products: Product[];
    type: "top" | "bottom";
}) {
    return (
        <div className="space-y-3">
            {products.map((product, i) => (
                <div
                    key={product.name}
                    className="flex items-center justify-between py-1"
                >
                    <div className="flex items-center gap-3">
                        <span className="font-mono-data text-xs text-muted-foreground w-4">
                            {i + 1}.
                        </span>
                        <div>
                            <p className="text-sm font-medium text-foreground">{product.name}</p>
                            <p className="text-xs text-muted-foreground">
                                {product.totalSold.toLocaleString("pt-BR")} unidades
                            </p>
                        </div>
                    </div>
                    <div className="text-right">
                        <p className="font-mono-data text-sm text-foreground">
                            R$ {product.revenue.toLocaleString("pt-BR")}
                        </p>
                        <div className="flex items-center justify-end gap-0.5">
                            {product.trend >= 0 ? (
                                <ArrowUpRight className="h-3 w-3 text-success" />
                            ) : (
                                <ArrowDownRight className="h-3 w-3 text-destructive" />
                            )}
                            <span
                                className={`text-xs font-medium ${product.trend >= 0 ? "text-success" : "text-destructive"
                                    }`}
                            >
                                {Math.abs(product.trend)}%
                            </span>
                        </div>
                    </div>
                </div>
            ))}
        </div>
    );
}

export function ProductRanking({
    topProducts,
    bottomProducts,
}: {
    topProducts: Product[];
    bottomProducts: Product[];
}) {
    return (
        <div className="grid grid-cols-1 lg:grid-cols-2 gap-4">
            <div className="bg-card border rounded-lg animate-slide-up" style={{ animationDelay: "250ms" }}>
                <div className="px-5 py-4 border-b flex items-center gap-2">
                    <TrendingUp className="h-4 w-4 text-success" />
                    <h2 className="text-sm font-semibold text-foreground">
                        Mais vendidos
                    </h2>
                </div>
                <div className="p-5">
                    <ProductList products={topProducts} type="top" />
                </div>
            </div>

            <div className="bg-card border rounded-lg animate-slide-up" style={{ animationDelay: "300ms" }}>
                <div className="px-5 py-4 border-b flex items-center gap-2">
                    <TrendingDown className="h-4 w-4 text-destructive" />
                    <h2 className="text-sm font-semibold text-foreground">
                        Menos vendidos
                    </h2>
                </div>
                <div className="p-5">
                    <ProductList products={bottomProducts} type="bottom" />
                </div>
            </div>
        </div>
    );
}
