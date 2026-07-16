import React, { useState, useEffect, useRef } from "react";
import { Search, Building2, X, Eye, LogOut } from "lucide-react";
import { useImpersonation } from "@/contexts/ImpersonationContext";
import { API_BASE_URL } from "@/lib/api";

interface Company {
  id: string;
  name: string;
  cnpj: string;
}

interface CompanySwitcherModalProps {
  isOpen: boolean;
  onClose: () => void;
}

export function CompanySwitcherModal({ isOpen, onClose }: CompanySwitcherModalProps) {
  const { impersonatedCompany, setImpersonatedCompany, clearImpersonation } = useImpersonation();
  const [search, setSearch] = useState("");
  const [companies, setCompanies] = useState<Company[]>([]);
  const [loading, setLoading] = useState(false);
  const searchRef = useRef<HTMLInputElement>(null);

  useEffect(() => {
    if (!isOpen) return;
    setSearch("");
    searchRef.current?.focus();
    fetchCompanies("");
  }, [isOpen]);

  useEffect(() => {
    if (!isOpen) return;
    const debounce = setTimeout(() => fetchCompanies(search), 300);
    return () => clearTimeout(debounce);
  }, [search, isOpen]);

  const fetchCompanies = async (term: string) => {
    setLoading(true);
    try {
      const token = sessionStorage.getItem("token") || localStorage.getItem("token");
      const res = await fetch(
        `${API_BASE_URL}/companies?pageSize=50&searchTerm=${encodeURIComponent(term)}`,
        { headers: { Authorization: `Bearer ${token}` } }
      );
      if (res.ok) {
        const data = await res.json();
        setCompanies(data.items ?? []);
      }
    } finally {
      setLoading(false);
    }
  };

  const handleSelect = (company: Company) => {
    setImpersonatedCompany({ id: company.id, name: company.name });
    onClose();
    // Reload the page so all data refetches with the new company context
    window.location.reload();
  };

  const handleClear = () => {
    clearImpersonation();
    onClose();
    window.location.reload();
  };

  if (!isOpen) return null;

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center">
      {/* Overlay */}
      <div
        className="absolute inset-0 bg-black/50 backdrop-blur-sm"
        onClick={onClose}
      />

      {/* Modal */}
      <div className="relative z-10 w-full max-w-md mx-4 bg-white dark:bg-zinc-900 rounded-2xl shadow-2xl border border-zinc-200 dark:border-zinc-700 overflow-hidden">
        {/* Header */}
        <div className="flex items-center justify-between px-5 py-4 border-b border-zinc-100 dark:border-zinc-800">
          <div className="flex items-center gap-2">
            <Eye className="h-4 w-4 text-blue-500" />
            <h2 className="text-sm font-semibold text-zinc-800 dark:text-zinc-100">
              Visualizar como empresa
            </h2>
          </div>
          <button
            onClick={onClose}
            className="p-1 rounded-lg hover:bg-zinc-100 dark:hover:bg-zinc-800 transition-colors"
          >
            <X className="h-4 w-4 text-zinc-400" />
          </button>
        </div>

        {/* Active impersonation banner */}
        {impersonatedCompany && (
          <div className="flex items-center justify-between px-5 py-3 bg-amber-50 dark:bg-amber-900/20 border-b border-amber-200 dark:border-amber-800">
            <div className="flex items-center gap-2">
              <Eye className="h-3.5 w-3.5 text-amber-600" />
              <span className="text-xs text-amber-700 dark:text-amber-300">
                Visualizando como:{" "}
                <strong>{impersonatedCompany.name}</strong>
              </span>
            </div>
            <button
              onClick={handleClear}
              className="flex items-center gap-1 text-xs text-amber-700 dark:text-amber-300 hover:text-amber-900 dark:hover:text-amber-100 font-medium transition-colors"
            >
              <LogOut className="h-3 w-3" />
              Sair
            </button>
          </div>
        )}

        {/* Search */}
        <div className="px-4 pt-4 pb-2">
          <div className="relative">
            <Search className="absolute left-3 top-1/2 -translate-y-1/2 h-4 w-4 text-zinc-400 pointer-events-none" />
            <input
              ref={searchRef}
              type="text"
              value={search}
              onChange={(e) => setSearch(e.target.value)}
              placeholder="Buscar empresa..."
              className="w-full pl-9 pr-4 py-2 text-sm bg-zinc-50 dark:bg-zinc-800 border border-zinc-200 dark:border-zinc-700 rounded-lg focus:outline-none focus:ring-2 focus:ring-blue-500/40 placeholder:text-zinc-400"
            />
          </div>
        </div>

        {/* List */}
        <div className="px-2 pb-3 max-h-72 overflow-y-auto">
          {loading && (
            <div className="flex items-center justify-center py-8 text-xs text-zinc-400">
              Carregando empresas...
            </div>
          )}

          {!loading && companies.length === 0 && (
            <div className="flex items-center justify-center py-8 text-xs text-zinc-400">
              Nenhuma empresa encontrada
            </div>
          )}

          {!loading && companies.map((company) => {
            const isActive = impersonatedCompany?.id === company.id;
            return (
              <button
                key={company.id}
                onClick={() => handleSelect(company)}
                className={`w-full flex items-center gap-3 px-3 py-2.5 rounded-xl text-left transition-all duration-150 group
                  ${isActive
                    ? "bg-blue-50 dark:bg-blue-900/30 border border-blue-200 dark:border-blue-700"
                    : "hover:bg-zinc-50 dark:hover:bg-zinc-800 border border-transparent"
                  }`}
              >
                <div className={`h-8 w-8 rounded-lg flex items-center justify-center shrink-0
                  ${isActive ? "bg-blue-100 dark:bg-blue-900" : "bg-zinc-100 dark:bg-zinc-800 group-hover:bg-zinc-200 dark:group-hover:bg-zinc-700"}`}
                >
                  <Building2 className={`h-4 w-4 ${isActive ? "text-blue-600" : "text-zinc-500"}`} />
                </div>
                <div className="min-w-0">
                  <p className={`text-sm font-medium truncate ${isActive ? "text-blue-700 dark:text-blue-300" : "text-zinc-800 dark:text-zinc-200"}`}>
                    {company.name}
                  </p>
                  <p className="text-[11px] text-zinc-400 truncate">{company.cnpj}</p>
                </div>
                {isActive && (
                  <Eye className="h-3.5 w-3.5 text-blue-500 ml-auto shrink-0" />
                )}
              </button>
            );
          })}
        </div>
      </div>
    </div>
  );
}
