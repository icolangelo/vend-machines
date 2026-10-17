import { render, screen } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";
import ProtectedRoute from "@/components/ProtectedRoute";

const auth = vi.hoisted(() => ({
    state: { token: "test-token" as string | null, user: { role: "Admin" }, loading: false },
}));

vi.mock("@/contexts/AuthContext", () => ({
    useAuth: () => auth.state,
}));

describe("ProtectedRoute role restrictions", () => {
    beforeEach(() => {
        auth.state = { token: "test-token", user: { role: "Admin" }, loading: false };
    });

    const renderRestrictedRoute = (path = "/payment-simulator") => render(
        <MemoryRouter initialEntries={[path]}>
            <Routes>
                <Route
                    path="/payment-simulator"
                    element={
                        <ProtectedRoute allowedRoles={["Admin", "PlatformAdmin"]}>
                            <div>Simulador Pix autorizado</div>
                        </ProtectedRoute>
                    }
                />
                <Route
                    path="/machines/new"
                    element={
                        <ProtectedRoute allowedRoles={["Admin", "PlatformAdmin"]}>
                            <div>Formulário da máquina</div>
                        </ProtectedRoute>
                    }
                />
                <Route
                    path="/machines/:id/edit"
                    element={
                        <ProtectedRoute allowedRoles={["Admin", "PlatformAdmin"]}>
                            <div>Formulário da máquina</div>
                        </ProtectedRoute>
                    }
                />
                <Route path="/dashboard" element={<div>Painel</div>} />
                <Route path="/" element={<div>Login</div>} />
            </Routes>
        </MemoryRouter>,
    );

    it.each(["Admin", "PlatformAdmin"])("allows %s", (role) => {
        auth.state.user = { role };

        renderRestrictedRoute();

        expect(screen.getByText("Simulador Pix autorizado")).toBeInTheDocument();
    });

    it("redirects Operator to the dashboard", () => {
        auth.state.user = { role: "Operator" };

        renderRestrictedRoute();

        expect(screen.getByText("Painel")).toBeInTheDocument();
        expect(screen.queryByText("Simulador Pix autorizado")).not.toBeInTheDocument();
    });

    it.each(["/machines/new", "/machines/machine-1/edit"])("redirects Operator from %s", (path) => {
        auth.state.user = { role: "Operator" };

        renderRestrictedRoute(path);

        expect(screen.getByText("Painel")).toBeInTheDocument();
        expect(screen.queryByText("Formulário da máquina")).not.toBeInTheDocument();
    });
});
