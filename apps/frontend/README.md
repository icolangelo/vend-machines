# Frontend - Vend Machines

Este diretório contém a aplicação Frontend do projeto **Vend Machines**, responsável pela interface de gerenciamento das máquinas. O projeto foi construído utilizando as seguintes tecnologias:

- **React** (via Vite)
- **TypeScript**
- **Tailwind CSS**
- **Shadcn UI** (Componentes de interface)

---

## 🚀 Como Executar o Projeto Localmente

Siga os passos abaixo para configurar e rodar o projeto em sua máquina local.

### 1. Pré-requisitos
Certifique-se de ter o [Node.js](https://nodejs.org/) instalado em sua máquina. Recomenda-se a versão LTS mais recente (ex: 20.x ou superior).

### 2. Instalação das Dependências
Navegue até o diretório do frontend e instale as dependências:
```bash
cd apps/frontend
npm install
```

### 3. Rodando o Servidor de Desenvolvimento
Para iniciar a aplicação em modo de desenvolvimento com Hot-Module Replacement (HMR):
```bash
npm run dev
```
O servidor será iniciado logo em seguida. Acesse o link fornecido no terminal (geralmente `http://localhost:5173`) no seu navegador.

---

## 🛠️ Scripts Disponíveis

No diretório do projeto, você pode rodar os seguintes comandos:

- **`npm run dev`**: Inicia o servidor de desenvolvimento.
- **`npm run build`**: Transpila o TypeScript e faz o build de produção utilizando o Vite. Os arquivos gerados ficam na pasta `dist/`.
- **`npm run preview`**: Inicia um servidor local simples para você testar a build gerada no comando anterior.
- **`npm run lint`**: Roda o ESLint para encontrar (e possivelmente corrigir) problemas no código analisado.
- **`npm run test`**: Roda a suíte de testes unitários utilizando o Vitest.
- **`npm run test:watch`**: Roda os testes no modo "watch" (observador), re-executando-os sempre que houver mudanças.

---

## 📁 Estrutura de Pastas Principal

- `src/components/`: Componentes reutilizáveis, incluindo a base do Shadcn UI na pasta `ui/`.
- `src/pages/` ou `src/` (conforme as rotas): Telas da aplicação.
- `src/lib/`: Utilitários e configurações (ex: `utils.ts` do Tailwind).
- `src/hooks/`: Hooks customizados do React.
- `test/`: Arquivos relacionados à configurações de testes ou testes globais.