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
dotnet user-secrets set "OpenRouter:Model" "stepfun/step-3.5-flash:free" --project .\OpenRouterAgent.Console\OpenRouterAgent.Console.csproj
```

### Option 2: environment variables

```powershell
$env:OPENROUTER__APIKEY = "<your-openrouter-api-key>"
$env:OPENROUTER__MODEL = "stepfun/step-3.5-flash:free"
```

You can also edit [OpenRouterAgent.Console/appsettings.json](OpenRouterAgent.Console/appsettings.json) for non-secret defaults such as model, prompt, and temperature.

Tool configuration is under `AgentTools`:

- `EnableTools`: globally turn tool-calling on/off
- `EnabledTools`: allow-list of tool names (empty means all tools)
- `DisabledTools`: block-list of tool names (applied after `EnabledTools`)

Example:

```json
"AgentTools": {
	"EnableTools": true,
	"EnabledTools": ["get_current_time", "sum_two_numbers"],
	"DisabledTools": ["get_suspects"]
}
```

## Run

```powershell
dotnet run --project .\OpenRouterAgent.Console\OpenRouterAgent.Console.csproj
```

Override model at runtime:

```powershell
dotnet run --project .\OpenRouterAgent.Console\OpenRouterAgent.Console.csproj -- --model "stepfun/step-3.5-flash:free"
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