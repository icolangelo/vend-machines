import { SidebarProvider, SidebarTrigger } from "@/components/ui/sidebar";
import { AppSidebar } from "@/components/AppSidebar";
import { useState } from "react";
import { Button } from "@/components/ui/button";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { BarChart3, Play, Printer, FileSpreadsheet, Box, Users, Percent, AlertCircle } from "lucide-react";
import { type Machine } from "@/data/mockData";
import { getMachines } from "@/lib/api";
import { toast } from "sonner";

export default function Reports() {
    const [selectedReport, setSelectedReport] = useState<string>("machines");
    const [isExecuting, setIsExecuting] = useState(false);
    const [reportData, setReportData] = useState<Machine[] | null>(null);
    const [executedAt, setExecutedAt] = useState<string>("");

    const handleExecute = async () => {
        setIsExecuting(true);
        try {
            if (selectedReport === "machines") {
                const data = await getMachines();
                setReportData(data);
                const now = new Date();
                setExecutedAt(now.toLocaleString("pt-BR"));
                toast.success("Relatório gerado com sucesso!");
            } else {
                toast.error("Tipo de relatório não suportado.");
            }
        } catch (error) {
            console.error("Erro ao executar relatório:", error);
            toast.error("Falha ao buscar os dados do relatório.");
        } finally {
            setIsExecuting(false);
        }
    };

    const handlePrint = () => {
        window.print();
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
                        <div className="flex flex-col md:flex-row md:items-end justify-between gap-4 bg-card p-5 rounded-lg border print:hidden shadow-sm">
                            <div className="flex flex-col sm:flex-row gap-4 items-end flex-1 max-w-2xl">
                                <div className="w-full sm:w-80">
                                    <label className="text-xs font-semibold mb-1.5 block text-muted-foreground uppercase tracking-wider">
                                        Selecione o Relatório
                                    </label>
                                    <Select value={selectedReport} onValueChange={setSelectedReport}>
                                        <SelectTrigger className="w-full">
                                            <SelectValue placeholder="Selecione um relatório..." />
                                        </SelectTrigger>
                                        <SelectContent>
                                            <SelectItem value="machines">Máquinas e Estoque</SelectItem>
                                        </SelectContent>
                                    </Select>
                                </div>
                                <Button 
                                    onClick={handleExecute} 
                                    disabled={isExecuting}
                                    className="w-full sm:w-auto gap-2 bg-primary hover:bg-primary/95 text-primary-foreground font-semibold"
                                >
                                    <Play className="w-4 h-4" />
                                    {isExecuting ? "Executando..." : "Executar"}
                                </Button>
                            </div>

                            {reportData && (
                                <div className="flex gap-2 w-full md:w-auto">
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

                        {/* Report Output */}
                        {reportData ? (
                            <div className="bg-card border rounded-lg shadow-sm p-6 flex flex-col gap-6 print:border-0 print:p-0 print:shadow-none animate-slide-up">
                                
                                {/* Report Information Header */}
                                <div className="flex flex-col sm:flex-row justify-between items-start sm:items-center border-b pb-4 gap-4">
                                    <div>
                                        <h2 className="text-xl font-bold text-foreground print:text-black">
                                            Relatório de Máquinas e Estoque
                                        </h2>
                                        <p className="text-xs text-muted-foreground mt-0.5">
                                            Listagem detalhada das vending machines, localizações e status do estoque.
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
                                                <th className="text-right px-4 py-3">Produtos Alocados</th>
                                                <th className="text-center px-4 py-3 w-[200px]">% do Estoque</th>
                                            </tr>
                                        </thead>
                                        <tbody>
                                            {reportData.length === 0 ? (
                                                <tr>
                                                    <td colSpan={5} className="px-4 py-8 text-center text-muted-foreground">
                                                        Nenhuma máquina localizada para o relatório.
                                                    </td>
                                                </tr>
                                            ) : (
                                                reportData.map((machine) => {
                                                    // Calculate allocated products count based on stock level.
                                                    // Assumes maximum capacity of 150 items per machine.
                                                    const allocatedProducts = Math.round(machine.stockLevel * 1.5);
                                                    
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
                                                            <td className="px-4 py-3.5 text-right font-mono-data font-medium print:text-black">
                                                                {allocatedProducts} <span className="text-[10px] text-muted-foreground">itens</span>
                                                            </td>
                                                            <td className="px-4 py-3.5">
                                                                <div className="flex items-center justify-end sm:justify-start gap-3">
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
                                                                </div>
                                                            </td>
                                                        </tr>
                                                    );
                                                })
                                            )}
                                        </tbody>
                                    </table>
                                </div>
                            </div>
                        ) : (
                            <div className="flex flex-col items-center justify-center py-20 bg-card border rounded-lg shadow-sm border-dashed">
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
