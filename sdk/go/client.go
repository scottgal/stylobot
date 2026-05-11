package stylobot

import (
	"context"
	"fmt"
	"time"

	pb "github.com/scottgal/stylobot-go/proto"
	"google.golang.org/grpc"
	"google.golang.org/grpc/credentials/insecure"
	"google.golang.org/grpc/metadata"
)

// NewClient dials the StyloBot sidecar and returns a Client.
// The endpoint must be a host:port string (e.g., "localhost:5090").
// The connection uses h2c (HTTP/2 cleartext); pass TLS options via Option if needed.
func NewClient(endpoint string, opts ...Option) (Client, error) {
	o := &clientOptions{timeout: 50 * time.Millisecond}
	for _, opt := range opts {
		opt(o)
	}
	conn, err := grpc.NewClient(endpoint, grpc.WithTransportCredentials(insecure.NewCredentials()))
	if err != nil {
		return nil, fmt.Errorf("stylobot: dial %q: %w", endpoint, err)
	}
	return &grpcClient{
		conn: conn,
		pb:   pb.NewDetectionServiceClient(conn),
		opts: o,
	}, nil
}

type grpcClient struct {
	conn *grpc.ClientConn
	pb   pb.DetectionServiceClient
	opts *clientOptions
}

func (c *grpcClient) Detect(ctx context.Context, req DetectRequest) (*Verdict, error) {
	ctx, cancel := context.WithTimeout(ctx, c.opts.timeout)
	defer cancel()
	if c.opts.apiKey != "" {
		ctx = metadata.AppendToOutgoingContext(ctx, "x-sb-api-key", c.opts.apiKey)
	}
	resp, err := c.pb.Detect(ctx, toProtoRequest(req))
	if err != nil {
		return nil, err
	}
	return fromProtoResponse(resp), nil
}

func (c *grpcClient) DetectBatch(ctx context.Context, reqs []DetectRequest) ([]*Verdict, error) {
	ctx, cancel := context.WithTimeout(ctx, c.opts.timeout)
	defer cancel()
	if c.opts.apiKey != "" {
		ctx = metadata.AppendToOutgoingContext(ctx, "x-sb-api-key", c.opts.apiKey)
	}
	batch := &pb.DetectBatchRequest{}
	for _, r := range reqs {
		batch.Requests = append(batch.Requests, toProtoRequest(r))
	}
	resp, err := c.pb.DetectBatch(ctx, batch)
	if err != nil {
		return nil, err
	}
	out := make([]*Verdict, len(resp.Responses))
	for i, r := range resp.Responses {
		out[i] = fromProtoResponse(r)
	}
	return out, nil
}

func (c *grpcClient) RenderWidget(ctx context.Context, req RenderRequest) (*RenderResponse, error) {
	ctx, cancel := context.WithTimeout(ctx, c.opts.timeout)
	defer cancel()
	if c.opts.apiKey != "" {
		ctx = metadata.AppendToOutgoingContext(ctx, "x-sb-api-key", c.opts.apiKey)
	}
	pbReq := &pb.RenderWidgetRequest{
		Template: req.Template,
		Vars:     req.Vars,
	}
	if req.Verdict != nil {
		pbReq.Verdict = toProtoResponse(req.Verdict)
	}
	resp, err := c.pb.RenderWidget(ctx, pbReq)
	if err != nil {
		return nil, err
	}
	return &RenderResponse{HTML: resp.Html, Success: resp.Success, Error: resp.Error}, nil
}

func (c *grpcClient) Close() error {
	return c.conn.Close()
}

func toProtoRequest(r DetectRequest) *pb.DetectRequest {
	p := &pb.DetectRequest{
		Method:   r.Method,
		Path:     r.Path,
		Headers:  r.Headers,
		RemoteIp: r.RemoteIP,
		Protocol: r.Protocol,
	}
	if p.Protocol == "" {
		p.Protocol = "https"
	}
	if r.TLS != nil {
		p.Tls = &pb.TlsInfo{Version: r.TLS.Version, Cipher: r.TLS.Cipher, Ja3: r.TLS.JA3, Ja4: r.TLS.JA4}
	}
	return p
}

func fromProtoResponse(r *pb.DetectResponse) *Verdict {
	v := &Verdict{
		IsBot:             r.IsBot,
		BotProbability:    r.BotProbability,
		Confidence:        r.Confidence,
		BotType:           r.BotType,
		BotName:           r.BotName,
		RiskBand:          riskBandName(int32(r.RiskBand)),
		RecommendedAction: actionName(int32(r.RecommendedAction)),
		ThreatScore:       r.ThreatScore,
		ThreatBand:        threatBandName(int32(r.ThreatBand)),
		ProcessingTimeMs:  r.ProcessingTimeMs,
		DetectorsRun:      r.DetectorsRun,
	}
	for _, reason := range r.Reasons {
		v.Reasons = append(v.Reasons, Reason{Detector: reason.Detector, Detail: reason.Detail, Impact: reason.Impact})
	}
	return v
}

var reverseRiskBand = map[string]pb.RiskBand{
	"VeryLow":  pb.RiskBand_RISK_BAND_VERY_LOW,
	"Low":      pb.RiskBand_RISK_BAND_LOW,
	"Elevated": pb.RiskBand_RISK_BAND_ELEVATED,
	"Medium":   pb.RiskBand_RISK_BAND_MEDIUM,
	"High":     pb.RiskBand_RISK_BAND_HIGH,
	"VeryHigh": pb.RiskBand_RISK_BAND_VERY_HIGH,
	"Verified": pb.RiskBand_RISK_BAND_VERIFIED,
}

var reverseAction = map[string]pb.RecommendedAction{
	"Allow":     pb.RecommendedAction_RECOMMENDED_ACTION_ALLOW,
	"Throttle":  pb.RecommendedAction_RECOMMENDED_ACTION_THROTTLE,
	"Challenge": pb.RecommendedAction_RECOMMENDED_ACTION_CHALLENGE,
	"Block":     pb.RecommendedAction_RECOMMENDED_ACTION_BLOCK,
}

var reverseThreatBand = map[string]pb.ThreatBand{
	"None":     pb.ThreatBand_THREAT_BAND_NONE,
	"Low":      pb.ThreatBand_THREAT_BAND_LOW,
	"Elevated": pb.ThreatBand_THREAT_BAND_ELEVATED,
	"High":     pb.ThreatBand_THREAT_BAND_HIGH,
	"Critical": pb.ThreatBand_THREAT_BAND_CRITICAL,
}

func toProtoResponse(v *Verdict) *pb.DetectResponse {
	return &pb.DetectResponse{
		IsBot:             v.IsBot,
		BotProbability:    v.BotProbability,
		Confidence:        v.Confidence,
		BotType:           v.BotType,
		BotName:           v.BotName,
		RiskBand:          reverseRiskBand[v.RiskBand],
		RecommendedAction: reverseAction[v.RecommendedAction],
		ThreatScore:       v.ThreatScore,
		ThreatBand:        reverseThreatBand[v.ThreatBand],
		ProcessingTimeMs:  v.ProcessingTimeMs,
		DetectorsRun:      v.DetectorsRun,
	}
}
