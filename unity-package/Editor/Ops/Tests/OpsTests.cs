using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using AgenLink;
using AgenLink.Ops;

public class OpsTests
{
    private readonly List<GameObject> _cleanup = new List<GameObject>();

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
}
