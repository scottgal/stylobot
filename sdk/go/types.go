package stylobot

import "context"

// DetectRequest is the framework-agnostic request type.
type DetectRequest struct {
	Method   string
	Path     string
	Headers  map[string]string
	RemoteIP string
	Protocol string // "http" or "https"; defaults to "https" if empty
	TLS      *TLSInfo
}

// TLSInfo carries optional TLS fingerprint data.
type TLSInfo struct {
	Version string
	Cipher  string
	JA3     string
	JA4     string
}

// Verdict is the framework-agnostic detection result.
type Verdict struct {
	IsBot             bool
	BotProbability    float32
	Confidence        float32
	BotType           string
	BotName           string
	RiskBand          string
	RecommendedAction string
	ThreatScore       float32
	ThreatBand        string
	ProcessingTimeMs  float32
	DetectorsRun      int32
	Reasons           []Reason
}

// Reason is a single detector's contribution to the verdict.
type Reason struct {
	Detector string
	Detail   string
	Impact   float32
}

// RenderRequest is a Liquid template rendering request.
type RenderRequest struct {
	Template string
	Verdict  *Verdict          // optional; nil = no detection context injected
	Vars     map[string]string // additional template variables
}

// RenderResponse is the rendered HTML result.
type RenderResponse struct {
	HTML    string
	Success bool
	Error   string
}

// Client is the primary interface for StyloBot sidecar interaction.
// Callers depend on this interface, not the concrete gRPC implementation.
type Client interface {
	Detect(ctx context.Context, req DetectRequest) (*Verdict, error)
	DetectBatch(ctx context.Context, reqs []DetectRequest) ([]*Verdict, error)
	RenderWidget(ctx context.Context, req RenderRequest) (*RenderResponse, error)
	Close() error
}

var riskBandNames = map[int32]string{
	0: "Unknown", 1: "VeryLow", 2: "Low", 3: "Elevated",
	4: "Medium", 5: "High", 6: "VeryHigh", 7: "Verified",
}

var actionNames = map[int32]string{
	0: "Allow", 1: "Throttle", 2: "Challenge", 3: "Block",
}

var threatBandNames = map[int32]string{
	0: "None", 1: "Low", 2: "Elevated", 3: "High", 4: "Critical",
}

func riskBandName(v int32) string {
	if s, ok := riskBandNames[v]; ok {
		return s
	}
	return "Unknown"
}

func actionName(v int32) string {
	if s, ok := actionNames[v]; ok {
		return s
	}
	return "Allow"
}

func threatBandName(v int32) string {
	if s, ok := threatBandNames[v]; ok {
		return s
	}
	return "None"
}
