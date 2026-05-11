package stylobot_test

import (
	"context"
	"net"
	"testing"
	"time"

	stylobot "github.com/scottgal/stylobot-go"
	pb "github.com/scottgal/stylobot-go/proto"
	"google.golang.org/grpc"
)

type mockServer struct {
	pb.UnimplementedDetectionServiceServer
	detectResp *pb.DetectResponse
	renderResp *pb.RenderWidgetResponse
}

func (m *mockServer) Detect(_ context.Context, _ *pb.DetectRequest) (*pb.DetectResponse, error) {
	return m.detectResp, nil
}

func (m *mockServer) RenderWidget(_ context.Context, _ *pb.RenderWidgetRequest) (*pb.RenderWidgetResponse, error) {
	return m.renderResp, nil
}

func (m *mockServer) DetectBatch(_ context.Context, req *pb.DetectBatchRequest) (*pb.DetectBatchResponse, error) {
	resp := &pb.DetectBatchResponse{}
	for range req.Requests {
		resp.Responses = append(resp.Responses, m.detectResp)
	}
	return resp, nil
}

func startMock(t *testing.T, mock *mockServer) string {
	t.Helper()
	lis, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	s := grpc.NewServer()
	pb.RegisterDetectionServiceServer(s, mock)
	go s.Serve(lis) //nolint:errcheck
	t.Cleanup(s.Stop)
	return lis.Addr().String()
}

func TestDetect_Bot(t *testing.T) {
	addr := startMock(t, &mockServer{
		detectResp: &pb.DetectResponse{
			IsBot:             true,
			BotProbability:    0.95,
			Confidence:        0.9,
			RiskBand:          pb.RiskBand_RISK_BAND_VERY_HIGH,
			RecommendedAction: pb.RecommendedAction_RECOMMENDED_ACTION_BLOCK,
			ThreatBand:        pb.ThreatBand_THREAT_BAND_HIGH,
		},
	})

	c, err := stylobot.NewClient(addr, stylobot.WithTimeout(2*time.Second))
	if err != nil {
		t.Fatal(err)
	}
	defer c.Close()

	v, err := c.Detect(context.Background(), stylobot.DetectRequest{
		Method: "GET", Path: "/", RemoteIP: "1.2.3.4", Headers: map[string]string{},
	})
	if err != nil {
		t.Fatal(err)
	}
	if !v.IsBot {
		t.Error("expected IsBot=true")
	}
	if v.RiskBand != "VeryHigh" {
		t.Errorf("RiskBand: got %q, want VeryHigh", v.RiskBand)
	}
	if v.RecommendedAction != "Block" {
		t.Errorf("RecommendedAction: got %q, want Block", v.RecommendedAction)
	}
	if v.BotProbability != 0.95 {
		t.Errorf("BotProbability: got %v, want 0.95", v.BotProbability)
	}
	if v.Confidence != 0.9 {
		t.Errorf("Confidence: got %v, want 0.9", v.Confidence)
	}
}

func TestDetect_Human(t *testing.T) {
	addr := startMock(t, &mockServer{
		detectResp: &pb.DetectResponse{
			IsBot:             false,
			BotProbability:    0.05,
			Confidence:        0.95,
			RiskBand:          pb.RiskBand_RISK_BAND_VERY_LOW,
			RecommendedAction: pb.RecommendedAction_RECOMMENDED_ACTION_ALLOW,
			ThreatBand:        pb.ThreatBand_THREAT_BAND_NONE,
		},
	})

	c, err := stylobot.NewClient(addr, stylobot.WithTimeout(2*time.Second))
	if err != nil {
		t.Fatal(err)
	}
	defer c.Close()

	v, err := c.Detect(context.Background(), stylobot.DetectRequest{
		Method: "GET", Path: "/", RemoteIP: "5.6.7.8", Headers: map[string]string{},
	})
	if err != nil {
		t.Fatal(err)
	}
	if v.IsBot {
		t.Error("expected IsBot=false")
	}
	if v.RiskBand != "VeryLow" {
		t.Errorf("RiskBand: got %q, want VeryLow", v.RiskBand)
	}
	if v.RecommendedAction != "Allow" {
		t.Errorf("RecommendedAction: got %q, want Allow", v.RecommendedAction)
	}
}

func TestRenderWidget(t *testing.T) {
	addr := startMock(t, &mockServer{
		detectResp: &pb.DetectResponse{},
		renderResp: &pb.RenderWidgetResponse{Html: "<p>Score: 0.95</p>", Success: true},
	})

	c, err := stylobot.NewClient(addr, stylobot.WithTimeout(2*time.Second))
	if err != nil {
		t.Fatal(err)
	}
	defer c.Close()

	r, err := c.RenderWidget(context.Background(), stylobot.RenderRequest{
		Template: "<p>Score: {{ probability }}</p>",
		Vars:     map[string]string{"probability": "0.95"},
	})
	if err != nil {
		t.Fatal(err)
	}
	if !r.Success {
		t.Errorf("expected Success=true, error=%q", r.Error)
	}
	if r.HTML != "<p>Score: 0.95</p>" {
		t.Errorf("HTML: got %q", r.HTML)
	}
}

func TestNewClient_InvalidEndpoint(t *testing.T) {
	c, err := stylobot.NewClient("127.0.0.1:1", stylobot.WithTimeout(10*time.Millisecond))
	if err != nil {
		t.Fatal(err)
	}
	defer c.Close()
	_, err = c.Detect(context.Background(), stylobot.DetectRequest{})
	if err == nil {
		t.Error("expected error on unreachable endpoint")
	}
}

func TestDetectBatch(t *testing.T) {
	addr := startMock(t, &mockServer{
		detectResp: &pb.DetectResponse{IsBot: true, BotProbability: 0.9},
	})

	c, err := stylobot.NewClient(addr, stylobot.WithTimeout(2*time.Second))
	if err != nil {
		t.Fatal(err)
	}
	defer c.Close()

	reqs := []stylobot.DetectRequest{
		{Method: "GET", Path: "/a", RemoteIP: "1.1.1.1", Headers: map[string]string{}},
		{Method: "GET", Path: "/b", RemoteIP: "2.2.2.2", Headers: map[string]string{}},
	}
	verdicts, err := c.DetectBatch(context.Background(), reqs)
	if err != nil {
		t.Fatal(err)
	}
	if len(verdicts) != 2 {
		t.Fatalf("got %d verdicts, want 2", len(verdicts))
	}
	for i, v := range verdicts {
		if !v.IsBot {
			t.Errorf("verdict[%d]: expected IsBot=true", i)
		}
		if v.BotProbability != 0.9 {
			t.Errorf("verdict[%d]: BotProbability got %v, want 0.9", i, v.BotProbability)
		}
	}
}

func TestWithAPIKey(t *testing.T) {
	addr := startMock(t, &mockServer{detectResp: &pb.DetectResponse{}})
	c, err := stylobot.NewClient(addr, stylobot.WithTimeout(2*time.Second), stylobot.WithAPIKey("test-key-123"))
	if err != nil {
		t.Fatal(err)
	}
	defer c.Close()
	_, err = c.Detect(context.Background(), stylobot.DetectRequest{
		Method: "GET", Path: "/", RemoteIP: "1.2.3.4", Headers: map[string]string{},
	})
	if err != nil {
		t.Errorf("WithAPIKey caused unexpected error: %v", err)
	}
}
