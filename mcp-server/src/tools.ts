import { z } from "zod";
import { agenLinkRequest } from "./agenLinkClient.js";
import { memoryTools } from "./memory.js";

export interface ToolDef {
  name: string;
  description: string;
  schema: z.ZodRawShape;
  run: (port: number, args: Record<string, unknown>) => Promise<unknown>;
}

/**
 * The MCP tools exposed to Claude. Each forwards a command to the live Unity Editor over the TCP bridge.
 * These deliberately cover only what the filesystem CANNOT give Claude — live editor state — since Claude
 * already reads/writes the project's files with its built-in tools.
 */
export const tools: ToolDef[] = [
  {
    name: "agen_get_project_info",
    description:
      "Get the currently open Unity project's info: Unity version, absolute project path, product/company " +
      "name, active build target/platform, render pipeline, active scene path, and play/compile state.",
    schema: {},
    run: (port) => agenLinkRequest(port, "get_project_info"),
  },
  {
    name: "agen_read_console",
    description:
      "Read recent Unity Editor console messages (errors, warnings, logs). Use this to see runtime problems, " +
      "e.g. after the user runs the game, or to review a project for issues. Filter by type and limit the count.",
    schema: {
      type: z
        .enum(["all", "error", "warning", "log"])
        .optional()
        .describe("Filter by message type. Default 'all'."),
      max: z.number().int().positive().max(500).optional().describe("Max messages to return (default 50)."),
    },
    run: (port, a) => agenLinkRequest(port, "read_console", { type: a.type ?? "all", max: a.max ?? 50 }),
  },
  {
    name: "agen_get_compile_errors",
    description:
      "Get the current C# compile errors and warnings from Unity's latest compilation. ALWAYS call this " +
      "after creating or editing scripts (following agen_refresh_assets): if errorCount > 0, read the " +
      "messages and fix the code before finishing.",
    schema: {},
    run: (port) => agenLinkRequest(port, "get_compile_errors"),
  },
  {
    name: "agen_refresh_assets",
    description:
      "Ask Unity to import changed files and recompile scripts (AssetDatabase.Refresh + RequestScriptCompilation). " +
      "Call this after writing/editing C# files, wait briefly, then poll agen_get_compile_errors until isCompiling is false.",
    schema: {},
    run: (port) => agenLinkRequest(port, "refresh_assets"),
  },
  {
    name: "agen_get_scene_hierarchy",
    description:
      "Get the active scene's GameObject hierarchy: names, active state, attached component type names, and " +
      "children, down to a depth. Useful for understanding the scene before writing code that references objects.",
    schema: {
      maxDepth: z.number().int().positive().max(8).optional().describe("Max tree depth (default 3)."),
    },
    run: (port, a) => agenLinkRequest(port, "get_scene_hierarchy", { maxDepth: a.maxDepth ?? 3 }),
  },
  {
    name: "agen_get_selection",
    description: "Get the objects currently selected in the Unity Editor (names, types, and asset paths if any).",
    schema: {},
    run: (port) => agenLinkRequest(port, "get_selection"),
  },
  {
    name: "agen_find_assets",
    description:
      "Search the project's asset database with a Unity filter string (e.g. 't:MonoScript', 't:Prefab', " +
      "'t:Material wood', 'PlayerController t:MonoScript', 'l:Player'). Returns matching asset paths and GUIDs.",
    schema: {
      query: z
        .string()
        .describe("Unity AssetDatabase.FindAssets filter, e.g. 't:Prefab', 't:Scene', 'Player t:MonoScript'."),
      max: z.number().int().positive().max(500).optional().describe("Max results (default 100)."),
    },
    run: (port, a) => agenLinkRequest(port, "find_assets", { query: a.query, max: a.max ?? 100 }),
  },
  {
    name: "agen_graph_build",
    description:
      "(Re)build the cached dependency/knowledge graph of the open Unity project: scripts (inheritance, " +
      "interfaces, serialized-field composition) and prefabs/scenes (attached MonoBehaviour components + asset " +
      "references). Returns immediately; the build runs in the Editor — then poll agen_graph_status until " +
      "building=false and hasCache=true. NOTE: this is a STRUCTURAL graph, not a method-call graph.",
    schema: {},
    run: (port) => agenLinkRequest(port, "graph_build"),
  },
  {
    name: "agen_graph_status",
    description:
      "Check the Unity project knowledge-graph cache: is it building?, does a cache exist?, and node/edge " +
      "counts. Poll this after agen_graph_build until building=false and hasCache=true before agen_graph_query.",
    schema: {},
    run: (port) => agenLinkRequest(port, "graph_status"),
  },
  {
    name: "agen_graph_query",
    description:
      "Query the cached Unity project dependency graph — the live WIRING you can't easily grep: which scripts " +
      "are components on which prefabs/scenes, prefab/scene -> asset references, and script inheritance / " +
      "interface / serialized-field links. Two modes: (1) FOCUSED — pass `entity` (script full name, asset " +
      "path, GUID, or display name) to get that node + its neighbors within `depth` hops; (2) WHOLE-PROJECT " +
      "FILTERED — omit `entity` and pass `kinds`. Returns node IDs + typed edges only (no file contents), " +
      "capped by `limit`. If the cache is empty, call agen_graph_build first and poll agen_graph_status.",
    schema: {
      entity: z
        .string()
        .optional()
        .describe("Focus node: script full name, asset path, GUID, or display name."),
      direction: z
        .enum(["out", "in", "both"])
        .optional()
        .describe("Edge direction from the focus (default 'both'). 'in' = what depends ON it; 'out' = what it uses."),
      depth: z.number().int().positive().max(4).optional().describe("Neighborhood hops from the focus (default 1)."),
      kinds: z
        .array(z.enum(["Script", "Prefab", "Scene", "Asset", "GameObject"]))
        .optional()
        .describe("Restrict to these node kinds."),
      relations: z
        .array(z.enum(["Inherits", "Implements", "HasField", "Component", "AssetRef", "Contains", "PrefabSource", "References"]))
        .optional()
        .describe("Restrict to these edge relations. 'References' = script-to-script code usage."),
      limit: z.number().int().positive().max(500).optional().describe("Max nodes returned (default 200)."),
    },
    run: (port, a) =>
      agenLinkRequest(port, "graph_query", {
        entity: a.entity ?? "",
        direction: a.direction ?? "both",
        depth: a.depth ?? 1,
        kinds: Array.isArray(a.kinds) ? (a.kinds as string[]).join(",") : "",
        relations: Array.isArray(a.relations) ? (a.relations as string[]).join(",") : "",
        limit: a.limit ?? 200,
      }),
  },
  {
    name: "agen_graph_systems",
    description:
      "List the Neuron graph's auto-detected 'systems' (clusters of inter-linked scripts/assets, grouped per " +
      "scene plus a Shared·Core and a Project bucket). Each entry has: id, current name, owner (scene/shared/" +
      "project), the main (hub) script, member names, and `needsNaming` (true when the cluster has no cached " +
      "human name yet). Use this to find clusters that need naming, then call agen_graph_name_systems. Naming " +
      "is cached by cluster membership, so already-named clusters stay named across rebuilds with no LLM calls.",
    schema: {},
    run: (port) => agenLinkRequest(port, "graph_systems"),
  },
  {
    name: "agen_graph_name_systems",
    description:
      "Assign human-meaningful names to one or more Neuron systems (clusters). Pass the system `id`s from " +
      "agen_graph_systems and a concise name for each (e.g. 'Teleport Locomotion', 'Gaze Interaction', " +
      "'Save/Load'). Names are persisted in the graph cache keyed by cluster membership, so they survive " +
      "rebuilds and recompiles until that cluster's membership changes. Only name clusters where needsNaming=true " +
      "(or to rename). Derive each name from the cluster's main script + members.",
    schema: {
      assignments: z
        .array(
          z.object({
            id: z.string().describe("System id from agen_graph_systems."),
            name: z.string().describe("Concise human name for the system."),
          }),
        )
        .describe("System id → name assignments to apply."),
    },
    run: (port, a) => {
      const list = (a.assignments as Array<{ id: string; name: string }>) ?? [];
      return agenLinkRequest(port, "graph_name_systems", {
        systemIds: list.map((x) => x.id),
        systemNames: list.map((x) => x.name),
      });
    },
  },
  {
    name: "agen_audit_scene",
    description:
      "Run the scene optimization audit on the ACTIVE Unity scene: per-renderer polycounts, missing LODs, " +
      "realtime lights/shadows, lightmap & occlusion status, transparent overdraw, heavy MeshColliders, " +
      "particles, camera planes, URP/quality settings, missing scripts. Returns scene stats + structured " +
      "findings {severity, target, evidence, recommendation, fixType?}. Auto-fixable findings can be " +
      "applied with agen_apply_fixes. For the full optimization loop: audit -> agen_perf_start/report -> " +
      "report to the user -> fix -> re-run both -> compare before/after numbers.",
    schema: {
      max: z.number().int().positive().max(1000).optional().describe("Max findings to return (default 200)."),
    },
    run: (port, a) => agenLinkRequest(port, "audit_scene", { max: a.max ?? 200 }),
  },
  {
    name: "agen_audit_assets",
    description:
      "Audit the import settings of every asset the active scene depends on: oversized/uncompressed " +
      "textures, missing Android/ASTC overrides (critical for Quest), NPOT textures, mesh Read/Write, " +
      "audio Decompress-On-Load on long clips. Same finding format as agen_audit_scene; pair them.",
    schema: {
      max: z.number().int().positive().max(1000).optional().describe("Max findings to return (default 200)."),
    },
    run: (port, a) => agenLinkRequest(port, "audit_assets", { max: a.max ?? 200 }),
  },
  {
    name: "agen_perf_start",
    description:
      "Start a play-mode performance recording (ProfilerRecorder counters: frame time, batches, SetPass " +
      "calls, draw calls, triangles, GC alloc, memory). Enters play mode by default, which triggers a " +
      "domain reload — the bridge briefly disconnects; just poll agen_perf_status until ready=true, then " +
      "call agen_perf_report. If entering play mode stalls, ask the user to click the Unity window once.",
    schema: {
      frames: z.number().int().positive().max(5000).optional().describe("Frames to sample (default 300)."),
      enterPlayMode: z.boolean().optional().describe("Enter play mode if not playing (default true)."),
      exitPlayMode: z.boolean().optional().describe("Exit play mode when recording completes (default true)."),
    },
    run: (port, a) =>
      agenLinkRequest(port, "perf_start", {
        frames: a.frames ?? 300,
        enterPlayMode: a.enterPlayMode ?? true,
        exitPlayMode: a.exitPlayMode ?? true,
      }),
  },
  {
    name: "agen_perf_status",
    description: "Poll the play-mode performance recording: {armed, playing, framesDone, framesTarget, ready}.",
    schema: {},
    run: (port) => agenLinkRequest(port, "perf_status"),
  },
  {
    name: "agen_perf_report",
    description:
      "Fetch the finished performance recording: min/avg/p95/max per counter (frame ms, batches, SetPass, " +
      "draw calls, triangles, vertices, GC bytes/frame, total memory MB, GPU frame ms when available), plus " +
      "stats.markers (top PlayerLoop stages by avg ms) and stats.scriptMarkers (top user-script costs) that " +
      "show WHERE the frame goes. Editor numbers are indicative — always say so in reports; device profiling " +
      "is ground truth. Compare before/after when verifying fixes.",
    schema: {},
    run: (port) => agenLinkRequest(port, "perf_report"),
  },
  {
    name: "agen_apply_fixes",
    description:
      "Apply whitelisted optimization fixes from audit findings (use each finding's fixType/fixValue and " +
      "target). Scene fixes are Undo-able and NOT saved — tell the user to review and save; asset import " +
      "fixes reimport immediately (permanent:true in the result). Types: set_static_flags, set_light_mode, " +
      "set_light_shadows, set_shadow_casting, set_camera_far, set_particle_max, set_reflection_probe_mode, " +
      "add_lod_group, set_texture_max_size, set_texture_compression, set_audio_load_type, set_mesh_readwrite, " +
      "set_texture_mip_streaming, set_mesh_compression.",
    schema: {
      fixes: z
        .array(
          z.object({
            type: z.string().describe("Fix type (a finding's fixType)."),
            target: z.string().describe("Scene hierarchy path or asset path (the finding's target)."),
            value: z.union([z.string(), z.number(), z.boolean()]).optional()
              .describe("Fix value (the finding's fixValue, or your own)."),
          }),
        )
        .min(1)
        .describe("Fixes to apply, usually taken from audit findings."),
    },
    run: (port, a) => agenLinkRequest(port, "apply_fixes", { fixes: a.fixes }, 60000),
  },
];

// Shared project-memory tools (filesystem-backed; bridge-independent). Both CLIs get them.
tools.push(...memoryTools);
