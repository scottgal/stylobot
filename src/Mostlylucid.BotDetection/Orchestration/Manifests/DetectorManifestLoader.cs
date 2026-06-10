using System.Reflection;
using VYaml.Serialization;

namespace Mostlylucid.BotDetection.Orchestration.Manifests;

/// <summary>
/// Loads detector manifests from YAML files for dynamic composition.
/// Uses VYaml source-generated deserializers for AOT compatibility.
/// </summary>
public sealed class DetectorManifestLoader
{
    private readonly Dictionary<string, DetectorManifest> _detectorManifests = new();
    private readonly Dictionary<string, PipelineManifest> _pipelineManifests = new();

    /// <summary>
    /// Load all detector manifests from embedded resources.
    /// </summary>
    public IReadOnlyDictionary<string, DetectorManifest> LoadEmbeddedManifests()
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resourceNames = assembly.GetManifestResourceNames();

            foreach (var resourceName in resourceNames.Where(n =>
                n.EndsWith(".detector.yaml", StringComparison.OrdinalIgnoreCase)))
            {
                LoadDetectorFromResource(assembly, resourceName);
            }

            foreach (var resourceName in resourceNames.Where(n =>
                n.EndsWith(".pipeline.yaml", StringComparison.OrdinalIgnoreCase)))
            {
                LoadPipelineFromResource(assembly, resourceName);
            }
        }
        catch (Exception)
        {
            // Silently handle any errors during manifest loading
        }

        return _detectorManifests;
    }

    private void LoadDetectorFromResource(Assembly assembly, string resourceName)
    {
        try
        {
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null) return;

            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            var manifest = YamlSerializer.Deserialize<DetectorManifest>(ms.ToArray());

            if (manifest != null)
            {
                _detectorManifests[manifest.Name] = manifest;
            }
        }
        catch (Exception)
        {
            // Silently skip manifests that fail to parse
        }
    }

    private void LoadPipelineFromResource(Assembly assembly, string resourceName)
    {
        try
        {
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null) return;

            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            var manifest = YamlSerializer.Deserialize<PipelineManifest>(ms.ToArray());

            if (manifest != null)
            {
                _pipelineManifests[manifest.Name] = manifest;
            }
        }
        catch (Exception)
        {
            // Silently skip manifests that fail to parse
        }
    }

    /// <summary>
    /// Load detector manifests from a directory.
    /// </summary>
    // Manifests are small (the largest embedded one is a few KB); anything
    // megabytes-large in the manifest directory is a mistake, and feeding it to
    // the YAML deserializer would just burn memory.
    private const long MaxManifestFileBytes = 1_048_576;

    public IReadOnlyDictionary<string, DetectorManifest> LoadFromDirectory(string directory)
    {
        if (!Directory.Exists(directory))
            return _detectorManifests;

        foreach (var file in Directory.GetFiles(directory, "*.detector.yaml", SearchOption.AllDirectories))
        {
            if (new FileInfo(file).Length > MaxManifestFileBytes) continue;
            var bytes = File.ReadAllBytes(file);
            var manifest = YamlSerializer.Deserialize<DetectorManifest>(bytes);

            if (manifest != null)
            {
                _detectorManifests[manifest.Name] = manifest;
            }
        }

        foreach (var file in Directory.GetFiles(directory, "*.pipeline.yaml", SearchOption.AllDirectories))
        {
            if (new FileInfo(file).Length > MaxManifestFileBytes)
                throw new System.IO.InvalidDataException($"Pipeline manifest '{file}' exceeds {MaxManifestFileBytes} bytes.");
            var bytes = File.ReadAllBytes(file);
            var manifest = YamlSerializer.Deserialize<PipelineManifest>(bytes);

            if (manifest != null)
            {
                _pipelineManifests[manifest.Name] = manifest;
            }
        }

        return _detectorManifests;
    }

    /// <summary>
    /// Get a specific detector manifest by name.
    /// </summary>
    public DetectorManifest? GetDetectorManifest(string name)
    {
        return _detectorManifests.TryGetValue(name, out var manifest) ? manifest : null;
    }

    /// <summary>
    /// Get a specific pipeline manifest by name.
    /// </summary>
    public PipelineManifest? GetPipelineManifest(string name)
    {
        return _pipelineManifests.TryGetValue(name, out var manifest) ? manifest : null;
    }

    /// <summary>
    /// Get all loaded detector manifests.
    /// </summary>
    public IReadOnlyDictionary<string, DetectorManifest> GetAllDetectorManifests() => _detectorManifests;

    /// <summary>
    /// Get all loaded pipeline manifests.
    /// </summary>
    public IReadOnlyDictionary<string, PipelineManifest> GetAllPipelineManifests() => _pipelineManifests;

    /// <summary>
    /// Get all detector manifests sorted by priority (highest first).
    /// </summary>
    public IReadOnlyList<DetectorManifest> GetOrderedDetectorManifests()
    {
        return _detectorManifests.Values
            .Where(m => m.Enabled)
            .OrderByDescending(m => m.Priority)
            .ToList();
    }

    /// <summary>
    /// Get all signal contracts for LLM consumption.
    /// Returns a summary of what each detector emits and consumes.
    /// </summary>
    public string GetSignalContractsSummary()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# Detector Signal Contracts");
        sb.AppendLine();

        foreach (var manifest in GetOrderedDetectorManifests())
        {
            var contract = DetectorSignalContract.FromManifest(manifest);
            sb.AppendLine(contract.ToDescription());
        }

        return sb.ToString();
    }

    /// <summary>
    /// Get all signal contracts as structured data.
    /// </summary>
    public IReadOnlyList<DetectorSignalContract> GetAllContracts()
    {
        return GetOrderedDetectorManifests()
            .Select(DetectorSignalContract.FromManifest)
            .ToList();
    }

    /// <summary>
    /// Get detectors that can run given available signals.
    /// </summary>
    public IReadOnlyList<DetectorManifest> GetRunnableDetectors(IReadOnlySet<string> availableSignals)
    {
        return GetOrderedDetectorManifests()
            .Where(m => CanRun(m, availableSignals))
            .ToList();
    }

    /// <summary>
    /// Check if a detector can run given available signals.
    /// </summary>
    public bool CanRun(DetectorManifest manifest, IReadOnlySet<string> availableSignals)
    {
        // Check skip conditions first
        foreach (var skipSignal in manifest.Triggers.SkipWhen)
        {
            var resolved = ResolveSignalPattern(skipSignal, manifest.Name);
            if (availableSignals.Contains(resolved))
                return false;
        }

        // Check required signals
        foreach (var requirement in manifest.Triggers.Requires)
        {
            if (!availableSignals.Contains(requirement.Signal))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Get all signals that a detector emits.
    /// </summary>
    public IReadOnlySet<string> GetEmittedSignals(DetectorManifest manifest)
    {
        var signals = new HashSet<string>();

        signals.UnionWith(manifest.Emits.OnStart);
        signals.UnionWith(manifest.Emits.OnComplete.Select(s => s.Key));
        signals.UnionWith(manifest.Emits.OnFailure);
        signals.UnionWith(manifest.Emits.Conditional.Select(s => s.Key));

        return signals;
    }

    /// <summary>
    /// Get all signals that a detector listens to.
    /// </summary>
    public IReadOnlySet<string> GetListenedSignals(DetectorManifest manifest)
    {
        var signals = new HashSet<string>();

        signals.UnionWith(manifest.Listens.Required);
        signals.UnionWith(manifest.Listens.Optional);
        signals.UnionWith(manifest.Triggers.Requires.Select(r => r.Signal));

        return signals;
    }

    /// <summary>
    /// Build a dependency graph of detectors.
    /// </summary>
    public Dictionary<string, HashSet<string>> BuildDependencyGraph()
    {
        var graph = new Dictionary<string, HashSet<string>>();
        var signalToDetector = new Dictionary<string, string>();

        // First pass: map signals to producing detectors
        foreach (var manifest in _detectorManifests.Values)
        {
            foreach (var signal in GetEmittedSignals(manifest))
            {
                signalToDetector[signal] = manifest.Name;
            }
        }

        // Second pass: build dependencies
        foreach (var manifest in _detectorManifests.Values)
        {
            graph[manifest.Name] = new HashSet<string>();

            foreach (var signal in GetListenedSignals(manifest))
            {
                if (signalToDetector.TryGetValue(signal, out var producer) && producer != manifest.Name)
                {
                    graph[manifest.Name].Add(producer);
                }
            }
        }

        return graph;
    }

    /// <summary>
    /// Get topologically sorted detector execution order.
    /// </summary>
    public IReadOnlyList<string> GetExecutionOrder()
    {
        var graph = BuildDependencyGraph();
        var sorted = new List<string>();
        var visited = new HashSet<string>();
        var visiting = new HashSet<string>();

        void Visit(string detector)
        {
            if (visited.Contains(detector)) return;
            if (visiting.Contains(detector))
                throw new InvalidOperationException($"Circular dependency detected involving {detector}");

            visiting.Add(detector);

            if (graph.TryGetValue(detector, out var deps))
            {
                foreach (var dep in deps)
                {
                    Visit(dep);
                }
            }

            visiting.Remove(detector);
            visited.Add(detector);
            sorted.Add(detector);
        }

        foreach (var detector in graph.Keys.OrderByDescending(d => _detectorManifests[d].Priority))
        {
            Visit(detector);
        }

        return sorted;
    }

    private static string ResolveSignalPattern(string pattern, string detectorName)
    {
        return pattern.Replace("{name}", detectorName);
    }
}
