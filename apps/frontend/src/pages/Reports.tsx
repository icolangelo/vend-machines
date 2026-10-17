import { SidebarProvider, SidebarTrigger } from "@/components/ui/sidebar";
import { AppSidebar } from "@/components/AppSidebar";
import { useEffect, useState } from "react";
import { Button } from "@/components/ui/button";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Input } from "@/components/ui/input";
import { BarChart3, Play, Printer, FileSpreadsheet } from "lucide-react";
import { type Machine, type Location, type Product } from "@/data/mockData";
import { financialStatusLabel } from "@/lib/financialStatusLabel";
import { exportSalesReport, getBottomProducts, getInventoryMovements, getLocations, getMachines, getProducts, getSalesReport, getTopProducts, type InventoryMovementPage, type SalesReportResult } from "@/lib/api";
import type { FullProduct } from "@/data/mockData";
import { toast } from "sonner";

const formatCurrency = (amount: number) => new Intl.NumberFormat("pt-BR", { style: "currency", currency: "BRL" }).format(amount);
const INVENTORY_PAGE_SIZE = 50;
const inventoryMovementLabel = (movementType: string) => ({
    Sale: "Venda",
    Restock: "Reposição",
    OpeningBalance: "Saldo inicial",
    Adjustment: "Ajuste",
}[movementType] ?? "Outro");

const exportCsv = (filename: string, headers: string[], rows: Array<Array<string | number>>) => {
    const escapeCell = (value: string | number) => {
        const text = String(value ?? "");
        const safeText = /^[=+\-@\t\r]/.test(text) ? `'${text}` : text;
        return `"${safeText.replace(/"/g, '""')}"`;
    };
    const contents = [headers, ...rows].map(row => row.map(escapeCell).join(";")).join("\r\n");
    const blob = new Blob(["\uFEFF", contents], { type: "text/csv;charset=utf-8" });
    const url = URL.createObjectURL(blob);
    const link = document.createElement("a");
    link.href = url;
    link.download = filename;
    document.body.appendChild(link);
    link.click();
    link.remove();
    window.setTimeout(() => URL.revokeObjectURL(url), 0);
};

