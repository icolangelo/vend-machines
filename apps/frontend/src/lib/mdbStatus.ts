export const MDB_STATUS_DETAILS: Record<string, { label: string; description: string }> = {
    inactive_state: {
        label: "Sem comunicação MDB",
        description: "A placa está sem comunicação ou acabou de ser ligada.",
    },
    disable_state: {
        label: "MDB bloqueado",
        description: "Há comunicação, mas a máquina ainda não autorizou o funcionamento ou possui algum bloqueio.",
    },
    enabled_state: {
        label: "Disponível para sessão",
        description: "O sistema está ativo, sem sessão aberta, e pode iniciar uma sessão.",
    },
    idle_state: {
        label: "Aguardando seleção",
        description: "Existe uma sessão aberta aguardando o usuário selecionar um produto.",
    },
    vend_state: {
        label: "Venda em andamento",
        description: "O produto foi selecionado e a máquina aguarda a venda ser aprovada ou negada.",
    },
};

export function getMdbStatusDetail(status?: string | null) {
    if (!status) {
        return {
            label: "Status MDB desconhecido",
            description: "Ainda não houve uma resposta válida ou o status está desatualizado.",
        };
    }
    return MDB_STATUS_DETAILS[status] ?? {
        label: "Status MDB desconhecido",
        description: `A máquina retornou um estado não reconhecido: ${status}.`,
    };
}
