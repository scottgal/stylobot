At a high level you’d be building **“Stylobot bouncer” modules for Caddy**, very similar in spirit to the CrowdSec Caddy
bouncer (which already supports both HTTP and Layer4) – except your bouncer talks to **Stylobot** instead of
CrowdSec. ([GitHub][1])

Below is the pattern I’d use.

---

## 1. Overall idea: Stylobot as an external decision engine

Keep Stylobot exactly as you have it now:

* ASP.NET service that exposes `/api/detect`
* You pass:

    * IP, UA, headers, path, method for HTTP
    * IP, port + maybe protocol hints for Layer 4 (SSH, TLS, etc.)
* You get back:

    * `riskBand`, `isBot`, `recommendedAction`, etc.

Then you write **small Go modules** that plug into Caddy:

* For HTTP: `http.handlers.stylobot_guard`
* For Layer 4: `layer4.handlers.stylobot_guard` (using the `caddy-l4` app) ([GitHub][2])

These modules **call Stylobot**, then:

* For HTTP:

    * set headers like `X-Stylobot-IsBot`, `X-Stylobot-RiskBand`, or
    * short-circuit with a 403 / CAPTCHA / honeypot route.
* For Layer 4:

    * allow → next handler (usually `layer4.handlers.proxy`)
    * deny → close/tarpit/redirect to honeypot upstream.

---

## 2. HTTP shim: `http.handlers.stylobot_guard`

### Behaviour

1. Extract features:

    * `RemoteAddr` (IP), `User-Agent`, headers, path, method.
2. Call Stylobot:

    * POST to `http://stylobot:5005/api/detect`.
3. Interpret result:

    * If `riskBand` is `High` or `isBot=true` → **tag** or **block**.
    * If low risk → pass downstream.

### Minimal skeleton

This is trimmed down but shows the shape (based on the official “visitor_ip” example handler). ([Caddy Web Server][3])

```go
package stylobotguard

import (
	"context"
	"encoding/json"
	"net"
	"net/http"
	"time"

	"github.com/caddyserver/caddy/v2"
	"github.com/caddyserver/caddy/v2/caddyconfig/caddyfile"
	"github.com/caddyserver/caddy/v2/caddyconfig/httpcaddyfile"
	"github.com/caddyserver/caddy/v2/modules/caddyhttp"
)

func init() {
	caddy.RegisterModule(Middleware{})
	httpcaddyfile.RegisterHandlerDirective("stylobot_guard", parseCaddyfile)
}

type Middleware struct {
	ApiURL          string        `json:"api_url,omitempty"`
	Timeout         time.Duration `json:"timeout,omitempty"`
	BlockOnHighRisk bool          `json:"block_on_high_risk,omitempty"`

	client *http.Client
	logger *zap.Logger
}

func (Middleware) CaddyModule() caddy.ModuleInfo {
	return caddy.ModuleInfo{
		ID:  "http.handlers.stylobot_guard",
		New: func() caddy.Module { return new(Middleware) },
	}
}

func (m *Middleware) Provision(ctx caddy.Context) error {
	m.logger = ctx.Logger()
	if m.ApiURL == "" {
		m.ApiURL = "http://stylobot:5005/api/detect"
	}
	if m.Timeout == 0 {
		m.Timeout = 500 * time.Millisecond
	}
	m.client = &http.Client{Timeout: m.Timeout}
	return nil
}

type detectRequest struct {
	IP        string            `json:"ip"`
	Path      string            `json:"path"`
	Method    string            `json:"method"`
	UserAgent string            `json:"user_agent"`
	Headers   map[string]string `json:"headers"`
}

type detectResponse struct {
	IsBot    bool    `json:"isBot"`
	RiskBand string  `json:"riskBand"`
	Risk     float64 `json:"botProbability"`
}

func (m Middleware) ServeHTTP(w http.ResponseWriter, r *http.Request, next caddyhttp.Handler) error {
	ctx, cancel := context.WithTimeout(r.Context(), m.Timeout)
	defer cancel()

	ip, _, _ := net.SplitHostPort(r.RemoteAddr)
	dr := detectRequest{
		IP:        ip,
		Path:      r.URL.Path,
		Method:    r.Method,
		UserAgent: r.UserAgent(),
		Headers:   map[string]string{},
	}
	for k, v := range r.Header {
		if len(v) > 0 {
			dr.Headers[k] = v[0]
		}
	}

	body, _ := json.Marshal(dr)
	req, err := http.NewRequestWithContext(ctx, http.MethodPost, m.ApiURL, bytes.NewReader(body))
	if err == nil {
		req.Header.Set("Content-Type", "application/json")
		resp, err := m.client.Do(req)
		if err == nil && resp != nil && resp.Body != nil {
			defer resp.Body.Close()
			var d detectResponse
			if json.NewDecoder(resp.Body).Decode(&d) == nil {
				// Attach Stylobot info as headers/placeholders for routing
				r.Header.Set("X-Stylobot-IsBot", strconv.FormatBool(d.IsBot))
				r.Header.Set("X-Stylobot-RiskBand", d.RiskBand)

				if m.BlockOnHighRisk && (d.RiskBand == "High" || d.RiskBand == "VeryHigh") {
					w.WriteHeader(http.StatusForbidden)
					_, _ = w.Write([]byte("Access blocked by Stylobot"))
					return nil
				}
			}
		}
	}

	return next.ServeHTTP(w, r)
}

// Caddyfile support: `stylobot_guard <apiUrl> [block]`
func (m *Middleware) UnmarshalCaddyfile(d *caddyfile.Dispenser) error {
	d.Next() // consume directive
	if d.NextArg() {
		m.ApiURL = d.Val()
	}
	for d.NextArg() {
		if d.Val() == "block" {
			m.BlockOnHighRisk = true
		}
	}
	return nil
}

func parseCaddyfile(h httpcaddyfile.Helper) (caddyhttp.MiddlewareHandler, error) {
	var m Middleware
	err := m.UnmarshalCaddyfile(h.Dispenser)
	return m, err
}

var (
	_ caddy.Provisioner           = (*Middleware)(nil)
	_ caddyhttp.MiddlewareHandler = (*Middleware)(nil)
	_ caddyfile.Unmarshaler       = (*Middleware)(nil)
)
```

