"use client";

import { ApiError, api } from "@/lib/api";
import type {
  FamilyInfo,
  GraphFilters,
  GraphProjection,
  InteractionDetail,
  MemoryPressure,
  NodeDetail,
  ProjectedEdge,
  ProjectedNode,
  RetentionLevel,
  RunStatus,
  SliceSummary,
} from "@/lib/contracts";
import { useCallback, useEffect, useMemo, useState } from "react";
import { GraphCanvas } from "./graph-canvas";

const DEFAULT_FILTERS: GraphFilters = {
  minWeight: 0,
  predicate: "",
  nodeKind: "",
  maxNodes: 250,
  maxEdges: 200,
};
const VISIBLE_WINDOW_COUNT = 5;

function number(value?: number | null) {
  if (value == null) return "-";
  return new Intl.NumberFormat("en", { notation: value >= 1_000_000 ? "compact" : "standard", maximumFractionDigits: 1 }).format(value);
}

function bytes(value?: number | null) {
  if (value == null) return "-";
  const units = ["B", "KB", "MB", "GB"];
  let size = value;
  let unit = 0;
  while (size >= 1024 && unit < units.length - 1) {
    size /= 1024;
    unit++;
  }
  return `${size.toFixed(unit > 1 ? 1 : 0)} ${units[unit]}`;
}

function time(iso: string) {
  return new Intl.DateTimeFormat("en", { month: "short", day: "2-digit", hour: "2-digit", minute: "2-digit", second: "2-digit" }).format(new Date(iso));
}

function retentionTone(level: RetentionLevel) {
  return level === "Detailed" ? "text-[#50e3a4] border-[#285b49] bg-[#102d24]" :
    level === "Projection" ? "text-[#f5b95f] border-[#604a28] bg-[#2a2113]" :
    "text-[#78958c] border-[#2a413b] bg-[#101e1b]";
}

