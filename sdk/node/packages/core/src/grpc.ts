import * as grpc from '@grpc/grpc-js';
import type { DetectRequest, Verdict } from './types.js';
import { createGrpcDetectionClient, grpcDetect, grpcRenderWidget, mapGrpcVerdict, type GrpcClientOptions } from './grpc-client.js';

export class StyloBotGrpcClient {
  private readonly client: grpc.Client;
  private readonly timeoutMs: number;

  constructor(endpoint: string, timeoutMs = 5000, options?: GrpcClientOptions) {
    this.client = createGrpcDetectionClient(endpoint, options);
    this.timeoutMs = timeoutMs;
  }

  detect(req: DetectRequest): Promise<Verdict> {
    return grpcDetect(this.client, req, this.timeoutMs).then(mapGrpcVerdict);
  }

  renderWidget(template: string, verdict?: Verdict, vars?: Record<string, string>): Promise<string> {
    return grpcRenderWidget(this.client, template, verdict, vars, this.timeoutMs);
  }

  close(): void {
    this.client.close();
  }
}
