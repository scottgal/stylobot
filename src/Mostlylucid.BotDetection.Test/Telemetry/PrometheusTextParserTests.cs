using Mostlylucid.BotDetection.Telemetry.Prometheus;

namespace Mostlylucid.BotDetection.Test.Telemetry;

public class PrometheusTextParserTests
{
    private const string CounterFamily = """
# HELP http_requests_total Total HTTP requests.
# TYPE http_requests_total counter
http_requests_total{method="GET",code="200"} 12345
http_requests_total{method="POST",code="200"} 678
""";

    [Fact]
    public void Parses_counter_with_labels()
    {
        var metrics = PrometheusTextParser.Parse(CounterFamily);
        var fam = Assert.Single(metrics);
        Assert.Equal("http_requests_total", fam.Name);
        Assert.Equal(PrometheusMetricKind.Counter, fam.Kind);
        Assert.Equal("Total HTTP requests.", fam.Help);
        Assert.Equal(2, fam.Samples.Count);
        Assert.Equal("GET", fam.Samples[0].Labels["method"]);
        Assert.Equal("200", fam.Samples[0].Labels["code"]);
        Assert.Equal(12345d, fam.Samples[0].Value);
        Assert.Equal("POST", fam.Samples[1].Labels["method"]);
        Assert.Equal(678d, fam.Samples[1].Value);
    }

    [Fact]
    public void Parses_gauge_without_labels()
    {
        const string body = """
# HELP queue_depth Current queue depth.
# TYPE queue_depth gauge
queue_depth 42
""";
        var metrics = PrometheusTextParser.Parse(body);
        var fam = Assert.Single(metrics);
        Assert.Equal("queue_depth", fam.Name);
        Assert.Equal(PrometheusMetricKind.Gauge, fam.Kind);
        Assert.Single(fam.Samples);
        Assert.Empty(fam.Samples[0].Labels);
        Assert.Equal(42d, fam.Samples[0].Value);
    }

    [Fact]
    public void Parses_histogram_with_bucket_sum_count_under_single_family()
    {
        const string body = """
# HELP request_latency_seconds Request latency.
# TYPE request_latency_seconds histogram
request_latency_seconds_bucket{le="0.1"} 24054
request_latency_seconds_bucket{le="0.5"} 33444
request_latency_seconds_bucket{le="1"} 100392
request_latency_seconds_bucket{le="+Inf"} 144320
request_latency_seconds_sum 53423
request_latency_seconds_count 144320
""";
        var metrics = PrometheusTextParser.Parse(body);
        var fam = Assert.Single(metrics);
        Assert.Equal("request_latency_seconds", fam.Name);
        Assert.Equal(PrometheusMetricKind.Histogram, fam.Kind);
        Assert.Equal(6, fam.Samples.Count);

        Assert.Contains(fam.Samples, s => s.Name.EndsWith("_sum"));
        Assert.Contains(fam.Samples, s => s.Name.EndsWith("_count"));
        Assert.Equal(4, fam.Samples.Count(s => s.Name.EndsWith("_bucket")));
    }

    [Fact]
    public void Parses_summary_with_quantile_label()
    {
        const string body = """
# HELP rpc_durations_seconds RPC duration.
# TYPE rpc_durations_seconds summary
rpc_durations_seconds{quantile="0.5"} 0.012
rpc_durations_seconds{quantile="0.99"} 0.044
rpc_durations_seconds_sum 1.7560473e+02
rpc_durations_seconds_count 2693
""";
        var metrics = PrometheusTextParser.Parse(body);
        var fam = Assert.Single(metrics);
        Assert.Equal("rpc_durations_seconds", fam.Name);
        Assert.Equal(PrometheusMetricKind.Summary, fam.Kind);

        var p50 = fam.Samples.Single(s => s.Labels.TryGetValue("quantile", out var q) && q == "0.5");
        Assert.Equal(0.012d, p50.Value);
        var p99 = fam.Samples.Single(s => s.Labels.TryGetValue("quantile", out var q) && q == "0.99");
        Assert.Equal(0.044d, p99.Value);
    }

