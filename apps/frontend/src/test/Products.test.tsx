import { fireEvent, render, screen } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import Products from "@/pages/Products";

const { getProducts, getProductTypes, toast } = vi.hoisted(() => ({
    getProducts: vi.fn(),
    getProductTypes: vi.fn(),
    toast: vi.fn(),
}));

vi.mock("@/lib/api", () => ({
    getProducts,
    getProductTypes,
    createProduct: vi.fn(),
    updateProduct: vi.fn(),
    deleteProduct: vi.fn(),
}));

vi.mock("@/components/AppSidebar", () => ({
    AppSidebar: () => <nav aria-label="Navegação" />,
}));

vi.mock("@/contexts/AuthContext", () => ({
    useAuth: () => ({ logout: vi.fn(), user: { role: "Operator" } }),
}));

vi.mock("@/components/ui/use-toast", () => ({
    useToast: () => ({ toast }),
}));

describe("Products", () => {
    beforeEach(() => {
        vi.clearAllMocks();
        getProducts.mockResolvedValue([]);
        getProductTypes.mockResolvedValue([]);
    });

    it("does not present the catalog as empty when loading fails and allows retry", async () => {
        getProducts
            .mockReset()
            .mockRejectedValueOnce(new Error("API indisponível"))
            .mockResolvedValueOnce([]);

        render(<Products />);

        expect(await screen.findByRole("alert")).toHaveTextContent("Não foi possível carregar o catálogo de produtos.");
        expect(screen.queryByText("Nenhum produto cadastrado.")).not.toBeInTheDocument();
        expect(screen.queryByText("Total de Produtos")).not.toBeInTheDocument();

        fireEvent.click(screen.getByRole("button", { name: "Tentar novamente" }));
        expect(await screen.findByText("Nenhum produto cadastrado.")).toBeInTheDocument();
        expect(screen.queryByRole("alert")).not.toBeInTheDocument();
        expect(screen.getByText("Total de Produtos")).toBeInTheDocument();
    });
});
