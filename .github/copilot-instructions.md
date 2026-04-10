# Project Guidelines

## Code Style
- Follow existing C# conventions used across both projects: top-level Program files, dependency injection, options binding, and sealed concrete classes for services/tools.
- For new agent tools, implement IAgentTool with:
  - stable snake_case Name constant
  - ChatToolDefinition schema in Definition
  - ExecuteAsync(ChatToolCall, CancellationToken) returning ToolExecutionResult
- Keep changes small and local. Prefer extending existing services/registries over adding parallel patterns.

## Architecture
- Solution contains two executable projects:
  - OpenRouterAgent.Console: AI agent with interactive mode and HTTP mode.
  - NegotiationsTool.Console: HTTP query API over CSV-backed SQLite data.
- OpenRouterAgent boundaries:
  - Agent/: orchestration, session state, console loop.
  - Agent/Tools/: tool implementations + registry.
  - OpenRouter/: client contracts, DTOs, and provider options.
- NegotiationsTool boundaries:
  - Services/: initialization state, CSV download, DB init/import, and query flow.
  - Interfaces/: contracts used by services.
  - Models/: request/response payloads.

## Build and Test
- Build solution:
  - dotnet build OpenRouterAgent.sln
  - or use the VS Code task: dotnet: build
- Run OpenRouterAgent (interactive):
  - dotnet run --project .\OpenRouterAgent.Console\OpenRouterAgent.Console.csproj
- Run OpenRouterAgent (HTTP serve mode):
  - dotnet run --project .\OpenRouterAgent.Console\OpenRouterAgent.Console.csproj -- --serve
- Run NegotiationsTool:
  - dotnet run --project .\NegotiationsTool.Console\NegotiationsTool.Console.csproj
- No dedicated test projects are currently present in this repository.

## Conventions
- Configuration is critical and validated on startup. Prefer user-secrets or environment variables for keys; do not hardcode secrets.
- Task-specific agent profiles are selected via DOTNET_ENVIRONMENT (for example task19 -> appsettings.task19.json).
- In OpenRouterAgent, keep tool registration centralized in Program.cs and BuiltInAgentToolRegistry.
- In NegotiationsTool, treat startup as asynchronous initialization:
  - /query may return initializing state until CSV->SQLite setup is complete.
  - preserve existing DB reuse behavior when tables already exist.
- When changing provider behavior, preserve OpenRouterOptions helper methods (effective base URL, key selection, responses path).

## Documentation
- Primary documentation is in README.md. Link to README sections instead of duplicating configuration tables or full command examples.
