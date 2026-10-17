import { fireEvent, render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";
import Locations from "@/pages/Locations";

const { getLocations } = vi.hoisted(() => ({ getLocations: vi.fn() }));

vi.mock("@/lib/api", () => ({
    getLocations,
    createLocation: vi.fn(),
    updateLocation: vi.fn(),
    deleteLocation: vi.fn(),
}));

vi.mock("@/components/AppSidebar", () => ({
    AppSidebar: () => <nav aria-label="Navegação" />,
}));

vi.mock("@/contexts/AuthContext", () => ({
    useAuth: () => ({ user: { role: "Operator" } }),
}));

describe("Locations", () => {
    beforeEach(() => {
        vi.clearAllMocks();
        getLocations.mockResolvedValue([]);
    });

    it("distinguishes an unavailable location list from an empty list and retries", async () => {
        getLocations
            .mockReset()
            .mockRejectedValueOnce(new Error("API indisponível"))
            .mockResolvedValueOnce([]);

        render(
            <MemoryRouter>
                <Locations />
            </MemoryRouter>,
        );

        expect(await screen.findByRole("alert")).toHaveTextContent("Não foi possível carregar as localizações.");
        expect(screen.queryByText("Nenhuma localização cadastrada.")).not.toBeInTheDocument();
        expect(screen.queryByText("Total de Localizações")).not.toBeInTheDocument();

        fireEvent.click(screen.getByRole("button", { name: "Tentar novamente" }));
        expect(await screen.findByText("Nenhuma localização cadastrada.")).toBeInTheDocument();
        expect(screen.queryByRole("alert")).not.toBeInTheDocument();
        expect(screen.getByText("Total de Localizações")).toBeInTheDocument();
    });
});