    [Fact]
    public void Recognises_help_type_and_unit_directives()
    {
        const string body = """
# HELP cpu_usage CPU utilisation ratio.
# TYPE cpu_usage gauge
# UNIT cpu_usage ratio
cpu_usage 0.42
""";
        var metrics = PrometheusTextParser.Parse(body);
        var fam = Assert.Single(metrics);
        Assert.Equal("CPU utilisation ratio.", fam.Help);
        Assert.Equal("ratio", fam.Unit);
        Assert.Equal(PrometheusMetricKind.Gauge, fam.Kind);
    }

    [Fact]
    public void Sample_without_type_directive_is_untyped()
    {
        const string body = """
mystery_metric{name="foo"} 7
""";
        var metrics = PrometheusTextParser.Parse(body);
        var fam = Assert.Single(metrics);
        Assert.Equal("mystery_metric", fam.Name);
        Assert.Equal(PrometheusMetricKind.Untyped, fam.Kind);
        Assert.Equal(7d, fam.Samples[0].Value);
    }

    [Fact]
    public void Tolerates_malformed_line_in_the_middle()
    {
        const string body = """
# TYPE good_a counter
good_a 1
this is not a valid line at all !!!!
# TYPE good_b gauge
good_b 99
""";
        var metrics = PrometheusTextParser.Parse(body);
        Assert.Equal(2, metrics.Count);
        Assert.Contains(metrics, m => m.Name == "good_a" && m.Samples[0].Value == 1d);
        Assert.Contains(metrics, m => m.Name == "good_b" && m.Samples[0].Value == 99d);
    }

    [Fact]
    public void Empty_body_returns_empty_list()
    {
        Assert.Empty(PrometheusTextParser.Parse(""));
        Assert.Empty(PrometheusTextParser.Parse("   "));
        Assert.Empty(PrometheusTextParser.Parse("\n\n\n"));
        Assert.Empty(PrometheusTextParser.Parse(null!));
    }

    [Fact]
    public void Normalises_crlf_line_endings()
    {
        var body = "# TYPE thing counter\r\nthing 5\r\n";
        var metrics = PrometheusTextParser.Parse(body);
        var fam = Assert.Single(metrics);
        Assert.Equal("thing", fam.Name);
        Assert.Equal(PrometheusMetricKind.Counter, fam.Kind);
        Assert.Equal(5d, fam.Samples[0].Value);
    }

    [Fact]
    public void Unescapes_label_values()
    {
        // The triple-quoted literal preserves the backslash sequences verbatim
        // so the parser sees the standard Prometheus escapes.
        const string body = """
# TYPE labelled gauge
labelled{path="C:\\Users\\jane",quote="she said \"hi\"",lines="a\nb"} 1
""";
        var metrics = PrometheusTextParser.Parse(body);
        var fam = Assert.Single(metrics);
        var sample = Assert.Single(fam.Samples);
        Assert.Equal(@"C:\Users\jane", sample.Labels["path"]);
        Assert.Equal("she said \"hi\"", sample.Labels["quote"]);
        Assert.Equal("a\nb", sample.Labels["lines"]);
    }

    [Fact]
    public void Parses_explicit_timestamp_when_present()
    {
        const string body = """
# TYPE ts_metric gauge
ts_metric 3.14 1620000000000
""";
        var metrics = PrometheusTextParser.Parse(body);
        var fam = Assert.Single(metrics);
        Assert.Equal(1620000000000L, fam.Samples[0].TimestampMs);
    }

    [Fact]
    public void Skips_lone_pound_comment_lines()
    {
        const string body = """
# this is a plain comment
# TYPE x gauge
x 1
""";
        var metrics = PrometheusTextParser.Parse(body);
        var fam = Assert.Single(metrics);
        Assert.Equal("x", fam.Name);
    }
}
