package stylobot_test

import (
	"context"
	"crypto/tls"
	"fmt"
	"net/http"
	"net/http/httptest"
	"testing"

	"github.com/caddyserver/caddy/v2/modules/caddyhttp"
	stylobot "github.com/scottgal/caddy-stylobot"
	sb "github.com/scottgal/stylobot-go"
)

type mockClient struct {
	verdict *sb.Verdict
	err     error
}

func (m *mockClient) Detect(_ context.Context, _ sb.DetectRequest) (*sb.Verdict, error) {
	return m.verdict, m.err
}
func (m *mockClient) DetectBatch(_ context.Context, _ []sb.DetectRequest) ([]*sb.Verdict, error) {
	return []*sb.Verdict{m.verdict}, m.err
}
func (m *mockClient) RenderWidget(_ context.Context, _ sb.RenderRequest) (*sb.RenderResponse, error) {
	return &sb.RenderResponse{HTML: "", Success: true}, nil
}
func (m *mockClient) Close() error { return nil }

func TestExtractIPFromXFF(t *testing.T) {
	req := httptest.NewRequest(http.MethodGet, "/", nil)
	req.Header.Set("X-Forwarded-For", "1.2.3.4, 10.0.0.1")
	if got := stylobot.ExtractIP(req); got != "1.2.3.4" {
		t.Errorf("expected 1.2.3.4, got %q", got)
	}
}

func TestExtractIPFromRemoteAddr(t *testing.T) {
	req := httptest.NewRequest(http.MethodGet, "/", nil)
	req.RemoteAddr = "5.6.7.8:12345"
	if got := stylobot.ExtractIP(req); got != "5.6.7.8" {
		t.Errorf("expected 5.6.7.8, got %q", got)
	}
}

func TestExtractTLSReturnsNilWithoutTLS(t *testing.T) {
	req := httptest.NewRequest(http.MethodGet, "/", nil)
	if got := stylobot.ExtractTLS(req); got != nil {
		t.Errorf("expected nil for non-TLS request, got %+v", got)
	}
}

func TestExtractTLSPopulatesVersionAndCipher(t *testing.T) {
	req := httptest.NewRequest(http.MethodGet, "/", nil)
	req.TLS = &tls.ConnectionState{
		Version:     tls.VersionTLS13,
		CipherSuite: tls.TLS_AES_128_GCM_SHA256,
	}
	info := stylobot.ExtractTLS(req)
	if info == nil {
		t.Fatal("expected non-nil TLSInfo")
	}
	if info.Version != "TLSv1.3" {
		t.Errorf("expected TLSv1.3, got %q", info.Version)
	}
	if info.Cipher != tls.CipherSuiteName(tls.TLS_AES_128_GCM_SHA256) {
		t.Errorf("expected cipher name, got %q", info.Cipher)
	}
	if info.JA3 != "" || info.JA4 != "" {
		t.Errorf("expected empty JA3/JA4 when headers absent, got JA3=%q JA4=%q", info.JA3, info.JA4)
	}
}

func TestExtractTLSReadsJa3AndJa4FromHeaders(t *testing.T) {
	req := httptest.NewRequest(http.MethodGet, "/", nil)
	req.TLS = &tls.ConnectionState{Version: tls.VersionTLS12, CipherSuite: tls.TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256}
	req.Header.Set("X-JA3-Hash", "769,4866-4867,0-23-65281-10-11,29-23-24,0")
	req.Header.Set("X-JA4", "t13d1516h2_8daaf6152771_b0da82dd1658")

	info := stylobot.ExtractTLS(req)
	if info.JA3 != "769,4866-4867,0-23-65281-10-11,29-23-24,0" {
		t.Errorf("JA3 not picked up: %q", info.JA3)
	}
	if info.JA4 != "t13d1516h2_8daaf6152771_b0da82dd1658" {
		t.Errorf("JA4 not picked up: %q", info.JA4)
	}
}

func TestExtractHeadersLowercase(t *testing.T) {
	req := httptest.NewRequest(http.MethodGet, "/", nil)
	req.Header.Set("User-Agent", "TestBot/1.0")
	headers := stylobot.ExtractHeaders(req)
	if headers["user-agent"] != "TestBot/1.0" {
		t.Errorf("expected user-agent header, got %v", headers)
	}
}

func TestInjectsHeaders(t *testing.T) {
	m := &stylobot.StyloBot{OnBlock: 403}
	m.SetClient(&mockClient{verdict: &sb.Verdict{
		IsBot:             false,
		BotProbability:    0.12,
		Confidence:        0.95,
		RiskBand:          "Low",
		RecommendedAction: "Allow",
		ThreatBand:        "None",
	}})

	req := httptest.NewRequest(http.MethodGet, "/test", nil)
	rr := httptest.NewRecorder()
	var captured *http.Request
	next := caddyhttp.HandlerFunc(func(w http.ResponseWriter, r *http.Request) error {
		captured = r
		return nil
	})

	if err := m.ServeHTTP(rr, req, next); err != nil {
		t.Fatal(err)
	}
	if captured == nil {
		t.Fatal("next was not called")
	}
	if captured.Header.Get("X-StyloBot-IsBot") != "false" {
		t.Errorf("X-StyloBot-IsBot: got %q", captured.Header.Get("X-StyloBot-IsBot"))
	}
	if captured.Header.Get("X-StyloBot-RiskBand") != "Low" {
		t.Errorf("X-StyloBot-RiskBand: got %q", captured.Header.Get("X-StyloBot-RiskBand"))
	}
	if captured.Header.Get("X-StyloBot-Action") != "Allow" {
		t.Errorf("X-StyloBot-Action: got %q", captured.Header.Get("X-StyloBot-Action"))
	}
}

func TestFailsOpen(t *testing.T) {
	m := &stylobot.StyloBot{OnBlock: 403}
	m.SetClient(&mockClient{err: fmt.Errorf("connection refused")})

	req := httptest.NewRequest(http.MethodGet, "/", nil)
	rr := httptest.NewRecorder()
	called := false
	next := caddyhttp.HandlerFunc(func(w http.ResponseWriter, r *http.Request) error {
		called = true
		return nil
	})
	_ = m.ServeHTTP(rr, req, next)
	if !called {
		t.Error("should fail open and call next on gRPC error")
	}
}

func TestBlocksBot(t *testing.T) {
	m := &stylobot.StyloBot{OnBlock: 403}
	m.SetClient(&mockClient{verdict: &sb.Verdict{
		IsBot:             true,
		BotProbability:    0.98,
		RecommendedAction: "Block",
		RiskBand:          "VeryHigh",
	}})

	req := httptest.NewRequest(http.MethodGet, "/", nil)
	rr := httptest.NewRecorder()
	called := false
	next := caddyhttp.HandlerFunc(func(w http.ResponseWriter, r *http.Request) error {
		called = true
		return nil
	})
	_ = m.ServeHTTP(rr, req, next)
	if called {
		t.Error("next should not be called when action is Block")
	}
	if rr.Code != 403 {
		t.Errorf("expected 403, got %d", rr.Code)
	}
}
