import type {
  FamilyInfo,
  GraphFilters,
  GraphProjection,
  IngestionControlResult,
  InteractionDetail,
  MemoryPressure,
  NodeDetail,
  RunInfo,
  RunStatus,
  SliceSummary,
  StartIngestionRequest,
} from "./contracts";

const API = "/api/spectre";

export class ApiError extends Error {
  constructor(public status: number, message: string) {
    super(message);
  }
}

async function get<T>(path: string): Promise<T> {
  const response = await fetch(`${API}${path}`, { cache: "no-store" });
  if (!response.ok) throw new ApiError(response.status, `${response.status} ${response.statusText}`);
  return response.json() as Promise<T>;
}

function runQuery(runId?: string | null) {
  return runId ? `runId=${encodeURIComponent(runId)}` : "";
}

function appendRun(path: string, runId?: string | null) {
  const query = runQuery(runId);
  if (!query) return path;
  return `${path}${path.includes("?") ? "&" : "?"}${query}`;
}

async function post<T>(path: string, body?: unknown): Promise<T> {
  const response = await fetch(`${API}${path}`, {
    method: "POST",
    headers: body == null ? undefined : { "Content-Type": "application/json" },
    body: body == null ? undefined : JSON.stringify(body),
    cache: "no-store",
  });

  if (!response.ok) {
    let message = `${response.status} ${response.statusText}`;
    try {
      const payload = await response.json() as { message?: string; error?: string };
      message = payload.message ?? payload.error ?? message;
    } catch {
      // Keep the HTTP status text when the backend returns an empty/non-JSON error.
    }
    throw new ApiError(response.status, message);
  }

  return response.json() as Promise<T>;
}

export const api = {
  runs: () => get<RunInfo[]>("/runs"),
  status: (runId?: string | null) => get<RunStatus>(appendRun("/status", runId)),
  startIngestion: (inputPath?: string) => {
    const request: StartIngestionRequest = inputPath ? { inputPath } : {};
    return post<IngestionControlResult>("/ingestion/start", request);
  },
  cancelIngestion: () => post<IngestionControlResult>("/ingestion/cancel"),
  memory: () => get<MemoryPressure>("/memory"),
  families: (runId?: string | null) => get<FamilyInfo[]>(appendRun("/families", runId)),
  predicates: (runId?: string | null) => get<string[]>(appendRun("/predicates", runId)),
  nodeKinds: (runId?: string | null) => get<string[]>(appendRun("/node-kinds", runId)),
  windows: (familyId: number, runId?: string | null) => get<SliceSummary[]>(appendRun(`/families/${familyId}/windows`, runId)),
  graph: (familyId: number, windowStart: string, filters: GraphFilters, runId?: string | null) => {
    const query = new URLSearchParams({
      minWeight: String(filters.minWeight),
      maxNodes: String(filters.maxNodes),
      maxEdges: String(filters.maxEdges),
    });
    if (runId) query.set("runId", runId);
    if (filters.predicate) query.set("predicate", filters.predicate);
    if (filters.nodeKind) query.set("nodeKind", filters.nodeKind);
    return get<GraphProjection>(`/families/${familyId}/windows/${windowStart}/graph?${query}`);
  },
  node: (familyId: number, windowStart: string, nodeId: string, runId?: string | null) =>
    get<NodeDetail>(appendRun(`/families/${familyId}/windows/${windowStart}/nodes/${nodeId}`, runId)),
  interaction: (familyId: number, windowStart: string, source: string, target: string, runId?: string | null) =>
    get<InteractionDetail>(
      appendRun(`/families/${familyId}/windows/${windowStart}/interactions/${source}/${target}`, runId),
    ),
  eventsUrl: `${API}/events`,
};
