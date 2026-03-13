# OpenRouterAgent

Simple .NET console AI agent that sends chat completions through OpenRouter.

## Requirements

- .NET 10 SDK
- An OpenRouter API key

## Configuration

Prefer user secrets or environment variables so the API key does not live in source control.

### Option 1: user secrets

```powershell
dotnet user-secrets set "OpenRouter:ApiKey" "<your-openrouter-api-key>" --project .\OpenRouterAgent.Console\OpenRouterAgent.Console.csproj
dotnet user-secrets set "OpenRouter:Model" "openai/gpt-4o-mini" --project .\OpenRouterAgent.Console\OpenRouterAgent.Console.csproj
```

### Option 2: environment variables

```powershell
$env:OPENROUTER__APIKEY = "<your-openrouter-api-key>"
$env:OPENROUTER__MODEL = "openai/gpt-4o-mini"
```

You can also edit [OpenRouterAgent.Console/appsettings.json](OpenRouterAgent.Console/appsettings.json) for non-secret defaults such as model, prompt, and temperature.

## Run

```powershell
dotnet run --project .\OpenRouterAgent.Console\OpenRouterAgent.Console.csproj
```

## Commands

- `/help` shows available commands
- `/reset` clears conversation history
- `/system <prompt>` replaces the system prompt and resets history
- `/exit` quits the app

## Notes

- The app validates required configuration on startup.
- Conversation history is kept in memory only for the current process.
- The OpenRouter client sets `HTTP-Referer` and `X-Title` headers when configured.