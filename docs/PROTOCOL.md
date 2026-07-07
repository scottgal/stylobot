# Agent channel protocol

Simple file-drop channel between the Claude Code agents working on the stylobot codebase. Multiple agents now share the channel — messages route by filename prefix.

## Directory layout

```
/tmp/agent-channel/
├── PROTOCOL.md      (this file)
├── inbox/           (new messages — routed by filename prefix)
└── outbox/          (replies — routed by filename prefix)
```

## Routing prefixes

Every filename **must** start with a prefix that identifies its addressee:

| Prefix     | Agent                                          | Scope                                                                 |
|------------|------------------------------------------------|-----------------------------------------------------------------------|
| `overview-`| Session-persistence / architecture agent (main worktree) | Refactor context, FOSS+commercial architecture, DI wiring, cross-cutting design. |
| `foss-`    | FOSS-side agent (currently `claude/atom-followups` worktree) | FOSS-only work: atom contract tests, manifests, orchestrator, detector code. **Runtime issues live here too** — live-system incidents, prod/staging misbehaviour, request-path debugging, detection-pipeline runtime bugs. |
| `feature-` | Commercial feature agent (certificate + editor stuff) | Commercial features: license certs, config editor, dashboard UX built on top of FOSS. |

New agents joining the channel should pick an existing prefix if their scope matches, or propose a new one via an `overview-` message before starting to send.

## Filename convention

### Inbox — new messages

```
inbox/<addressee-prefix>-<slug>.md
```

- `<addressee-prefix>` is who READS the message.
- `<slug>` is kebab-case, descriptive, unique per topic.

Examples:
- `inbox/overview-manifest-hygiene-question.md` — someone asks the overview agent about manifest hygiene.
- `inbox/foss-atom-contract-followup.md` — someone asks the FOSS agent about atom contracts.
- `inbox/feature-cert-rotation-design.md` — someone asks the feature agent about cert rotation.

### Outbox — replies

```
outbox/<original-sender-prefix>-<slug>.reply.md
```

- `<original-sender-prefix>` is who reads the REPLY (i.e., the agent who originally sent the inbox message).
- `<slug>` matches the original inbox slug exactly.
- `.reply.md` suffix always.

Example: if `overview` sends `inbox/foss-atom-contract-followup.md`, the FOSS agent replies with `outbox/overview-atom-contract-followup.reply.md`.

### Threads longer than one round

If a reply prompts a follow-up, drop a new inbox file prefixed `follow-up-` after the routing prefix:

```
inbox/foss-follow-up-atom-contract-diagnostic-results.md
```

Reply lands as `outbox/overview-follow-up-atom-contract-diagnostic-results.reply.md`. The `follow-up-` marker helps humans scan the channel history.

## Monitoring — what each agent watches

Every agent runs a `fswatch` monitor (or equivalent) on **two** patterns:

1. `inbox/<my-prefix>-*.md` — new messages TO me.
2. `outbox/<my-prefix>-*.reply.md` — replies to messages I sent.

Skill: use `Monitor` with a shell script that filters `basename` startswith `<my-prefix>-`. Do not watch other prefixes.

## Message shape

Free-form markdown. Standard header at the top:

```markdown
# <topic in one line>

**From:** <agent name / role>
**Timestamp:** <ISO-8601 UTC>
**Referring to:** <commit hash / file path / prior message slug / task # if applicable>

<body — the actual question or observation>
```

## Reply shape

Replies land at `outbox/<original-sender-prefix>-<slug>.reply.md`. Each reply follows this shape so the sender can act on it without re-grepping context:

```markdown
# Re: <original question topic>

**Replying to:** inbox/<addressee-prefix>-<slug>.md
**Timestamp:** <ISO-8601 UTC>
**Confidence:** <high / medium / low — how sure I am about the answer>

## Direct answer

<the actual answer, sized to the question. One paragraph for a
clarification; several sections for a design call.>

## Why (short reasoning)

<key facts + principles behind the answer.>

## Relevant docs

<bullet list of paths inside stylobot / commercial repos, with a one-liner
on what each covers. Prefer specific docs to broad ones.>

## Relevant code

<bullet list of paths + line numbers when meaningful. Include commit hash
if the answer depends on state at a specific commit.>

## Open follow-ups (if any)

<things this answer surfaces that need a next step.>
```

Match the *shape* of the reply to the question — a one-line clarification gets a compact reply; a design question gets the full structure. But every reply gets doc/code pointers so the sender can verify + extend without re-derived context.

## Priority rule

**Inbox messages take priority over other work.** When your monitor fires, finish the immediate tool call in flight, then reply to inbox before returning to your own task queue. Peer agents block on our answers.

## Etiquette

- One question per file. Three questions = three files.
- Include enough context that the addressee can answer without grepping from scratch: commit hash, file path, or a quoted excerpt.
- Include your own commit SHA + worktree name in the `**From:**` line so the addressee knows what state you're on.
- When you notice your work will overlap another agent's area, drop a `heads-up` inbox message BEFORE starting, not after committing.
- If you touch anything that will affect another agent, send them a follow-up outbox message when you commit — include the SHA and any action items they need to take on rebase.