export function InvestigationDashboard() {
  const [status, setStatus] = useState<RunStatus | null>(null);
  const [memory, setMemory] = useState<MemoryPressure | null>(null);
  const [families, setFamilies] = useState<FamilyInfo[]>([]);
  const [windows, setWindows] = useState<SliceSummary[]>([]);
  const [predicates, setPredicates] = useState<string[]>([]);
  const [nodeKinds, setNodeKinds] = useState<string[]>([]);
  const [familyId, setFamilyId] = useState<number | null>(null);
  const [windowStart, setWindowStart] = useState<string | null>(null);
  const [filters, setFilters] = useState<GraphFilters>(DEFAULT_FILTERS);
  const [graph, setGraph] = useState<GraphProjection | null>(null);
  const [graphLoading, setGraphLoading] = useState(false);
  const [notice, setNotice] = useState<string | null>(null);
  const [connection, setConnection] = useState<"connecting" | "live" | "offline">("connecting");
  const [selected, setSelected] = useState<NodeDetail | InteractionDetail | null>(null);
  const [detailLoading, setDetailLoading] = useState(false);

  const selectedWindow = useMemo(
    () => windows.find((window) => window.windowStartNanos === windowStart) ?? null,
    [windows, windowStart],
  );
  const selectedFamily = families.find((family) => family.id === familyId) ?? null;
  const customFilters = JSON.stringify(filters) !== JSON.stringify(DEFAULT_FILTERS);
  const visibleWindows = useMemo(
    () => windows.slice(-VISIBLE_WINDOW_COUNT).reverse(),
    [windows],
  );

  const refreshGlobal = useCallback(async () => {
    try {
      const [nextStatus, nextMemory, nextFamilies, nextPredicates, nextKinds] = await Promise.all([
        api.status(), api.memory(), api.families(), api.predicates(), api.nodeKinds(),
      ]);
      setStatus(nextStatus);
      setMemory(nextMemory);
      setFamilies(nextFamilies);
      setPredicates(nextPredicates);
      setNodeKinds(nextKinds);
      setFamilyId((current) => current ?? nextFamilies.at(-1)?.id ?? null);
    } catch {
      setConnection("offline");
    }
  }, []);

  const refreshWindows = useCallback(async (id: number) => {
    const next = await api.windows(id);
    const latestVisible = next.slice(-VISIBLE_WINDOW_COUNT);
    const latestGraphWindow = latestVisible.findLast((window) => window.retentionLevel !== "Summary");
    setWindows(next);
    setWindowStart((current) =>
      current != null && latestVisible.some((window) =>
        window.windowStartNanos === current && window.retentionLevel !== "Summary")
        ? current
        : latestGraphWindow?.windowStartNanos ?? next.at(-1)?.windowStartNanos ?? null,
    );
  }, []);

  useEffect(() => {
    const initialRefresh = window.setTimeout(refreshGlobal, 0);
    const source = new EventSource(api.eventsUrl);
    source.onopen = () => setConnection("live");
    source.onerror = () => {
      setConnection("connecting");
      refreshGlobal();
      if (familyId != null) refreshWindows(familyId);
    };
    source.addEventListener("run-status", (event) => setStatus(JSON.parse(event.data) as RunStatus));
    source.addEventListener("memory-pressure", (event) => setMemory(JSON.parse(event.data) as MemoryPressure));
    source.addEventListener("slice-closed", () => {
      refreshGlobal();
      if (familyId != null) refreshWindows(familyId);
    });
    source.addEventListener("retention-changed", () => {
      if (familyId != null) refreshWindows(familyId);
    });
    const reconciliation = window.setInterval(refreshGlobal, 10_000);
    return () => {
      source.close();
      window.clearTimeout(initialRefresh);
      window.clearInterval(reconciliation);
    };
  }, [familyId, refreshGlobal, refreshWindows]);

  useEffect(() => {
    let refresh: number | undefined;
    if (familyId != null) {
      refresh = window.setTimeout(() => {
        refreshWindows(familyId).catch(() => setNotice("Unable to load this family's windows."));
      }, 0);
    }
    return () => {
      if (refresh != null) window.clearTimeout(refresh);
    };
  }, [familyId, refreshWindows]);

  useEffect(() => {
    let active = true;
    const load = window.setTimeout(() => {
      if (familyId == null || windowStart == null || selectedWindow?.retentionLevel === "Summary") {
        setGraph(null);
        setGraphLoading(false);
        setNotice(selectedWindow?.retentionLevel === "Summary"
          ? "This window retains summary statistics only. Select a detailed or projection window to view its graph."
          : null);
        return;
      }
      if (selectedWindow?.retentionLevel === "Projection" && customFilters) {
        setNotice("Custom filters require detailed retention. Reset filters to view this projection.");
        setGraph(null);
        return;
      }
      setGraphLoading(true);
      setNotice(null);
      setSelected(null);
      api.graph(familyId, windowStart, filters)
        .then((next) => {
          if (!active) return;
          setGraph(next);
          setNotice(null);
        })
        .catch((error: unknown) => {
          if (!active) return;
          setGraph(null);
          setNotice(error instanceof ApiError && error.status === 410
            ? "Graph data for this window has been evicted from memory."
            : "The graph projection could not be loaded.");
        })
        .finally(() => active && setGraphLoading(false));
    }, 0);
    return () => {
      active = false;
      window.clearTimeout(load);
    };
  }, [customFilters, familyId, filters, selectedWindow?.retentionLevel, windowStart]);

  async function selectNode(node: ProjectedNode) {
    if (familyId == null || windowStart == null) return;
    if (selectedWindow?.retentionLevel !== "Detailed") {
      setNotice("Node details are available only while this window has detailed retention.");
      return;
    }
    setDetailLoading(true);
    try {
      setSelected(await api.node(familyId, windowStart, node.id));
    } catch (error) {
      if (error instanceof ApiError && error.status === 410) {
        await refreshWindows(familyId);
        setNotice("This window moved from detailed to projection retention before the node details loaded.");
      } else {
        setNotice("Node details could not be loaded.");
      }
    } finally {
      setDetailLoading(false);
    }
  }

  async function selectEdge(edge: ProjectedEdge) {
    if (familyId == null || windowStart == null) return;
    if (selectedWindow?.retentionLevel !== "Detailed") {
      setNotice("Interaction evidence is available only while this window has detailed retention.");
      return;
    }
    setDetailLoading(true);
    try {
      setSelected(await api.interaction(familyId, windowStart, edge.source, edge.target, edge.predicate));
    } catch (error) {
      if (error instanceof ApiError && error.status === 410) {
        await refreshWindows(familyId);
        setNotice("This window moved from detailed to projection retention before the interaction evidence loaded.");
      } else {
        setNotice("Interaction details could not be loaded.");
      }
    } finally {
      setDetailLoading(false);
    }
  }

  return (
    <main className="min-h-screen p-3 lg:h-screen lg:overflow-hidden">
      <header className="mb-3 flex flex-wrap items-center justify-between gap-3 px-1">
        <div className="flex items-center gap-3">
          <div className="grid h-9 w-9 place-items-center rounded-lg border border-[#2b6552] bg-[#102d24] text-lg font-black text-[#50e3a4]">S</div>
          <div>
            <h1 className="text-[15px] font-bold tracking-[0.18em] text-[#e8f6f1]">SPECTRE</h1>
            <p className="eyebrow">Semantic investigation workspace</p>
          </div>
        </div>
        <div className="flex items-center gap-4 rounded-lg border border-[#1d3731] bg-[#0a1714] px-4 py-2">
          <span className={`h-2 w-2 rounded-full ${connection === "live" ? "bg-[#50e3a4] shadow-[0_0_10px_#50e3a4]" : "bg-[#f5b95f]"}`} />
          <span className="eyebrow">{connection === "live" ? "Live stream" : connection}</span>
          <span className="h-4 w-px bg-[#28453e]" />
          <span className="text-xs font-semibold text-[#cde0d9]">{status?.state ?? "Connecting"}</span>
          {status?.isPartial && <span className="rounded border border-[#6f4a2c] bg-[#2e1f14] px-2 py-0.5 text-[10px] font-bold text-[#f5b95f]">PARTIAL</span>}
          <span className="mono text-[11px] text-[#78958c]">{number(status?.elapsedSeconds)}s</span>
        </div>
      </header>

      <section className="grid gap-3 lg:h-[calc(100vh-72px)] lg:grid-cols-[245px_minmax(0,1fr)_300px]">
        <aside className="panel flex min-h-0 flex-col overflow-hidden">
          <div className="border-b border-[#1d3731] p-4">
            <p className="eyebrow mb-2">Input family</p>
            <select className="control text-xs" value={familyId ?? ""} onChange={(event) => setFamilyId(Number(event.target.value))}>
              {families.length === 0 && <option value="">Awaiting slices</option>}
              {families.map((family) => <option key={family.id} value={family.id}>{family.name}</option>)}
            </select>
          </div>
          <div className="border-b border-[#1d3731] p-4">
            <p className="eyebrow mb-3">Current run</p>
            <div className="grid grid-cols-2 gap-x-4 gap-y-3">
              <Stat label="Facts" value={status?.indexingMetrics?.factsRead} />
              <Stat label="Slices" value={status?.indexingMetrics?.slicesEmitted ?? memory?.totalSummaries} />
              <Stat label="Documents" value={status?.indexingMetrics?.documentsClosed} />
              <Stat label="Interactions" value={status?.indexingMetrics?.interactionsClosed} />
              <Stat label="Late skipped" value={status?.indexingMetrics?.lateFactsSkipped} />
              <Stat label="Unknown kinds" value={status?.indexingMetrics?.unknownNodeKindUses} />
            </div>
          </div>
          <div className="min-h-0 flex-1 overflow-y-auto p-2">
            <div className="flex items-center justify-between px-2 py-2">
              <p className="eyebrow">Window timeline</p>
              <span className="mono text-[10px] text-[#78958c]">
                latest {Math.min(windows.length, VISIBLE_WINDOW_COUNT)} / {windows.length}
              </span>
            </div>
            <div className="space-y-1">
              {visibleWindows.map((window) => (
                <button
                  key={window.windowStartNanos}
                  onClick={() => setWindowStart(window.windowStartNanos)}
                  className={`w-full rounded-lg border px-3 py-2 text-left transition ${windowStart === window.windowStartNanos ? "border-[#3c806a] bg-[#123329]" : "border-transparent hover:border-[#24463d] hover:bg-[#0d211d]"}`}
                >
                  <div className="mb-1 flex items-center justify-between gap-2">
                    <span className="mono text-[10px] text-[#b9d0c8]">{time(window.windowStartIso)}</span>
                    <span className={`rounded border px-1.5 py-0.5 text-[8px] font-bold uppercase ${retentionTone(window.retentionLevel)}`}>{window.retentionLevel}</span>
                  </div>
                  <div className="flex gap-3 text-[10px] text-[#78958c]">
                    <span>{number(window.documentCount)} nodes</span>
                    <span>{number(window.interactionCount)} edges</span>
                  </div>
                </button>
              ))}
            </div>
          </div>
        </aside>

        <section className="flex min-h-0 flex-col gap-3">
          <div className="panel flex flex-wrap items-end gap-3 p-3">
            <Filter label="Min weight">
              <input className="control w-24 text-xs" type="number" min="0" step="1" value={filters.minWeight} onChange={(e) => setFilters({ ...filters, minWeight: Number(e.target.value) })} />
            </Filter>
            <Filter label="Predicate">
              <select className="control w-44 text-xs" value={filters.predicate} onChange={(e) => setFilters({ ...filters, predicate: e.target.value })}>
                <option value="">All predicates</option>
                {predicates.map((predicate) => <option key={predicate}>{predicate}</option>)}
              </select>
            </Filter>
            <Filter label="Node kind">
              <select className="control w-40 text-xs" value={filters.nodeKind} onChange={(e) => setFilters({ ...filters, nodeKind: e.target.value })}>
                <option value="">All node kinds</option>
                {nodeKinds.map((kind) => <option key={kind}>{kind}</option>)}
              </select>
            </Filter>
            <Filter label="Node cap">
              <input className="control w-20 text-xs" type="number" min="2" max="1000" value={filters.maxNodes} onChange={(e) => setFilters({ ...filters, maxNodes: Number(e.target.value) })} />
            </Filter>
            <Filter label="Edge cap">
              <input className="control w-20 text-xs" type="number" min="1" max="2000" value={filters.maxEdges} onChange={(e) => setFilters({ ...filters, maxEdges: Number(e.target.value) })} />
            </Filter>
            <button onClick={() => setFilters(DEFAULT_FILTERS)} disabled={!customFilters} className="mb-px rounded-md border border-[#2b4b43] px-3 py-2 text-[10px] font-bold uppercase tracking-wider text-[#8ba69d] disabled:opacity-30">Reset</button>
            <div className="ml-auto text-right">
              <p className="eyebrow">{selectedFamily?.name ?? "No family"}</p>
              <p className="mono mt-1 text-[10px] text-[#78958c]">{selectedWindow ? time(selectedWindow.windowStartIso) : "No window selected"}</p>
            </div>
          </div>

          {notice && (
            <button onClick={() => setNotice(null)} className="rounded-lg border border-[#604a28] bg-[#2a2113] px-3 py-2 text-left text-xs text-[#f5cc8c]">
              {notice} <span className="float-right text-[#a98958]">Dismiss</span>
            </button>
          )}

          <div className="panel min-h-[460px] flex-1 overflow-hidden p-2">
            <GraphCanvas graph={graph} loading={graphLoading} onNode={selectNode} onEdge={selectEdge} />
          </div>

          <div className="panel grid grid-cols-2 gap-px overflow-hidden bg-[#1d3731] sm:grid-cols-4">
            <BottomStat label="Projected nodes" value={graph?.nodes.length} />
            <BottomStat label="Projected edges" value={graph?.edges.length} />
            <BottomStat label="Matching interactions" value={graph?.totalMatchingInteractions} />
            <BottomStat label="Projection state" value={graph?.truncated ? "Truncated" : graph?.retentionLevel ?? "-"} />
          </div>
        </section>

        <aside className="flex min-h-0 flex-col gap-3">
          <div className="panel p-4">
            <div className="mb-4 flex items-center justify-between">
              <p className="eyebrow">Retention pressure</p>
              <span className="mono text-[10px] text-[#78958c]">{bytes(memory?.workingSetBytes)} WS</span>
            </div>
            <MemoryRow label="Detailed" value={memory?.estimatedDetailedBytes} limit={memory?.detailedBytesLimit} count={memory?.retainedDetailedSlices} />
            <MemoryRow label="Projections" value={memory?.estimatedProjectionBytes} limit={memory?.projectionBytesLimit} count={memory?.retainedProjections} />
            <div className="mt-4 grid grid-cols-3 gap-2 border-t border-[#1d3731] pt-3 text-center">
              <MiniStat label="Summaries" value={memory?.totalSummaries} />
              <MiniStat label="Detail evict" value={memory?.evictedDetailedSlices} />
              <MiniStat label="Proj evict" value={memory?.evictedProjections} />
            </div>
          </div>

          <div className="panel min-h-0 flex-1 overflow-y-auto p-4">
            <div className="mb-4 flex items-center justify-between">
              <p className="eyebrow">Inspector</p>
              {selected && <button className="text-[10px] uppercase text-[#78958c]" onClick={() => setSelected(null)}>Clear</button>}
            </div>
            {detailLoading ? <EmptyDetail text="Loading retained detail" /> :
              selected ? <Detail selected={selected} /> :
              selectedWindow ? <WindowDetail window={selectedWindow} /> :
              <EmptyDetail text="Select a window, node, or edge" />}
          </div>
        </aside>
      </section>
    </main>
  );
}

