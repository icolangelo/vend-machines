import React from "react";
import {
  Dialog,
  DialogContent,
  DialogDescription,
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
            Política de Privacidade
          </DialogTitle>
          <DialogDescription className="sr-only">
            Informações sobre os dados tratados e o armazenamento necessário para usar a plataforma.
          </DialogDescription>
        </DialogHeader>
        
        <div className="flex-1 overflow-y-auto my-4 pr-2 text-sm text-slate-600 space-y-4 leading-relaxed scrollbar-thin">
          <p className="font-semibold text-slate-800">
            Última atualização: 26 de outubro de 2026
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
              <li><strong>Dados do Usuário:</strong> nome completo, e-mail comercial, CPF, senha protegida por hash e perfil de acesso.</li>
              <li><strong>Dados da Empresa:</strong> Razão Social/Nome de Fantasia e CNPJ.</li>
              <li><strong>Dados Técnicos e de Navegação:</strong> endereço IP, cookies de sessão, registros de telemetria de vendas das máquinas associadas e registros do sistema.</li>
              <li><strong>Dados de Pagamento:</strong> valor, descrição e referência da transação; status e identificadores recebidos do provedor; nome e e-mail do pagador e CPF quando informados para a cobrança.</li>
            </ul>
          </div>

          <div>
            <h4 className="font-bold text-slate-800 mb-1">2. Finalidade do Tratamento de Dados</h4>
            <p className="mb-1">Os dados coletados são utilizados para:</p>
            <ul className="list-disc pl-5 space-y-1">
              <li>Criação de conta de usuário administrador e configuração do perfil da empresa.</li>
              <li>Gerenciamento das máquinas de venda automática, monitoramento de telemetria e controle de estoque de produtos.</li>
              <li>Geração de relatórios financeiros e monitoramento de desempenho de vendas.</li>
              <li>Garantir a segurança e integridade do acesso à plataforma.</li>
            </ul>
          </div>

          <div>
            <h4 className="font-bold text-slate-800 mb-1">3. Cookies e armazenamento local</h4>
            <p className="mb-2">
              Utilizamos armazenamento técnico necessário para manter sua sessão e lembrar preferências da interface.
              Atualmente, a plataforma não carrega ferramentas de análise ou publicidade de terceiros.
            </p>
            <p className="mt-2 text-xs italic">
              Se forem adicionadas ferramentas opcionais de análise ou publicidade, esta política e o aviso de privacidade
              serão atualizados antes da ativação.
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
              Não comercializamos seus dados pessoais. Para processar e reconciliar pagamentos, compartilhamos com o Mercado Pago os dados necessários à cobrança, como valor, descrição ou referência da transação e os dados do pagador informados. Para confirmação de e-mail, convites e recuperação de acesso, o provedor de e-mail configurado recebe o endereço do destinatário e o link de uso único correspondente. Também usamos provedores de infraestrutura necessários para operar a plataforma.
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
            Fechar
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
