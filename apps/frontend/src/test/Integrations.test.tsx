import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import Integrations from "@/pages/Integrations";

const api = vi.hoisted(() => ({
    getIntegration: vi.fn(),
    toggleIntegration: vi.fn(),
    getOauthConfig: vi.fn(),
    disconnectIntegration: vi.fn(),
    toast: vi.fn(),
    userRole: "Operator",
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

vi.mock("@/contexts/useAuth", () => ({
    useAuth: () => ({ user: { role: api.userRole, companyName: "Empresa teste" }, logout: vi.fn() }),
}));

vi.mock("@/components/ui/use-toast", () => ({
    useToast: () => ({ toast: api.toast }),
}));

describe("Integration loading state", () => {
    beforeEach(() => {
        vi.clearAllMocks();
        api.userRole = "Operator";
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

    it("does not render a token fingerprint in the integration diagnostics", async () => {
        api.getIntegration.mockResolvedValue({
            companyId: "company-1",
            ownerName: "Responsável",
            ownerCpf: "00000000000",
            ownerEmail: "responsavel@example.test",
            ownerPhone: "",
            businessName: "Empresa teste",
            tradeName: "",
            cnpj: "00000000000100",
            businessEmail: "",
            businessPhone: "",
            accessToken: "",
            hasAccessToken: true,
            hasRefreshToken: true,
            publicKey: "",
            mercadoPagoUserId: "seller-1",
            mercadoPagoNickname: "empresa-teste",
            mercadoPagoSiteId: "MLB",
            tokenFingerprint: "fingerprint-private-value",
            isActive: true,
        });

        render(<Integrations />);

        expect(await screen.findByText("Diagnóstico Mercado Pago")).toBeInTheDocument();
        expect(screen.queryByText(/fingerprint/i)).not.toBeInTheDocument();
        expect(screen.queryByText("fingerprint-private-value")).not.toBeInTheDocument();
    });

    it("does not start a simulated OAuth flow when the production client ID is missing", async () => {
        api.userRole = "Admin";
        api.getIntegration.mockResolvedValue({
            companyId: "company-1",
            ownerName: "",
            ownerCpf: "",
            ownerEmail: "",
            ownerPhone: "",
            businessName: "Empresa teste",
            tradeName: "",
            cnpj: "00000000000100",
            businessEmail: "",
            businessPhone: "",
            accessToken: "",
            publicKey: "",
            isActive: false,
        });
        api.getOauthConfig.mockResolvedValue({ clientId: "", redirectUri: "https://app.example.test/" });

        render(<Integrations />);
        await screen.findByText("Pronto para começar?");
        fireEvent.click(screen.getByRole("button", { name: "Conectar com Mercado Pago" }));

        await waitFor(() => expect(api.toast).toHaveBeenCalledWith(expect.objectContaining({
            title: "Erro",
            description: expect.stringContaining("não está configurada"),
        })));
    });
});