function Stat({ label, value }: { label: string; value?: number | null }) {
  return <div><p className="mono text-[14px] font-semibold text-[#dcebe6]">{number(value)}</p><p className="mt-0.5 text-[9px] uppercase tracking-wider text-[#78958c]">{label}</p></div>;
}

function Filter({ label, children }: { label: string; children: React.ReactNode }) {
  return <label><span className="eyebrow mb-1.5 block">{label}</span>{children}</label>;
}

function BottomStat({ label, value }: { label: string; value?: number | string | null }) {
  return <div className="bg-[#0b1715] px-4 py-3"><p className="eyebrow">{label}</p><p className="mono mt-1 text-sm font-semibold text-[#cfe1db]">{typeof value === "number" ? number(value) : value ?? "-"}</p></div>;
}

function MemoryRow({ label, value = 0, limit = 1, count = 0 }: { label: string; value?: number; limit?: number; count?: number }) {
  const percent = Math.min(100, (value / Math.max(limit, 1)) * 100);
  return <div className="mb-3"><div className="mb-1.5 flex justify-between text-[10px]"><span className="font-semibold text-[#aac2ba]">{label} <span className="text-[#607d74]">({count})</span></span><span className="mono text-[#78958c]">{bytes(value)} / {bytes(limit)}</span></div><div className="metric-bar"><span style={{ width: `${percent}%` }} /></div></div>;
}

