import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { MachineAlertTable } from "@/components/dashboard/MachineAlertTable";
import { MachineStatusSummary } from "@/components/dashboard/MachineStatusSummary";
import type { Machine } from "@/data/mockData";

const machines: Machine[] = [
  {
    id: "VM-LOW",
    name: "Máquina abaixo da meta",
    clientName: "Unidade A",
    location: "Recepção",
    status: "online",
    stockLevel: 80,
    inventoryConfigured: true,
    lowStock: false,
    revenue30d: 40,
    totalSales30d: 4,
    minimumSalesTarget30d: 5,
    belowSalesTarget: true,
    lastSync: "agora",
    serialNumber: "SN-LOW",
  },
  {
    id: "VM-MET",
    name: "Máquina na meta",
    clientName: "Unidade A",
    location: "Loja",
    status: "online",
    stockLevel: 75,
    inventoryConfigured: true,
    lowStock: false,
    revenue30d: 50,
    totalSales30d: 5,
    minimumSalesTarget30d: 5,
    belowSalesTarget: false,
    lastSync: "agora",
    serialNumber: "SN-MET",
  },
  {
    id: "VM-NONE",
    name: "Máquina sem meta",
    clientName: "Unidade A",
    location: "Corredor",
    status: "online",
    stockLevel: 60,
    inventoryConfigured: true,
    lowStock: false,
    revenue30d: 0,
    totalSales30d: 0,
    lastSync: "agora",
    serialNumber: "SN-NONE",
  },
];

describe("machine thirty-day sales target", () => {
  it("counts only machines flagged below their configured target", () => {
    render(<MachineStatusSummary machines={machines} />);

    const metric = screen.getByText("Abaixo da meta").parentElement;
    expect(metric).toHaveTextContent("1");
  });

  it("shows below-target online machines and excludes machines at target or without a target", () => {
    render(<MachineAlertTable machines={machines} />);

    expect(screen.getByText("VM-LOW")).toBeVisible();
    expect(screen.getByText("4 / 5")).toBeVisible();
    expect(screen.getByText("Abaixo da meta")).toBeVisible();
    expect(screen.queryByText("VM-MET")).not.toBeInTheDocument();
    expect(screen.queryByText("VM-NONE")).not.toBeInTheDocument();
  });
});