export default function Reports() {
    const [selectedReport, setSelectedReport] = useState<string>("machines");
    const [isExecuting, setIsExecuting] = useState(false);
    const [executionError, setExecutionError] = useState<string | null>(null);
    const [reportData, setReportData] = useState<Machine[] | null>(null);
    const [salesReport, setSalesReport] = useState<SalesReportResult | null>(null);
    const [productReport, setProductReport] = useState<{ top: Product[]; bottom: Product[] } | null>(null);
    const [locationsReport, setLocationsReport] = useState<Location[] | null>(null);
    const [inventoryReport, setInventoryReport] = useState<InventoryMovementPage | null>(null);
    const [inventoryPage, setInventoryPage] = useState(1);
    const [inventoryPageToRetry, setInventoryPageToRetry] = useState(1);
    const [inventoryPageRequestFailed, setInventoryPageRequestFailed] = useState(false);
    const [executedAt, setExecutedAt] = useState<string>("");
    const today = new Date().toISOString().slice(0, 10);
    const defaultFrom = new Date(Date.now() - 29 * 24 * 60 * 60 * 1000).toISOString().slice(0, 10);
    const [dateFrom, setDateFrom] = useState(defaultFrom);
    const [dateTo, setDateTo] = useState(today);
    const [filterMachines, setFilterMachines] = useState<Machine[]>([]);
    const [filterLocations, setFilterLocations] = useState<Location[]>([]);
    const [filterProducts, setFilterProducts] = useState<FullProduct[]>([]);
    const [machineFilter, setMachineFilter] = useState("all");
    const [locationFilter, setLocationFilter] = useState("all");
    const [productFilter, setProductFilter] = useState("all");
    const [itemNumberFilter, setItemNumberFilter] = useState("");

    useEffect(() => {
        let active = true;
        Promise.all([getMachines(), getLocations(), getProducts()])
            .then(([machines, locations, products]) => {
                if (!active) return;
                setFilterMachines(machines);
                setFilterLocations(locations);
                setFilterProducts(products);
            })
            .catch(error => {
                console.error("Erro ao carregar opções dos filtros de relatórios:", error);
                toast.error("Não foi possível carregar as opções dos filtros.");
            });
        return () => { active = false; };
    }, []);

    const machinesForSelectedLocation = filterMachines.filter(machine =>
        locationFilter === "all" || machine.locationId === locationFilter);

    const handleExecute = async () => {
        setIsExecuting(true);
        setExecutionError(null);
        setInventoryPageRequestFailed(false);
        try {
            if (selectedReport === "machines") {
                const data = await getMachines();
                setSalesReport(null);
                setProductReport(null);
                setLocationsReport(null);
                setInventoryReport(null);
                setReportData(data);
                const now = new Date();
                setExecutedAt(now.toLocaleString("pt-BR"));
                toast.success("Relatório gerado com sucesso!");
            } else if (selectedReport === "sales") {
                const data = await getSalesReport(dateFrom, dateTo, {
                    machineId: machineFilter === "all" ? undefined : machineFilter,
                    locationId: locationFilter === "all" ? undefined : locationFilter,
                    productId: productFilter === "all" ? undefined : productFilter
                });
                setReportData(null);
                setProductReport(null);
                setLocationsReport(null);
                setInventoryReport(null);
                setSalesReport(data);
                setExecutedAt(new Date().toLocaleString("pt-BR"));
                toast.success("Relatório de vendas gerado.");
            } else if (selectedReport === "products") {
                const [top, bottom] = await Promise.all([getTopProducts(), getBottomProducts()]);
                setReportData(null);
                setSalesReport(null);
                setLocationsReport(null);
                setInventoryReport(null);
                setProductReport({ top, bottom });
                setExecutedAt(new Date().toLocaleString("pt-BR"));
                toast.success("Ranking de produtos gerado.");
            } else if (selectedReport === "locations") {
                const data = await getLocations();
                setReportData(null);
                setSalesReport(null);
                setProductReport(null);
                setInventoryReport(null);
                setLocationsReport(data);
                setExecutedAt(new Date().toLocaleString("pt-BR"));
                toast.success("Relatório de clientes e localizações gerado.");
            } else if (selectedReport === "inventory") {
                const itemNumber = itemNumberFilter.trim() ? Number(itemNumberFilter) : undefined;
                if (itemNumber !== undefined && (!Number.isInteger(itemNumber) || itemNumber < 0)) {
                    const message = "Informe um número de compartimento válido.";
                    setExecutionError(message);
                    toast.error(message);
                    return;
                }
                const data = await getInventoryMovements(1, INVENTORY_PAGE_SIZE, {
                    machineId: machineFilter === "all" ? undefined : machineFilter,
                    itemNumber
                });
                setReportData(null);
                setSalesReport(null);
                setProductReport(null);
                setLocationsReport(null);
                setInventoryPage(1);
                setInventoryReport(data);
                setExecutedAt(new Date().toLocaleString("pt-BR"));
                toast.success("Histórico de estoque gerado.");
            }
        } catch (error) {
            console.error("Erro ao executar relatório:", error);
            const message = "Não foi possível gerar o relatório. Confira a conexão e tente novamente.";
            setExecutionError(message);
            toast.error("Falha ao buscar os dados do relatório.");
        } finally {
            setIsExecuting(false);
        }
    };

    const handleInventoryPageChange = async (page: number) => {
        setIsExecuting(true);
        setExecutionError(null);
        setInventoryPageRequestFailed(false);
        setInventoryPageToRetry(page);
        try {
            const itemNumber = itemNumberFilter.trim() ? Number(itemNumberFilter) : undefined;
            const data = await getInventoryMovements(page, INVENTORY_PAGE_SIZE, {
                machineId: machineFilter === "all" ? undefined : machineFilter,
                itemNumber
            });
            setInventoryReport(data);
            setInventoryPage(data.page);
            setExecutedAt(new Date().toLocaleString("pt-BR"));
        } catch (error) {
            console.error("Erro ao paginar histórico de estoque:", error);
            setInventoryPageRequestFailed(true);
            setExecutionError("Não foi possível carregar esta página do histórico. Confira a conexão e tente novamente.");
            toast.error("Falha ao buscar as movimentações de estoque.");
        } finally {
            setIsExecuting(false);
        }
    };

    const handlePrint = () => {
        window.print();
    };

    const handleExportSales = async () => {
        try {
            const blob = await exportSalesReport(dateFrom, dateTo, {
                machineId: machineFilter === "all" ? undefined : machineFilter,
                locationId: locationFilter === "all" ? undefined : locationFilter,
                productId: productFilter === "all" ? undefined : productFilter
            });
            const url = URL.createObjectURL(blob);
            const link = document.createElement("a");
            link.href = url;
            link.download = `vendas-${dateFrom}-${dateTo}.csv`;
            link.click();
            URL.revokeObjectURL(url);
        } catch (error) {
            toast.error(error instanceof Error ? error.message : "Não foi possível exportar o relatório.");
        }
    };

    const handleExportReport = () => {
        if (reportData) {
            const statusLabels = { online: "Online", offline: "Offline", warning: "Atenção", unmonitored: "Sem monitoramento", inactive: "Inativa" };
            exportCsv("maquinas-estoque.csv", ["ID da máquina", "Máquina", "Cliente", "Localização", "Status", "Última comunicação", "Vendas 30 dias", "Estoque cadastrado", "Estoque percentual"], reportData.map(machine => [
                machine.id, machine.name, machine.clientName, machine.location, statusLabels[machine.status], machine.lastSync || "Sem comunicação", machine.totalSales30d, machine.inventoryConfigured ? "Sim" : "Não", machine.inventoryConfigured ? machine.stockLevel : "",
            ]));
        } else if (productReport) {
            const products = [
                ...productReport.top.map(product => ["Mais vendidos", product] as const),
                ...productReport.bottom.map(product => ["Menos vendidos", product] as const),
            ];
            exportCsv("ranking-produtos.csv", ["Ranking", "Produto", "Unidades vendidas", "Receita após estornos antes da taxa"], products.map(([ranking, product]) => [ranking, product.name, product.totalSold, product.revenue]));
        } else if (locationsReport) {
            exportCsv("clientes-localizacoes.csv", ["Cliente / localização", "Máquinas", "Unidades vendidas 30 dias", "Receita após estornos antes da taxa 30 dias"], locationsReport.map(location => [location.name, location.machineCount, location.totalSales30d, location.revenue30d]));
        } else if (inventoryReport) {
            exportCsv(`movimentacoes-estoque-pagina-${inventoryReport.page}.csv`, ["Data", "Máquina", "Compartimento", "Produto", "Tipo", "Variação", "Saldo", "Motivo"], inventoryReport.items.map(item => [new Date(item.createdAt).toLocaleString("pt-BR"), item.machineId, item.itemNumber, item.productName || item.productId, inventoryMovementLabel(item.movementType), item.quantityDelta, item.quantityAfter, item.reason]));
        }
    };

    return (
        <SidebarProvider>
            <div className="min-h-screen flex w-full">
                <AppSidebar />
                <div className="flex-1 flex flex-col min-w-0 bg-background print:bg-white print:p-0">
                    
                    {/* Header */}
                    <header className="h-12 flex items-center border-b bg-card px-4 gap-3 print:hidden">
                        <SidebarTrigger />
                        <div className="flex items-center gap-2">
                            <BarChart3 className="w-4 h-4 text-primary" />
                            <h1 className="text-sm font-semibold text-foreground">Relatórios</h1>
                        </div>
                    </header>

                    {/* Main Content */}
                    <main className="flex-1 p-6 flex flex-col gap-6 overflow-auto print:p-0">
                        
                        {/* Interactive Controls Header */}
                        <div className="flex flex-col md:flex-row md:items-end justify-between gap-4 bg-card p-5 rounded-lg border print:hidden shadow-xs">
                            <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 2xl:grid-cols-6 gap-4 items-end flex-1">
                                <div className="w-full">
                                    <label className="text-xs font-semibold mb-1.5 block text-muted-foreground uppercase tracking-wider">
                                        Selecione o Relatório
                                    </label>
                                    <Select value={selectedReport} onValueChange={setSelectedReport}>
                                        <SelectTrigger className="w-full">
                                            <SelectValue placeholder="Selecione um relatório..." />
                                        </SelectTrigger>
                                        <SelectContent>
                                            <SelectItem value="machines">Máquinas, Estoque e Disponibilidade</SelectItem>
                                            <SelectItem value="sales">Vendas e Receita</SelectItem>
                                            <SelectItem value="products">Ranking de Produtos</SelectItem>
                                            <SelectItem value="locations">Clientes e Localizações</SelectItem>
                                            <SelectItem value="inventory">Movimentações de Estoque</SelectItem>
                                        </SelectContent>
                                    </Select>
                                </div>
                                {selectedReport === "sales" && <>
                                    <div className="w-full">
                                        <label className="text-xs font-semibold mb-1.5 block text-muted-foreground">De</label>
                                        <Input type="date" value={dateFrom} onChange={event => setDateFrom(event.target.value)} />
                                    </div>
                                    <div className="w-full">
                                        <label className="text-xs font-semibold mb-1.5 block text-muted-foreground">Até</label>
                                        <Input type="date" value={dateTo} onChange={event => setDateTo(event.target.value)} />
                                    </div>
                                    <div className="w-full">
                                        <label className="text-xs font-semibold mb-1.5 block text-muted-foreground">Localização</label>
                                        <Select value={locationFilter} onValueChange={value => {
                                            setLocationFilter(value);
                                            setMachineFilter("all");
                                        }}>
                                            <SelectTrigger><SelectValue placeholder="Todas" /></SelectTrigger>
                                            <SelectContent>
                                                <SelectItem value="all">Todas as localizações</SelectItem>
                                                {filterLocations.map(location => <SelectItem key={location.id} value={location.id}>{location.name}</SelectItem>)}
                                            </SelectContent>
                                        </Select>
                                    </div>
                                    <div className="w-full">
                                        <label className="text-xs font-semibold mb-1.5 block text-muted-foreground">Máquina</label>
                                        <Select value={machineFilter} onValueChange={setMachineFilter}>
                                            <SelectTrigger><SelectValue placeholder="Todas" /></SelectTrigger>
                                            <SelectContent>
                                                <SelectItem value="all">Todas as máquinas</SelectItem>
                                                {machinesForSelectedLocation.map(machine => <SelectItem key={machine.id} value={machine.id}>{machine.name}</SelectItem>)}
                                            </SelectContent>
                                        </Select>
                                    </div>
                                    <div className="w-full">
                                        <label className="text-xs font-semibold mb-1.5 block text-muted-foreground">Produto</label>
                                        <Select value={productFilter} onValueChange={setProductFilter}>
                                            <SelectTrigger><SelectValue placeholder="Todos" /></SelectTrigger>
                                            <SelectContent>
                                                <SelectItem value="all">Todos os produtos</SelectItem>
                                                {filterProducts.map(product => <SelectItem key={product.id} value={product.id}>{product.name}</SelectItem>)}
                                            </SelectContent>
                                        </Select>
                                    </div>
                                </>}
                                {selectedReport === "inventory" && <>
                                    <div className="w-full">
                                        <label className="text-xs font-semibold mb-1.5 block text-muted-foreground">Máquina</label>
                                        <Select value={machineFilter} onValueChange={setMachineFilter}>
                                            <SelectTrigger><SelectValue placeholder="Todas" /></SelectTrigger>
                                            <SelectContent>
                                                <SelectItem value="all">Todas as máquinas</SelectItem>
                                                {filterMachines.map(machine => <SelectItem key={machine.id} value={machine.id}>{machine.name}</SelectItem>)}
                                            </SelectContent>
                                        </Select>
                                    </div>
                                    <div className="w-full">
                                        <label className="text-xs font-semibold mb-1.5 block text-muted-foreground">Compartimento</label>
                                        <Input type="number" min="0" step="1" value={itemNumberFilter} onChange={event => setItemNumberFilter(event.target.value)} placeholder="Todos" />
                                    </div>
                                </>}
                                <Button 
                                    onClick={handleExecute} 
                                    disabled={isExecuting}
                                    className="w-full gap-2 bg-primary hover:bg-primary/95 text-primary-foreground font-semibold"
                                >
                                    <Play className="w-4 h-4" />
                                    {isExecuting ? "Executando..." : "Executar"}
                                </Button>
                            </div>

                            {(reportData || salesReport || productReport || locationsReport || inventoryReport) && (
                                <div className="flex gap-2 w-full md:w-auto">
                                    {salesReport && <Button variant="outline" onClick={() => void handleExportSales()} className="flex-1 md:flex-initial gap-2">
                                        <FileSpreadsheet className="w-4 h-4" /> Exportar CSV
                                    </Button>}
                                    {!salesReport && <Button variant="outline" onClick={handleExportReport} className="flex-1 md:flex-initial gap-2">
                                        <FileSpreadsheet className="w-4 h-4" /> {inventoryReport ? "Exportar página CSV" : "Exportar CSV"}
                                    </Button>}
                                    <Button 
                                        variant="outline" 
                                        onClick={handlePrint}
                                        className="flex-1 md:flex-initial gap-2 text-muted-foreground hover:text-foreground"
                                    >
                                        <Printer className="w-4 h-4" />
                                        Imprimir
                                    </Button>
                                </div>
                            )}
                        </div>

                        {executionError && (
                            <div role="alert" className="flex flex-wrap items-center justify-between gap-3 rounded-md border border-destructive/30 bg-destructive/5 p-3 text-sm">
                                <span>{executionError}</span>
                                <Button variant="outline" size="sm" onClick={() => void (inventoryPageRequestFailed ? handleInventoryPageChange(inventoryPageToRetry) : handleExecute())} disabled={isExecuting}>
                                    Tentar novamente
                                </Button>
                            </div>
                        )}

                        {salesReport && <div className="bg-card border rounded-lg shadow-xs p-6 flex flex-col gap-5 print:border-0 print:p-0 print:shadow-none">
                            <div className="flex flex-col sm:flex-row justify-between gap-3 border-b pb-4">
                                <div>
                                    <h2 className="text-xl font-bold">Relatório de vendas e receita</h2>
                                    <p className="text-xs text-muted-foreground">Período UTC: {salesReport.from} a {salesReport.to}. Receita após estornos é exibida antes da taxa da plataforma. O líquido após taxa é o valor original da venda; estornos aparecem à parte. Tarifas do Mercado Pago não estão incluídas.</p>
                                </div>
                                <div className="text-sm text-muted-foreground">{salesReport.total} vendas · gerado em {executedAt}</div>
                            </div>
                            {salesReport.truncated && <p className="text-sm text-warning">Exibindo as 10.000 vendas mais recentes; use períodos menores para ver o conjunto completo.</p>}
                            <div className="overflow-x-auto">
                                <table className="w-full text-sm border-collapse">
                                    <thead><tr className="border-b bg-muted/30 text-left text-xs uppercase text-muted-foreground">
                                        <th className="px-3 py-3">Data UTC</th><th className="px-3 py-3">Máquina</th><th className="px-3 py-3">Cliente / local</th><th className="px-3 py-3">Produto</th><th className="px-3 py-3 text-right">Qtd.</th><th className="px-3 py-3 text-right">Bruto</th><th className="px-3 py-3 text-right">Estorno</th><th className="px-3 py-3 text-right">Receita após estornos (pré-taxa)</th><th className="px-3 py-3 text-right">Taxa plataforma</th><th className="px-3 py-3 text-right">Líquido original após taxa</th><th className="px-3 py-3">Situação</th>
                                    </tr></thead>
                                    <tbody>{salesReport.items.length === 0 ? <tr><td colSpan={11} className="px-4 py-8 text-center text-muted-foreground">Não há vendas registradas no período selecionado.</td></tr> : salesReport.items.map(sale => <tr key={sale.id} className="border-b last:border-0">
                                        <td className="px-3 py-3 whitespace-nowrap">{new Date(sale.soldAt).toLocaleString("pt-BR", { timeZone: "UTC" })}</td>
                                        <td className="px-3 py-3">{sale.machineName}</td>
                                        <td className="px-3 py-3">{sale.clientName}{sale.location ? ` · ${sale.location}` : ""}</td>
                                        <td className="px-3 py-3">{sale.productName}</td>
                                        <td className="px-3 py-3 text-right">{sale.quantity}</td>
                                        <td className="px-3 py-3 text-right">{formatCurrency(sale.grossAmount)}</td>
                                        <td className="px-3 py-3 text-right">{formatCurrency(sale.refundedAmount)}</td>
                                        <td className="px-3 py-3 text-right">{formatCurrency(sale.netRevenue)}</td>
                                        <td className="px-3 py-3 text-right">{formatCurrency(sale.applicationFee)}</td>
                                        <td className="px-3 py-3 text-right">{formatCurrency(sale.netAfterPlatformFee)}</td>
                                        <td className="px-3 py-3">{financialStatusLabel(sale.financialStatus)}</td>
                                    </tr>)}</tbody>
                                </table>
                            </div>
                        </div>}

                        {productReport && <div className="grid grid-cols-1 xl:grid-cols-2 gap-5">
                            {[{ title: "Mais vendidos", items: productReport.top }, { title: "Menos vendidos", items: productReport.bottom }].map(group => <section key={group.title} className="bg-card border rounded-lg p-5 print:break-inside-avoid">
                                <h2 className="text-lg font-bold mb-1">{group.title}</h2>
                                <p className="text-xs text-muted-foreground mb-4">Vendas entregues nos últimos 30 dias · receita após estornos, antes da taxa da plataforma · atualizado em {executedAt}</p>
                                <div className="overflow-x-auto"><table className="w-full text-sm"><thead><tr className="border-b text-left text-xs uppercase text-muted-foreground"><th className="py-2">Produto</th><th className="py-2 text-right">Unidades</th><th className="py-2 text-right">Receita após estornos (pré-taxa)</th></tr></thead>
                                    <tbody>{group.items.length === 0 ? <tr><td colSpan={3} className="py-6 text-center text-muted-foreground">Sem vendas mapeadas neste período.</td></tr> : group.items.map((product, index) => <tr key={`${product.name}-${index}`} className="border-b last:border-0"><td className="py-3">{product.name}</td><td className="py-3 text-right">{product.totalSold}</td><td className="py-3 text-right">{formatCurrency(product.revenue)}</td></tr>)}</tbody>
                                </table></div>
                            </section>)}</div>}

                        {locationsReport && <div className="bg-card border rounded-lg shadow-xs p-6 print:break-inside-avoid">
                            <h2 className="text-xl font-bold">Clientes e localizações</h2><p className="text-xs text-muted-foreground mt-1 mb-4">Indicadores de vendas entregues nos últimos 30 dias · receita após estornos, antes da taxa da plataforma · atualizado em {executedAt}</p>
                            <div className="overflow-x-auto"><table className="w-full text-sm"><thead><tr className="border-b text-left text-xs uppercase text-muted-foreground"><th className="py-3">Cliente / localização</th><th className="py-3 text-right">Máquinas</th><th className="py-3 text-right">Unidades vendidas</th><th className="py-3 text-right">Receita após estornos (pré-taxa)</th></tr></thead>
                                <tbody>{locationsReport.length === 0 ? <tr><td colSpan={4} className="py-8 text-center text-muted-foreground">Nenhuma localização cadastrada.</td></tr> : locationsReport.map(location => <tr key={location.id} className="border-b last:border-0"><td className="py-3">{location.name}</td><td className="py-3 text-right">{location.machineCount}</td><td className="py-3 text-right">{location.totalSales30d}</td><td className="py-3 text-right">{formatCurrency(location.revenue30d)}</td></tr>)}</tbody>
                            </table></div>
                        </div>}

                        {inventoryReport && <div className="bg-card border rounded-lg shadow-xs p-6 print:break-inside-avoid">
                            <h2 className="text-xl font-bold">Movimentações de estoque</h2><p className="text-xs text-muted-foreground mt-1 mb-4">Exibindo {inventoryReport.totalItems === 0 ? 0 : (inventoryPage - 1) * inventoryReport.pageSize + 1}–{(inventoryPage - 1) * inventoryReport.pageSize + inventoryReport.items.length} de {inventoryReport.totalItems} movimentações · atualizado em {executedAt}</p>
                            <div className="overflow-x-auto"><table className="w-full text-sm"><thead><tr className="border-b text-left text-xs uppercase text-muted-foreground"><th className="py-3">Data</th><th className="py-3">Máquina</th><th className="py-3">Produto</th><th className="py-3">Tipo</th><th className="py-3 text-right">Variação</th><th className="py-3 text-right">Saldo</th><th className="py-3">Motivo</th></tr></thead>
                                <tbody>{inventoryReport.items.length === 0 ? <tr><td colSpan={7} className="py-8 text-center text-muted-foreground">Nenhuma movimentação cadastrada.</td></tr> : inventoryReport.items.map(movement => <tr key={movement.id} className="border-b last:border-0"><td className="py-3 whitespace-nowrap">{new Date(movement.createdAt).toLocaleString("pt-BR")}</td><td className="py-3">{movement.machineId} · comp. {movement.itemNumber}</td><td className="py-3">{movement.productName || movement.productId}</td><td className="py-3">{inventoryMovementLabel(movement.movementType)}</td><td className="py-3 text-right">{movement.quantityDelta > 0 ? "+" : ""}{movement.quantityDelta}</td><td className="py-3 text-right">{movement.quantityAfter}</td><td className="py-3">{movement.reason}</td></tr>)}</tbody>
                            </table></div>
                            {inventoryReport.totalPages > 1 && <div className="flex items-center justify-between gap-3 pt-3">
                                <span className="text-sm text-muted-foreground">Página {inventoryPage} de {inventoryReport.totalPages}</span>
                                <div className="flex gap-2">
                                    <Button variant="outline" size="sm" onClick={() => void handleInventoryPageChange(inventoryPage - 1)} disabled={isExecuting || inventoryPage <= 1}>Anterior</Button>
                                    <Button variant="outline" size="sm" onClick={() => void handleInventoryPageChange(inventoryPage + 1)} disabled={isExecuting || inventoryPage >= inventoryReport.totalPages}>Próxima</Button>
                                </div>
                            </div>}
                        </div>}

                        {/* Report Output */}
                        {reportData ? (
                            <div className="bg-card border rounded-lg shadow-xs p-6 flex flex-col gap-6 print:border-0 print:p-0 print:shadow-none animate-slide-up">
                                
                                {/* Report Information Header */}
                                <div className="flex flex-col sm:flex-row justify-between items-start sm:items-center border-b pb-4 gap-4">
                                    <div>
                                        <h2 className="text-xl font-bold text-foreground print:text-black">
                                            Relatório de Máquinas e Estoque
                                        </h2>
                                        <p className="text-xs text-muted-foreground mt-0.5">
                                            Listagem detalhada das máquinas, localizações e situação do estoque.
                                        </p>
                                    </div>
                                    <div className="text-left sm:text-right text-xs text-muted-foreground">
                                        <p>Gerado em: <span className="font-mono-data font-medium text-foreground print:text-black">{executedAt}</span></p>
                                        <p>Total de registros: <span className="font-mono-data font-medium text-foreground print:text-black">{reportData.length}</span></p>
                                    </div>
                                </div>

                                {/* Printable Table */}
                                <div className="overflow-x-auto">
                                    <table className="w-full text-sm border-collapse">
                                        <thead>
                                            <tr className="border-b bg-muted/30 text-muted-foreground text-xs uppercase tracking-wider font-semibold">
                                                <th className="text-left px-4 py-3">ID da Máquina</th>
                                                <th className="text-left px-4 py-3">Máquina</th>
                                                <th className="text-left px-4 py-3">Localização</th>
                                                <th className="text-left px-4 py-3">Status</th>
                                                <th className="text-left px-4 py-3">Última comunicação</th>
                                                <th className="text-right px-4 py-3">Vendas (30 dias)</th>
                                                <th className="text-center px-4 py-3 w-[200px]">Estoque cadastrado</th>
                                            </tr>
                                        </thead>
                                        <tbody>
                                            {reportData.length === 0 ? (
                                                <tr>
                                                    <td colSpan={7} className="px-4 py-8 text-center text-muted-foreground">
                                                        Nenhuma máquina localizada para o relatório.
                                                    </td>
                                                </tr>
                                            ) : (
                                                reportData.map((machine) => {
                                                    return (
                                                        <tr 
                                                            key={machine.id} 
                                                            className="border-b last:border-b-0 hover:bg-muted/10 transition-colors print:hover:bg-transparent"
                                                        >
                                                            <td className="px-4 py-3.5 font-mono-data text-xs text-muted-foreground font-semibold print:text-black">
                                                                {machine.id}
                                                            </td>
                                                            <td className="px-4 py-3.5 font-medium text-foreground print:text-black">
                                                                {machine.name}
                                                            </td>
                                                            <td className="px-4 py-3.5 text-muted-foreground print:text-black">
                                                                {machine.clientName}
                                                            </td>
                                                            <td className="px-4 py-3.5">
                                                                {{ online: "Online", offline: "Offline", warning: "Atenção", unmonitored: "Sem monitoramento", inactive: "Inativa" }[machine.status]}
                                                            </td>
                                                            <td className="px-4 py-3.5 text-xs text-muted-foreground">{machine.lastSync || "Sem comunicação"}</td>
                                                            <td className="px-4 py-3.5 text-right font-mono-data font-medium print:text-black">
                                                                {machine.totalSales30d}
                                                            </td>
                                                            <td className="px-4 py-3.5">
                                                                {!machine.inventoryConfigured ? <span className="text-xs text-muted-foreground">Não cadastrado</span> : <div className="flex items-center justify-end sm:justify-start gap-3">
                                                                    <div className="w-24 h-2 bg-muted rounded-full overflow-hidden hidden sm:block print:block">
                                                                        <div
                                                                            className={`h-full rounded-full ${
                                                                                machine.stockLevel > 50
                                                                                    ? "bg-success"
                                                                                    : machine.stockLevel > 20
                                                                                        ? "bg-warning"
                                                                                        : "bg-destructive"
                                                                            }`}
                                                                            style={{ width: `${machine.stockLevel}%` }}
                                                                        />
                                                                    </div>
                                                                    <span className={`font-mono-data font-semibold text-xs px-2 py-0.5 rounded ${
                                                                        machine.stockLevel > 50
                                                                            ? "bg-success/15 text-success"
                                                                            : machine.stockLevel > 20
                                                                                ? "bg-warning/15 text-warning"
                                                                                : "bg-destructive/15 text-destructive"
                                                                    }`}>
                                                                        {machine.stockLevel}%
                                                                    </span>
                                                                </div>}
                                                            </td>
                                                        </tr>
                                                    );
                                                })
                                            )}
                                        </tbody>
                                    </table>
                                </div>
                            </div>
                        ) : salesReport || productReport || locationsReport || inventoryReport || executionError || isExecuting ? null : (
                            <div className="flex flex-col items-center justify-center py-20 bg-card border rounded-lg shadow-xs border-dashed">
                                <BarChart3 className="w-12 h-12 text-muted-foreground/45 mb-4" />
                                <h3 className="text-base font-semibold text-foreground">
                                    Nenhum relatório executado
                                </h3>
                                <p className="text-sm text-muted-foreground mt-1 max-w-sm text-center">
                                    Escolha o relatório desejado no combo acima e clique em Executar para visualizar os dados.
                                </p>
                            </div>
                        )}
                    </main>
                </div>
            </div>
        </SidebarProvider>
    );
}
