import type {
  FamilyInfo,
  GraphFilters,
  GraphProjection,
  InteractionDetail,
  MemoryPressure,
  NodeDetail,
  RunStatus,
  SliceSummary,
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

export const api = {
  status: () => get<RunStatus>("/status"),
  memory: () => get<MemoryPressure>("/memory"),
  families: () => get<FamilyInfo[]>("/families"),
  predicates: () => get<string[]>("/predicates"),
  nodeKinds: () => get<string[]>("/node-kinds"),
  windows: (familyId: number) => get<SliceSummary[]>(`/families/${familyId}/windows`),
  graph: (familyId: number, windowStart: string, filters: GraphFilters) => {
    const query = new URLSearchParams({
      minWeight: String(filters.minWeight),
      maxNodes: String(filters.maxNodes),
      maxEdges: String(filters.maxEdges),
    });
    if (filters.predicate) query.set("predicate", filters.predicate);
    if (filters.nodeKind) query.set("nodeKind", filters.nodeKind);
    return get<GraphProjection>(`/families/${familyId}/windows/${windowStart}/graph?${query}`);
  },
  node: (familyId: number, windowStart: string, nodeId: string) =>
    get<NodeDetail>(`/families/${familyId}/windows/${windowStart}/nodes/${nodeId}`),
  interaction: (familyId: number, windowStart: string, source: string, target: string) =>
    get<InteractionDetail>(
      `/families/${familyId}/windows/${windowStart}/interactions/${source}/${target}`,
    ),
  eventsUrl: `${API}/events`,
};
