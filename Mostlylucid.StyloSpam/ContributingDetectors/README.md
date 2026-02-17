# StyloSpam Contributing Detectors

This folder will host detector contributors that plug into the StyloSpam scoring engine.

## Implemented

- `AuthenticationSignalsContributor`
- `UrlPatternContributor`
- `SpamPhraseContributor`
- `AttachmentRiskContributor`
- `RecipientSpreadContributor`
- `OutgoingVelocityContributor`
- `LocalLlmSemanticContributor` (small local model endpoint, disabled by default)

## Planned

- Sender infrastructure reputation contributor (domain age, ASN risk, brand-mimic distance).
- URL expansion and redirect chain risk contributor.
- Attachment deep-inspection contributor (archive nesting and macro/script indicators).
- Campaign similarity contributor (SimHash/MinHash near-duplicate clustering).
- Graph anomaly contributor (sender-recipient temporal community anomalies).
- Cross-tenant reputation contributor (privacy-preserving aggregate risk).

See `Specs/SPEC_StyloSpam_Detectors.md` for the full state-of-the-art roadmap and references.

