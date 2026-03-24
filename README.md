# OpenRouterAgent

Simple .NET console AI agent that sends chat completions through LLM Router responses API.

## Requirements

- .NET 10 SDK
- An LLM Router API key

## Configuration

Prefer user secrets or environment variables so the API key does not live in source control.

Configuration sources used by the app:

- `OpenRouterAgent.Console/appsettings.json`
- `OpenRouterAgent.Console/appsettings.{DOTNET_ENVIRONMENT}.json`
- user secrets
- environment variables
- command-line args

### Option 1: user secrets

```powershell
dotnet user-secrets set "OpenRouter:Provider" "llmrouter" --project .\OpenRouterAgent.Console\OpenRouterAgent.Console.csproj
dotnet user-secrets set "LlmRouter:ApiKey" "<your-llmrouter-api-key>" --project .\OpenRouterAgent.Console\OpenRouterAgent.Console.csproj
dotnet user-secrets set "OpenRouter:ApiKey" "<your-openrouter-api-key>" --project .\OpenRouterAgent.Console\OpenRouterAgent.Console.csproj
dotnet user-secrets set "OpenRouter:Model" "stepfun/step-3.5-flash:free" --project .\OpenRouterAgent.Console\OpenRouterAgent.Console.csproj
dotnet user-secrets set "AgentTools:ApiKey" "<your-ag3nts-api-key>" --project .\OpenRouterAgent.Console\OpenRouterAgent.Console.csproj
```

### Option 2: environment variables

```powershell
$env:OPENROUTER__PROVIDER = "llmrouter"
$env:LLMROUTER__APIKEY = "<your-llmrouter-api-key>"
$env:OPENROUTER__APIKEY = "<your-openrouter-api-key>"
$env:OPENROUTER__MODEL = "stepfun/step-3.5-flash:free"
$env:AGENTTOOLS__APIKEY = "<your-ag3nts-api-key>"
```

You can also edit [OpenRouterAgent.Console/appsettings.json](OpenRouterAgent.Console/appsettings.json) for defaults such as provider, model, prompt, temperature, tool configuration, and logging.

Quick switching:
- Set OpenRouter:Provider to llmrouter or openrouter.
- Keep both keys configured (LlmRouter:ApiKey and OpenRouter:ApiKey).
- Optionally use --provider or -p at runtime.

### OpenRouter options

Section: `OpenRouter`

| Key | Type | Required | Default | Notes |
| --- | --- | --- | --- | --- |
| `Provider` | `string` | no | `llmrouter` | Active provider. Supported values: `llmrouter`, `openrouter` |
| `BaseUrl` | `string` | no | empty | Optional override for active provider base URL |
| `OpenRouterBaseUrl` | `string` | no | `https://openrouter.ai/` | Default base URL when provider is `openrouter` |
| `LlmRouterBaseUrl` | `string` | no | `https://llmrouter.gft.com/openai/v1/` | Default base URL when provider is `llmrouter` |
| `ApiKey` | `string` | no* | empty | OpenRouter API key (required when provider is `openrouter`) |
| `Model` | `string` | yes | `openai/gpt-4o-mini` | Primary chat model |
| `ToolModel` | `string` | no | `openrouter/free` | Model used by tool calls such as OCR and image-description flows |
| `SystemPrompt` | `string` | no | built-in assistant prompt | Initial system prompt |
| `Temperature` | `decimal` | no | `0.2` | Sampling temperature |
| `MaxTokens` | `int?` | no | `null` | Optional max completion tokens |
| `AppName` | `string?` | no | `OpenRouterAgent.Console` | Sent as `X-Title` header |
| `AppUrl` | `string?` | no | `null` | Sent as `HTTP-Referer` header |

Environment variable mappings:

- `OPENROUTER__PROVIDER`
- `OPENROUTER__BASEURL`
- `OPENROUTER__OPENROUTERBASEURL`
- `OPENROUTER__LLMROUTERBASEURL`
- `OPENROUTER__APIKEY`
- `OPENROUTER__MODEL`
- `OPENROUTER__TOOLMODEL`
- `OPENROUTER__SYSTEMPROMPT`
- `OPENROUTER__TEMPERATURE`
- `OPENROUTER__MAXTOKENS`
- `OPENROUTER__APPNAME`
- `OPENROUTER__APPURL`

### LlmRouter options

Section: `LlmRouter`

| Key | Type | Required | Default | Notes |
| --- | --- | --- | --- | --- |
| `ApiKey` | `string` | yes | empty | API key used for calls to `https://llmrouter.gft.com/openai/v1/responses` |

Environment variable mappings:

- `LLMROUTER__APIKEY`

### AgentTools options

Section: `AgentTools`

| Key | Type | Required | Default | Notes |
| --- | --- | --- | --- | --- |
| `EnableTools` | `bool` | no | `true` | Globally enables or disables tool calling |
| `EnabledTools` | `string[]` | no | empty array | Allow-list. Empty means all registered tools are allowed |
| `DisabledTools` | `string[]` | no | empty array | Block-list applied after `EnabledTools` |
| `ApiKey` | `string` | task-dependent | empty | API key used by AG3NTS task tools that call the hub/verify endpoints |

Environment variable mappings:

