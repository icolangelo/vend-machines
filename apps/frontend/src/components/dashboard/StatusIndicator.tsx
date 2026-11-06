interface StatusIndicatorProps {
    status: "online" | "offline" | "warning";
}

const statusConfig = {
    online: { label: "Online", colorClass: "bg-success" },
    offline: { label: "Offline", colorClass: "bg-destructive" },
    warning: { label: "Atenção", colorClass: "bg-warning" },
};

export function StatusIndicator({ status }: StatusIndicatorProps) {
    const config = statusConfig[status];
    return (
        <div className="flex items-center gap-1.5">
            <div className="relative flex items-center justify-center">
                <span className={`h-2 w-2 rounded-full ${config.colorClass}`} />
                <span className={`absolute h-3 w-3 rounded-full ${config.colorClass} opacity-20`} />
            </div>
            <span className="text-xs text-muted-foreground">{config.label}</span>
        </div>
    );
}
