# Third-Party Dependencies

StyloBot wouldn't exist without these projects. Listed by purpose, with links to source repos and the project files where they're consumed. Microsoft.* framework dependencies (`Microsoft.AspNetCore.*`, `Microsoft.Extensions.*`) are baseline ASP.NET Core 10 and not enumerated separately.

If we use your project and you're not listed here, please open an issue - credit is owed and we want to get it right.

## Detection pipeline

| Project | Purpose | License | Used in |
|---|---|---|---|
| [VYaml](https://github.com/hadashiA/VYaml) | AOT-native YAML parser + source generator. Replaced YamlDotNet in 6.5.0; powers the manifest loader for 57 detectors. | MIT | `Mostlylucid.BotDetection` |
| [HNSW](https://github.com/curiosity-ai/hnsw-sharp) | Hierarchical Navigable Small World approximate-nearest-neighbour index. Sub-millisecond cosine search over fingerprint centroids for Leiden community detection + entity resolution. | MIT | `Mostlylucid.BotDetection` |
| [MathNet.Numerics](https://github.com/mathnet/mathnet-numerics) | Statistics (Fisher discriminant ratios for identity calibration), distribution fitting, FFT (autocorrelation in the behavioural waveform detector). | MIT | `Mostlylucid.BotDetection` |
| [AngleSharp](https://github.com/AngleSharp/AngleSharp) | HTML parser used by the holodeck to inject HMAC canary tokens into fake responses without breaking the served DOM. | MIT | `Mostlylucid.BotDetection` |
| [Microsoft.Recognizers.Text](https://github.com/microsoft/Recognizers-Text) | Date/time/numeric phrase recognition for intent classification. | MIT | `Mostlylucid.BotDetection` |

## Reverse proxy + transport

| Project | Purpose | License | Used in |
|---|---|---|---|
| [YARP (Yet Another Reverse Proxy)](https://github.com/dotnet/yarp) | The gateway, sidecar, and `stylobot-all` all use YARP for upstream routing + per-request detection hooks. | MIT | `Stylobot.Gateway`, `Stylobot.All`, `Mostlylucid.BotDetection.Console` |
| [Grpc.AspNetCore](https://github.com/grpc/grpc-dotnet) | gRPC server for the sidecar's `Detect` RPC. | Apache-2.0 | `Mostlylucid.BotDetection.Sidecar` |

## SQLite + storage

| Project | Purpose | License | Used in |
|---|---|---|---|
| [Microsoft.Data.Sqlite](https://github.com/dotnet/efcore/tree/main/src/Microsoft.Data.Sqlite.Core) | Raw ADO.NET SQLite client used directly (no EF Core) for the dashboard event store, identity layer, cluster snapshot, label / approval / pin / session stores. | MIT | core |
| [SQLitePCLRaw.bundle_e_sqlite3](https://github.com/ericsink/SQLitePCL.raw) | Bundled native SQLite for cross-platform single-file deploys. | Apache-2.0 | core |

## Dashboard + UI

| Project | Purpose | License | Used in |
|---|---|---|---|
| [Microsoft.AspNetCore.SignalR](https://github.com/dotnet/aspnetcore) + [SignalR.Client](https://github.com/dotnet/aspnetcore) | Real-time invalidation beacons from gateway → browser via local hub; client used by `Stylobot.Ui`'s `SignalRBeaconRelay` to forward beacons from a remote gateway. | MIT | `Mostlylucid.BotDetection.UI`, `Stylobot.Ui` |
| [Fluid.Core](https://github.com/sebastienros/fluid) | Liquid template engine for the Node SDK widget rendering path. | MIT | `Mostlylucid.BotDetection.UI` |
| [Markdig](https://github.com/xoofx/markdig) | Renders the dashboard's in-app help content (markdown → HTML). | BSD-2-Clause | `Mostlylucid.BotDetection.UI` |
| [XenoAtom.Terminal.UI](https://github.com/xoofx/XenoAtom.Terminal.UI) | Live-update TUI for `stylobot dashboard <url>` (remote dashboard viewer in the terminal). | BSD-2-Clause | `Mostlylucid.BotDetection.Console` |

## LLM providers

LLM features are opt-in. Enabling any provider pulls in its respective dependency.

| Project | Purpose | License | Used in |
|---|---|---|---|
| [LLamaSharp](https://github.com/SciSharp/LLamaSharp) | In-process LLM inference via llama.cpp; default for cluster naming + classification escalation on Apple Silicon (Metal). | MIT | `Mostlylucid.BotDetection.Llm.LlamaSharp` |
| [OllamaSharp](https://github.com/awaescher/OllamaSharp) | HTTP client for a local or remote Ollama server. | MIT | `Mostlylucid.BotDetection.Llm.Ollama` |
| [mostlylucid.mockllmapi](https://www.nuget.org/packages/mostlylucid.mockllmapi) | LLM-shaped mock API for development against fake providers. | MIT | `Mostlylucid.BotDetection.Demo` |

## Geo / certificates / mail

| Project | Purpose | License | Used in |
|---|---|---|---|
| [MaxMind.GeoIP2](https://github.com/maxmind/GeoIP2-dotnet) | Optional MaxMind GeoLite2 database lookup for country/city/ASN enrichment. Falls back to ip-api when MaxMind isn't configured. | Apache-2.0 | `Mostlylucid.GeoDetection` |
| [LettuceEncrypt](https://github.com/natemcmaster/LettuceEncrypt) | Let's Encrypt certificate provisioning for the gateway. | Apache-2.0 | `Stylobot.Gateway` |
| [MailKit](https://github.com/jstedfast/MailKit) + [MimeKit](https://github.com/jstedfast/MimeKit) | SMTP send for dashboard auth flows (confirmation, password reset). | MIT | `Mostlylucid.BotDetection.UI` |
| [SmtpServer](https://github.com/cosullivan/SmtpServer) | Local test SMTP server for dashboard auth flows in development. | MIT | `Mostlylucid.BotDetection.UI` |

## Telemetry + logging

| Project | Purpose | License | Used in |
|---|---|---|---|
| [Serilog](https://github.com/serilog/serilog) (+ Serilog.AspNetCore, Sinks.Console, Sinks.File, Settings.Configuration) | Structured logging across every binary. | Apache-2.0 | every binary |
| [OpenTelemetry .NET](https://github.com/open-telemetry/opentelemetry-dotnet) | OpenTelemetry SDK + ASP.NET Core instrumentation + Prometheus scraping endpoint. | Apache-2.0 | core, `Stylobot.Gateway` |

## Detection ergonomics + atoms (Mostlylucid)

These are first-party packages by the same author, but live in separate repos and are listed for transparency.

| Project | Purpose | License | Source |
|---|---|---|---|
| [Mostlylucid.StyloFlow.Core](https://github.com/scottgal/styloflow) + StyloFlow.Retrieval.Core | Manifest-driven detector composition + signal/analysis wave framework. | MIT | scottgal/styloflow |
| [Mostlylucid.Ephemeral.*](https://github.com/scottgal/mostlylucid.atoms) (`.Atoms.Taxonomy`, `.Atoms.KeyedSequential`, `.Atoms.SlidingCache`, `.Atoms.Batching`) | Per-request signal sink + coordination primitives (DetectionLedger, blackboard atoms, sliding caches). | MIT | scottgal/mostlylucid.atoms |

## Tests + benchmarks (not shipped in runtime binaries)

| Project | Purpose | License |
|---|---|---|
| [xUnit](https://github.com/xunit/xunit) | Unit + integration test framework. | Apache-2.0 |
| [BenchmarkDotNet](https://github.com/dotnet/BenchmarkDotNet) | Detector benchmark harness in `Mostlylucid.BotDetection.Benchmarks`. | MIT |
| [PuppeteerSharp](https://github.com/hardkoded/puppeteer-sharp) | Headless-browser detection tests. | MIT |
| [Moq](https://github.com/devlooped/moq) / [NSubstitute](https://github.com/nsubstitute/NSubstitute) / [FluentAssertions](https://github.com/fluentassertions/fluentassertions) / [Verify.Xunit](https://github.com/VerifyTests/Verify) / [Bogus](https://github.com/bchavez/Bogus) | Test utilities. | MIT / Apache-2.0 (FluentAssertions: Apache-2.0 ≤ v7) |
| [coverlet.collector](https://github.com/coverlet-coverage/coverlet) | Coverage. | MIT |

## Build + release tooling

| Project | Purpose | License |
|---|---|---|
| [MinVer](https://github.com/adamralph/minver) | Git-tag-driven SemVer for NuGet packages (`allbot-v*` tags). | Apache-2.0 |
| [Grpc.Tools](https://github.com/grpc/grpc-dotnet) | Build-time `.proto` → C# code generation. | Apache-2.0 |
| [System.IO.Hashing](https://github.com/dotnet/runtime/tree/main/src/libraries/System.IO.Hashing) | xxHash for fingerprint identity keys. | MIT |
| [System.Numerics.Tensors](https://github.com/dotnet/runtime/tree/main/src/libraries/System.Numerics.Tensors) | SIMD-accelerated vector ops for cosine similarity. | MIT |

## Distribution channels

Not direct dependencies but worth crediting - the people whose pipelines we lean on for distribution.

- [Docker Hub](https://hub.docker.com/u/scottgal) - `scottgal/stylobot`, `scottgal/stylobot-gateway`, `scottgal/stylobot-sidecar`, `scottgal/stylobot-demo`, `scottgal/stylobot-ui`, `scottgal/stylobot-all`.
- [Sigstore cosign](https://github.com/sigstore/cosign) - Docker image signing.
- [Chocolatey](https://chocolatey.org/) - Windows package distribution.
- [Homebrew](https://brew.sh/) - macOS / Linux package distribution via `scottgal/stylobot` tap.
- [NuGet](https://www.nuget.org/) - library distribution.
- [Cloudsmith](https://cloudsmith.io/) - apt / yum distribution for Linux servers.