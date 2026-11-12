#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace TruckSim.EditorTools
{
    /// <summary>QA tool: renders every truck in Resources/Trucks from outside and from the driver's seat.
    /// Batch mode: -executeMethod TruckSim.EditorTools.TruckShots.Run -qaOut /path (needs graphics, not -nographics).</summary>
    public static class TruckShots
    {
        [MenuItem("Truck Sim/QA: Render truck shots")]
        public static void Run()
        {
            string outDir = Arg("-qaOut", "QA_Shots");
            string only = Arg("-qaOnly", "");
            Directory.CreateDirectory(outDir);
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var sunGo = new GameObject("Sun"); var sun = sunGo.AddComponent<Light>();
            sun.type = LightType.Directional; sun.intensity = 1.2f; sun.shadows = LightShadows.Soft;
            sunGo.transform.rotation = Quaternion.Euler(42f, -35f, 0f);
            var sky = Shader.Find("Skybox/Procedural"); if (sky) RenderSettings.skybox = new Material(sky);
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.62f, 0.7f, 0.82f);
            RenderSettings.ambientEquatorColor = new Color(0.55f, 0.57f, 0.55f);
            RenderSettings.ambientGroundColor = new Color(0.28f, 0.27f, 0.24f);
            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane); ground.transform.localScale = Vector3.one * 20f;
            var camGo = new GameObject("QACam"); var cam = camGo.AddComponent<Camera>(); cam.farClipPlane = 2000f;
            var rt = new RenderTexture(1280, 720, 24); cam.targetTexture = rt;

            foreach (var path in Directory.GetFiles("Assets/Resources/Trucks", "*.glb"))
            {
                string id = Path.GetFileNameWithoutExtension(path);
                if (only != "" && System.Array.IndexOf(only.Split(','), id.Substring(0, 2)) < 0) continue;
                var prefab = Resources.Load<GameObject>("Trucks/" + id);
                if (!prefab) { Debug.LogError("[QA] cannot load " + id); continue; }
                TruckSpec spec = null;
                foreach (var s in TruckDatabase.All) if (s.id == id) spec = s;
                if (spec == null) spec = new TruckSpec { id = id, displayName = id, lengthMeters = float.Parse(Arg("-qaLen", "6.6"), System.Globalization.CultureInfo.InvariantCulture), wheelbase = 3.9f, axles = 3 };
                var root = new GameObject("QA_" + id);
                var rig = TruckModelRig.Build(root.transform, prefab, spec);
                Debug.Log($"[QA] {id}: size={rig.localBounds.size} eye={rig.eyeLocal} steeringWheel={(rig.steeringWheel ? rig.steeringWheel.localPosition.ToString() : "none")} wheels={rig.wheels.Count}/{rig.wheelsDetected} | {rig.report}");
                var b = rig.localBounds;
                if (Arg("-qaDiscs", "") == "1")
                {
                    var bb = b;
                    var list = rig.CollectDiscs(root.transform, rig.visualRoot.gameObject,
                        c => (c - spec.steeringWheelHint).magnitude < 0.45f, false);
                    list.Sort((x, y) => y.score.CompareTo(x.score));
                    for (int i = 0; i < Mathf.Min(14, list.Count); i++)
                    {
                        var c = list[i];
                        Debug.Log($"[QA] {id} disc{i}: {c.name} c=({c.center.x:0.00},{c.center.y:0.00},{c.center.z:0.00}) n=({c.normal.x:0.00},{c.normal.y:0.00},{c.normal.z:0.00}) r={c.radius:0.00} flat={c.flat:0.00} round={c.round:0.00} rim={c.rim:0.00} tris={c.triCount} score={c.score:0.00}");
                    }
                    Debug.Log($"[QA] {id} bounds min={b.min} max={b.max}");
                }
                // exterior 3/4 front
                cam.fieldOfView = 50f; cam.nearClipPlane = 0.1f;
                Vector3 pivot = b.center;
                cam.transform.position = pivot + Quaternion.Euler(12f, 215f, 0) * Vector3.back * -(b.size.z + 4f);
                cam.transform.LookAt(pivot);
                Shot(cam, rt, Path.Combine(outDir, id + "_ext.png"));
                // cab views
                cam.fieldOfView = 70f; cam.nearClipPlane = 0.03f;
                float[] yaws = { 0f, -55f, 50f, 180f };
                string[] names = { "cab", "cab_left", "cab_right", "cab_back" };
                for (int i = 0; i < yaws.Length; i++)
                {
                    cam.transform.position = root.transform.TransformPoint(rig.eyeLocal);
                    cam.transform.rotation = Quaternion.Euler(i == 0 ? 8f : 12f, yaws[i], 0);
                    Shot(cam, rt, Path.Combine(outDir, id + "_" + names[i] + ".png"));
                }
                // steering check: wheel turned 90 degrees to the right, seen from the driver's eye looking at the wheel
                if (rig.steeringWheel != null)
                {
                    cam.fieldOfView = 55f;
                    cam.transform.position = root.transform.TransformPoint(rig.eyeLocal);
                    cam.transform.LookAt(root.transform.TransformPoint(rig.steeringWheel.localPosition));
                    rig.SetSteeringWheel(0f);
                    Shot(cam, rt, Path.Combine(outDir, id + "_wheel0.png"));
                    rig.SetSteeringWheel(90f);
                    Shot(cam, rt, Path.Combine(outDir, id + "_wheelR90.png"));
                    rig.SetSteeringWheel(0f);
                }
                // side section through the driver's area (left cab wall cut away): red = eye, green = wheel centre
                {
                    var eyeM = GameObject.CreatePrimitive(PrimitiveType.Sphere); eyeM.transform.localScale = Vector3.one * 0.08f;
                    eyeM.transform.position = root.transform.TransformPoint(rig.eyeLocal);
                    eyeM.GetComponent<Renderer>().sharedMaterial = new Material(Shader.Find("Unlit/Color")) { color = Color.red };
                    GameObject whM = null;
                    if (rig.steeringWheel != null)
                    {
                        whM = GameObject.CreatePrimitive(PrimitiveType.Sphere); whM.transform.localScale = Vector3.one * 0.06f;
                        whM.transform.position = rig.steeringWheel.position;
                        whM.GetComponent<Renderer>().sharedMaterial = new Material(Shader.Find("Unlit/Color")) { color = Color.green };
                    }
                    cam.orthographic = true; cam.orthographicSize = 1.3f;
                    // camera on the passenger side, passenger half cut away: the driver's seat and wheel are seen against the left wall
                    float cutX = -0.18f;
                    cam.transform.position = new Vector3(6f, rig.eyeLocal.y - 0.3f, rig.eyeLocal.z + 0.5f);
                    cam.transform.rotation = Quaternion.LookRotation(Vector3.left);
                    cam.nearClipPlane = 6f - cutX; cam.farClipPlane = 20f;
                    Shot(cam, rt, Path.Combine(outDir, id + "_section.png"));
                    cam.orthographic = false; cam.farClipPlane = 2000f;
                    Object.DestroyImmediate(eyeM); if (whM) Object.DestroyImmediate(whM);
                }
                // lights check
                Debug.Log($"[QA] {id} lights: head=[{string.Join(",", rig.headLightMeshes.ConvertAll(r => r.name))}] tail=[{string.Join(",", rig.tailLightMeshes.ConvertAll(r => r.name))}]");
                Object.DestroyImmediate(root);
            }
            Debug.Log("[QA] DONE");
            if (Application.isBatchMode) EditorApplication.Exit(0);
        }

        static void Shot(Camera cam, RenderTexture rt, string file)
        {
            cam.Render();
            var prev = RenderTexture.active; RenderTexture.active = rt;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0); tex.Apply();
            RenderTexture.active = prev;
            File.WriteAllBytes(file, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
        }

        static string Arg(string a, string def)
        {
            var args = System.Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++) if (args[i] == a) return args[i + 1];
            return def;
        }
    }
}
#endif
