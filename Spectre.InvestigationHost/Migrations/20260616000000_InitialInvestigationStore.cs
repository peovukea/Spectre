using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Spectre.InvestigationHost.Data;

#nullable disable

namespace Spectre.InvestigationHost.Migrations;

[DbContext(typeof(InvestigationDbContext))]
[Migration("20260616000000_InitialInvestigationStore")]
public partial class InitialInvestigationStore : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
CREATE TABLE IF NOT EXISTS investigation_runs (
    id bigserial PRIMARY KEY,
    started_at_utc timestamptz NOT NULL DEFAULT now(),
    completed_at_utc timestamptz NULL,
    state text NOT NULL,
    elapsed_seconds bigint NOT NULL DEFAULT 0,
    is_partial boolean NOT NULL DEFAULT false,
    indexing_metrics jsonb NULL,
    filtering_metrics jsonb NULL
);

CREATE TABLE IF NOT EXISTS families (
    run_id bigint NOT NULL REFERENCES investigation_runs(id) ON DELETE CASCADE,
    family_id integer NOT NULL,
    key text NOT NULL,
    name text NOT NULL,
    first_window_start_nanos bigint NOT NULL,
    last_window_start_nanos bigint NOT NULL,
    PRIMARY KEY (run_id, family_id),
    UNIQUE (run_id, key)
);

CREATE TABLE IF NOT EXISTS slice_summaries (
    run_id bigint NOT NULL,
    family_id integer NOT NULL,
    family_key text NOT NULL,
    family_name text NOT NULL,
    window_start_nanos bigint NOT NULL,
    window_end_nanos bigint NOT NULL,
    window_start_iso text NOT NULL,
    document_count integer NOT NULL,
    interaction_count integer NOT NULL,
    max_semantic_weight double precision NOT NULL,
    total_semantic_weight double precision NOT NULL,
    predicate_counts jsonb NOT NULL,
    node_kind_counts jsonb NOT NULL,
    jaccard_node_kind jsonb NOT NULL,
    jaccard_previous_self jsonb NOT NULL,
    reduction jsonb NOT NULL,
    retention_level text NOT NULL,
    PRIMARY KEY (run_id, family_id, window_start_nanos),
    FOREIGN KEY (run_id, family_id) REFERENCES families(run_id, family_id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS node_documents (
    run_id bigint NOT NULL,
    family_id integer NOT NULL,
    window_start_nanos bigint NOT NULL,
    node_id uuid NOT NULL,
    node_kind text NOT NULL,
    jaccard_node_kind double precision NULL,
    jaccard_previous_self double precision NULL,
    term_counts jsonb NOT NULL,
    tfidf_weights jsonb NOT NULL,
    PRIMARY KEY (run_id, family_id, window_start_nanos, node_id),
    FOREIGN KEY (run_id, family_id, window_start_nanos) REFERENCES slice_summaries(run_id, family_id, window_start_nanos) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS backbone_interactions (
    run_id bigint NOT NULL,
    family_id integer NOT NULL,
    window_start_nanos bigint NOT NULL,
    source_node_id uuid NOT NULL,
    target_node_id uuid NOT NULL,
    count integer NOT NULL,
    semantic_weight double precision NOT NULL,
    predicate_counts jsonb NOT NULL,
    predicate_semantic_weights jsonb NOT NULL,
    term_counts jsonb NOT NULL,
    evidence jsonb NOT NULL,
    source_degree integer NOT NULL,
    source_strength double precision NOT NULL,
    source_normalized_weight double precision NOT NULL,
    source_significance double precision NULL,
    source_is_significant boolean NOT NULL,
    target_degree integer NOT NULL,
    target_strength double precision NOT NULL,
    target_normalized_weight double precision NOT NULL,
    target_significance double precision NULL,
    target_is_significant boolean NOT NULL,
    PRIMARY KEY (run_id, family_id, window_start_nanos, source_node_id, target_node_id),
    FOREIGN KEY (run_id, family_id, window_start_nanos) REFERENCES slice_summaries(run_id, family_id, window_start_nanos) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS ix_runs_started_at ON investigation_runs(started_at_utc DESC);
CREATE INDEX IF NOT EXISTS ix_summaries_run_family_window ON slice_summaries(run_id, family_id, window_start_nanos DESC);
CREATE INDEX IF NOT EXISTS ix_documents_kind ON node_documents(run_id, family_id, window_start_nanos, node_kind);
CREATE INDEX IF NOT EXISTS ix_interactions_weight ON backbone_interactions(run_id, family_id, window_start_nanos, semantic_weight DESC);
CREATE INDEX IF NOT EXISTS ix_interactions_predicate_counts_gin ON backbone_interactions USING gin(predicate_counts);
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
DROP TABLE IF EXISTS backbone_interactions;
DROP TABLE IF EXISTS node_documents;
DROP TABLE IF EXISTS slice_summaries;
DROP TABLE IF EXISTS families;
DROP TABLE IF EXISTS investigation_runs;
""");
    }
}
