export type RunState = "NotStarted" | "Running" | "Completed" | "Failed" | "Canceled";
export type RetentionLevel = "Summary" | "Projection" | "Detailed";
export type Int64String = string;

export interface IndexingMetrics {
  factsRead: Int64String;
  edgeFactsRead: Int64String;
  attributeFactsRead: Int64String;
  documentsCreated: Int64String;
  documentsClosed: Int64String;
  interactionsCreated: Int64String;
  interactionsClosed: Int64String;
  factsSkippedWithoutTimestamp: Int64String;
  lateFactsSkipped: Int64String;
  slicesEmitted: Int64String;
  unknownNodeKindUses: Int64String;
}

export interface RunStatus {
  state: RunState;
  elapsedSeconds: Int64String;
  isPartial: boolean;
  indexingMetrics: IndexingMetrics | null;
  filteringMetrics: FilteringMetrics | null;
}

export interface FilteringMetrics {
  sourceDocuments: Int64String;
  sourceInteractions: Int64String;
  candidateEdges: Int64String;
  retainedDocuments: Int64String;
  retainedEdges: Int64String;
  sourceSemanticWeight: number;
  retainedSemanticWeight: number;
  slicesEmitted: Int64String;
}

export interface MemoryPressure {
  retainedDetailedSlices: number;
  retainedProjections: number;
  totalSummaries: number;
  estimatedDetailedBytes: Int64String;
  estimatedProjectionBytes: Int64String;
  detailedBytesLimit: Int64String;
  projectionBytesLimit: Int64String;
  evictedDetailedSlices: number;
  evictedProjections: number;
  gcTotalMemoryBytes: Int64String;
  workingSetBytes: Int64String;
  gcHeapSizeBytes: Int64String;
}

export interface FamilyInfo {
  id: number;
  key: string;
  name: string;
  firstWindowStartNanos: Int64String;
  lastWindowStartNanos: Int64String;
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
  windowStartNanos: Int64String;
  windowEndNanos: Int64String;
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
  syncBlockOffset: Int64String;
  timestampNanos: Int64String | null;
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
