package stylobot

import (
	"crypto/tls"
	"net"
	"net/http"
	"strings"

	sb "github.com/scottgal/stylobot-go"
)

// ExtractIP returns the originating client IP from X-Forwarded-For or RemoteAddr.
func ExtractIP(r *http.Request) string {
	if xff := r.Header.Get("X-Forwarded-For"); xff != "" {
		parts := strings.SplitN(xff, ",", 2)
		return strings.TrimSpace(parts[0])
	}
	host, _, err := net.SplitHostPort(r.RemoteAddr)
	if err != nil {
		return r.RemoteAddr
	}
	return host
}

// ExtractHeaders returns a lowercase-keyed copy of the request headers.
func ExtractHeaders(r *http.Request) map[string]string {
	out := make(map[string]string, len(r.Header))
	for k, v := range r.Header {
		out[strings.ToLower(k)] = strings.Join(v, ", ")
	}
	return out
}

// ExtractTLS returns the TLS fingerprint metadata for the request, or nil when
// the request did not arrive over TLS. JA3/JA4 are populated only when a
// third-party Caddy plugin has set the X-JA3-Hash / X-JA4 headers (the standard
// library doesn't compute these). Version + Cipher always populate when r.TLS
// is set, since Caddy is doing TLS termination and has them natively.
func ExtractTLS(r *http.Request) *sb.TLSInfo {
	if r.TLS == nil {
		return nil
	}
	info := &sb.TLSInfo{
		Version: tlsVersionString(r.TLS.Version),
		Cipher:  tls.CipherSuiteName(r.TLS.CipherSuite),
	}
	if ja3 := r.Header.Get("X-JA3-Hash"); ja3 != "" {
		info.JA3 = ja3
	}
	if ja4 := r.Header.Get("X-JA4"); ja4 != "" {
		info.JA4 = ja4
	} else if ja4 := r.Header.Get("X-JA4-Fingerprint"); ja4 != "" {
		info.JA4 = ja4
	}
	return info
}

func tlsVersionString(v uint16) string {
	switch v {
	case tls.VersionTLS13:
		return "TLSv1.3"
	case tls.VersionTLS12:
		return "TLSv1.2"
	case tls.VersionTLS11:
		return "TLSv1.1"
	case tls.VersionTLS10:
		return "TLSv1.0"
	case tls.VersionSSL30:
		return "SSLv3"
	default:
		return ""
	}
}
