import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";
import VerifyEmail from "@/pages/VerifyEmail";

const { verifyEmail, resendEmailVerification } = vi.hoisted(() => ({
    verifyEmail: vi.fn(),
    resendEmailVerification: vi.fn(),
}));

vi.mock("@/lib/api", () => ({ verifyEmail, resendEmailVerification }));

describe("VerifyEmail", () => {
    beforeEach(() => {
        vi.clearAllMocks();
    });

    it("does not consume the email token until the user confirms", async () => {
        verifyEmail.mockResolvedValue({ emailVerified: true, accountActive: false, message: "E-mail confirmado." });
        render(
            <MemoryRouter initialEntries={["/verify-email?token=verification-token"]}>
                <VerifyEmail />
            </MemoryRouter>,
        );

        expect(verifyEmail).not.toHaveBeenCalled();
        fireEvent.click(screen.getByRole("button", { name: "Confirmar meu e-mail" }));

        await waitFor(() => expect(verifyEmail).toHaveBeenCalledWith("verification-token"));
        expect(await screen.findByRole("status")).toHaveTextContent("E-mail confirmado.");
    });

    it("requests a replacement link without exposing whether the address exists", async () => {
        resendEmailVerification.mockResolvedValue({ message: "Se houver um cadastro aguardando confirmação, enviaremos um novo link para o endereço informado." });
        render(
            <MemoryRouter initialEntries={["/verify-email"]}>
                <VerifyEmail />
            </MemoryRouter>,
        );

        fireEvent.change(screen.getByLabelText("E-mail do cadastro"), { target: { value: "pessoa@example.com" } });
        fireEvent.click(screen.getByRole("button", { name: "Solicitar novo link" }));

        await waitFor(() => expect(resendEmailVerification).toHaveBeenCalledWith("pessoa@example.com"));
        expect(await screen.findByText(/Se houver um cadastro aguardando confirmação/)).toBeInTheDocument();
    });

    it("shows a Portuguese message and permits retry when email confirmation cannot reach the API", async () => {
        verifyEmail.mockRejectedValue(new TypeError("Failed to fetch"));
        render(<MemoryRouter initialEntries={["/verify-email?token=verification-token"]}><VerifyEmail /></MemoryRouter>);
        fireEvent.click(screen.getByRole("button", { name: "Confirmar meu e-mail" }));

        expect(await screen.findByText("Não foi possível confirmar o e-mail. Tente novamente.")).toBeInTheDocument();
        expect(screen.getByRole("button", { name: "Confirmar meu e-mail" })).toBeEnabled();
    });

    it("permits retry when requesting another verification link fails at the network", async () => {
        resendEmailVerification.mockRejectedValue(new TypeError("Failed to fetch"));
        render(<MemoryRouter initialEntries={["/verify-email"]}><VerifyEmail /></MemoryRouter>);
        fireEvent.change(screen.getByLabelText("E-mail do cadastro"), { target: { value: "pessoa@example.com" } });
        fireEvent.click(screen.getByRole("button", { name: "Solicitar novo link" }));

        expect(await screen.findByText("Não foi possível solicitar um novo link. Tente novamente.")).toBeInTheDocument();
        expect(screen.getByRole("button", { name: "Solicitar novo link" })).toBeEnabled();
    });
});
