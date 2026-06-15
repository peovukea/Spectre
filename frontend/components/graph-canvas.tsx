"use client";

import type { GraphProjection, ProjectedEdge, ProjectedNode } from "@/lib/contracts";
import Graph from "graphology";
import type Sigma from "sigma";
import { useEffect, useRef, useState } from "react";

interface Props {
  graph: GraphProjection | null;
  loading: boolean;
  onNode: (node: ProjectedNode) => void;
  onEdge: (edge: ProjectedEdge) => void;
}

function colorForKind(kind: string) {
  let hash = 0;
  for (let index = 0; index < kind.length; index++) hash = (hash * 31 + kind.charCodeAt(index)) | 0;
  const palette = ["#50e3a4", "#66b8ff", "#f5b95f", "#d28cff", "#ff8f83", "#90e36e", "#64d8d2"];
  return palette[Math.abs(hash) % palette.length];
}

export function GraphCanvas({ graph, loading, onNode, onEdge }: Props) {
  const containerRef = useRef<HTMLDivElement>(null);
  const rendererRef = useRef<Sigma | null>(null);
  const onNodeRef = useRef(onNode);
  const onEdgeRef = useRef(onEdge);
  const selectedNodeRef = useRef<string | null>(null);
  const hoveredNodeRef = useRef<string | null>(null);
  const selectedEdgeRef = useRef<string | null>(null);
  const [selectedNode, setSelectedNode] = useState<string | null>(null);
  const [hoveredNode, setHoveredNode] = useState<string | null>(null);
  const [selectedEdge, setSelectedEdge] = useState<string | null>(null);
  const [zoom, setZoom] = useState(100);
  const [renderError, setRenderError] = useState<string | null>(null);

  useEffect(() => {
    onNodeRef.current = onNode;
    onEdgeRef.current = onEdge;
  }, [onEdge, onNode]);

  useEffect(() => {
    selectedNodeRef.current = selectedNode;
    hoveredNodeRef.current = hoveredNode;
    selectedEdgeRef.current = selectedEdge;
    rendererRef.current?.refresh();
  }, [hoveredNode, selectedEdge, selectedNode]);

  useEffect(() => {
    const container = containerRef.current;
    rendererRef.current?.kill();
    rendererRef.current = null;

    if (!container || !graph) return;

    let active = true;
    void import("sigma").then(({ default: SigmaRenderer }) => {
      if (!active) return;
      try {
      const sigmaGraph = new Graph({ multi: true, type: "directed" });
      const degree = new Map(graph.nodes.map((node) => [node.id, 0]));
      graph.edges.forEach((edge) => {
        degree.set(edge.source, (degree.get(edge.source) ?? 0) + 1);
        degree.set(edge.target, (degree.get(edge.target) ?? 0) + 1);
      });

      const kinds = [...new Set(graph.nodes.map((node) => node.kind))].sort();
      graph.nodes.forEach((node) => {
        const kindIndex = kinds.indexOf(node.kind);
        const kindNodes = graph.nodes.filter((candidate) => candidate.kind === node.kind);
        const kindPosition = kindNodes.findIndex((candidate) => candidate.id === node.id);
        const angle = (kindPosition / Math.max(kindNodes.length, 1)) * Math.PI * 2 + kindIndex * 0.7;
        const radius = 0.35 + kindIndex * 0.17;
        sigmaGraph.addNode(node.id, {
          ...node,
          x: Math.cos(angle) * radius,
          y: Math.sin(angle) * radius,
          size: Math.min(12, 3.5 + Math.sqrt(degree.get(node.id) ?? 0)),
          color: colorForKind(node.kind),
          label: `${node.label} · ${node.kind}`,
        });
      });

      const weights = graph.edges.map((edge) => edge.semanticWeight);
      const minWeight = Math.min(...weights, 1);
      const maxWeight = Math.max(...weights, 1);
      graph.edges.forEach((edge, index) => {
        sigmaGraph.addDirectedEdgeWithKey(`edge-${index}`, edge.source, edge.target, {
          ...edge,
          size: 0.4 + (Math.log1p(edge.semanticWeight - minWeight) / Math.max(Math.log1p(maxWeight - minWeight), 1)) * 2.2,
          color: "#326c5f",
        });
      });

      const renderer = new SigmaRenderer(sigmaGraph, container, {
        allowInvalidContainer: true,
        defaultEdgeColor: "#326c5f",
        defaultNodeColor: "#50e3a4",
        enableEdgeEvents: true,
        hideEdgesOnMove: true,
        labelColor: { color: "#dcebe6" },
        labelDensity: 0.05,
        labelGridCellSize: 120,
        labelRenderedSizeThreshold: 11,
        renderEdgeLabels: false,
        stagePadding: 42,
        nodeReducer: (node, data) => {
          const focused = selectedNodeRef.current ?? hoveredNodeRef.current;
          if (!focused) return data;
          const connected = node === focused || sigmaGraph.hasEdge(node, focused) || sigmaGraph.hasEdge(focused, node);
          return {
            ...data,
            color: node === focused ? "#f5b95f" : connected ? data.color : "#263d37",
            label: connected ? data.label : "",
            highlighted: node === focused,
            zIndex: connected ? 2 : 0,
          };
        },
        edgeReducer: (edge, data) => {
          const focused = selectedNodeRef.current ?? hoveredNodeRef.current;
          const selected = edge === selectedEdgeRef.current;
          if (!focused) return selected ? { ...data, color: "#f5b95f", size: data.size + 2, zIndex: 3 } : data;
          const [source, target] = sigmaGraph.extremities(edge);
          const connected = source === focused || target === focused;
          return {
            ...data,
            color: selected ? "#f5b95f" : connected ? "#66d9b5" : "#172c27",
            size: selected ? data.size + 2 : data.size,
            zIndex: selected || connected ? 2 : 0,
          };
        },
      });

      renderer.on("clickNode", ({ node }) => {
        setSelectedNode(node);
        setSelectedEdge(null);
        onNodeRef.current(sigmaGraph.getNodeAttributes(node) as unknown as ProjectedNode);
      });
      renderer.on("clickEdge", ({ edge }) => {
        setSelectedEdge(edge);
        setSelectedNode(null);
        onEdgeRef.current(sigmaGraph.getEdgeAttributes(edge) as unknown as ProjectedEdge);
      });
      renderer.on("clickStage", () => {
        setSelectedNode(null);
        setSelectedEdge(null);
      });
      renderer.on("enterNode", ({ node }) => setHoveredNode(node));
      renderer.on("leaveNode", () => setHoveredNode(null));
      renderer.getCamera().on("updated", (state) => setZoom(Math.round((1 / state.ratio) * 100)));
      rendererRef.current = renderer;
      renderer.getCamera().animatedReset({ duration: 350 });
      } catch (error) {
        setRenderError(error instanceof Error ? error.message : "Unknown Sigma rendering error");
      }
    }).catch((error: unknown) => {
      if (active) setRenderError(error instanceof Error ? error.message : "Unable to load Sigma renderer");
    });

    return () => {
      active = false;
      rendererRef.current?.kill();
      rendererRef.current = null;
    };
  }, [graph]);

  function zoomBy(factor: number) {
    const camera = rendererRef.current?.getCamera();
    if (!camera) return;
    camera.animate({ ratio: camera.getState().ratio * factor }, { duration: 180 });
  }

  function resetCamera() {
    rendererRef.current?.getCamera().animatedReset({ duration: 250 });
  }

  return (
    <div className="relative h-full min-h-[420px] overflow-hidden rounded-lg bg-[#06100e]">
      <div
        className="pointer-events-none absolute inset-0 opacity-20"
        style={{
          backgroundImage:
            "linear-gradient(#204038 1px, transparent 1px), linear-gradient(90deg, #204038 1px, transparent 1px)",
          backgroundSize: "28px 28px",
        }}
      />
      <div ref={containerRef} className="absolute inset-0" />

      {graph && (
        <>
          <div className="pointer-events-none absolute left-3 top-3 rounded border border-[#285448] bg-[#07130fdd] px-2.5 py-1.5 text-[9px] font-bold uppercase tracking-wider text-[#8eb5a9]">
            {graph.nodes.length} nodes · {graph.edges.length} edges
          </div>
          <div className="absolute bottom-3 right-3 flex items-center overflow-hidden rounded-lg border border-[#285448] bg-[#07130fee] shadow-lg">
            <button type="button" aria-label="Zoom out" onClick={() => zoomBy(1.35)} className="grid h-9 w-9 place-items-center border-r border-[#285448] text-lg font-semibold text-[#9fc8ba] hover:bg-[#123329]">−</button>
            <button type="button" aria-label="Reset graph viewport" onClick={resetCamera} className="mono h-9 min-w-14 border-r border-[#285448] px-2 text-[10px] font-semibold text-[#9fc8ba] hover:bg-[#123329]">{zoom}%</button>
            <button type="button" aria-label="Zoom in" onClick={() => zoomBy(0.74)} className="grid h-9 w-9 place-items-center text-lg font-semibold text-[#9fc8ba] hover:bg-[#123329]">+</button>
          </div>
        </>
      )}

      {!graph && !loading && (
        <div className="absolute inset-0 grid place-items-center text-center text-sm text-[#78958c]">
          <div><div className="mb-2 text-3xl text-[#315b50]">◇</div>Select a retained window to inspect its graph</div>
        </div>
      )}
      {loading && (
        <div className="absolute inset-0 grid place-items-center bg-[#06100ecc] text-xs font-semibold uppercase tracking-[0.2em] text-[#50e3a4]">Building projection</div>
      )}
      {renderError && (
        <div className="absolute inset-0 grid place-items-center bg-[#160b0bdd] p-8 text-center text-xs text-[#ff9b94]">
          <div><p className="mb-2 font-bold uppercase tracking-wider">WebGL renderer failed</p><p className="mono break-all">{renderError}</p></div>
        </div>
      )}
    </div>
  );
}
