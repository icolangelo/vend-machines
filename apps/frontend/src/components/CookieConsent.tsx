import { useEffect, useState } from "react";
import { PrivacyPolicyModal } from "@/components/PrivacyPolicyModal";

const noticeStorageKey = "vm-cookies-notice-v2";

export function CookieConsent() {
  const [isVisible, setIsVisible] = useState(false);
  const [isPolicyOpen, setIsPolicyOpen] = useState(false);

  useEffect(() => {
    try {
      setIsVisible(localStorage.getItem(noticeStorageKey) !== "read");
    } catch {
      // The notice remains usable when browser storage is unavailable.
      setIsVisible(true);
    }
  }, []);

  const acknowledgeNotice = () => {
    try {
      localStorage.setItem(noticeStorageKey, "read");
    } catch {
      // Dismissing the notice must not depend on browser storage.
    }
    setIsVisible(false);
  };

  return (
    <>
      {isVisible && (
        <aside
          aria-label="Aviso sobre cookies e armazenamento"
          className="fixed bottom-6 left-6 right-6 md:left-auto md:right-6 md:max-w-md bg-white/95 backdrop-blur-md border border-slate-200/80 shadow-2xl rounded-2xl p-5 z-9999 font-sans text-slate-800"
        >
          <h2 className="font-bold text-sm text-slate-900">Privacidade e armazenamento</h2>
          <p className="mt-2 text-xs text-slate-600 leading-relaxed">
            Usamos armazenamento necessário para manter sua sessão e lembrar preferências da interface.
            Atualmente, a plataforma não carrega ferramentas de análise ou publicidade.
          </p>
          <div className="mt-4 flex flex-wrap items-center justify-end gap-3 border-t border-slate-100 pt-3">
            <button
              type="button"
              onClick={() => setIsPolicyOpen(true)}
              className="mr-auto text-xs font-semibold text-blue-700 hover:underline"
            >
              Política de Privacidade
            </button>
            <button
              type="button"
              onClick={acknowledgeNotice}
              className="text-xs font-bold text-white bg-blue-600 hover:bg-blue-700 px-4 py-2 rounded-lg"
            >
              Entendi
            </button>
          </div>
        </aside>
      )}
      <PrivacyPolicyModal isOpen={isPolicyOpen} onClose={() => setIsPolicyOpen(false)} />
    </>
  );
}
