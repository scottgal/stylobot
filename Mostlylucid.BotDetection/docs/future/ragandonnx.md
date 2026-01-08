# Feature Specification: RAG‑Enhanced ONNX Contributor

## Overview

Introduce a **Retrieval‑Augmented Generation (RAG) layer** into the bot detection pipeline. This layer enriches request
signatures with contextual embeddings and clusters them across instances. An **ONNX contributor** provides low‑latency
classification of cluster similarity, enabling adaptive routing and shared memory across servers.

---

## Goals

- **Adaptive clustering**: Group similar behavioural signatures (e.g., UA anomalies, header quirks, rate patterns)
  across servers.
- **Low‑latency inference**: Use ONNX runtime for millisecond‑scale embedding and classification.
- **Shared immune memory**: Registry of clusters acts as distributed memory, propagating detections across instances.
- **Explainability**: Contributions logged as JSON scorecards with cluster IDs, confidence, and reasons.
- **Scalability**: Horizontal deployment with deterministic ONNX outputs ensures consistency across nodes.

---

## Architecture

1. **Trigger Conditions**
    - Fires after basic detectors and heuristics have contributed.
    - Requires at least one behavioural or header signal.

2. **Pipeline Flow**
    - **Fast Path**: Deterministic detectors (UA, IP, headers, behavioural).
    - **Heuristic Contributor**: Learned weights refine confidence.
    - **RAG Layer**:
        - Generate embeddings for request signatures.
        - Query central registry for nearest clusters.
        - Return cluster similarity + metadata.
    - **ONNX Contributor**:
        - Classify cluster similarity (bot vs human vs unknown).
        - Output confidence delta + weighted impact.
    - **LLM Contributors**:
        - Escalate ambiguous cases to contextual reasoning.
    - **Quorum**: Weighted contributions produce final verdict.

3. **Outputs**
    - Signals:
        - `ClusterId` (UUID of matched cluster)
        - `ClusterConfidence` (0–1)
        - `AiPrediction` (bot/human/unknown)
    - Reasons:
        - “Cluster #42 matched: datacenter IP + missing Sec‑Fetch headers + burst rate.”
    - Weight: Configurable (default 2.5, stronger than heuristics but weaker than deep LLM).

---

## Security & Trust

- **Reputational weighting**: Contributions from instances weighted by historical accuracy.
- **Cryptographic signing**: Registry entries signed to prevent poisoning.
- **Decay policies**: Clusters expire after configurable TTL to avoid stale detections.
- **Supply chain security**: Registry integrity verified before propagation.

---

## Performance Targets

- **Embedding generation**: < 10ms per request (MiniLM ONNX baseline).
- **Cluster lookup**: < 50ms with vector index (FAISS or equivalent).
- **Contributor runtime**: < 100ms end‑to‑end.
- **Registry sync**: Near‑real‑time (< 1s propagation across instances).

---

## Future Extensions

- **Federated registries**: Multiple registries with trust weighting.
- **Adaptive pipeline routing**: Requests dynamically routed to detector pairs based on cluster shape.
- **Explainable dashboards**: Visualize cluster evolution and cross‑instance propagation.

---

This spec positions the ONNX + RAG layer as the **memory + adaptive intelligence tier** in your immune‑system
architecture: fast path for innate checks, heuristics for lightweight adaptation, ONNX + RAG for shared memory, and LLMs
for deep reasoning.
