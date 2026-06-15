export type RunState = "NotStarted" | "Running" | "Completed" | "Failed" | "Canceled";
export type RetentionLevel = "Summary" | "Projection" | "Detailed";

export interface IndexingMetrics {
  factsRead: number;
  edgeFactsRead: number;
  attributeFactsRead: number;
  documentsCreated: number;
  documentsClosed: number;
  interactionsCreated: number;
  interactionsClosed: number;
  factsSkippedWithoutTimestamp: number;
  lateFactsSkipped: number;
  slicesEmitted: number;
  unknownNodeKindUses: number;
}

export interface RunStatus {
  state: RunState;
  elapsedSeconds: number;
  isPartial: boolean;
  indexingMetrics: IndexingMetrics | null;
  filteringMetrics: FilteringMetrics | null;
}

export interface FilteringMetrics {
  sourceDocuments: number;
  sourceInteractions: number;
  candidateEdges: number;
  retainedDocuments: number;
  retainedEdges: number;
  sourceSemanticWeight: number;
  retainedSemanticWeight: number;
  slicesEmitted: number;
}

export interface MemoryPressure {
  retainedDetailedSlices: number;
  retainedProjections: number;
  totalSummaries: number;
  estimatedDetailedBytes: number;
  estimatedProjectionBytes: number;
  detailedBytesLimit: number;
  projectionBytesLimit: number;
  evictedDetailedSlices: number;
  evictedProjections: number;
  gcTotalMemoryBytes: number;
  workingSetBytes: number;
  gcHeapSizeBytes: number;
}

export interface FamilyInfo {
  id: number;
  key: string;
  name: string;
  firstWindowStartNanos: string;
  lastWindowStartNanos: string;
}

export interface JaccardDistribution {
  count: number;
  nullCount: number;
  min: number;
  max: number;
  mean: number;
  p25: number;
  p50: number;
  p75: number;
}

export interface SliceSummary {
  familyId: number;
  familyKey: string;
  familyName: string;
  windowStartNanos: string;
  windowEndNanos: string;
  windowStartIso: string;
  documentCount: number;
  interactionCount: number;
  maxSemanticWeight: number;
  totalSemanticWeight: number;
  predicateCounts: Record<string, number>;
  nodeKindCounts: Record<string, number>;
  jaccardNodeKind: JaccardDistribution;
  jaccardPreviousSelf: JaccardDistribution;
  reduction: DisparitySliceReduction;
  retentionLevel: RetentionLevel;
}

export interface DisparitySliceReduction {
  alpha: number;
  sourceDocumentCount: number;
  sourceInteractionCount: number;
  candidateEdgeCount: number;
  retainedDocumentCount: number;
  retainedEdgeCount: number;
  sourceSemanticWeight: number;
  retainedSemanticWeight: number;
}

export interface DirectionalDisparityScore {
  degree: number;
  strength: number;
  normalizedWeight: number;
  significance: number | null;
  isSignificant: boolean;
}

export interface ProjectedNode {
  id: string;
  kind: string;
  label: string;
  jaccardNodeKind: number | null;
  jaccardPreviousSelf: number | null;
}

export interface ProjectedEdge {
  source: string;
  target: string;
  count: number;
  semanticWeight: number;
  predicateCounts: Record<string, number>;
  sourceOutgoing: DirectionalDisparityScore;
  targetIncoming: DirectionalDisparityScore;
}

export interface GraphProjection {
  nodes: ProjectedNode[];
  edges: ProjectedEdge[];
  truncated: boolean;
  totalMatchingEdges: number;
  appliedMaxNodes: number;
  appliedMaxEdges: number;
  retentionLevel: RetentionLevel;
}

export interface NodeDetail extends ProjectedNode {
  termCounts: Record<string, number>;
  tfidfWeights: Record<string, number>;
}

export interface EvidencePointer {
  segmentPath: string;
  syncBlockOffset: number;
  timestampNanos: number | null;
  eventId: string | null;
}

export interface InteractionDetail {
  sourceId: string;
  targetId: string;
  count: number;
  semanticWeight: number;
  predicateCounts: Record<string, number>;
  predicateSemanticWeights: Record<string, number>;
  termCounts: Record<string, number>;
  evidence: EvidencePointer[];
  sourceOutgoing: DirectionalDisparityScore;
  targetIncoming: DirectionalDisparityScore;
}

export interface GraphFilters {
  minWeight: number;
  predicate: string;
  nodeKind: string;
  maxNodes: number;
  maxEdges: number;
}