function MiniStat({ label, value }: { label: string; value?: number }) {
  return <div><p className="mono text-xs font-semibold">{number(value)}</p><p className="mt-1 text-[8px] uppercase tracking-wider text-[#78958c]">{label}</p></div>;
}

function EmptyDetail({ text }: { text: string }) {
  return <div className="grid min-h-48 place-items-center text-center text-xs text-[#607d74]"><div><div className="mb-2 text-xl text-[#315b50]">+</div>{text}</div></div>;
}

function WindowDetail({ window }: { window: SliceSummary }) {
  return <div className="space-y-4">
    <div><p className="text-sm font-semibold">Window summary</p><p className="mono mt-1 text-[10px] text-[#78958c]">{window.windowStartIso}</p></div>
    <div className="grid grid-cols-2 gap-2"><DetailMetric label="Documents" value={number(window.documentCount)} /><DetailMetric label="Interactions" value={number(window.interactionCount)} /><DetailMetric label="Max weight" value={number(window.maxSemanticWeight)} /><DetailMetric label="Total weight" value={number(window.totalSemanticWeight)} /></div>
    <Distribution title="Node-kind overlap" mean={window.jaccardNodeKind.mean} p25={window.jaccardNodeKind.p25} p75={window.jaccardNodeKind.p75} />
    <Distribution title="Previous-self overlap" mean={window.jaccardPreviousSelf.mean} p25={window.jaccardPreviousSelf.p25} p75={window.jaccardPreviousSelf.p75} />
    <KeyValues title="Top predicates" values={window.predicateCounts} />
    <KeyValues title="Node kinds" values={window.nodeKindCounts} />
  </div>;
}

