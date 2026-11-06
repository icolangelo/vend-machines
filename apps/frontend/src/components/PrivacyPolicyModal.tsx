import React from "react";
import {
  Dialog,
  DialogContent,
  DialogHeader,
  DialogTitle,
  DialogFooter,
} from "@/components/ui/dialog";
import { Button } from "@/components/ui/button";

interface PrivacyPolicyModalProps {
  isOpen: boolean;
  onClose: () => void;
}

export function PrivacyPolicyModal({ isOpen, onClose }: PrivacyPolicyModalProps) {
  return (
    <Dialog open={isOpen} onOpenChange={(open) => !open && onClose()}>
      <DialogContent className="max-w-2xl max-h-[85vh] flex flex-col p-6 font-sans">
        <DialogHeader className="pb-2 border-b">
          <DialogTitle className="text-xl font-bold text-slate-900">
            Política de Privacidade e Termos de Uso
          </DialogTitle>
        </DialogHeader>
        
        <div className="flex-1 overflow-y-auto my-4 pr-2 text-sm text-slate-600 space-y-4 leading-relaxed scrollbar-thin">
          <p className="font-semibold text-slate-800">
            Última atualização: 10 de junho de 2026
          </p>
          
          <p>
            Esta Política de Privacidade explica como o <strong>VendMachine.com.br</strong> coleta, usa, 
            compartilha e protege as informações dos usuários cadastrados na nossa plataforma, em conformidade com a 
            <strong> Lei Geral de Proteção de Dados Pessoais (LGPD - Lei nº 13.709/2018)</strong>.
          </p>

          <div>
            <h4 className="font-bold text-slate-800 mb-1">1. Informações que Coletamos</h4>
            <p className="mb-1">
              Coletamos dados necessários para identificar você e sua empresa e viabilizar o uso da plataforma:
            </p>
            <ul className="list-disc pl-5 space-y-1">
              <li><strong>Dados do Usuário:</strong> Nome Completo, E-mail Comercial, CPF, Senha Hachada e Perfil de acesso.</li>
              <li><strong>Dados da Empresa:</strong> Razão Social/Nome de Fantasia e CNPJ.</li>
              <li><strong>Dados Técnicos e de Navegação:</strong> Endereço IP, cookies de sessão, registros de telemetria de vendas de vending machines associadas e logs de sistema.</li>
            </ul>
          </div>

          <div>
            <h4 className="font-bold text-slate-800 mb-1">2. Finalidade do Tratamento de Dados</h4>
            <p className="mb-1">Os dados coletados são utilizados para:</p>
            <ul className="list-disc pl-5 space-y-1">
              <li>Criação de conta de usuário administrador e configuração do perfil da empresa.</li>
              <li>Gerenciamento de vending machines, monitoramento de telemetria e controle de estoque de produtos.</li>
              <li>Geração de relatórios financeiros e monitoramento de desempenho de vendas.</li>
              <li>Garantir a segurança e integridade do acesso à plataforma.</li>
            </ul>
          </div>

          <div>
            <h4 className="font-bold text-slate-800 mb-1">3. Cookies e Rastreamento (Google Analytics & Meta Ads)</h4>
            <p className="mb-2">
              Utilizamos cookies essenciais para manter você conectado de forma segura. Adicionalmente, nossa plataforma 
              contempla futuras integrações com serviços de terceiros para fins de análise e marketing:
            </p>
            <ul className="list-disc pl-5 space-y-1">
              <li><strong>Google Analytics:</strong> Coleta de dados de comportamento de navegação de forma anônima para compreender como nossos usuários interagem com a plataforma e aprimorar a usabilidade.</li>
              <li><strong>Meta Ads (Pixel do Facebook):</strong> Otimização de campanhas publicitárias e medição de conversões de visitas de marketing.</li>
            </ul>
            <p className="mt-2 text-xs italic">
              Você pode alterar suas preferências de cookies ou revogar seu consentimento a qualquer momento utilizando nosso painel de gerenciamento de cookies.
            </p>
          </div>

          <div>
            <h4 className="font-bold text-slate-800 mb-1">4. Seus Direitos (Art. 18 da LGPD)</h4>
            <p className="mb-1">
              Você, como titular de dados pessoais, tem direito a obter da plataforma, a qualquer momento, mediante requisição:
            </p>
            <ul className="list-disc pl-5 space-y-1">
              <li>Confirmação da existência de tratamento e acesso aos seus dados.</li>
              <li>Correção de dados incompletos, inexatos ou desatualizados.</li>
              <li>Eliminação de dados tratados com o seu consentimento (exceto quando o armazenamento for exigido por lei).</li>
              <li>Portabilidade dos dados a outro fornecedor de serviço.</li>
              <li>Revogação do consentimento concedido.</li>
            </ul>
          </div>

          <div>
            <h4 className="font-bold text-slate-800 mb-1">5. Compartilhamento e Segurança</h4>
            <p>
              Não comercializamos seus dados pessoais. Seus dados são armazenados em servidores seguros em nuvem e são compartilhados 
              apenas com provedores de infraestrutura estritamente necessários para o funcionamento do sistema e com ferramentas autorizadas por você (como cookies de terceiros).
            </p>
          </div>

          <div>
            <h4 className="font-bold text-slate-800 mb-1">6. Contato do DPO / Encarregado</h4>
            <p>
              Caso queira exercer quaisquer dos seus direitos de titular de dados ou esclarecer dúvidas sobre esta Política, 
              entre em contato com nosso Encarregado pelo Tratamento de Dados Pessoais (DPO) através do e-mail: 
              <a href="mailto:dpo@vendmachine.com.br" className="text-blue-600 font-semibold hover:underline ml-1">
                dpo@vendmachine.com.br
              </a>.
            </p>
          </div>
        </div>

        <DialogFooter className="pt-2 border-t flex items-center justify-end">
          <Button onClick={onClose} className="bg-blue-600 hover:bg-blue-700 text-white font-medium text-sm px-5 py-2">
            Entendido e Aceito
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
