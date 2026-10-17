# Publicação do frontend

O frontend agora é empacotado em uma imagem estática independente da API. A imagem executa NGINX sem root na porta interna `8080`, responde prontidão em `/health`, serve rotas do React Router com fallback para `index.html`, aplica cabeçalhos de segurança e evita cache prolongado do HTML. Arquivos com hash em `/assets/` podem ser cacheados por um ano.

O workflow `.github/workflows/frontend-image.yml` executa audit de dependências, tipos, lint, Vitest e Playwright no Vite, depois carrega uma única imagem SHA no Docker, testa-a com Chromium e publica essa mesma imagem no GHCR como `ghcr.io/<owner>/vend-machines-frontend:<sha>`. Não existe uma segunda reconstrução entre o teste e o push. O fluxo não publica `latest`, não chama Coolify e não altera nenhum serviço. O endpoint da API é compilado em `VITE_API_URL`; variáveis Vite não são segredos, pois ficam visíveis nos arquivos JavaScript entregues ao navegador.

Para homologar a imagem, crie/atualize um serviço frontend separado na plataforma, escolha o SHA exato publicado, use a porta `8080` e configure o health check HTTP `/health`. Configure TLS e o domínio no proxy e revise a política CSP para os fluxos realmente usados, incluindo autenticação e integrações de pagamento. O serviço frontend deve permitir rollback para a imagem SHA anterior. A atualização da interface não reinicia a API nem seus WebSockets de telemetria.

Ainda não há evidência de que esse serviço separado foi configurado no Coolify, de que o GHCR foi autorizado nessa instância ou de que o domínio real da aplicação aponta para ele. Não promova um candidato até homologar login, navegação direta em rotas, refresh de página, chamadas API, WebSocket do painel e rollback no domínio de homologação.