function Detail({ selected }: { selected: NodeDetail | InteractionDetail }) {
  if ("sourceId" in selected) {
    return <div className="space-y-4">
      <div><p className="text-sm font-semibold text-[#f5b95f]">{selected.predicate}</p><p className="mono mt-2 break-all text-[9px] text-[#78958c]">{selected.sourceId}<br />to {selected.targetId}</p></div>
      <div className="grid grid-cols-2 gap-2"><DetailMetric label="Count" value={number(selected.count)} /><DetailMetric label="Semantic weight" value={number(selected.semanticWeight)} /></div>
      <KeyValues title="Semantic terms" values={selected.termCounts} />
      <div><p className="eyebrow mb-2">Evidence ({selected.evidence.length})</p>{selected.evidence.map((item, index) => <div key={`${item.segmentPath}-${index}`} className="mb-2 rounded border border-[#1d3731] bg-[#081411] p-2"><p className="break-all text-[9px] text-[#9ab2aa]">{item.segmentPath}</p><p className="mono mt-1 text-[8px] text-[#607d74]">offset {item.syncBlockOffset}</p></div>)}</div>
    </div>;
  }
  return <div className="space-y-4">
    <div><p className="text-sm font-semibold text-[#50e3a4]">{selected.kind}</p><p className="mono mt-2 break-all text-[9px] text-[#78958c]">{selected.id}</p></div>
    <div className="grid grid-cols-2 gap-2"><DetailMetric label="Kind overlap" value={selected.jaccardNodeKind?.toFixed(3) ?? "-"} /><DetailMetric label="Previous overlap" value={selected.jaccardPreviousSelf?.toFixed(3) ?? "-"} /></div>
    <KeyValues title="Term counts" values={selected.termCounts} />
    <KeyValues title="Top TF-IDF weights" values={selected.tfidfWeights} />
  </div>;
}

