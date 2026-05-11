package stylobot

import "time"

type clientOptions struct {
	timeout time.Duration
	apiKey  string
}

// Option configures a Client.
type Option func(*clientOptions)

// WithTimeout sets the per-request gRPC deadline (default: 50ms).
func WithTimeout(d time.Duration) Option {
	return func(o *clientOptions) { o.timeout = d }
}

// WithAPIKey sets an API key forwarded as gRPC metadata.
func WithAPIKey(key string) Option {
	return func(o *clientOptions) { o.apiKey = key }
}
