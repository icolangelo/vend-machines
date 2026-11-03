import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import Settings from "@/pages/Settings";

const { getProductTypes, toast } = vi.hoisted(() => ({
    getProductTypes: vi.fn(),
    toast: vi.fn(),
}));

vi.mock("@/lib/api", () => ({
    getProductTypes,
    createProductType: vi.fn(),
    updateProductType: vi.fn(),
    deleteProductType: vi.fn(),
}));

vi.mock("@/components/AppSidebar", () => ({
    AppSidebar: () => <nav aria-label="Navegação" />,
}));

vi.mock("@/contexts/useAuth", () => ({
    useAuth: () => ({ logout: vi.fn(), user: { role: "Operator" } }),
}));

vi.mock("@/components/ui/use-toast", () => ({
    useToast: () => ({ toast }),
}));

describe("Settings", () => {
    beforeEach(() => {
        vi.clearAllMocks();
        getProductTypes.mockResolvedValue([]);
    });

    it("does not show an empty catalog when the initial request fails and allows retry", async () => {
        getProductTypes
            .mockReset()
            .mockRejectedValueOnce(new Error("API indisponível"))
            .mockResolvedValueOnce([]);

        render(<Settings />);

        expect(await screen.findByRole("alert")).toHaveTextContent("Não foi possível carregar os tipos de produto.");
        expect(screen.queryByText("Nenhum tipo de produto cadastrado.")).not.toBeInTheDocument();

        fireEvent.click(screen.getByRole("button", { name: "Tentar novamente" }));
        expect(await screen.findByText("Nenhum tipo de produto cadastrado.")).toBeInTheDocument();
        expect(screen.queryByRole("alert")).not.toBeInTheDocument();
    });

    it("keeps the last successful list visible when refresh fails", async () => {
        getProductTypes
            .mockResolvedValueOnce([{ id: "type-1", name: "Bebidas Frias" }])
            .mockRejectedValueOnce(new Error("API indisponível"));

        render(<Settings />);

        expect(await screen.findByText("Bebidas Frias")).toBeInTheDocument();
        fireEvent.click(screen.getByRole("button", { name: "Atualizar tipos de produto" }));

        expect(await screen.findByRole("alert")).toHaveTextContent("Os dados exibidos podem estar desatualizados.");
        expect(screen.getByText("Bebidas Frias")).toBeInTheDocument();
        await waitFor(() => expect(getProductTypes).toHaveBeenCalledTimes(2));
    });
});