function DetailMetric({ label, value }: { label: string; value: string }) {
  return <div className="rounded border border-[#1d3731] bg-[#081411] p-2"><p className="eyebrow">{label}</p><p className="mono mt-1 text-xs">{value}</p></div>;
}

function Distribution({ title, mean, p25, p75 }: { title: string; mean: number; p25: number; p75: number }) {
  return <div><p className="eyebrow mb-2">{title}</p><div className="metric-bar mb-2"><span style={{ marginLeft: `${p25 * 100}%`, width: `${Math.max((p75 - p25) * 100, 1)}%` }} /></div><div className="flex justify-between text-[9px] text-[#78958c]"><span>P25 {p25.toFixed(2)}</span><span>mean {mean.toFixed(2)}</span><span>P75 {p75.toFixed(2)}</span></div></div>;
}

function KeyValues({ title, values }: { title: string; values: Record<string, number> }) {
  const entries = Object.entries(values).sort((a, b) => b[1] - a[1]).slice(0, 12);
  return <div><p className="eyebrow mb-2">{title}</p><div className="space-y-1">{entries.length ? entries.map(([key, value]) => <div key={key} className="flex items-start justify-between gap-3 rounded border border-[#152c27] bg-[#081411] px-2 py-1.5"><span className="break-all text-[9px] text-[#9ab2aa]">{key}</span><span className="mono text-[9px] text-[#50e3a4]">{number(value)}</span></div>) : <p className="text-[10px] text-[#607d74]">No values retained</p>}</div></div>;
}
