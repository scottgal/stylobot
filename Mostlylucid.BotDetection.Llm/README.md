# Mostlylucid.BotDetection.Llm

LLM abstraction layer for [Mostlylucid.BotDetection](https://www.nuget.org/packages/mostlylucid.botdetection).

Defines the interfaces and base classes for pluggable LLM-based bot classification providers. Install a concrete provider package to use LLM escalation:

| Provider | Package |
|----------|---------|
| Ollama (remote) | `Mostlylucid.BotDetection.Llm.Ollama` |
| LlamaSharp (local CPU) | `Mostlylucid.BotDetection.Llm.LlamaSharp` |

## Usage

```csharp
// Register in DI (provider packages add extension methods)
builder.Services.AddBotDetectionLlmOllama("http://localhost:11434", "qwen3:0.6b");
```

## License

[GNU AGPLv3](https://www.gnu.org/licenses/agpl-3.0)
