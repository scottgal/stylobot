# NuGet publishing

The package is `Mostlylucid.SignalShingle`, multi-targeting .NET 8, 9, and 10. The package contains the Stylobot icon, README, Razor tag helper, and static web asset.

```bash
dotnet test tests/Mostlylucid.SignalShingle.Tests/Mostlylucid.SignalShingle.Tests.csproj
dotnet pack src/Mostlylucid.SignalShingle/Mostlylucid.SignalShingle.csproj -c Release
dotnet nuget push src/Mostlylucid.SignalShingle/bin/Release/*.nupkg --api-key "$NUGET_API_KEY" --source https://api.nuget.org/v3/index.json
```

Publish only a version that has been validated from a clean checkout. The included workflow uses [NuGet trusted publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing): it requests a GitHub OIDC token, exchanges it with `NuGet/login@v1` as the policy creator (`mostlylucid`) for a one-hour API key, then pushes the package. No long-lived NuGet API key is stored in GitHub.

On NuGet.org, create a trusted-publishing policy owned by the intended package owner with:

- Repository owner: `scottgal`
- Repository: `Mostlylucid.SignalShingle`
- Workflow file: `publish.yml`
- Environment: leave empty (the workflow does not use a GitHub Environment)

Publishing runs only for tags named `signalshingle-v*`, for example `signalshingle-v1.0.0`.
