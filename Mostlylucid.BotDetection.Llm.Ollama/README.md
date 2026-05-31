# Mostlylucid.BotDetection.Llm.Ollama

Ollama LLM provider for [Mostlylucid.BotDetection](https://www.nuget.org/packages/mostlylucid.botdetection) using [OllamaSharp](https://github.com/awaescher/OllamaSharp).

Connects to a local or remote Ollama instance for LLM-based bot classification and intent analysis.

## Installation

```bash
dotnet add package Mostlylucid.BotDetection.Llm.Ollama
```

## Usage

```csharp
builder.Services.AddAdvancedBotDetection("http://localhost:11434", "qwen3:0.6b");
```

## License

[GNU AGPLv3](https://www.gnu.org/licenses/agpl-3.0)
