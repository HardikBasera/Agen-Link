using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using AgenLink;
using AgenLink.Ops;

public class OpsTests
{
    private readonly List<GameObject> _cleanup = new List<GameObject>();

    /// <summary>
    /// Every root object these tests create. Fixtures are built with `new GameObject()` in the CURRENTLY
    /// OPEN scene, and <see cref="TearDown"/> only runs if the test finishes — a domain reload mid-run (or
    /// an aborted run) discards <see cref="_cleanup"/> and strands them in the user's real scene, dirtying
    /// it. Two stray "OpsComp" objects were found that way in a live project. SetUp therefore sweeps
    /// leftovers before each test instead of trusting the previous run's exit. Exact names, not an "Ops"
    /// prefix match, so a user object that merely starts with "Ops" is never touched.
    /// Children (e.g. "OpsCopyChild") need no entry — destroying their tracked root takes them along.
    /// </summary>
    private static readonly HashSet<string> FixtureRootNames = new HashSet<string>
    {
        "OpsRoot_Unique", "OpsChild_Unique", "OpsDup_Ambiguous", "OpsMass", "OpsVecA", "OpsVecB",
        "OpsPartial", "OpsRead", "OpsCube", "OpsCubeRenamed", "OpsFindMe", "OpsComp",
        "OpsCopyParent", "OpsEmptyRoot", "OpsEcho", "OpsBatchA", "OpsBatchB",
    };

