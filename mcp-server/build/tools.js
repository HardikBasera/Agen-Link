import { z } from "zod";
import { agenLinkRequest } from "./agenLinkClient.js";
import { memoryTools } from "./memory.js";
/**
 * The MCP tools exposed to Claude. Each forwards a command to the live Unity Editor over the TCP bridge.
 * These deliberately cover only what the filesystem CANNOT give Claude — live editor state — since Claude
 * already reads/writes the project's files with its built-in tools.
 */
export const tools = [
    {
        name: "agen_get_project_info",
        description: "Get the currently open Unity project's info: Unity version, absolute project path, product/company " +
            "name, active build target/platform, render pipeline, active scene path, loaded scenes, and " +
            "play/compile state. Check this instead of asking the user which scene is open or whether the game is running.",
        schema: {},
        run: (port) => agenLinkRequest(port, "get_project_info"),
    },
    {
        name: "agen_read_console",
        description: "Read recent Unity Editor console messages (errors, warnings, logs). Use this to see runtime problems, " +
            "e.g. after the user runs the game, or to review a project for issues. Filter by type and limit the count. " +
            "ALWAYS check the console here (and agen_get_compile_errors) yourself before asking the user about errors.",
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
        description: "Get the current C# compile errors and warnings from Unity's latest compilation. ALWAYS call this " +
            "after creating or editing scripts (following agen_refresh_assets): if errorCount > 0, read the " +
            "messages and fix the code before finishing.",
        schema: {},
        run: (port) => agenLinkRequest(port, "get_compile_errors"),
    },
    {
        name: "agen_refresh_assets",
        description: "Ask Unity to import changed files and recompile scripts (AssetDatabase.Refresh + RequestScriptCompilation). " +
            "Call this after writing/editing C# files, wait briefly, then poll agen_get_compile_errors until isCompiling is false.",
        schema: {},
        run: (port) => agenLinkRequest(port, "refresh_assets"),
    },
    {
        name: "agen_get_scene_hierarchy",
        description: "Get the active scene's GameObject hierarchy: instanceID, name, hierarchy path, active state, attached " +
            "component type names, and children, down to a depth. The instanceIDs it returns can be passed straight " +
            "to agen_get_gameobject / agen_modify_gameobject / agen_manage_component. ALWAYS call this (or " +
            "agen_find_gameobjects) instead of asking the user what is in the scene.",
        schema: {
            maxDepth: z.number().int().positive().max(8).optional().describe("Max tree depth (default 3)."),
        },
        run: (port, a) => agenLinkRequest(port, "get_scene_hierarchy", { maxDepth: a.maxDepth ?? 3 }),
    },
    {
        name: "agen_get_selection",
        description: "Get the objects currently selected in the Unity Editor (names, types, and asset paths if any). " +
            "Read-only — to change the selection use agen_set_selection.",
        schema: {},
        run: (port) => agenLinkRequest(port, "get_selection"),
    },
    {
        name: "agen_find_assets",
        description: "Search the project's asset database with a Unity filter string (e.g. 't:MonoScript', 't:Prefab', " +
            "'t:Material wood', 'PlayerController t:MonoScript', 'l:Player'). Returns matching asset paths and GUIDs. " +
            "Read-only — to move/copy/delete or create assets use agen_manage_asset.",
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
        description: "(Re)build the cached dependency/knowledge graph of the open Unity project: scripts (inheritance, " +
            "interfaces, serialized-field composition) and prefabs/scenes (attached MonoBehaviour components + asset " +
            "references). Returns immediately; the build runs in the Editor — then poll agen_graph_status until " +
            "building=false and hasCache=true. NOTE: this is a STRUCTURAL graph, not a method-call graph.",
        schema: {},
        run: (port) => agenLinkRequest(port, "graph_build"),
    },
    {
        name: "agen_graph_status",
        description: "Check the Unity project knowledge-graph cache: is it building?, does a cache exist?, and node/edge " +
            "counts. Poll this after agen_graph_build until building=false and hasCache=true before agen_graph_query.",
        schema: {},
        run: (port) => agenLinkRequest(port, "graph_status"),
    },
    {
        name: "agen_graph_query",
        description: "Query the cached Unity project dependency graph — the live WIRING you can't easily grep: which scripts " +
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
        run: (port, a) => agenLinkRequest(port, "graph_query", {
            entity: a.entity ?? "",
            direction: a.direction ?? "both",
            depth: a.depth ?? 1,
            kinds: Array.isArray(a.kinds) ? a.kinds.join(",") : "",
            relations: Array.isArray(a.relations) ? a.relations.join(",") : "",
            limit: a.limit ?? 200,
        }),
    },
    {
        name: "agen_graph_systems",
        description: "List the Neuron graph's auto-detected 'systems' (clusters of inter-linked scripts/assets, grouped per " +
            "scene plus a Shared·Core and a Project bucket). Each entry has: id, current name, owner (scene/shared/" +
            "project), the main (hub) script, member names, and `needsNaming` (true when the cluster has no cached " +
            "human name yet). Use this to find clusters that need naming, then call agen_graph_name_systems. Naming " +
            "is cached by cluster membership, so already-named clusters stay named across rebuilds with no LLM calls.",
        schema: {},
        run: (port) => agenLinkRequest(port, "graph_systems"),
    },
    {
        name: "agen_graph_name_systems",
        description: "Assign human-meaningful names to one or more Neuron systems (clusters). Pass the system `id`s from " +
            "agen_graph_systems and a concise name for each (e.g. 'Teleport Locomotion', 'Gaze Interaction', " +
            "'Save/Load'). Names are persisted in the graph cache keyed by cluster membership, so they survive " +
            "rebuilds and recompiles until that cluster's membership changes. Only name clusters where needsNaming=true " +
            "(or to rename). Derive each name from the cluster's main script + members.",
        schema: {
            assignments: z
                .array(z.object({
                id: z.string().describe("System id from agen_graph_systems."),
                name: z.string().describe("Concise human name for the system."),
            }))
                .describe("System id → name assignments to apply."),
        },
        run: (port, a) => {
            const list = a.assignments ?? [];
            return agenLinkRequest(port, "graph_name_systems", {
                systemIds: list.map((x) => x.id),
                systemNames: list.map((x) => x.name),
            });
        },
    },
    {
        name: "agen_audit_scene",
        description: "Run the scene optimization audit on the ACTIVE Unity scene: per-renderer polycounts, missing LODs, " +
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
        description: "Audit the import settings of every asset the active scene depends on: oversized/uncompressed " +
            "textures, missing Android/ASTC overrides (critical for Quest), NPOT textures, mesh Read/Write, " +
            "audio Decompress-On-Load on long clips. Same finding format as agen_audit_scene; pair them.",
        schema: {
            max: z.number().int().positive().max(1000).optional().describe("Max findings to return (default 200)."),
        },
        run: (port, a) => agenLinkRequest(port, "audit_assets", { max: a.max ?? 200 }),
    },
    {
        name: "agen_perf_start",
        description: "Start a play-mode performance recording (ProfilerRecorder counters: frame time, batches, SetPass " +
            "calls, draw calls, triangles, GC alloc, memory). Enters play mode by default, which triggers a " +
            "domain reload — the bridge briefly disconnects; just poll agen_perf_status until ready=true, then " +
            "call agen_perf_report. If entering play mode stalls, ask the user to click the Unity window once.",
        schema: {
            frames: z.number().int().positive().max(5000).optional().describe("Frames to sample (default 300)."),
            enterPlayMode: z.boolean().optional().describe("Enter play mode if not playing (default true)."),
            exitPlayMode: z.boolean().optional().describe("Exit play mode when recording completes (default true)."),
        },
        run: (port, a) => agenLinkRequest(port, "perf_start", {
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
        description: "Fetch the finished performance recording: min/avg/p95/max per counter (frame ms, batches, SetPass, " +
            "draw calls, triangles, vertices, GC bytes/frame, total memory MB, GPU frame ms when available), plus " +
            "stats.markers (top PlayerLoop stages by avg ms) and stats.scriptMarkers (top user-script costs) that " +
            "show WHERE the frame goes. Editor numbers are indicative — always say so in reports; device profiling " +
            "is ground truth. Compare before/after when verifying fixes.",
        schema: {},
        run: (port) => agenLinkRequest(port, "perf_report"),
    },
    {
        name: "agen_apply_fixes",
        description: "Apply whitelisted optimization fixes from audit findings (use each finding's fixType/fixValue and " +
            "target). Scene fixes are Undo-able and NOT saved — tell the user to review and save; asset import " +
            "fixes reimport immediately (permanent:true in the result). Types: set_static_flags, set_light_mode, " +
            "set_light_shadows, set_shadow_casting, set_camera_far, set_particle_max, set_reflection_probe_mode, " +
            "add_lod_group, set_texture_max_size, set_texture_compression, set_audio_load_type, set_mesh_readwrite, " +
            "set_texture_mip_streaming, set_mesh_compression. For arbitrary component/property changes beyond this " +
            "whitelist, use agen_set_component_properties.",
        schema: {
            fixes: z
                .array(z.object({
                type: z.string().describe("Fix type (a finding's fixType)."),
                target: z.string().describe("Scene hierarchy path or asset path (the finding's target)."),
                value: z.union([z.string(), z.number(), z.boolean()]).optional()
                    .describe("Fix value (the finding's fixValue, or your own)."),
            }))
                .min(1)
                .describe("Fixes to apply, usually taken from audit findings."),
        },
        run: (port, a) => agenLinkRequest(port, "apply_fixes", { fixes: a.fixes }, 60000),
    },
    // ===================== Editor control (v0.2) =====================
    // These MUTATE the live Editor directly. The CLI must use them instead of writing and compiling a
    // throwaway editor script, and must query the scene/console with the read tools instead of asking the user.
    {
        name: "agen_create_gameobject",
        description: "Create a GameObject in the open scene — use THIS, never a C# editor script, to add objects. Make it " +
            "empty, a primitive (cube/sphere/capsule/cylinder/plane/quad), an instance of a prefab (prefab = asset " +
            "path or GUID; keeps the prefab link), or a copy of an existing object (copyFrom; the copy is a plain " +
            "object, not prefab-linked). Optional parent, position/rotation(euler degrees)/scale, name. Returns the " +
            "new object's instanceID for follow-up calls. Undo-able; the scene is marked dirty, not saved.",
        schema: {
            name: z.string().optional().describe("Name for the new object."),
            primitive: z.enum(["cube", "sphere", "capsule", "cylinder", "plane", "quad"]).optional()
                .describe("Create a built-in primitive."),
            prefab: z.string().optional().describe("Instantiate this prefab ('Assets/..' path or GUID)."),
            copyFrom: z.string().optional().describe("Duplicate this existing object (instanceID, path, or name)."),
            parent: z.string().optional().describe("Parent object ref (instanceID, path, or name)."),
            position: z.array(z.number()).length(3).optional().describe("Position [x,y,z]."),
            rotation: z.array(z.number()).length(3).optional().describe("Euler rotation [x,y,z] in degrees."),
            scale: z.array(z.number()).length(3).optional().describe("Local scale [x,y,z]."),
            worldSpace: z.boolean().optional().describe("Treat position/rotation as world-space (default false = local)."),
        },
        run: (port, a) => agenLinkRequest(port, "create_gameobject", {
            name: a.name ?? "",
            primitive: a.primitive ?? "",
            prefab: a.prefab ?? "",
            copyFrom: a.copyFrom ?? "",
            parent: a.parent ?? "",
            position: a.position,
            rotation: a.rotation,
            scale: a.scale,
            worldSpace: a.worldSpace ?? false,
        }),
    },
    {
        name: "agen_modify_gameobject",
        description: "Modify an existing scene GameObject: rename, reparent (parent:'' moves to scene root), set active " +
            "state, tag, layer, static flag, and/or transform (local space by default; worldSpace:true for world). " +
            "Pass only the fields you want to change. Target it by instanceID (from any read tool), hierarchy path, " +
            "or name. NOT for component fields — use agen_set_component_properties. Undo-able; scene dirty, not saved.",
        schema: {
            target: z.string().describe("Object to modify: instanceID, 'Parent/Child' path, or name."),
            name: z.string().optional().describe("New name."),
            parent: z.string().optional().describe("New parent ref, or '' for scene root."),
            active: z.boolean().optional().describe("SetActive state."),
            tag: z.string().optional().describe("Tag (must already be defined in the project)."),
            layer: z.union([z.number().int(), z.string()]).optional().describe("Layer index or name."),
            isStatic: z.boolean().optional().describe("Set all static editor flags on/off."),
            position: z.array(z.number()).length(3).optional().describe("Position [x,y,z]."),
            rotation: z.array(z.number()).length(3).optional().describe("Euler rotation [x,y,z] in degrees."),
            scale: z.array(z.number()).length(3).optional().describe("Local scale [x,y,z]."),
            worldSpace: z.boolean().optional().describe("Position/rotation in world space (default local)."),
        },
        run: (port, a) => agenLinkRequest(port, "modify_gameobject", a),
    },
    {
        name: "agen_delete_gameobjects",
        description: "Delete one or more scene GameObjects. Each target is an instanceID, hierarchy path, or name. " +
            "Undo-able; the scene is marked dirty, not saved.",
        schema: {
            targets: z.array(z.string()).min(1).describe("Objects to delete (instanceID / path / name each)."),
        },
        run: (port, a) => agenLinkRequest(port, "delete_gameobjects", { targets: a.targets }),
    },
    {
        name: "agen_find_gameobjects",
        description: "Find GameObjects in the loaded scenes (including inactive) by name substring, exact hierarchy path, " +
            "component type, and/or tag (filters combine with AND). Returns instanceID + path + components for each " +
            "match. ALWAYS use this (or agen_get_scene_hierarchy) instead of asking the user what exists in the scene.",
        schema: {
            name: z.string().optional().describe("Case-insensitive name substring."),
            path: z.string().optional().describe("Exact 'Parent/Child' hierarchy path."),
            component: z.string().optional().describe("Only objects with this component type (e.g. 'Rigidbody')."),
            tag: z.string().optional().describe("Only objects with this tag."),
            max: z.number().int().positive().max(500).optional().describe("Max matches (default 100)."),
        },
        run: (port, a) => agenLinkRequest(port, "find_gameobjects", {
            gname: a.name ?? "",
            gpath: a.path ?? "",
            component: a.component ?? "",
            tag: a.tag ?? "",
            max: a.max ?? 100,
        }),
    },
    {
        name: "agen_get_gameobject",
        description: "Get one GameObject's full data: every component with its serialized property names and current values — " +
            "the SAME names agen_set_component_properties accepts. Call this BEFORE setting properties to discover " +
            "valid fields and copy exact paths. Depth and array length are capped to keep the output small.",
        schema: {
            target: z.string().describe("Object: instanceID, 'Parent/Child' path, or name."),
            includeProperties: z.boolean().optional().describe("Include each component's serialized property values (default true)."),
            maxDepth: z.number().int().positive().max(6).optional().describe("How deep to serialize nested structs (default 2)."),
        },
        run: (port, a) => agenLinkRequest(port, "get_gameobject", {
            target: a.target,
            includeProperties: a.includeProperties ?? true,
            maxDepth: a.maxDepth ?? 2,
        }),
    },
    {
        name: "agen_manage_component",
        description: "Add or remove a component on a GameObject. componentType accepts a full name ('UnityEngine.Rigidbody') " +
            "or a class name ('Rigidbody'); project MonoBehaviours work too. For remove, index picks the nth component " +
            "of that type (default 0). After adding, set its fields with agen_set_component_properties. Undo-able; " +
            "scene marked dirty, not saved.",
        schema: {
            action: z.enum(["add", "remove"]).describe("Add or remove a component."),
            target: z.string().describe("Object: instanceID, path, or name."),
            componentType: z.string().describe("Component type, full or short name."),
            index: z.number().int().nonnegative().optional().describe("For remove: nth of that type (default 0)."),
        },
        run: (port, a) => agenLinkRequest(port, "manage_component", {
            action: a.action,
            target: a.target,
            componentType: a.componentType,
            index: a.index ?? 0,
        }),
    },
    {
        name: "agen_set_component_properties",
        description: "Set serialized fields on a component, exactly like editing the Inspector (Undo-able; scene marked dirty, " +
            "NOT saved). properties is {name: value}: numbers/bools/strings, enum names, Vector2/3/4 as [x,y,z] or " +
            "{x,y,z}, Color as {r,g,b,a}, Quaternion as {x,y,z,w} or a 3-number euler array, object references as an " +
            "instanceID / 'Assets/..' path / {guid} / scene path, arrays as JSON arrays, nested structs as objects. " +
            "Get valid names + current values from agen_get_gameobject first. Failures are reported per-property; the rest apply.",
        schema: {
            target: z.string().describe("Object: instanceID, path, or name."),
            componentType: z.string().describe("Component type, full or short name."),
            componentIndex: z.number().int().nonnegative().optional().describe("Which component of that type (default 0)."),
            properties: z.record(z.string(), z.unknown()).describe("Map of serialized property name -> value."),
        },
        run: (port, a) => agenLinkRequest(port, "set_component_properties", a),
    },
    {
        name: "agen_manage_scene",
        description: "Save, open, or create Unity scenes (action: save|open|create). save writes the active scene to disk — " +
            "only call it once the user has agreed. open/create refuse to discard unsaved changes unless force:true, " +
            "and refuse during play mode. Loaded scenes are also listed by agen_get_project_info.",
        schema: {
            action: z.enum(["save", "open", "create"]).describe("Scene operation."),
            path: z.string().optional().describe("Scene asset path 'Assets/Scenes/X.unity' (save target / open source / create save)."),
            additive: z.boolean().optional().describe("open/create alongside the current scene(s) (default false)."),
            force: z.boolean().optional().describe("Discard unsaved changes for open/create (default false)."),
        },
        run: (port, a) => agenLinkRequest(port, "manage_scene", {
            action: a.action,
            path: a.path ?? "",
            additive: a.additive ?? false,
            force: a.force ?? false,
        }),
    },
    {
        name: "agen_manage_asset",
        description: "Asset operations that keep GUIDs and .meta files consistent — NEVER move/copy/delete Unity assets with " +
            "plain file commands, use this. Actions: move/copy/delete an asset; create_prefab from a scene GameObject " +
            "(target) at path, connecting the scene object to the new prefab; create_material with an optional shader " +
            "and a {'_Property': value} map. Changes are immediate and permanent (delete goes to the OS trash).",
        schema: {
            action: z.enum(["move", "copy", "delete", "create_prefab", "create_material"]).describe("Asset operation."),
            path: z.string().describe("Asset acted on, or created (e.g. 'Assets/Materials/Red.mat')."),
            destination: z.string().optional().describe("For move/copy: the new asset path."),
            target: z.string().optional().describe("For create_prefab: the scene GameObject (instanceID/path/name)."),
            shader: z.string().optional().describe("For create_material: shader name (default = render pipeline default)."),
            properties: z.record(z.string(), z.unknown()).optional().describe("For create_material: shader property '_Name' -> value."),
        },
        run: (port, a) => agenLinkRequest(port, "manage_asset", a),
    },
    {
        name: "agen_execute_menu_item",
        description: "Execute a Unity Editor menu item by its exact path, e.g. 'GameObject/UI/Button' or 'Assets/Refresh' — the " +
            "escape hatch for operations without a dedicated agen_ tool. AVOID items that open a modal dialog or wizard: " +
            "they freeze the editor (and every tool) until a human closes them.",
        schema: { menuPath: z.string().describe("Exact menu path, e.g. 'GameObject/Align With View'.") },
        run: (port, a) => agenLinkRequest(port, "execute_menu_item", { menuPath: a.menuPath }),
    },
    {
        name: "agen_playmode",
        description: "Control or query play mode: action play|stop|pause|unpause|step|status. play/stop trigger a domain " +
            "reload — the bridge drops for a few seconds, so poll {action:'status'} until it answers again before " +
            "doing anything else. Check status instead of asking the user whether the game is running.",
        schema: { action: z.enum(["play", "stop", "pause", "unpause", "step", "status"]).describe("Play-mode command.") },
        run: (port, a) => agenLinkRequest(port, "playmode", { action: a.action }),
    },
    {
        name: "agen_set_selection",
        description: "Select objects in the Editor and ping-highlight the first so the user can SEE what you mean — do this " +
            "before asking them to review something. Scene objects by instanceID/path/name; assets by 'Assets/..' path.",
        schema: {
            targets: z.array(z.string()).min(1).describe("Objects/assets to select."),
            ping: z.boolean().optional().describe("Ping (flash) the first target in its window (default true)."),
        },
        run: (port, a) => agenLinkRequest(port, "set_selection", { targets: a.targets, ping: a.ping ?? true }),
    },
    {
        name: "agen_capture_screenshot",
        description: "Capture the Game view (main camera; live gameplay if playing) or the Scene view to a PNG under " +
            "AgenLink~/screenshots/ and return its absolute path — then READ that image file to look at it yourself " +
            "instead of asking the user what things look like. Note: screen-space-overlay UI only appears in play-mode game captures.",
        schema: {
            view: z.enum(["game", "scene"]).optional().describe("Which view to capture (default 'game')."),
            width: z.number().int().positive().max(4096).optional().describe("Image width (default 1280; ignored for play-mode game capture)."),
            height: z.number().int().positive().max(4096).optional().describe("Image height (default 720; ignored for play-mode game capture)."),
        },
        run: (port, a) => agenLinkRequest(port, "capture_screenshot", { view: a.view ?? "game", width: a.width ?? 1280, height: a.height ?? 720 }),
    },
];
// Shared project-memory tools (filesystem-backed; bridge-independent). Both CLIs get them.
tools.push(...memoryTools);
