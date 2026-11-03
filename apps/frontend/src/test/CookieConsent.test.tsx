import { fireEvent, render, screen } from "@testing-library/react";
import { beforeEach, describe, expect, it } from "vitest";
import { CookieConsent } from "@/components/CookieConsent";

describe("CookieConsent", () => {
  beforeEach(() => {
    localStorage.clear();
  });

  it("explains the current essential storage without offering inactive trackers", async () => {
    render(<CookieConsent />);

    expect(await screen.findByText(/não carrega ferramentas de análise ou publicidade/i)).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /aceitar todos|rejeitar/i })).not.toBeInTheDocument();
  });

  it("opens the privacy policy and remembers when the notice was read", async () => {
    render(<CookieConsent />);

    fireEvent.click(await screen.findByRole("button", { name: "Política de Privacidade" }));
    expect(await screen.findByRole("heading", { name: "Política de Privacidade" })).toBeInTheDocument();
    expect(screen.getByText("Última atualização: 26 de outubro de 2026")).toBeInTheDocument();
    expect(screen.queryByRole("heading", { name: "Política de Privacidade e Termos de Uso" })).not.toBeInTheDocument();
    expect(await screen.findByText(/status e identificadores recebidos do provedor/i)).toBeInTheDocument();
    expect(screen.getByText(/compartilhamos com o Mercado Pago os dados necessários à cobrança/i)).toBeInTheDocument();
    expect(screen.getByText(/o provedor de e-mail configurado recebe o endereço do destinatário/i)).toBeInTheDocument();
    expect(await screen.findByRole("heading", { name: "3. Cookies e armazenamento local" })).toBeInTheDocument();
    fireEvent.click(screen.getAllByRole("button", { name: "Fechar" })[0]);

    fireEvent.click(screen.getByRole("button", { name: "Entendi" }));
    expect(screen.queryByLabelText("Aviso sobre cookies e armazenamento")).not.toBeInTheDocument();
    expect(localStorage.getItem("vm-cookies-notice-v2")).toBe("read");

    render(<CookieConsent />);
    expect(screen.queryByLabelText("Aviso sobre cookies e armazenamento")).not.toBeInTheDocument();
  });
});
