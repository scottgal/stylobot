# Endpoint Pinning

Endpoint pinning lets operators pre-register paths of interest and mark honeypot paths before traffic arrives.

---

## Quick Start

Inject `IPinnedEndpointStore` and call `AddAsync` to register paths at startup or on demand:

```csharp
public class MySetupService(IPinnedEndpointStore pins)
{
    public async Task SeedAsync()
    {
        await pins.AddAsync("ANY", "/wp-login.php", isHoneypot: true, note: "WordPress login probe");
        await pins.AddAsync("POST", "/xmlrpc.php", isHoneypot: true, note: "XML-RPC exploit probe");
        await pins.AddAsync("GET", "/api/health", isHoneypot: false, note: "Internal health check");
    }
}
```

No extra registration is needed. `IPinnedEndpointStore` is automatically available when you call `AddStyloBot(...)` or `AddStyloBotDashboard()`.

---

## Interface

```csharp
public sealed record PinnedEndpoint(long Id, string Method, string Path, bool IsHoneypot, string? Note, DateTimeOffset CreatedAt);

public interface IPinnedEndpointStore
{
    Task<IReadOnlyList<PinnedEndpoint>> GetAllAsync(CancellationToken ct = default);
    Task<PinnedEndpoint?> AddAsync(string method, string path, bool isHoneypot, string? note, CancellationToken ct = default);
    Task<bool> RemoveAsync(long id, CancellationToken ct = default);
}
```

The `Method` field accepts: `ANY`, `GET`, `POST`, `PUT`, `DELETE`, `PATCH`, `HEAD`, `OPTIONS`. Use `ANY` to match all methods on a path.

The `Note` field is for operator context only (why the path is interesting, which CVE it relates to, etc.). It has no effect on detection or policy.

---

## Dashboard API

The `/_stylobot` dashboard exposes three routes for managing pins.

### List pins

```bash
curl http://localhost:5080/_stylobot/api/endpoint-pins
```

Returns a JSON array of all `PinnedEndpoint` objects.

### Add a pin

```bash
curl -X POST http://localhost:5080/_stylobot/api/endpoint-pins \
  -H "Content-Type: application/json" \
  -d '{"path":"/wp-login.php","method":"ANY","isHoneypot":true,"note":"WordPress probe"}'
```

Body fields (JSON or form-encoded):

| Field | Type | Required |
|-------|------|----------|
| `path` | string | yes |
| `method` | string | yes |
| `isHoneypot` | bool | yes |
| `note` | string | no |

Returns `201` with the new pin object. Adding a duplicate `(method, path)` pair is a no-op; the existing record is returned.

### Remove a pin

```bash
# Replace 1 with the actual id from the pin object
curl -X DELETE http://localhost:5080/_stylobot/api/endpoint-pins/1
```

Returns `204` on success, `404` if the id does not exist.

---

## Dashboard UI

The Endpoints tab in `/_stylobot` shows a "Pin Endpoint" button that opens an inline form. Pinned paths show a pin icon; honeypot paths show a warning icon. Both the full sortable list and the compact view display these indicators.

---

## What Pinning Does

**Zero-traffic visibility.** Pinned paths appear in the endpoint detail view immediately, even before any traffic arrives. When traffic does arrive later, the pin flags are stamped onto the traffic record automatically.

**Holodeck hint.** Marking a path as a honeypot signals the holodeck system to serve fake responses to bots that probe it. This requires `AddApiHolodeck()` to be registered; the flag alone has no effect without it.

---

## What Pinning Does Not Do

- Pinning is an annotation layer. It does not change detection behavior by itself.
- The `isHoneypot` flag is a hint to the holodeck system, not an action policy. You still need `AddApiHolodeck()` for dynamic fake responses to be served.
- There are no per-method policy overrides on pins. Use `BotPolicyAttribute` on your controllers or endpoints for per-route detection thresholds.

---

## Implementation Details

`SqlitePinnedEndpointStore` backs the interface with a `pinned_endpoints` table in `sessions.db`. The table has a unique index on `(method, path)`. Duplicate inserts use `ON CONFLICT DO NOTHING` followed by a re-SELECT, so `AddAsync` always returns the canonical record. All writes go through a semaphore to serialize concurrent access.
