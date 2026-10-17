import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import Integrations from "@/pages/Integrations";

const api = vi.hoisted(() => ({
    getIntegration: vi.fn(),
    toggleIntegration: vi.fn(),
    getOauthConfig: vi.fn(),
    disconnectIntegration: vi.fn(),
    toast: vi.fn(),
}));

vi.mock("@/lib/api", () => ({
    getIntegration: api.getIntegration,
    toggleIntegration: api.toggleIntegration,
    getOauthConfig: api.getOauthConfig,
    disconnectIntegration: api.disconnectIntegration,
}));

vi.mock("@/components/AppSidebar", () => ({
    AppSidebar: () => <nav aria-label="Navegação" />,
}));

vi.mock("@/contexts/AuthContext", () => ({
    useAuth: () => ({ user: { role: "Operator", companyName: "Empresa teste" }, logout: vi.fn() }),
}));

vi.mock("@/components/ui/use-toast", () => ({
    useToast: () => ({ toast: api.toast }),
}));

describe("Integration loading state", () => {
    beforeEach(() => {
        vi.clearAllMocks();
    });

    it("does not show the integration as unconfigured when its initial read fails", async () => {
        api.getIntegration
            .mockRejectedValueOnce(new Error("API indisponível"))
            .mockResolvedValueOnce({
                companyId: "company-1",
                ownerName: "",
                ownerCpf: "",
                ownerEmail: "",
                ownerPhone: "",
                businessName: "",
                tradeName: "",
                cnpj: "",
                businessEmail: "",
                businessPhone: "",
                accessToken: "",
                publicKey: "",
                isActive: false,
            });

        render(<Integrations />);
        await waitFor(() => expect(api.getIntegration).toHaveBeenCalledTimes(1));

        expect(await screen.findByRole("alert")).toHaveTextContent("O estado da conta não está confirmado.");
        expect(screen.queryByText("Não Configurado")).not.toBeInTheDocument();
        expect(screen.queryByText("Pronto para começar?")).not.toBeInTheDocument();

        fireEvent.click(screen.getByRole("button", { name: "Tentar novamente" }));
        expect(await screen.findByText("Não Configurado")).toBeInTheDocument();
        expect(screen.getByText("Pronto para começar?")).toBeInTheDocument();
        expect(screen.queryByRole("alert")).not.toBeInTheDocument();
    });
});
