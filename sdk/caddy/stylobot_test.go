package stylobot_test

import (
	"context"
	"net"
	"net/http"
	"net/http/httptest"
	"testing"

	stylobot "github.com/scottgal/caddy-stylobot"
	"github.com/caddyserver/caddy/v2/modules/caddyhttp"
	pb "github.com/scottgal/caddy-stylobot/proto"
	"google.golang.org/grpc"
	"google.golang.org/grpc/credentials/insecure"
)

type mockDetectServer struct {
	pb.UnimplementedDetectionServiceServer
	resp *pb.DetectResponse
}

func (m *mockDetectServer) Detect(_ context.Context, _ *pb.DetectRequest) (*pb.DetectResponse, error) {
	return m.resp, nil
}

func startMockGRPC(t *testing.T, resp *pb.DetectResponse) string {
	t.Helper()
	lis, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	s := grpc.NewServer()
	pb.RegisterDetectionServiceServer(s, &mockDetectServer{resp: resp})
	go s.Serve(lis) //nolint:errcheck
	t.Cleanup(s.Stop)
	return lis.Addr().String()
}

func newMiddleware(t *testing.T, addr string, onBlock int) *stylobot.StyloBot {
	t.Helper()
	conn, err := grpc.NewClient(addr, grpc.WithTransportCredentials(insecure.NewCredentials()))
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { conn.Close() }) //nolint:errcheck
	m := &stylobot.StyloBot{OnBlock: onBlock}
	m.SetConn(conn)
	return m
}

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

func TestExtractHeadersLowercase(t *testing.T) {
	req := httptest.NewRequest(http.MethodGet, "/", nil)
	req.Header.Set("User-Agent", "TestBot/1.0")
	headers := stylobot.ExtractHeaders(req)
	if headers["user-agent"] != "TestBot/1.0" {
		t.Errorf("expected user-agent header, got %v", headers)
	}
}

func TestInjectsHeaders(t *testing.T) {
	addr := startMockGRPC(t, &pb.DetectResponse{
		IsBot:             false,
		BotProbability:    0.12,
		Confidence:        0.95,
		RiskBand:          pb.RiskBand_RISK_BAND_LOW,
		RecommendedAction: pb.RecommendedAction_RECOMMENDED_ACTION_ALLOW,
		ThreatBand:        pb.ThreatBand_THREAT_BAND_NONE,
	})
	m := newMiddleware(t, addr, 403)

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
	conn, _ := grpc.NewClient("127.0.0.1:1", grpc.WithTransportCredentials(insecure.NewCredentials()))
	t.Cleanup(func() { conn.Close() }) //nolint:errcheck
	m := &stylobot.StyloBot{OnBlock: 403}
	m.SetConn(conn)

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
	addr := startMockGRPC(t, &pb.DetectResponse{
		IsBot:             true,
		BotProbability:    0.98,
		RecommendedAction: pb.RecommendedAction_RECOMMENDED_ACTION_BLOCK,
		RiskBand:          pb.RiskBand_RISK_BAND_VERY_HIGH,
	})
	m := newMiddleware(t, addr, 403)

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
