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
	pb "github.com/scottgal/caddy-stylobot/proto"
	"go.uber.org/zap"
	"google.golang.org/grpc"
	"google.golang.org/grpc/credentials/insecure"
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

	timeout time.Duration
	conn    *grpc.ClientConn
	client  pb.DetectionServiceClient
	logger  *zap.Logger
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
	conn, err := grpc.NewClient(s.Endpoint,
		grpc.WithTransportCredentials(insecure.NewCredentials()),
	)
	if err != nil {
		return fmt.Errorf("stylobot: grpc dial %q: %w", s.Endpoint, err)
	}
	s.conn = conn
	s.client = pb.NewDetectionServiceClient(conn)
	return nil
}

// Validate checks required configuration.
func (s *StyloBot) Validate() error {
	if s.Endpoint == "" {
		return fmt.Errorf("stylobot: endpoint is required")
	}
	return nil
}

// SetConn injects a pre-dialed connection. Used by tests only.
func (s *StyloBot) SetConn(conn *grpc.ClientConn) {
	s.conn = conn
	s.client = pb.NewDetectionServiceClient(conn)
	s.timeout = 2 * time.Second
	if s.logger == nil {
		s.logger = zap.NewNop()
	}
}

// Cleanup closes the gRPC connection on module teardown.
func (s *StyloBot) Cleanup() error {
	if s.conn != nil {
		return s.conn.Close()
	}
	return nil
}

// ServeHTTP calls the StyloBot sidecar, injects detection headers, and optionally blocks bots.
// Fails open: if the sidecar is unreachable or times out, the request forwards unchanged.
func (s *StyloBot) ServeHTTP(w http.ResponseWriter, r *http.Request, next caddyhttp.Handler) error {
	ctx, cancel := context.WithTimeout(r.Context(), s.timeout)
	defer cancel()

	req := &pb.DetectRequest{
		Method:   r.Method,
		Path:     r.URL.RequestURI(),
		RemoteIp: ExtractIP(r),
		Protocol: r.Proto,
		Headers:  ExtractHeaders(r),
	}

	resp, err := s.client.Detect(ctx, req)
	if err != nil {
		s.logger.Warn("stylobot detect failed, failing open", zap.Error(err))
		return next.ServeHTTP(w, r)
	}

	injectHeaders(r, resp)

	if resp.IsBot && s.OnBlock > 0 &&
		resp.RecommendedAction == pb.RecommendedAction_RECOMMENDED_ACTION_BLOCK {
		http.Error(w, "Forbidden", s.OnBlock)
		return nil
	}

	return next.ServeHTTP(w, r)
}

var riskBandNames = map[pb.RiskBand]string{
	pb.RiskBand_RISK_BAND_UNKNOWN:   "Unknown",
	pb.RiskBand_RISK_BAND_VERY_LOW:  "VeryLow",
	pb.RiskBand_RISK_BAND_LOW:       "Low",
	pb.RiskBand_RISK_BAND_ELEVATED:  "Elevated",
	pb.RiskBand_RISK_BAND_MEDIUM:    "Medium",
	pb.RiskBand_RISK_BAND_HIGH:      "High",
	pb.RiskBand_RISK_BAND_VERY_HIGH: "VeryHigh",
	pb.RiskBand_RISK_BAND_VERIFIED:  "Verified",
}

var actionNames = map[pb.RecommendedAction]string{
	pb.RecommendedAction_RECOMMENDED_ACTION_ALLOW:     "Allow",
	pb.RecommendedAction_RECOMMENDED_ACTION_THROTTLE:  "Throttle",
	pb.RecommendedAction_RECOMMENDED_ACTION_CHALLENGE: "Challenge",
	pb.RecommendedAction_RECOMMENDED_ACTION_BLOCK:     "Block",
}

var threatBandNames = map[pb.ThreatBand]string{
	pb.ThreatBand_THREAT_BAND_NONE:     "None",
	pb.ThreatBand_THREAT_BAND_LOW:      "Low",
	pb.ThreatBand_THREAT_BAND_ELEVATED: "Elevated",
	pb.ThreatBand_THREAT_BAND_HIGH:     "High",
	pb.ThreatBand_THREAT_BAND_CRITICAL: "Critical",
}

func injectHeaders(r *http.Request, resp *pb.DetectResponse) {
	h := r.Header
	h.Set("X-StyloBot-IsBot", fmt.Sprintf("%v", resp.IsBot))
	h.Set("X-StyloBot-Probability", fmt.Sprintf("%.4f", resp.BotProbability))
	h.Set("X-StyloBot-Confidence", fmt.Sprintf("%.4f", resp.Confidence))
	h.Set("X-StyloBot-BotType", resp.BotType)
	h.Set("X-StyloBot-BotName", resp.BotName)
	h.Set("X-StyloBot-RiskBand", riskBandNames[resp.RiskBand])
	h.Set("X-StyloBot-Action", actionNames[resp.RecommendedAction])
	h.Set("X-StyloBot-ThreatScore", fmt.Sprintf("%.4f", resp.ThreatScore))
	h.Set("X-StyloBot-ThreatBand", threatBandNames[resp.ThreatBand])
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
