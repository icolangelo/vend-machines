import { type LucideIcon } from "lucide-react";

interface KpiCardProps {
    title: string;
    value: string;
    subtitle?: string;
    icon: LucideIcon;
    trend?: { value: number; label: string };
    variant?: "default" | "success" | "warning" | "destructive";
}

const variantStyles = {
    default: "border-border",
    success: "border-l-2 border-l-success",
    warning: "border-l-2 border-l-warning",
    destructive: "border-l-2 border-l-destructive",
};

export function KpiCard({ title, value, subtitle, icon: Icon, trend, variant = "default" }: KpiCardProps) {
    return (
        <div
            className={`bg-card border rounded-lg p-5 animate-slide-up ${variantStyles[variant]}`}
        >
            <div className="flex items-start justify-between mb-3">
                <span className="text-label">{title}</span>
                <Icon className="h-4 w-4 text-muted-foreground" />
            </div>
            <p className="font-mono-data text-2xl font-medium text-foreground leading-none">
                {value}
            </p>
            <div className="flex items-center gap-2 mt-2">
                {trend && (
                    <span
                        className={`text-xs font-medium ${trend.value >= 0 ? "text-success" : "text-destructive"
                            }`}
                    >
                        {trend.value >= 0 ? "+" : ""}
                        {trend.value}%
                    </span>
                )}
                {subtitle && (
                    <span className="text-xs text-muted-foreground">{subtitle}</span>
                )}
            </div>
        </div>
    );
}
