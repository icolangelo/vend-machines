import { act, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { CompanySwitcherModal } from "@/components/CompanySwitcherModal";

const { getCompanies, setImpersonatedCompany, clearImpersonation } = vi.hoisted(() => ({
  getCompanies: vi.fn(),
  setImpersonatedCompany: vi.fn(),
  clearImpersonation: vi.fn(),
}));

vi.mock("@/lib/api", () => ({ getCompanies }));
vi.mock("@/contexts/useImpersonation", () => ({
  useImpersonation: () => ({ impersonatedCompany: null, setImpersonatedCompany, clearImpersonation }),
}));

describe("seleção de empresa", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    getCompanies.mockReset();
  });

  it("shows a query failure and lets the user retry instead of displaying an empty list", async () => {
    getCompanies.mockRejectedValueOnce(new Error("falha sintética"))
      .mockResolvedValueOnce({ items: [{ id: "a", name: "Empresa A" }] });
    render(<CompanySwitcherModal isOpen onClose={vi.fn()} />);

    expect(await screen.findByRole("alert")).toHaveTextContent("Não foi possível consultar as empresas.");
    expect(screen.queryByText("Nenhuma empresa encontrada")).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Tentar novamente" }));
    expect(await screen.findByRole("button", { name: /Empresa A/ })).toBeEnabled();
  });

  it("does not replace newer search results with an older response", async () => {
    let resolveOld!: (value: { items: { id: string; name: string }[] }) => void;
    let resolveNew!: (value: { items: { id: string; name: string }[] }) => void;
    getCompanies.mockImplementation((_page: number, _size: number, term: string) => new Promise(resolve => {
      if (term === "") resolveOld = resolve;
      else resolveNew = resolve;
    }));
    render(<CompanySwitcherModal isOpen onClose={vi.fn()} />);
    await waitFor(() => expect(getCompanies).toHaveBeenCalledWith(1, 50, ""));
    fireEvent.change(screen.getByLabelText("Buscar empresa"), { target: { value: "Hospital" } });
    await waitFor(() => expect(getCompanies).toHaveBeenCalledWith(1, 50, "Hospital"));
    await act(async () => resolveNew({ items: [{ id: "b", name: "Hospital" }] }));
    expect(await screen.findByRole("button", { name: /Hospital/ })).toBeEnabled();
    await act(async () => resolveOld({ items: [{ id: "a", name: "Empresa antiga" }] }));
    expect(screen.getByRole("button", { name: /Hospital/ })).toBeEnabled();
    expect(screen.queryByRole("button", { name: /Empresa antiga/ })).not.toBeInTheDocument();
  });

  it("keeps previously loaded results visible but disabled when the refresh fails", async () => {
    getCompanies.mockResolvedValueOnce({ items: [{ id: "a", name: "Empresa A" }] })
      .mockRejectedValueOnce(new Error("falha sintética"));
    const { rerender } = render(<CompanySwitcherModal isOpen onClose={vi.fn()} />);
    expect(await screen.findByRole("button", { name: /Empresa A/ })).toBeEnabled();
    rerender(<CompanySwitcherModal isOpen={false} onClose={vi.fn()} />);
    rerender(<CompanySwitcherModal isOpen onClose={vi.fn()} />);
    expect(await screen.findByRole("alert")).toHaveTextContent("A lista anterior está desatualizada.");
    expect(screen.getByRole("button", { name: /Empresa A/ })).toBeDisabled();
  });
});