- `AGENTTOOLS__ENABLETOOLS`
- `AGENTTOOLS__ENABLEDTOOLS__0`
- `AGENTTOOLS__ENABLEDTOOLS__1`
- `AGENTTOOLS__DISABLEDTOOLS__0`
- `AGENTTOOLS__DISABLEDTOOLS__1`
- `AGENTTOOLS__APIKEY`

Tool configuration is under `AgentTools`:

- `EnableTools`: globally turn tool-calling on/off
- `EnabledTools`: allow-list of tool names (empty means all tools)
- `DisabledTools`: block-list of tool names (applied after `EnabledTools`)

Registered tool names you can use in `EnabledTools` / `DisabledTools`:

- `get_current_time`
- `sum_two_numbers`
- `distance_between_locations`
- `get_suspects`
- `get_suspect_locations`
- `get_suspect_access_level`
- `get_powerplant_locations`
- `check_package_status`
- `redirect_package`
- `GetDocumentation`
- `ocr_image_document`
- `describe_connection_map`
- `rotate_tile`
- `verify_declaration`
- `get_cargo_descriptions`
- `categorize_cargo`
- `railway_api`

Example:

```json
"AgentTools": {
	"EnableTools": true,
	"EnabledTools": ["get_current_time", "sum_two_numbers"],
	"DisabledTools": ["get_suspects"]
}
```

Example full configuration:

```json
{
	"OpenRouter": {
		"Provider": "llmrouter",
		"OpenRouterBaseUrl": "https://openrouter.ai/",
		"LlmRouterBaseUrl": "https://llmrouter.gft.com/openai/v1/",
		"BaseUrl": "",
		"ApiKey": "",
		"Model": "openai/gpt-5-mini",
		"ToolModel": "google/gemini-3-flash-preview",
		"SystemPrompt": "You are a concise and reliable AI assistant.",
		"Temperature": 0.2,
		"MaxTokens": 6048,
		"AppName": "OpenRouterAgent.Console",
		"AppUrl": ""
	},
	"LlmRouter": {
		"ApiKey": ""
	},
	"AgentTools": {
		"EnableTools": true,
		"EnabledTools": ["describe_connection_map", "rotate_tile"],
		"DisabledTools": [],
		"ApiKey": ""
	}
}
```

## Run
Set `DOTNET_ENVIRONMENT` for a specific task profile.

Example:

```
set DOTNET_ENVIRONMENT=task2
```


Then run the console app:

```powershell
dotnet run --project .\OpenRouterAgent.Console\OpenRouterAgent.Console.csproj
```

Override model at runtime:

```powershell
dotnet run --project .\OpenRouterAgent.Console\OpenRouterAgent.Console.csproj -- --model "stepfun/step-3.5-flash:free"
```

Start HTTP service mode instead of the interactive console:

```powershell
dotnet run --project .\OpenRouterAgent.Console\OpenRouterAgent.Console.csproj -- --serve
```

## Command-line arguments

The application currently supports these command-line arguments:

| Argument | Value | Description |
| --- | --- | --- |
| `--serve` | none | Run as HTTP service instead of the interactive console |
| `--model` | required | Override `OpenRouter:Model` |
| `-m` | required | Short form of `--model` |
| `--provider` | required | Override `OpenRouter:Provider` (`llmrouter` or `openrouter`) |
| `-p` | required | Short form of `--provider` |

Examples:

```powershell
dotnet run --project .\OpenRouterAgent.Console\OpenRouterAgent.Console.csproj -- --serve
dotnet run --project .\OpenRouterAgent.Console\OpenRouterAgent.Console.csproj -- --model "openai/gpt-5-mini"
dotnet run --project .\OpenRouterAgent.Console\OpenRouterAgent.Console.csproj -- -m "openai/gpt-5-mini"
dotnet run --project .\OpenRouterAgent.Console\OpenRouterAgent.Console.csproj -- --provider llmrouter
dotnet run --project .\OpenRouterAgent.Console\OpenRouterAgent.Console.csproj -- -p openrouter
dotnet run --project .\OpenRouterAgent.Console\OpenRouterAgent.Console.csproj -- --serve --model "openai/gpt-5-mini" --provider llmrouter
```

Notes:

- `--serve` is removed from the configuration argument list before config binding and acts as a mode switch only.
- `--model` and `-m` both map to `OpenRouter:Model`.
- `--provider` and `-p` both map to `OpenRouter:Provider`.

HTTP endpoints in serve mode:

- `POST /chat`
- `DELETE /chat/{sessionId}`

## Commands

Interactive console commands:

- `/help`: show available commands
- `/reset`: clear the current conversation history
- `/tools`: list all currently registered tools
- `/tool <name> [json]`: execute a tool directly with optional JSON arguments
- `/system <prompt>`: replace the system prompt and reset history
- `/exit`: exit the application
- `/quit`: alias for `/exit`

Examples:

```text
/tools
/tool get_current_time
/tool sum_two_numbers {"a":2,"b":3}
/system You are a strict debugging assistant.
```

## Notes

- The app validates required configuration on startup.
- Required startup settings are provider-dependent API key (`LlmRouter:ApiKey` for llmrouter or `OpenRouter:ApiKey` for openrouter) and `OpenRouter:Model`.
- Conversation history is kept in memory only for the current process.
- The client sets `HTTP-Referer` and `X-Title` headers when configured.
- Runtime logs are written to the `logs` directory.