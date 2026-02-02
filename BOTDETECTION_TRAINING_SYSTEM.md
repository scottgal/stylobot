# Bot Detection Training System

## Overview

A comprehensive offline training system that learns bot patterns from labeled traffic data and updates detection weights. Designed for continuous learning and model improvement.

## Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│                    TRAINING SYSTEM                               │
│                                                                  │
│  ┌─────────────────┐      ┌──────────────────┐                 │
│  │  Offline Mode   │─────>│  Pattern         │                 │
│  │  (BotDetection) │      │  Generator       │                 │
│  │                 │      │                  │                 │
│  │  • Observe      │      │  • IP patterns   │                 │
│  │  • Extract      │      │  • UA patterns   │                 │
│  │  • Serialize    │      │  • Behavior      │                 │
│  └─────────────────┘      │  • Signatures    │                 │
│                           └──────────────────┘                 │
│                                    │                            │
│                                    v                            │
│  ┌─────────────────────────────────────────────────────────┐  │
│  │           Training Data Format (JSON)                    │  │
│  │                                                           │  │
│  │  {                                                        │  │
│  │    "sessions": [                                         │  │
│  │      {                                                    │  │
│  │        "signature": "...",                               │  │
│  │        "label": "bot|human",                             │  │
│  │        "confidence": 1.0,                                │  │
│  │        "features": {...},                                │  │
│  │        "observations": [...]                             │  │
│  │      }                                                    │  │
│  │    ]                                                      │  │
│  │  }                                                        │  │
│  └─────────────────────────────────────────────────────────┘  │
│                                    │                            │
│                                    v                            │
│  ┌─────────────────┐      ┌──────────────────┐                 │
│  │  YARP Learning  │<─────│  Training        │                 │
│  │  Mode           │      │  Orchestrator    │                 │
│  │                 │      │                  │                 │
│  │  • Wide window  │      │  • Batch process │                 │
│  │  • Signature    │      │  • Update weights│                 │
│  │  • Weight update│      │  • Validation    │                 │
│  └─────────────────┘      └──────────────────┘                 │
│                                                                  │
│  ┌─────────────────────────────────────────────────────────┐  │
│  │           Training Simulator                             │  │
│  │                                                           │  │
│  │  X-Training-Mode: simulate                               │  │
│  │  X-Training-UA: <user-agent>                            │  │
│  │  X-Training-IP: <ip>                                    │  │
│  │  X-Training-Behavior: <json>                            │  │
│  │  X-Training-Label: bot|human                            │  │
│  │  X-Training-Clear-Weights: true                         │  │
│  └─────────────────────────────────────────────────────────┘  │
└─────────────────────────────────────────────────────────────────┘
```

## 1. Offline Pattern Generation

### Purpose
Run BotDetection in offline mode to observe real traffic and extract patterns for training.

### Configuration

```json
{
  "BotDetection": {
    "OfflineMode": {
      "Enabled": true,
      "OutputPath": "./training-data",
      "FileFormat": "jsonl",
      "RotationPolicy": {
        "MaxSizeBytes": 104857600,
        "MaxAgeHours": 24
      },
      "ObservationWindow": {
        "MinRequests": 10,
        "MaxRequests": 100,
        "TimeWindowMinutes": 30
      },
      "IncludeFeatures": [
        "ua_signature",
        "ip_signature",
        "behavior_signature",
        "fingerprint_hash",
        "request_timing",
        "path_patterns",
        "header_analysis"
      ],
      "RequireManualLabeling": false,
      "AutoLabel": {
        "HighConfidenceThreshold": 0.95,
        "LowConfidenceThreshold": 0.05,
        "UncertainRange": [0.4, 0.6]
      }
    }
  }
}
```

### Output Format (JSONL - one session per line)

```jsonl
{"sessionId":"sess_001","signature":"ip:192.168.1.100|ua:chrome_120","label":"human","confidence":0.95,"startTime":"2025-12-09T10:00:00Z","endTime":"2025-12-09T10:15:00Z","features":{"ipSignature":"residential_us_comcast","uaSignature":"chrome_120_win10","behaviorSignature":"normal_browsing","fingerprintHash":"fp_abc123"},"observations":[{"timestamp":"2025-12-09T10:00:00Z","path":"/","method":"GET","statusCode":200,"responseTime":150,"headers":{"accept":"text/html","acceptLanguage":"en-US,en;q=0.9"},"cookies":["session=xyz"],"referrer":null},{"timestamp":"2025-12-09T10:01:30Z","path":"/products","method":"GET","statusCode":200,"responseTime":180,"headers":{"accept":"text/html","acceptLanguage":"en-US,en;q=0.9"},"cookies":["session=xyz"],"referrer":"http://localhost/"}],"detectorResults":{"UserAgent":{"confidence":0.05,"botType":null,"reason":"Standard Chrome browser"},"IP":{"confidence":0.02,"botType":null,"reason":"Residential ISP"},"Behavioral":{"confidence":0.08,"botType":null,"reason":"Normal request rate"},"Fingerprint":{"confidence":0.03,"botType":null,"reason":"Consistent browser fingerprint"}},"aggregatedScore":0.05,"metadata":{"requestCount":15,"avgRequestInterval":5.2,"pathDiversity":0.7,"cookiePersistence":true,"referrerConsistency":true}}
{"sessionId":"sess_002","signature":"ip:203.0.113.42|ua:scrapy_2.11","label":"bot","confidence":0.98,"startTime":"2025-12-09T10:05:00Z","endTime":"2025-12-09T10:06:30Z","features":{"ipSignature":"datacenter_aws_us-east-1","uaSignature":"scrapy_2_11","behaviorSignature":"rapid_sequential","fingerprintHash":"fp_missing"},"observations":[{"timestamp":"2025-12-09T10:05:00Z","path":"/","method":"GET","statusCode":200,"responseTime":45},{"timestamp":"2025-12-09T10:05:02Z","path":"/products","method":"GET","statusCode":200,"responseTime":48},{"timestamp":"2025-12-09T10:05:04Z","path":"/products/1","method":"GET","statusCode":200,"responseTime":46}],"detectorResults":{"UserAgent":{"confidence":0.95,"botType":"Scraper","reason":"Known scraper user agent"},"IP":{"confidence":0.85,"botType":"Scraper","reason":"Datacenter IP"},"Behavioral":{"confidence":0.92,"botType":"Scraper","reason":"Rapid sequential requests, no cookies"}},"aggregatedScore":0.98,"metadata":{"requestCount":50,"avgRequestInterval":2.0,"pathDiversity":0.2,"cookiePersistence":false,"referrerConsistency":false}}
```

### Implementation

```csharp
public class OfflineModeCollector : IHostedService
{
    private readonly ILogger<OfflineModeCollector> _logger;
    private readonly OfflineModeOptions _options;
    private readonly IMemoryCache _cache;
    private StreamWriter? _writer;
    private readonly SemaphoreSlim _writeLock = new(1);

