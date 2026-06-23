using Xunit;

namespace Mostlylucid.BotDetection.Orchestration.Tests.Unit.Identity;

/// <summary>
///     Serialises Identity tests that exercise <c>SqliteFingerprintStore</c> +
///     <c>IdentityProcessingCoordinator</c>. xUnit defaults to running test classes within
///     an assembly in parallel; under that scheme these tests intermittently fail because
///     SQLitePCLRaw's shared native connection pool serialises writes across distinct DB
///     paths, which under CPU saturation lets the next test's L1 lookup race the previous
///     observation's commit, dropping the observation_count_crossed signal or skipping a
///     ParentAbsorbPath emission.
///
///     Pinning these classes to a single collection (DisableParallelization = true)
///     removes the contention without slowing the broader suite, since the rest of the
///     orchestration tests remain parallelisable.
/// </summary>
[CollectionDefinition("IdentitySqlite", DisableParallelization = true)]
public class IdentitySqliteCollection;