    [SetUp]
    public void SweepStrayFixtures()
    {
        for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
        {
            UnityEngine.SceneManagement.Scene scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
            if (!scene.isLoaded) continue;
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                if (root == null) continue;
                if (IsStrayFixture(root.name)) UnityEngine.Object.DestroyImmediate(root);
            }
        }
    }

    /// <summary>An exact fixture name, or one Unity uniquified into "Name (1)" by a collision.</summary>
    private static bool IsStrayFixture(string name)
    {
        if (FixtureRootNames.Contains(name)) return true;
        int paren = name.IndexOf(" (", StringComparison.Ordinal);
        return paren > 0 && name.EndsWith(")", StringComparison.Ordinal)
            && FixtureRootNames.Contains(name.Substring(0, paren));
    }

    [TearDown]
    public void TearDown()
    {
        foreach (GameObject go in _cleanup) if (go != null) UnityEngine.Object.DestroyImmediate(go);
        _cleanup.Clear();
    }

    private GameObject Track(GameObject go) { _cleanup.Add(go); return go; }

    private static string SetLine(int targetId, string type, JObject props, int index = 0)
    {
        return new JObject
        {
            ["params"] = new JObject
            {
                ["target"] = targetId.ToString(),
                ["componentType"] = type,
                ["componentIndex"] = index,
                ["properties"] = props,
            },
        }.ToString();
    }

    // ---------- TypeResolver ----------

    [Test]
    public void TypeResolver_ResolvesShortAndFullNames()
    {
        Assert.AreEqual(typeof(Rigidbody), TypeResolver.Resolve("Rigidbody"));
        Assert.AreEqual(typeof(Rigidbody), TypeResolver.Resolve("UnityEngine.Rigidbody"));
    }

    // 'Transform' collides with UnityEngine.Rendering.RadeonRays.Transform and log4net.Util.Transform
    // when those assemblies are loaded, so a naive short-name scan finds 3 matches. Resolve must prefer
    // the canonical UnityEngine component instead of throwing "ambiguous" — it's the most common
    // component of all, and add/get/set_component would otherwise break per-project.
    [Test]
    public void TypeResolver_PrefersUnityEngineForAmbiguousShortName()
    {
        Assert.AreEqual(typeof(Transform), TypeResolver.Resolve("Transform"));
    }

    [Test]
    public void TypeResolver_RequireComponentType_RejectsNonComponent()
    {
        Assert.Throws<Exception>(() => TypeResolver.RequireComponentType("Material")); // not a Component
        Assert.Throws<Exception>(() => TypeResolver.RequireComponentType("NoSuchTypeXyz"));
    }

    // ---------- ObjectResolver ----------

    [Test]
    public void ObjectResolver_ResolvesByIdNameAndPath()
    {
        GameObject parent = Track(new GameObject("OpsRoot_Unique"));
        GameObject child = Track(new GameObject("OpsChild_Unique"));
        child.transform.SetParent(parent.transform);

        Assert.AreEqual(parent, ObjectResolver.ResolveGameObject(parent.GetInstanceID().ToString()));
        Assert.AreEqual(parent, ObjectResolver.ResolveGameObject("OpsRoot_Unique"));
        Assert.AreEqual(child, ObjectResolver.ResolveGameObject("OpsRoot_Unique/OpsChild_Unique"));
        Assert.AreEqual("OpsRoot_Unique/OpsChild_Unique", ObjectResolver.PathOf(child.transform));
    }

    [Test]
    public void ObjectResolver_AmbiguousNameThrows()
    {
        Track(new GameObject("OpsDup_Ambiguous"));
        Track(new GameObject("OpsDup_Ambiguous"));
        Assert.Throws<Exception>(() => ObjectResolver.ResolveGameObject("OpsDup_Ambiguous"));
    }

    // ---------- PropertyEngine round-trips ----------

    [Test]
    public void PropertyEngine_SetsFloat_WithNameMangling()
    {
        GameObject go = Track(new GameObject("OpsMass"));
        Rigidbody rb = go.AddComponent<Rigidbody>();
        PropertyEngine.Set(SetLine(go.GetInstanceID(), "Rigidbody", new JObject { ["mass"] = 5f })); // mass -> m_Mass
        Assert.AreEqual(5f, rb.mass, 0.0001f);
    }

    [Test]
    public void PropertyEngine_Set_EchoesTheWrittenValuesBack()
    {
        // Set returned only the NAMES it applied, so confirming a write cost the caller a whole extra
        // get_gameobject round trip. It now reads the values back off the object and returns them.
        GameObject go = Track(new GameObject("OpsEcho"));
        Rigidbody rb = go.AddComponent<Rigidbody>();

        var res = JObject.Parse(PropertyEngine.Set(
            SetLine(go.GetInstanceID(), "Rigidbody", new JObject { ["mass"] = 7.5f })));

        Assert.AreEqual(7.5f, rb.mass, 0.0001f, "the write itself must still land");
        Assert.IsNotNull(res["values"], "the reply must carry the post-write values");
        Assert.AreEqual(7.5f, (float)res["values"]["mass"], 0.0001f,
            "the echoed value is keyed by the name the caller used and read back off the object");
    }

    [Test]
    public void PropertyEngine_Set_AppliesToEveryTargetInOneCall()
    {
        // Setting the same fields on N objects used to cost N calls, and each call is a separate round
        // trip for the caller. targets:[..] collapses that to one.
        GameObject a = Track(new GameObject("OpsBatchA"));
        GameObject b = Track(new GameObject("OpsBatchB"));
        Rigidbody ra = a.AddComponent<Rigidbody>();
        Rigidbody rb = b.AddComponent<Rigidbody>();

        string line = new JObject
        {
            ["params"] = new JObject
            {
                ["targets"] = new JArray(a.GetInstanceID().ToString(), b.GetInstanceID().ToString()),
                ["componentType"] = "Rigidbody",
                ["componentIndex"] = 0,
                ["properties"] = new JObject { ["mass"] = 3f },
            },
        }.ToString();

        var res = JObject.Parse(PropertyEngine.Set(line));

        Assert.AreEqual(3f, ra.mass, 0.0001f, "first target");
        Assert.AreEqual(3f, rb.mass, 0.0001f, "second target");
        Assert.AreEqual(2, (int)res["targetCount"]);
        Assert.AreEqual(2, ((JArray)res["results"]).Count, "one result per target");
    }

    [Test]
    public void PropertyEngine_Set_BadTargetDoesNotLoseTheOtherWrites()
    {
        // A batch is not all-or-nothing: one unresolvable target must not discard the writes that landed.
        GameObject good = Track(new GameObject("OpsBatchA"));
        Rigidbody rg = good.AddComponent<Rigidbody>();

        string line = new JObject
        {
            ["params"] = new JObject
            {
                ["targets"] = new JArray("OpsNoSuchObject_Missing", good.GetInstanceID().ToString()),
                ["componentType"] = "Rigidbody",
                ["properties"] = new JObject { ["mass"] = 9f },
            },
        }.ToString();

        var res = JObject.Parse(PropertyEngine.Set(line));

        Assert.AreEqual(9f, rg.mass, 0.0001f, "the resolvable target still gets its write");
        Assert.AreEqual(1, (int)res["appliedCount"], "only the good target counts as applied");
        Assert.IsNotNull(((JArray)res["results"])[0]["error"], "the bad target reports its own error");
    }

    [Test]
    public void PropertyEngine_SetsVector3_FromArrayAndObject()
    {
        GameObject a = Track(new GameObject("OpsVecA"));
        PropertyEngine.Set(SetLine(a.GetInstanceID(), "Transform", new JObject { ["m_LocalPosition"] = new JArray(1f, 2f, 3f) }));
        Assert.AreEqual(new Vector3(1, 2, 3), a.transform.localPosition);

        GameObject b = Track(new GameObject("OpsVecB"));
        PropertyEngine.Set(SetLine(b.GetInstanceID(), "Transform",
            new JObject { ["m_LocalPosition"] = new JObject { ["x"] = 4f, ["y"] = 5f, ["z"] = 6f } }));
        Assert.AreEqual(new Vector3(4, 5, 6), b.transform.localPosition);
    }

    [Test]
    public void PropertyEngine_ReportsFailurePerProperty_ButAppliesRest()
    {
        GameObject go = Track(new GameObject("OpsPartial"));
        go.AddComponent<Rigidbody>();
        string result = PropertyEngine.Set(SetLine(go.GetInstanceID(), "Rigidbody",
            new JObject { ["mass"] = 7f, ["totallyNotAField"] = 1 }));
        var json = JObject.Parse(result);
        Assert.AreEqual(7f, go.GetComponent<Rigidbody>().mass, 0.0001f);
        Assert.AreEqual(1, ((JArray)json["failed"]).Count, "the bogus property should be reported as failed");
        Assert.Contains("mass", ((JArray)json["applied"]).ToObject<List<string>>());
    }

    // ---------- PropertyReader ----------

    [Test]
    public void PropertyReader_EmitsMangledNamesThatRoundTrip()
    {
        GameObject go = Track(new GameObject("OpsRead"));
        go.AddComponent<Rigidbody>();
        string props = PropertyReader.ReadComponentProperties(go.GetComponent<Rigidbody>(), 2);
        var json = JObject.Parse(props);
        Assert.IsNotNull(json["m_Mass"], "reader should expose the serialized name m_Mass");
    }

    // ---------- GameObjectOps ----------

    [Test]
    public void GameObjectOps_CreatePrimitive_Modify_Delete()
    {
        var create = new CommandHandlers.RequestParams { primitive = "cube", name = "OpsCube" };
        var created = JObject.Parse(GameObjectOps.Create(create));
        int id = (int)created["instanceID"];
        var go = (GameObject)EditorUtility.InstanceIDToObject(id);
        Track(go);
        Assert.AreEqual("OpsCube", go.name);
        Assert.IsNotNull(go.GetComponent<MeshFilter>(), "a cube primitive has a MeshFilter");

        string modLine = new JObject { ["params"] = new JObject { ["target"] = id.ToString(), ["name"] = "OpsCubeRenamed" } }.ToString();
        GameObjectOps.Modify(modLine);
        Assert.AreEqual("OpsCubeRenamed", go.name);

        var del = new CommandHandlers.RequestParams { targets = new[] { id.ToString() } };
        var delResult = JObject.Parse(GameObjectOps.Delete(del));
        Assert.AreEqual(1, (int)delResult["count"]);
        Assert.IsTrue(go == null, "the GameObject should be destroyed");
    }

    [Test]
    public void GameObjectOps_Find_ByNameAndComponent()
    {
        GameObject go = Track(new GameObject("OpsFindMe"));
        go.AddComponent<Rigidbody>();
        var p = new CommandHandlers.RequestParams { gname = "OpsFindMe", component = "Rigidbody", max = 50 };
        var result = JObject.Parse(GameObjectOps.Find(p));
        Assert.GreaterOrEqual((int)result["total"], 1);
        Assert.AreEqual(go.GetInstanceID(), (int)((JArray)result["matches"])[0]["instanceID"]);
    }

    // ---------- ComponentOps ----------

    [Test]
    public void ComponentOps_AddAndRemove()
    {
        GameObject go = Track(new GameObject("OpsComp"));
        var add = new CommandHandlers.RequestParams { action = "add", target = go.GetInstanceID().ToString(), componentType = "Rigidbody" };
        ComponentOps.Manage(add);
        Assert.IsNotNull(go.GetComponent<Rigidbody>());

        var remove = new CommandHandlers.RequestParams { action = "remove", target = go.GetInstanceID().ToString(), componentType = "Rigidbody" };
        ComponentOps.Manage(remove);
        Assert.IsNull(go.GetComponent<Rigidbody>());
    }

    // ---- Json.TryReadStringField -------------------------------------------------------------------
    // Used by the bridge on its socket thread, where JsonUtility (main-thread only) cannot be called.

    [Test]
    public void TryReadStringField_ReadsTopLevelFields()
    {
        const string line = "{\"id\":\"abc-123\",\"command\":\"ping\",\"params\":{}}";
        Assert.IsTrue(Json.TryReadStringField(line, "command", out string command));
        Assert.AreEqual("ping", command);
        Assert.IsTrue(Json.TryReadStringField(line, "id", out string id));
        Assert.AreEqual("abc-123", id);
    }

    [Test]
    public void TryReadStringField_HandlesEscapesAndMissingKeys()
    {
        const string line = "{\"command\":\"a\\\"b\\\\c\\nd\",\"n\":5}";
        Assert.IsTrue(Json.TryReadStringField(line, "command", out string v));
        Assert.AreEqual("a\"b\\c\nd", v);

        Assert.IsFalse(Json.TryReadStringField(line, "absent", out _), "missing key must return false");
        Assert.IsFalse(Json.TryReadStringField(line, "n", out _), "non-string value must return false");
        Assert.IsFalse(Json.TryReadStringField(null, "command", out _));
    }

    [Test]
    public void TryHandleOffMainThread_AnswersPingOnly()
    {
        Assert.IsTrue(CommandHandlers.TryHandleOffMainThread(
            "{\"id\":\"p1\",\"command\":\"ping\",\"params\":{}}", out string response));
        StringAssert.Contains("\"listenerAlive\":true", response);
        StringAssert.Contains("\"p1\"", response);

        Assert.IsFalse(CommandHandlers.TryHandleOffMainThread(
            "{\"id\":\"p2\",\"command\":\"get_project_info\"}", out _),
            "everything except ping must fall through to the main-thread path");
    }
    [Test]
    public void GameObjectOps_CreateEmpty_UsesTheRequestedNameExactly()
    {
        var create = new CommandHandlers.RequestParams { name = "OpsEmptyRoot" };
        var created = JObject.Parse(GameObjectOps.Create(create));
        var go = (GameObject)EditorUtility.InstanceIDToObject((int)created["instanceID"]);
        Track(go);

        // The empty branch created the object already named, then uniquified it against its own siblings,
        // so it collided with itself and every empty object came out as "Name (1)".
        Assert.AreEqual("OpsEmptyRoot", go.name);
        Assert.AreEqual("OpsEmptyRoot", (string)created["name"]);
    }

    [Test]
    public void GameObjectOps_Copy_GetsAUniqueNameAmongItsSiblings()
    {
        var parent = Track(new GameObject("OpsCopyParent"));
        var source = new GameObject("OpsCopyChild");
        source.transform.SetParent(parent.transform);

        var create = new CommandHandlers.RequestParams
        {
            copyFrom = source.GetInstanceID().ToString(),
            parent = parent.GetInstanceID().ToString(),
        };
        var created = JObject.Parse(GameObjectOps.Create(create));
        var copy = (GameObject)EditorUtility.InstanceIDToObject((int)created["instanceID"]);
        Track(copy);

        // A copy that keeps its source name is indistinguishable by hierarchy path, so path-based
        // targeting silently resolves to whichever sibling happens to come first.
        Assert.AreNotEqual(source.name, copy.name, "the copy must not reuse its source name");
    }

    [Test]
    public void BridgeServer_BindFailure_StaysAWarningUntilItPersists()
    {
        // A rebind that loses a race with the socket from the previous domain recovers by itself, so the
        // first failures are routine and must not be reported as errors. Only a failure that keeps failing
        // means the port is genuinely taken and the bridge is unusable.
        Assert.IsFalse(BridgeServer.BindFailureIsPersistent(0.0), "the first failure after a reload is routine");
        Assert.IsFalse(BridgeServer.BindFailureIsPersistent(BridgeServer.BindFailurePersistentSec - 1.0));
        Assert.IsTrue(BridgeServer.BindFailureIsPersistent(BridgeServer.BindFailurePersistentSec),
            "at the threshold the failure is no longer routine");
        Assert.IsTrue(BridgeServer.BindFailureIsPersistent(BridgeServer.BindFailurePersistentSec + 60.0));
    }

    [Test]
    public void AcceptLoop_AfterStopping_ExitsAndLeavesThePortRebindable()
    {
        // The bridge lost port 6578 for a whole Editor session because the accept thread was parked in
        // AcceptTcpClient: on Mono, closing the socket does not wake it, and SafeSocketHandle refuses to run
        // closesocket() while that call still holds a reference. The handle stayed open, the port stayed
        // bound, and once the domain reloaded no managed code could reach it again — only restarting Unity
        // freed it. Two things therefore have to hold, and the second is the one that actually bit us:
        // the loop must exit when asked, AND the port must be genuinely free once it has.
        var listener = new TcpListener(IPAddress.Loopback, 0);   // 0 = let the OS pick a free port
        listener.ExclusiveAddressUse = true;
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var running = true;
        var accepted = 0;
        var thread = new Thread(() => BridgeServer.RunAcceptLoop(
            listener, () => Volatile.Read(ref running), c => { Interlocked.Increment(ref accepted); c.Close(); }));
        thread.Start();

        // Let it reach the loop, then ask it to stop the way a domain reload does.
        Thread.Sleep(100);
        Volatile.Write(ref running, false);

        Assert.IsTrue(thread.Join(5000),
            "the accept loop must exit once its run flag clears; if it cannot, it is blocking in Accept");

        listener.Server.Close();
        listener.Stop();

        // The real assertion. A handle still referenced by a blocked Accept leaves the port bound even
        // though Close() was called and returned without error.
        Assert.DoesNotThrow(() =>
        {
            var rebind = new TcpListener(IPAddress.Loopback, port);
            rebind.ExclusiveAddressUse = true;
            rebind.Start();
            rebind.Stop();
        }, $"port {port} was still held after teardown, so the socket handle was never really closed");

        Assert.AreEqual(0, accepted, "no client connected, so nothing should have been accepted");
    }
}
