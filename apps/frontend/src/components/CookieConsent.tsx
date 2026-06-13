import React, { useState, useEffect } from "react";
import { ShieldCheck, Settings, X } from "lucide-react";

export function CookieConsent() {
  const [isVisible, setIsVisible] = useState(false);
  const [showSettings, setShowSettings] = useState(false);
  
  // Consentimentos individuais
  const [analyticsConsent, setAnalyticsConsent] = useState(false);
  const [marketingConsent, setMarketingConsent] = useState(false);

  useEffect(() => {
    const consent = localStorage.getItem("vm-cookies-consent");
    if (!consent) {
      // Pequeno atraso para dar uma sensação suave
      const timer = setTimeout(() => setIsVisible(true), 1500);
      return () => clearTimeout(timer);
    }
  }, []);

  const handleAcceptAll = () => {
    const consentObj = { essential: true, analytics: true, marketing: true };
    localStorage.setItem("vm-cookies-consent", JSON.stringify(consentObj));
    
    // Simular ativação das tags
    console.log("LGPD: Consentimento total concedido. Ativando Google Analytics e Meta Pixel...");
    
    setIsVisible(false);
  };

  const handleRejectAll = () => {
    const consentObj = { essential: true, analytics: false, marketing: false };
    localStorage.setItem("vm-cookies-consent", JSON.stringify(consentObj));
    
    console.log("LGPD: Apenas cookies essenciais permitidos.");
    
    setIsVisible(false);
  };

  const handleSaveCustom = () => {
    const consentObj = {
      essential: true,
      analytics: analyticsConsent,
      marketing: marketingConsent
    };
    localStorage.setItem("vm-cookies-consent", JSON.stringify(consentObj));
    
    console.log("LGPD: Consentimento personalizado salvo:", consentObj);
    
    setIsVisible(false);
  };

  if (!isVisible) return null;

  return (
    <div className="fixed bottom-6 left-6 right-6 md:left-auto md:right-6 md:max-w-md bg-white/95 backdrop-blur-md border border-slate-200/80 shadow-2xl rounded-2xl p-5 z-[9999] animate-in fade-in slide-in-from-bottom-5 duration-300 flex flex-col gap-4 font-sans text-slate-800">
      
      {/* Cabeçalho */}
      <div className="flex items-start justify-between gap-2">
        <div className="flex items-center gap-2">
          <div className="p-1.5 bg-blue-50 rounded-lg text-blue-600">
            <ShieldCheck className="w-5 h-5" />
          </div>
          <h4 className="font-bold text-sm text-slate-900 tracking-tight">Privacidade & Cookies</h4>
        </div>
        <button 
          onClick={handleRejectAll}
          className="text-slate-400 hover:text-slate-600 transition-colors p-1"
          title="Fechar e Rejeitar Opcionais"
        >
          <X className="w-4 h-4" />
        </button>
      </div>

      {/* Conteúdo Principal */}
      <div className="text-xs text-slate-600 leading-relaxed">
        {!showSettings ? (
          <p>
            Nós utilizamos cookies essenciais para o funcionamento da plataforma. Também desejamos utilizar cookies 
            opcionais para coletar dados anônimos de navegação (Google Analytics) e exibir anúncios otimizados (Meta Ads). 
            Você pode aceitar todos ou configurar suas preferências.
          </p>
        ) : (
          <div className="space-y-3 pt-1">
            <p className="text-[11px] text-slate-400 pb-1">Customize seus consentimentos de rastreamento:</p>
            
            {/* Essential (Sempre Ativo) */}
            <div className="flex items-center justify-between p-2 rounded-lg bg-slate-50 border">
              <div>
                <span className="font-bold text-slate-800 block text-[11px]">Cookies Essenciais</span>
                <span className="text-[10px] text-slate-400">Segurança, login e sessões da plataforma.</span>
              </div>
              <span className="text-[10px] font-bold text-blue-600 uppercase bg-blue-50 px-2 py-0.5 rounded border border-blue-200 select-none">
                Sempre Ativo
              </span>
            </div>

            {/* Analytics */}
            <div className="flex items-center justify-between p-2 rounded-lg bg-slate-50 border">
              <div>
                <span className="font-bold text-slate-800 block text-[11px]">Cookies de Análise (Google Analytics)</span>
                <span className="text-[10px] text-slate-400">Coleta estatísticas de uso para melhoria do sistema.</span>
              </div>
              <label className="relative inline-flex items-center cursor-pointer">
                <input 
                  type="checkbox" 
                  checked={analyticsConsent}
                  onChange={(e) => setAnalyticsConsent(e.target.checked)}
                  className="sr-only peer"
                />
                <div className="w-7 h-4 bg-slate-200 peer-focus:outline-none rounded-full peer peer-checked:after:translate-x-full peer-checked:after:border-white after:content-[''] after:absolute after:top-[2px] after:left-[2px] after:bg-white after:border-gray-300 after:border after:rounded-full after:h-3 after:w-3 after:transition-all peer-checked:bg-blue-600"></div>
              </label>
            </div>

            {/* Marketing */}
            <div className="flex items-center justify-between p-2 rounded-lg bg-slate-50 border">
              <div>
                <span className="font-bold text-slate-800 block text-[11px]">Cookies de Marketing (Meta Ads)</span>
                <span className="text-[10px] text-slate-400">Exibição e medição de anúncios relevantes para você.</span>
              </div>
              <label className="relative inline-flex items-center cursor-pointer">
                <input 
                  type="checkbox" 
                  checked={marketingConsent}
                  onChange={(e) => setMarketingConsent(e.target.checked)}
                  className="sr-only peer"
                />
                <div className="w-7 h-4 bg-slate-200 peer-focus:outline-none rounded-full peer peer-checked:after:translate-x-full peer-checked:after:border-white after:content-[''] after:absolute after:top-[2px] after:left-[2px] after:bg-white after:border-gray-300 after:border after:rounded-full after:h-3 after:w-3 after:transition-all peer-checked:bg-blue-600"></div>
              </label>
            </div>
          </div>
        )}
      </div>

      {/* Ações / Botões */}
      <div className="flex flex-wrap items-center justify-end gap-2 border-t pt-3 mt-1 border-slate-100">
        {!showSettings ? (
          <>
            <button
              onClick={() => setShowSettings(true)}
              className="text-xs font-semibold text-slate-500 hover:text-slate-700 flex items-center gap-1.5 mr-auto px-2 py-1.5 rounded-lg hover:bg-slate-50 transition-colors"
            >
              <Settings className="w-3.5 h-3.5" />
              Opções
            </button>
            
            <button
              onClick={handleRejectAll}
              className="text-xs font-semibold text-slate-600 hover:text-slate-800 px-3 py-1.5 rounded-lg border border-slate-200 bg-white hover:bg-slate-50 transition-all"
            >
              Rejeitar
            </button>
            <button
              onClick={handleAcceptAll}
              className="text-xs font-bold text-white bg-blue-600 hover:bg-blue-700 px-4 py-1.5 rounded-lg shadow-sm hover:shadow transition-all"
            >
              Aceitar Todos
            </button>
          </>
        ) : (
          <>
            <button
              onClick={() => setShowSettings(false)}
              className="text-xs font-semibold text-slate-500 hover:text-slate-700 mr-auto px-3 py-1.5 rounded-lg hover:bg-slate-50 transition-colors"
            >
              Voltar
            </button>
            <button
              onClick={handleSaveCustom}
              className="text-xs font-bold text-white bg-blue-600 hover:bg-blue-700 px-4 py-1.5 rounded-lg shadow-sm hover:shadow transition-all"
            >
              Salvar Preferências
            </button>
          </>
        )}
      </div>
    </div>
  );
}