    public async Task ProcessDetectionAsync(
        HttpContext context,
        AggregatedEvidence evidence,
        CancellationToken ct)
    {
        if (!_options.Enabled) return;

        var sessionKey = GetSessionKey(context);
        var session = GetOrCreateSession(sessionKey, context, evidence);

        // Add observation
        session.Observations.Add(CreateObservation(context, evidence));

        // Check if session is complete
        if (ShouldCompleteSession(session))
        {
            await WriteSessionAsync(session, ct);
            _cache.Remove(sessionKey);
        }
    }

    private async Task WriteSessionAsync(TrainingSession session, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            var json = JsonSerializer.Serialize(session, new JsonSerializerOptions
            {
                WriteIndented = false
            });

            await _writer.WriteLineAsync(json);
            await _writer.FlushAsync();

            _logger.LogInformation(
                "Wrote training session {SessionId}: {Label} (confidence: {Confidence:F2})",
                session.SessionId, session.Label, session.Confidence);
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
```

## 2. Training Data Format

### TrainingSession Schema

```csharp
public class TrainingSession
{
    /// <summary>Unique session identifier</summary>
    public string SessionId { get; set; } = "";

    /// <summary>Composite signature for this session</summary>
    public string Signature { get; set; } = "";

    /// <summary>Ground truth label: "bot" or "human"</summary>
    public string Label { get; set; } = "";

    /// <summary>Confidence in the label (0.0-1.0)</summary>
    public double Confidence { get; set; }

    /// <summary>Session start time</summary>
    public DateTime StartTime { get; set; }

    /// <summary>Session end time</summary>
    public DateTime EndTime { get; set; }

    /// <summary>Extracted feature signatures</summary>
    public SessionFeatures Features { get; set; } = new();

    /// <summary>Individual request observations</summary>
    public List<RequestObservation> Observations { get; set; } = new();

    /// <summary>Per-detector results from live detection</summary>
    public Dictionary<string, DetectorResult> DetectorResults { get; set; } = new();

    /// <summary>Aggregated bot score from live detection</summary>
    public double AggregatedScore { get; set; }

    /// <summary>Session-level metadata</summary>
    public SessionMetadata Metadata { get; set; } = new();
}

public class SessionFeatures
{
    public string IpSignature { get; set; } = "";
    public string UaSignature { get; set; } = "";
    public string BehaviorSignature { get; set; } = "";
    public string FingerprintHash { get; set; } = "";
    public Dictionary<string, object> Custom { get; set; } = new();
}

public class RequestObservation
{
    public DateTime Timestamp { get; set; }
    public string Path { get; set; } = "";
    public string Method { get; set; } = "";
    public int StatusCode { get; set; }
    public int ResponseTime { get; set; }
    public Dictionary<string, string> Headers { get; set; } = new();
    public List<string> Cookies { get; set; } = new();
    public string? Referrer { get; set; }
    public int? ContentLength { get; set; }
}

public class SessionMetadata
{
    public int RequestCount { get; set; }
    public double AvgRequestInterval { get; set; }
    public double PathDiversity { get; set; }
    public bool CookiePersistence { get; set; }
    public bool ReferrerConsistency { get; set; }
    public Dictionary<string, object> Custom { get; set; } = new();
}
```

## 3. YARP Learning Mode

### Purpose
Separate learning mode in YARP that ingests training data and updates detection weights using a wide observation window.

### Configuration

```json
{
  "BotDetection": {
    "LearningMode": {
      "Enabled": true,
      "TrainingDataPath": "./training-data",
      "ModelOutputPath": "./models",
      "WindowSize": {
        "MinObservations": 10,
        "MaxObservations": 1000,
        "TimeWindowMinutes": 60
      },
      "WeightUpdates": {
        "LearningRate": 0.01,
        "Momentum": 0.9,
        "WeightDecay": 0.0001,
        "BatchSize": 100
      },
      "Validation": {
        "SplitRatio": 0.2,
        "MinValidationAccuracy": 0.90,
        "EarlyStopping": {
          "Patience": 5,
          "MinDelta": 0.001
        }
      },
      "SignatureMatching": {
        "ExactMatch": 1.0,
        "PartialMatch": 0.7,
        "FuzzyMatch": 0.4
      }
    }
  }
}
```

### Learning Algorithm

```csharp
public class LearningModeOrchestrator
{
    private readonly ILogger<LearningModeOrchestrator> _logger;
    private readonly LearningModeOptions _options;
    private readonly WeightStore _weightStore;
    private readonly SignatureCoordinator _signatureCoordinator;

    public async Task<TrainingResult> TrainAsync(
        string trainingDataPath,
        CancellationToken ct = default)
    {
        _logger.LogInformation("Starting training from {Path}", trainingDataPath);

        // 1. Load training data
        var sessions = await LoadTrainingDataAsync(trainingDataPath, ct);
        _logger.LogInformation("Loaded {Count} training sessions", sessions.Count);

        // 2. Split train/validation
        var (trainSet, valSet) = SplitData(sessions, _options.Validation.SplitRatio);

        // 3. Extract features and build vocabulary
        var vocabulary = BuildSignatureVocabulary(trainSet);
        _logger.LogInformation("Built vocabulary with {Count} unique signatures",
            vocabulary.Count);

        // 4. Training loop
        var result = new TrainingResult();
        var epoch = 0;
        var bestValAccuracy = 0.0;
        var patienceCounter = 0;

        while (epoch < 100 && patienceCounter < _options.Validation.EarlyStopping.Patience)
        {
            epoch++;

            // Shuffle and batch
            var batches = CreateBatches(trainSet, _options.WeightUpdates.BatchSize);

            // Train on batches
            var trainMetrics = await TrainEpochAsync(batches, vocabulary, ct);

            // Validate
            var valMetrics = await ValidateAsync(valSet, vocabulary, ct);

            _logger.LogInformation(
                "Epoch {Epoch}: Train Loss={TrainLoss:F4}, Train Acc={TrainAcc:F4}, " +
                "Val Loss={ValLoss:F4}, Val Acc={ValAcc:F4}",
                epoch, trainMetrics.Loss, trainMetrics.Accuracy,
                valMetrics.Loss, valMetrics.Accuracy);

            result.EpochMetrics.Add(new EpochMetrics
            {
                Epoch = epoch,
                TrainLoss = trainMetrics.Loss,
                TrainAccuracy = trainMetrics.Accuracy,
                ValLoss = valMetrics.Loss,
                ValAccuracy = valMetrics.Accuracy
            });

            // Early stopping check
            if (valMetrics.Accuracy > bestValAccuracy + _options.Validation.EarlyStopping.MinDelta)
            {
                bestValAccuracy = valMetrics.Accuracy;
                patienceCounter = 0;
                await SaveCheckpointAsync($"model_epoch_{epoch}.json", ct);
            }
            else
            {
                patienceCounter++;
            }

            // Minimum accuracy check
            if (valMetrics.Accuracy < _options.Validation.MinValidationAccuracy)
            {
                _logger.LogWarning(
                    "Validation accuracy {Acc:F4} below minimum {Min:F4}",
                    valMetrics.Accuracy, _options.Validation.MinValidationAccuracy);
            }
        }

        result.FinalValAccuracy = bestValAccuracy;
        result.TotalEpochs = epoch;

        _logger.LogInformation(
            "Training complete: {Epochs} epochs, best val accuracy={Acc:F4}",
            epoch, bestValAccuracy);

        return result;
    }

    private async Task<Metrics> TrainEpochAsync(
        List<Batch> batches,
        SignatureVocabulary vocabulary,
        CancellationToken ct)
    {
        double totalLoss = 0;
        int correct = 0;
        int total = 0;

        foreach (var batch in batches)
        {
            // Process each session in batch
            foreach (var session in batch.Sessions)
            {
                // Extract features
                var features = ExtractFeatures(session, vocabulary);

                // Get current prediction
                var prediction = await PredictAsync(features, ct);

                // Calculate error
                var label = session.Label == "bot" ? 1.0 : 0.0;
                var error = label - prediction;
                totalLoss += error * error;

                // Update weights using gradient descent
                await UpdateWeightsAsync(features, error, ct);

                // Track accuracy
                var predictedLabel = prediction > 0.5 ? "bot" : "human";
                if (predictedLabel == session.Label)
                    correct++;
                total++;
            }
        }

        return new Metrics
        {
            Loss = totalLoss / total,
            Accuracy = (double)correct / total
        };
    }

    private async Task UpdateWeightsAsync(
        Dictionary<string, double> features,
        double error,
        CancellationToken ct)
    {
        var learningRate = _options.WeightUpdates.LearningRate;
        var momentum = _options.WeightUpdates.Momentum;

        foreach (var (signature, value) in features)
        {
            // Get current weight
            var currentWeight = await _weightStore.GetWeightAsync(signature, ct);

            // Calculate gradient
            var gradient = error * value;

            // Apply momentum (if previous gradient exists)
            var velocity = await _weightStore.GetVelocityAsync(signature, ct);
            velocity = momentum * velocity + learningRate * gradient;

            // Update weight
            var newWeight = currentWeight + velocity;

            // Apply weight decay (L2 regularization)
            newWeight *= (1 - _options.WeightUpdates.WeightDecay);

            // Store updated weight and velocity
            await _weightStore.SetWeightAsync(signature, newWeight, ct);
            await _weightStore.SetVelocityAsync(signature, velocity, ct);
        }
    }

    private Dictionary<string, double> ExtractFeatures(
        TrainingSession session,
        SignatureVocabulary vocabulary)
    {
        var features = new Dictionary<string, double>();

        // IP signature features
        if (!string.IsNullOrEmpty(session.Features.IpSignature))
        {
            features[$"ip:{session.Features.IpSignature}"] = 1.0;

            // Partial matches
            foreach (var knownSig in vocabulary.IpSignatures)
            {
                var similarity = CalculateSimilarity(
                    session.Features.IpSignature, knownSig);
                if (similarity > 0.4)
                {
                    features[$"ip_partial:{knownSig}"] = similarity;
                }
            }
        }

        // UA signature features
        if (!string.IsNullOrEmpty(session.Features.UaSignature))
        {
            features[$"ua:{session.Features.UaSignature}"] = 1.0;
        }

        // Behavior signature features
        if (!string.IsNullOrEmpty(session.Features.BehaviorSignature))
        {
            features[$"behavior:{session.Features.BehaviorSignature}"] = 1.0;
        }

        // Fingerprint features
        if (!string.IsNullOrEmpty(session.Features.FingerprintHash))
        {
            features[$"fp:{session.Features.FingerprintHash}"] = 1.0;
        }

        // Metadata features
        features["request_count"] = Math.Log(session.Metadata.RequestCount + 1) / 10.0;
        features["avg_interval"] = 1.0 / (session.Metadata.AvgRequestInterval + 0.1);
        features["path_diversity"] = session.Metadata.PathDiversity;
        features["cookie_persistence"] = session.Metadata.CookiePersistence ? 1.0 : 0.0;
        features["referrer_consistency"] = session.Metadata.ReferrerConsistency ? 1.0 : 0.0;

        return features;
    }
}
```

## 4. Training Simulator Headers

### Purpose
Inject realistic traffic patterns during training to simulate real-world scenarios.

### Headers

```http
POST /api/test HTTP/1.1
X-Training-Mode: simulate
X-Training-UA: Mozilla/5.0 (compatible; Googlebot/2.1)
X-Training-IP: 66.249.66.1
X-Training-IP-Type: datacenter
X-Training-IP-ASN: AS15169
X-Training-Behavior: {
  "requestCount": 50,
  "avgInterval": 2.5,
  "pathDiversity": 0.3,
  "cookiePersistence": false,
  "referrerConsistency": false
}
X-Training-Fingerprint: {
  "screenWidth": 1920,
  "screenHeight": 1080,
  "webdriver": false,
  "headless": true
}
X-Training-Label: bot
X-Training-Confidence: 0.95
X-Training-Session-Id: training_sess_001
```

### Special Control Headers

```http
# Clear all weights (start fresh)
X-Training-Clear-Weights: true

# Save checkpoint
X-Training-Save-Checkpoint: checkpoint_001

# Load checkpoint
X-Training-Load-Checkpoint: checkpoint_001

# Export current weights
X-Training-Export-Weights: ./weights/current.json

# Set learning rate dynamically
X-Training-Learning-Rate: 0.01
```

### Implementation

```csharp
public class TrainingSimulatorMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<TrainingSimulatorMiddleware> _logger;
    private readonly LearningModeOptions _options;
    private readonly WeightStore _weightStore;

    public async Task InvokeAsync(HttpContext context)
    {
        // Check if training mode is active
        var trainingMode = context.Request.Headers["X-Training-Mode"].FirstOrDefault();
        if (trainingMode != "simulate" || !_options.Enabled)
        {
            await _next(context);
            return;
        }

        _logger.LogInformation("Training simulator request detected");

        // Handle control commands first
        if (context.Request.Headers.TryGetValue("X-Training-Clear-Weights", out var clearHeader)
            && bool.TryParse(clearHeader, out var shouldClear) && shouldClear)
        {
            await HandleClearWeightsAsync(context);
            return;
        }

        if (context.Request.Headers.TryGetValue("X-Training-Save-Checkpoint", out var saveCheckpoint))
        {
            await HandleSaveCheckpointAsync(context, saveCheckpoint!);
            return;
        }

        if (context.Request.Headers.TryGetValue("X-Training-Export-Weights", out var exportPath))
        {
            await HandleExportWeightsAsync(context, exportPath!);
            return;
        }

        // Extract training parameters
        var trainingData = ExtractTrainingData(context);

        // Inject into context for detectors to use
        context.Items["Training.Mode"] = true;
        context.Items["Training.Data"] = trainingData;
        context.Items["Training.Label"] = trainingData.Label;

        // Override request data with training data
        if (!string.IsNullOrEmpty(trainingData.UserAgent))
        {
            context.Request.Headers["User-Agent"] = trainingData.UserAgent;
        }

        if (!string.IsNullOrEmpty(trainingData.IP))
        {
            context.Items["Override.ClientIP"] = trainingData.IP;
        }

        // Continue pipeline - detectors will use training data
        await _next(context);

        // After detection, update weights based on label
        if (context.Items.TryGetValue("BotDetection.AggregatedEvidence", out var evidenceObj)
            && evidenceObj is AggregatedEvidence evidence)
        {
            await UpdateWeightsFromTrainingAsync(evidence, trainingData);
        }
    }

    private async Task HandleClearWeightsAsync(HttpContext context)
    {
        _logger.LogWarning("Clearing all detection weights");

        await _weightStore.ClearAllAsync();

        context.Response.StatusCode = 200;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(new
        {
            success = true,
            message = "All weights cleared",
            timestamp = DateTime.UtcNow
        });
    }

    private TrainingData ExtractTrainingData(HttpContext context)
    {
        var data = new TrainingData();

        // Extract from headers
        data.UserAgent = context.Request.Headers["X-Training-UA"].FirstOrDefault();
        data.IP = context.Request.Headers["X-Training-IP"].FirstOrDefault();
        data.Label = context.Request.Headers["X-Training-Label"].FirstOrDefault() ?? "unknown";

        if (double.TryParse(
            context.Request.Headers["X-Training-Confidence"].FirstOrDefault(),
            out var confidence))
        {
            data.Confidence = confidence;
        }

        data.SessionId = context.Request.Headers["X-Training-Session-Id"].FirstOrDefault();

        // Parse behavior JSON
        var behaviorJson = context.Request.Headers["X-Training-Behavior"].FirstOrDefault();
        if (!string.IsNullOrEmpty(behaviorJson))
        {
            data.Behavior = JsonSerializer.Deserialize<BehaviorPattern>(behaviorJson);
        }

        // Parse fingerprint JSON
        var fingerprintJson = context.Request.Headers["X-Training-Fingerprint"].FirstOrDefault();
        if (!string.IsNullOrEmpty(fingerprintJson))
        {
            data.Fingerprint = JsonSerializer.Deserialize<BrowserFingerprint>(fingerprintJson);
        }

        return data;
    }

    private async Task UpdateWeightsFromTrainingAsync(
        AggregatedEvidence evidence,
        TrainingData trainingData)
    {
        if (string.IsNullOrEmpty(trainingData.Label))
            return;

        var targetLabel = trainingData.Label == "bot" ? 1.0 : 0.0;
        var prediction = evidence.BotProbability;
        var error = targetLabel - prediction;

        _logger.LogInformation(
            "Training update: Label={Label}, Prediction={Pred:F4}, Error={Error:F4}",
            trainingData.Label, prediction, error);

        // Update weights for each contributing signature
        foreach (var (signature, contribution) in evidence.SignatureContributions)
        {
            var currentWeight = await _weightStore.GetWeightAsync(signature);
            var learningRate = _options.WeightUpdates.LearningRate;

            // Gradient is error * feature value (contribution)
            var gradient = error * contribution;
            var update = learningRate * gradient;

            var newWeight = currentWeight + update;

            await _weightStore.SetWeightAsync(signature, newWeight);

            _logger.LogDebug(
                "Updated weight for {Signature}: {Old:F4} -> {New:F4} (update: {Update:F4})",
                signature, currentWeight, newWeight, update);
        }
    }
}

public class TrainingData
{
    public string? UserAgent { get; set; }
    public string? IP { get; set; }
    public string Label { get; set; } = "";
    public double Confidence { get; set; } = 1.0;
    public string? SessionId { get; set; }
    public BehaviorPattern? Behavior { get; set; }
    public BrowserFingerprint? Fingerprint { get; set; }
}
```

## 5. CLI Training Tool

```bash
# Generate training data from live traffic
dotnet run --project BotDetection.CLI -- offline-mode start \
  --output ./training-data \
  --duration 1h \
  --auto-label \
  --confidence-threshold 0.95

# Train from generated data
dotnet run --project BotDetection.CLI -- train \
  --data ./training-data/*.jsonl \
  --output ./models/model_v1 \
  --epochs 50 \
  --batch-size 100 \
  --learning-rate 0.01 \
  --validation-split 0.2

# Clear weights and retrain
dotnet run --project BotDetection.CLI -- train \
  --data ./training-data/*.jsonl \
  --clear-weights \
  --output ./models/model_v2

# Simulate training traffic
dotnet run --project BotDetection.CLI -- simulate-training \
  --target http://localhost:5000 \
  --sessions 1000 \
  --bot-ratio 0.3 \
  --adaptive

# Export weights for analysis
dotnet run --project BotDetection.CLI -- export-weights \
  --output ./weights/current.json \
  --format json

# Compare two weight sets
dotnet run --project BotDetection.CLI -- compare-weights \
  --baseline ./weights/baseline.json \
  --current ./weights/current.json \
  --output comparison.html
```

## 6. Training Pipeline Example

```csharp
// Complete training pipeline
public class TrainingPipeline
{
    public async Task<TrainingResult> RunAsync(CancellationToken ct)
    {
        // 1. Generate training data from live traffic
        _logger.LogInformation("Starting offline data collection...");
        await _offlineCollector.StartAsync(TimeSpan.FromHours(1), ct);
        var dataFiles = Directory.GetFiles("./training-data", "*.jsonl");
        _logger.LogInformation("Collected {Count} training files", dataFiles.Length);

        // 2. Clear previous weights
        _logger.LogInformation("Clearing previous weights...");
        await _weightStore.ClearAllAsync();

        // 3. Train on generated data
        _logger.LogInformation("Starting training...");
        var result = await _learningOrchestrator.TrainAsync(
            string.Join(",", dataFiles), ct);

        _logger.LogInformation(
            "Training complete: {Epochs} epochs, final accuracy={Acc:F4}",
            result.TotalEpochs, result.FinalValAccuracy);

        // 4. Validate on test set
        _logger.LogInformation("Running validation...");
        var testResult = await ValidateOnTestSetAsync(ct);
        _logger.LogInformation("Test accuracy: {Acc:F4}", testResult.Accuracy);

        // 5. Export trained model
        _logger.LogInformation("Exporting model...");
        await ExportModelAsync("./models/latest.json", ct);

        return result;
    }
}
```

## Files to Create

```
Mostlylucid.BotDetection/
├── Training/
│   ├── OfflineModeCollector.cs
│   ├── LearningModeOrchestrator.cs
│   ├── TrainingSimulatorMiddleware.cs
│   ├── WeightStore.cs
│   ├── SignatureVocabulary.cs
│   ├── Models/
│   │   ├── TrainingSession.cs
│   │   ├── TrainingData.cs
│   │   ├── TrainingResult.cs
│   │   └── Metrics.cs
│   └── Options/
│       ├── OfflineModeOptions.cs
│       └── LearningModeOptions.cs

Stylobot.Gateway/
├── Training/
│   ├── YarpLearningMode.cs
│   └── README_LEARNING_MODE.md

Mostlylucid.BotDetection.CLI/
├── Commands/
│   ├── OfflineModeCommand.cs
│   ├── TrainCommand.cs
│   ├── SimulateTrainingCommand.cs
│   └── ExportWeightsCommand.cs
└── Program.cs
```

This system provides:
- ✅ Offline pattern generation from real traffic
- ✅ JSONL format for efficient streaming
- ✅ YARP learning mode with wide observation windows
- ✅ Training simulator with header injection
- ✅ Weight management (clear, save, load, export)
- ✅ Validation and early stopping
- ✅ CLI tools for all operations
- ✅ Gradient descent with momentum
- ✅ Signature-based feature extraction
- ✅ Batch processing and checkpointing

Ready to implement?
