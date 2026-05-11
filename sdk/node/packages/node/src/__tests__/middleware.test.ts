import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { IncomingMessage } from 'node:http';
import { Socket } from 'node:net';
import { styloBotMiddleware } from '../middleware.ts';
import { mapGrpcVerdict } from '@stylobot/core';

function mockExpressReq(headers: Record<string, string> = {}): any {
  const socket = new Socket();
  Object.defineProperty(socket, 'remoteAddress', { value: '127.0.0.1', writable: true, configurable: true });
  const req = new IncomingMessage(socket);
  req.method = 'GET'; req.url = '/test'; req.headers = headers;
  (req as any).originalUrl = '/test'; (req as any).ip = '127.0.0.1'; (req as any).protocol = 'https';
  return req;
}

describe('styloBotMiddleware (header mode)', () => {
  it('parses X-StyloBot-* headers into req.stylobot', (_, done) => {
    const mw = styloBotMiddleware({ mode: 'headers' });
    const req = mockExpressReq({
      'x-stylobot-isbot': 'true', 'x-stylobot-probability': '0.88', 'x-stylobot-confidence': '0.75',
      'x-stylobot-bottype': 'AiBot', 'x-stylobot-botname': 'Claude', 'x-stylobot-riskband': 'Medium',
      'x-stylobot-action': 'Challenge', 'x-stylobot-threatscore': '0.05', 'x-stylobot-threatband': 'None',
    });
    mw(req, {} as any, () => {
      assert.equal(req.stylobot.isBot, true);
      assert.equal(req.stylobot.verdict.botProbability, 0.88);
      assert.equal(req.stylobot.verdict.botType, 'AiBot');
      assert.equal(req.stylobot.verdict.recommendedAction, 'Challenge');
      done();
    });
  });

  it('returns empty verdict when no headers present', (_, done) => {
    const mw = styloBotMiddleware({ mode: 'headers' });
    mw(mockExpressReq({}), {} as any, () => {
      assert.equal(mockExpressReq({}).stylobot, undefined);
      done();
    });
  });
});

describe('styloBotMiddleware (api mode)', () => {
  it('throws if endpoint is not provided', () => {
    assert.throws(() => styloBotMiddleware({ mode: 'api' }), /endpoint is required/);
  });
});

describe('styloBotMiddleware (grpc mode)', () => {
  it('throws if endpoint is not provided', () => {
    assert.throws(() => styloBotMiddleware({ mode: 'grpc' }), /endpoint is required/);
  });
});

describe('mapGrpcVerdict', () => {
  it('maps a bot response correctly', () => {
    const verdict = mapGrpcVerdict({
      isBot: true,
      botProbability: 0.92,
      confidence: 0.85,
      botType: 'AiBot',
      botName: 'GPTBot',
      riskBand: 'RISK_BAND_HIGH',
      recommendedAction: 'RECOMMENDED_ACTION_BLOCK',
      threatScore: 0.7,
      threatBand: 'THREAT_BAND_ELEVATED',
      processingTimeMs: 2.5,
      detectorsRun: 12,
    });
    assert.equal(verdict.isBot, true);
    assert.equal(verdict.botProbability, 0.92);
    assert.equal(verdict.riskBand, 'High');
    assert.equal(verdict.recommendedAction, 'Block');
    assert.equal(verdict.threatBand, 'Elevated');
    assert.equal(verdict.botType, 'AiBot');
    assert.equal(verdict.botName, 'GPTBot');
  });

  it('maps empty bot type and name to null', () => {
    const verdict = mapGrpcVerdict({
      isBot: false,
      botProbability: 0.1,
      confidence: 0.9,
      botType: '',
      botName: '',
      riskBand: 'RISK_BAND_VERY_LOW',
      recommendedAction: 'RECOMMENDED_ACTION_ALLOW',
      threatScore: 0,
      threatBand: 'THREAT_BAND_NONE',
      processingTimeMs: 0.8,
      detectorsRun: 8,
    });
    assert.equal(verdict.botType, null);
    assert.equal(verdict.botName, null);
    assert.equal(verdict.riskBand, 'VeryLow');
    assert.equal(verdict.recommendedAction, 'Allow');
    assert.equal(verdict.threatBand, 'None');
  });

  it('falls back to Unknown/Allow/None for unrecognized enum values', () => {
    const verdict = mapGrpcVerdict({
      isBot: false,
      botProbability: 0,
      confidence: 0,
      botType: '',
      botName: '',
      riskBand: 'SOME_UNKNOWN_VALUE',
      recommendedAction: 'SOME_UNKNOWN_ACTION',
      threatScore: 0,
      threatBand: 'SOME_UNKNOWN_BAND',
      processingTimeMs: 0,
      detectorsRun: 0,
    });
    assert.equal(verdict.riskBand, 'Unknown');
    assert.equal(verdict.recommendedAction, 'Allow');
    assert.equal(verdict.threatBand, 'None');
  });
});
