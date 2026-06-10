-- SqlitePolicyDecisionLog base schema. Forward-only ALTER patches live
-- under Policies/Decisions/Schema/*.sql and are applied separately by
-- ApplyForwardOnlyPatches() so the base CREATE stays migration-free.

CREATE TABLE IF NOT EXISTS policy_decisions (
    id            INTEGER PRIMARY KEY AUTOINCREMENT,
    rule_id       TEXT NOT NULL,
    winner_rule_id TEXT NOT NULL,
    matched       INTEGER NOT NULL,
    fingerprint   TEXT,
    action_kind   TEXT NOT NULL,
    mode          TEXT NOT NULL,
    eval_ticks    INTEGER NOT NULL,
    observed_at   INTEGER NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_policy_decisions_rule_observed
    ON policy_decisions(rule_id, observed_at);
CREATE INDEX IF NOT EXISTS ix_policy_decisions_winner_observed
    ON policy_decisions(winner_rule_id, observed_at);
CREATE INDEX IF NOT EXISTS ix_policy_decisions_fingerprint
    ON policy_decisions(fingerprint, observed_at);
