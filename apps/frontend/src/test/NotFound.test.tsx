import { render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { describe, expect, it } from "vitest";
import NotFound from "@/pages/NotFound";

describe("NotFound", () => {
    it("shows the Portuguese message and links back through the application router", () => {
        render(
            <MemoryRouter initialEntries={["/rota-inexistente"]}>
                <NotFound />
            </MemoryRouter>,
        );

        expect(screen.getByText("Página não encontrada")).toBeInTheDocument();
        expect(screen.getByRole("link", { name: "Voltar ao início" })).toHaveAttribute("href", "/");
    });
});
