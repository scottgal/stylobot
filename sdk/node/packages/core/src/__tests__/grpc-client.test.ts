import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { mapGrpcVerdict } from '../grpc-client.ts';

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
      isBot: false, botProbability: 0.1, confidence: 0.9,
      botType: '', botName: '',
      riskBand: 'RISK_BAND_VERY_LOW', recommendedAction: 'RECOMMENDED_ACTION_ALLOW',
      threatScore: 0, threatBand: 'THREAT_BAND_NONE',
      processingTimeMs: 0.8, detectorsRun: 8,
    });
    assert.equal(verdict.botType, null);
    assert.equal(verdict.botName, null);
    assert.equal(verdict.riskBand, 'VeryLow');
  });

  it('falls back to Unknown/Allow/None for unrecognized enum values', () => {
    const verdict = mapGrpcVerdict({
      isBot: false, botProbability: 0, confidence: 0,
      botType: '', botName: '',
      riskBand: 'SOME_UNKNOWN_VALUE', recommendedAction: 'SOME_UNKNOWN_ACTION',
      threatScore: 0, threatBand: 'SOME_UNKNOWN_BAND',
      processingTimeMs: 0, detectorsRun: 0,
    });
    assert.equal(verdict.riskBand, 'Unknown');
    assert.equal(verdict.recommendedAction, 'Allow');
    assert.equal(verdict.threatBand, 'None');
  });
});