### Using it in a Caddyfile

```caddyfile
{
    order stylobot_guard before reverse_proxy
}

example.com {
    stylobot_guard http://stylobot:5005/api/detect block

    @bots header X-Stylobot-IsBot true
    handle @bots {
        reverse_proxy http://bot-honeypot:8080
    }

    handle {
        reverse_proxy http://real-backend:8080
    }
}
```

* Stylobot runs its usual detectors.
* Handler stamps the request with headers.
* You route based on those headers (`@bots` matcher) to a different upstream.

This gives you **bot-aware HTTP routing** in Caddy.

---

## 3. TCP / SSH shim: `layer4.handlers.stylobot_guard`

For SSH and other non-HTTP protocols you combine:

* The `layer4` app (`github.com/mholt/caddy-l4`) ([GitHub][4])
* A Layer 4 handler that queries Stylobot.

### Behaviour

1. Caddy-l4 accepts the TCP connection (e.g. on `:22`).
2. Your `stylobot_guard` handler:

    * reads `conn.RemoteAddr()`
    * maybe sniffs first few bytes if you want protocol hints
    * calls Stylobot: `{ ip, port=22, service="ssh" }`
3. If allow:

    * call the **next** handler (`layer4.handlers.proxy` to `127.0.0.1:2222`).
4. If deny:

    * close the connection, or
    * proxy to an SSH honeypot upstream.

### Rough handler skeleton

The exact interface for handlers in caddy-l4 is `layer4.Handler` (from the plugin), but conceptually:

```go
package stylobotl4

import (
	"context"
	"encoding/json"
	"net"
	"net/http"
	"time"

	"github.com/caddyserver/caddy/v2"
	l4 "github.com/mholt/caddy-l4/layer4"
)

func init() {
	caddy.RegisterModule(Guard{})
}

type Guard struct {
	ApiURL  string        `json:"api_url,omitempty"`
	Timeout time.Duration `json:"timeout,omitempty"`

	client *http.Client
	logger *zap.Logger
}

func (Guard) CaddyModule() caddy.ModuleInfo {
	return caddy.ModuleInfo{
		ID:  "layer4.handlers.stylobot_guard",
		New: func() caddy.Module { return new(Guard) },
	}
}

func (g *Guard) Provision(ctx caddy.Context) error {
	g.logger = ctx.Logger()
	if g.ApiURL == "" {
		g.ApiURL = "http://stylobot:5005/api/detect"
	}
	if g.Timeout == 0 {
		g.Timeout = 500 * time.Millisecond
	}
	g.client = &http.Client{Timeout: g.Timeout}
	return nil
}

type l4DetectRequest struct {
	IP     string `json:"ip"`
	Port   int    `json:"port"`
	Flavor string `json:"flavor"` // e.g. "ssh", "raw-tcp"
}

type l4DetectResponse struct {
	Allow   bool   `json:"allow"`
	Risk    string `json:"risk_band"`
	IsBot   bool   `json:"is_bot"`
	Reason  string `json:"reason"`
}

func (g Guard) Handle(conn *l4.Conn, next l4.Handler) error {
	ctx, cancel := context.WithTimeout(context.Background(), g.Timeout)
	defer cancel()

	remote := conn.RemoteAddr().(*net.TCPAddr)
	req := l4DetectRequest{
		IP:     remote.IP.String(),
		Port:   remote.Port,
		Flavor: "ssh",
	}

	body, _ := json.Marshal(req)
	httpReq, err := http.NewRequestWithContext(ctx, http.MethodPost, g.ApiURL, bytes.NewReader(body))
	if err != nil {
		return conn.Close()
	}
	httpReq.Header.Set("Content-Type", "application/json")

	resp, err := g.client.Do(httpReq)
	if err != nil {
		// on error, be conservative (your choice: allow or deny)
		return conn.Close()
	}
	defer resp.Body.Close()

	var dr l4DetectResponse
	if err := json.NewDecoder(resp.Body).Decode(&dr); err != nil {
		return conn.Close()
	}

	if !dr.Allow {
		// Drop / tarpit / redirect – simplest is drop:
		return conn.Close()
	}

	return next.Handle(conn)
}

// interface guard would be something like:
// var _ l4.Handler = (*Guard)(nil)
```

(Names for `l4.Conn` and `l4.Handler` might differ slightly, but that’s the gist.)

### Example JSON config (SSH)

```jsonc
{
  "apps": {
    "layer4": {
      "servers": {
        "ssh": {
          "listen": [":22"],
          "routes": [
            {
              "handle": [
                {
                  "handler": "stylobot_guard",
                  "api_url": "http://stylobot:5005/api/detect"
                },
                {
                  "handler": "proxy",
                  "upstreams": [
                    { "dial": "127.0.0.1:2222" }
                  ]
                }
              ]
            }
          ]
        }
      }
    }
  }
}
```

Caddy-l4 docs show the same “routes + handlers” model you know from the HTTP app. ([GitHub][4])

---

## 4. How this plays with YARP / Stylobot today

You said “YARP can’t do it” – right: YARP is HTTP(S)-only. But a Caddy shim like this lets you:

* **For HTTP**

    * Either route directly from Caddy based on Stylobot’s decision,
    * Or send different headers to YARP so *its* clusters can behave differently (human vs bot clusters) while Caddy
      stays dumb.

* **For TCP/SSH**

    * Use Caddy-l4 as the front door on :22 / :443 etc.
    * Stylobot guard decides: “real SSH → actual sshd; obvious scanner → honeypot / instant close”.

Plus: the exact same **Stylobot decision API** and **Ephemeral signal window** powers both, so all your dashboards and
learning loops still work.

---

If you want, I can turn this into:

* a concrete repo layout (`cmd/caddy-stylobot`, `module/http`, `module/layer4`), and
* a first cut of the Go module code you can drop into a `xcaddy` build.

[1]: https://github.com/hslatman/caddy-crowdsec-bouncer/blob/main/README.md?utm_source=chatgpt.com "caddy-crowdsec-bouncer/README.md at main"

[2]: https://github.com/mholt/caddy-l4?utm_source=chatgpt.com "mholt/caddy-l4: Layer 4 (TCP/UDP) app for Caddy"

[3]: https://caddyserver.com/docs/extending-caddy "Extending Caddy — Caddy Documentation"

[4]: https://github.com/mholt/caddy-l4 "GitHub - mholt/caddy-l4: Layer 4 (TCP/UDP) app for Caddy"
