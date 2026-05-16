using Grpc.Core;
using Grpc.Core.Interceptors;
using Microsoft.Extensions.Logging;

namespace Mostlylucid.BotDetection.Sidecar.Services;

/// <summary>
///     gRPC server interceptor enforcing API-key authentication on every unary call.
///     The sidecar's gRPC surface (Detect / DetectBatch / RenderWidget) drives the full
///     detection pipeline and mutates shared, persistent reputation state, so it must
///     not be reachable unauthenticated once keys are configured. When no keys are
///     configured the interceptor passes calls through — the host refuses to start in
///     that state unless bound to loopback only (see Program.cs).
/// </summary>
public sealed class ApiKeyInterceptor : Interceptor
{
    private readonly ApiKeyGrpcAuthenticator _authenticator;
    private readonly ILogger<ApiKeyInterceptor> _logger;

    public ApiKeyInterceptor(ApiKeyGrpcAuthenticator authenticator, ILogger<ApiKeyInterceptor> logger)
    {
        _authenticator = authenticator;
        _logger = logger;
    }

    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request,
        ServerCallContext context,
        UnaryServerMethod<TRequest, TResponse> continuation)
    {
        if (_authenticator.KeysConfigured)
        {
            var presentedKey = context.RequestHeaders.GetValue(ApiKeyGrpcAuthenticator.MetadataKey);
            var (ok, error) = _authenticator.Validate(presentedKey, context.Method);
            if (!ok)
            {
                _logger.LogWarning("Rejected unauthenticated gRPC call to {Method}: {Error}",
                    context.Method, error);
                throw new RpcException(new Status(StatusCode.Unauthenticated, error ?? "unauthenticated"));
            }
        }

        return await continuation(request, context);
    }
}
