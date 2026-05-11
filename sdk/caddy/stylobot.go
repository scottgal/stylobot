package stylobot

import (
	"context"
	"fmt"
	"net/http"
	"time"

	"github.com/caddyserver/caddy/v2"
	"github.com/caddyserver/caddy/v2/caddyconfig/caddyfile"
	"github.com/caddyserver/caddy/v2/caddyconfig/httpcaddyfile"
	"github.com/caddyserver/caddy/v2/modules/caddyhttp"
	sb "github.com/scottgal/stylobot-go"
	"go.uber.org/zap"
)

func init() {
	caddy.RegisterModule(StyloBot{})
	httpcaddyfile.RegisterHandlerDirective("stylobot", parseCaddyfileHandler)
}

// StyloBot is a Caddy middleware that calls the StyloBot gRPC sidecar for bot detection.
// It injects detection headers into the upstream request and optionally blocks bots.
type StyloBot struct {
	// Endpoint is the host:port of the StyloBot sidecar gRPC server (required).
	Endpoint string `json:"endpoint"`
	// APIKey is an optional API key forwarded as gRPC metadata.
	APIKey string `json:"api_key,omitempty"`
	// Timeout is the per-request gRPC deadline (default: 50ms). Fails open on timeout.
	Timeout string `json:"timeout,omitempty"`
	// OnBlock is the HTTP status code returned when action=Block (default: 403). 0 = headers only.
	OnBlock int `json:"on_block,omitempty"`

	timeout  time.Duration
	sbClient sb.Client
	logger   *zap.Logger
}

// CaddyModule returns the Caddy module metadata.
func (StyloBot) CaddyModule() caddy.ModuleInfo {
	return caddy.ModuleInfo{
		ID:  "http.handlers.stylobot",
		New: func() caddy.Module { return new(StyloBot) },
	}
}

// Provision sets up the gRPC connection and configures defaults.
func (s *StyloBot) Provision(ctx caddy.Context) error {
	s.logger = ctx.Logger()
	s.timeout = 50 * time.Millisecond
	if s.Timeout != "" {
		d, err := time.ParseDuration(s.Timeout)
		if err != nil {
			return fmt.Errorf("stylobot: invalid timeout %q: %w", s.Timeout, err)
		}
		s.timeout = d
	}
	if s.OnBlock == 0 {
		s.OnBlock = 403
	}
	opts := []sb.Option{sb.WithTimeout(s.timeout)}
	if s.APIKey != "" {
		opts = append(opts, sb.WithAPIKey(s.APIKey))
	}
	client, err := sb.NewClient(s.Endpoint, opts...)
	if err != nil {
		return fmt.Errorf("stylobot: %w", err)
	}
	s.sbClient = client
	return nil
}

// Validate checks required configuration.
func (s *StyloBot) Validate() error {
	if s.Endpoint == "" {
		return fmt.Errorf("stylobot: endpoint is required")
	}
	return nil
}

// SetClient injects a pre-built client. Used by tests only.
func (s *StyloBot) SetClient(c sb.Client) {
	s.sbClient = c
	s.timeout = 2 * time.Second
	if s.logger == nil {
		s.logger = zap.NewNop()
	}
}

// Cleanup closes the gRPC connection on module teardown.
func (s *StyloBot) Cleanup() error {
	if s.sbClient != nil {
		return s.sbClient.Close()
	}
	return nil
}

// ServeHTTP calls the StyloBot sidecar, injects detection headers, and optionally blocks bots.
// Fails open: if the sidecar is unreachable or times out, the request forwards unchanged.
func (s *StyloBot) ServeHTTP(w http.ResponseWriter, r *http.Request, next caddyhttp.Handler) error {
	verdict, err := s.sbClient.Detect(context.Background(), sb.DetectRequest{
		Method:   r.Method,
		Path:     r.URL.RequestURI(),
		RemoteIP: ExtractIP(r),
		Protocol: r.Proto,
		Headers:  ExtractHeaders(r),
	})
	if err != nil {
		s.logger.Warn("stylobot detect failed, failing open", zap.Error(err))
		return next.ServeHTTP(w, r)
	}

	injectHeaders(r, verdict)

	if verdict.IsBot && s.OnBlock > 0 && verdict.RecommendedAction == "Block" {
		http.Error(w, "Forbidden", s.OnBlock)
		return nil
	}

	return next.ServeHTTP(w, r)
}

func injectHeaders(r *http.Request, v *sb.Verdict) {
	h := r.Header
	h.Set("X-StyloBot-IsBot", fmt.Sprintf("%v", v.IsBot))
	h.Set("X-StyloBot-Probability", fmt.Sprintf("%.4f", v.BotProbability))
	h.Set("X-StyloBot-Confidence", fmt.Sprintf("%.4f", v.Confidence))
	h.Set("X-StyloBot-BotType", v.BotType)
	h.Set("X-StyloBot-BotName", v.BotName)
	h.Set("X-StyloBot-RiskBand", v.RiskBand)
	h.Set("X-StyloBot-Action", v.RecommendedAction)
	h.Set("X-StyloBot-ThreatScore", fmt.Sprintf("%.4f", v.ThreatScore))
	h.Set("X-StyloBot-ThreatBand", v.ThreatBand)
}

// UnmarshalCaddyfile reads the stylobot Caddyfile block.
func (s *StyloBot) UnmarshalCaddyfile(d *caddyfile.Dispenser) error {
	for d.Next() {
		for d.NextBlock(0) {
			switch d.Val() {
			case "endpoint":
				if !d.NextArg() {
					return d.ArgErr()
				}
				s.Endpoint = d.Val()
			case "api_key":
				if !d.NextArg() {
					return d.ArgErr()
				}
				s.APIKey = d.Val()
			case "timeout":
				if !d.NextArg() {
					return d.ArgErr()
				}
				s.Timeout = d.Val()
			case "on_block":
				if !d.NextArg() {
					return d.ArgErr()
				}
				var code int
				if _, err := fmt.Sscanf(d.Val(), "%d", &code); err != nil {
					return d.Errf("on_block must be an integer: %v", err)
				}
				s.OnBlock = code
			default:
				return d.Errf("unrecognized directive: %s", d.Val())
			}
		}
	}
	return nil
}

func parseCaddyfileHandler(h httpcaddyfile.Helper) (caddyhttp.MiddlewareHandler, error) {
	var m StyloBot
	return &m, m.UnmarshalCaddyfile(h.Dispenser)
}

// Interface compliance assertions.
var (
	_ caddy.Module                = (*StyloBot)(nil)
	_ caddy.Provisioner           = (*StyloBot)(nil)
	_ caddy.Validator             = (*StyloBot)(nil)
	_ caddy.CleanerUpper          = (*StyloBot)(nil)
	_ caddyhttp.MiddlewareHandler = (*StyloBot)(nil)
	_ caddyfile.Unmarshaler       = (*StyloBot)(nil)
)
