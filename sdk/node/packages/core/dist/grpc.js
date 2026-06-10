import { createGrpcDetectionClient, grpcDetect, grpcRenderWidget, mapGrpcVerdict } from './grpc-client.js';
export class StyloBotGrpcClient {
    client;
    timeoutMs;
    constructor(endpoint, timeoutMs = 5000, options) {
        this.client = createGrpcDetectionClient(endpoint, options);
        this.timeoutMs = timeoutMs;
    }
    detect(req) {
        return grpcDetect(this.client, req, this.timeoutMs).then(mapGrpcVerdict);
    }
    renderWidget(template, verdict, vars) {
        return grpcRenderWidget(this.client, template, verdict, vars, this.timeoutMs);
    }
    close() {
        this.client.close();
    }
}
//# sourceMappingURL=grpc.js.map