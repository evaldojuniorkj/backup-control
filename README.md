# Backup Control

Central para gerenciar backups SQL Server de clientes Windows. A API recebe resultados e comandos, o agente executa os backups locais e envia arquivos ZIP ao FTP, e o painel apresenta o estado das rotinas e os arquivos disponíveis.

## Código nesta versão

- `api/` contém as fontes da API de produção de 19/11/2026. `web/` inclui a notificação de sucesso nas atualizações de agentes e exibe os 20 resultados mais recentes no histórico por cliente.
- `agent/` e `installer/` contêm as fontes das versões 0.9.2 do agente e 0.9.0 do instalador Windows. Após perder a base diferencial, o agente aguarda a confirmação do próximo backup completo agendado antes de voltar a executar diferenciais automáticos.
- `build/` contém o script de empacotamento. Cada ambiente fornece suas configurações e materiais de assinatura fora do repositório.

## Executar a central localmente

1. Copie `.env.example` para `.env`. Preencha `ADMIN_PASSWORD` com uma senha local de pelo menos 16 caracteres e defina `POSTGRES_PASSWORD` para o banco de desenvolvimento.
2. Na raiz, execute `docker compose up -d --build`.
3. Abra [o painel local](http://127.0.0.1:5180) e entre com o administrador configurado no primeiro início.

O Compose usa volumes locais. Configure um destino FTP de desenvolvimento no painel para testar envio e download.

## Compilar e testar

Use .NET SDK 10 e Node.js com npm. Execute os testes do agente e do instalador no Windows.

```powershell
dotnet test api.tests/BackupControl.Api.Tests.csproj
dotnet test agent.tests/BackupControl.Agent.Tests.csproj
dotnet test installer.tests/BackupControl.Setup.Tests.csproj
npm --prefix web ci
npm --prefix web run build
node --test tests/dashboard.test.mjs
```

Os testes HTTP com PostgreSQL exigem a variável `API_TEST_DATABASE` apontando para um banco descartável. Os testes de integração têm efeitos sobre dados e serviços de teste.
