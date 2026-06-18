using Microsoft.EntityFrameworkCore;
using Npgsql;
using Spectre.Ingestion;
using Spectre.DisparityFiltering;
using Spectre.InvestigationHost.Data;
using Spectre.InvestigationHost.Store;
using Spectre.SemanticIndexing;

namespace Spectre.InvestigationHost.Tests;

public sealed class PostgresInvestigationStoreTests
{
    private const string DefaultConnectionString = "Host=localhost;Port=55432;Database=spectre;Username=spectre;Password=spectre_dev_password";

    [Fact]
    public void Store_PersistsSliceAndRequeriesAfterRestart()
    {
        if (!PostgresTestsEnabled()) return;

        var connectionString = Environment.GetEnvironmentVariable("SPECTRE_TEST_POSTGRES")
            ?? DefaultConnectionString;
        if (!CanOpen(connectionString)) return;

        var dataSource = new NpgsqlDataSourceBuilder(connectionString).Build();
        try
        {
            ApplyMigrations(connectionString);
            ResetStore(dataSource);

            var store = new PostgresInvestigationStore(dataSource, new EventHub());
            var slice = CreateSlice();
            var edge = Assert.Single(slice.Interactions);
            var node = Assert.Single(slice.Documents, d => d.NodeId == edge.SourceNodeId);

            store.MarkRunState(RunState.Running);
            store.AcceptSlice(slice);

            Assert.Equal(RunState.Running, store.GetRunStatus().State);
            Assert.Equal(["READ", "WRITE"], store.GetPredicates().Order(StringComparer.Ordinal));
            Assert.Equal(["PROCESS"], store.GetNodeKinds().Order(StringComparer.Ordinal));
            Assert.Single(store.GetFamilies());
            Assert.Single(store.GetWindows(1));

            var projection = store.GetProjection(1, 0, new GraphQueryParameters(Predicate: "WRITE"));
            Assert.Equal(StoreQueryStatus.Found, projection.Status);
            Assert.Single(projection.Value!.Edges);
            Assert.Equal(1, projection.Value.TotalMatchingEdges);

            var nodeDetail = store.GetNodeDetail(1, 0, node.NodeId);
            Assert.Equal(StoreQueryStatus.Found, nodeDetail.Status);
            Assert.Equal("PROCESS", nodeDetail.Value!.Kind);

            var interactionDetail = store.GetInteractionDetail(1, 0, edge.SourceNodeId, edge.TargetNodeId);
            Assert.Equal(StoreQueryStatus.Found, interactionDetail.Status);
            Assert.Equal(3, interactionDetail.Value!.PredicateCounts["WRITE"]);
            Assert.Single(interactionDetail.Value.Evidence);

            var restartedStore = new PostgresInvestigationStore(dataSource, new EventHub());
            Assert.Single(restartedStore.GetFamilies());
            Assert.Single(restartedStore.GetWindows(1));
            Assert.Equal(StoreQueryStatus.Found, restartedStore.GetProjection(1, 0, new GraphQueryParameters()).Status);
            Assert.Equal(StoreQueryStatus.NotFound, restartedStore.GetProjection(1, 42, new GraphQueryParameters()).Status);
        }
        finally
        {
            dataSource.Dispose();
        }
    }

    private static bool PostgresTestsEnabled() =>
        string.Equals(Environment.GetEnvironmentVariable("SPECTRE_POSTGRES_TESTS"), "1", StringComparison.Ordinal);

    private static bool CanOpen(string connectionString)
    {
        try
        {
            using var connection = new NpgsqlConnection(connectionString);
            connection.Open();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void ApplyMigrations(string connectionString)
    {
        var options = new DbContextOptionsBuilder<InvestigationDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.SetPostgresVersion(17, 0))
            .Options;
        using var db = new InvestigationDbContext(options);
        db.Database.Migrate();
    }

    private static void ResetStore(NpgsqlDataSource dataSource)
    {
        using var connection = dataSource.OpenConnection();
        using var command = new NpgsqlCommand("TRUNCATE TABLE investigation_runs CASCADE", connection);
        command.ExecuteNonQuery();
    }

    private static DisparityGraphSlice CreateSlice()
    {
        var source = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var target = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var score = new DirectionalDisparityScore
        {
            Degree = 3,
            Strength = 10,
            NormalizedWeight = 0.8,
            Significance = 0.04,
            IsSignificant = true
        };

        return new DisparityGraphSlice
        {
            InputFamilyBasePath = "family.bin",
            WindowStartNanos = 0,
            WindowEndNanos = 1_000,
            Documents = [Document(source), Document(target)],
            Interactions =
            [
                new BackboneInteraction
                {
                    SourceNodeId = source,
                    TargetNodeId = target,
                    WindowStartNanos = 0,
                    WindowEndNanos = 1_000,
                    Count = 5,
                    SemanticWeight = 8,
                    PredicateCounts = new Dictionary<string, int> { ["READ"] = 2, ["WRITE"] = 3 },
                    PredicateSemanticWeights = new Dictionary<string, double> { ["READ"] = 3, ["WRITE"] = 5 },
                    TermCounts = new Dictionary<string, int> { ["TERM"] = 5 },
                    Evidence = [new EvidencePointer(new SourceLocation("segment.bin", 12), 100, Guid.Empty)],
                    SourceOutgoing = score,
                    TargetIncoming = score with { IsSignificant = false }
                }
            ],
            IndexingMetrics = new SemanticIndexingMetrics(),
            Reduction = new DisparitySliceReduction
            {
                Alpha = 0.05,
                SourceDocumentCount = 4,
                SourceInteractionCount = 4,
                CandidateEdgeCount = 3,
                RetainedDocumentCount = 2,
                RetainedEdgeCount = 1,
                SourceSemanticWeight = 10,
                RetainedSemanticWeight = 8
            },
            Metrics = new DisparityFilteringMetrics()
        };
    }

    private static BehavioralDocument Document(Guid nodeId) => new()
    {
        Key = new DocumentKey(nodeId, 0),
        NodeId = nodeId,
        WindowStartNanos = 0,
        WindowEndNanos = 1_000,
        NodeKind = "PROCESS",
        TermCounts = new Dictionary<string, int>(),
        TfidfWeights = new Dictionary<string, double>()
    };
}
