using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;

namespace TruckSim
{
    /// <summary>
    /// Takes an imported truck model (Sketchfab glTF), normalises scale / orientation, detects wheel meshes
    /// (to spin and steer them), finds the 3D steering wheel (by name or by its shape) with its real column axis,
    /// computes the driver eye point from it and collects head / tail light meshes.
    /// </summary>
    public class TruckModelRig
    {
        public class WheelVisual
        {
            public Transform pivot;
            public Vector3 localCenter;
            public float radius;
            public bool front;
            public bool left;
        }

        public Transform visualRoot;
        public Bounds localBounds;           // bounds in truck-root space after normalisation
        public readonly List<WheelVisual> wheels = new List<WheelVisual>();
        public Transform steeringWheel;      // pivot for the 3D steering wheel, may be null
        public Vector3 steeringWheelAxis = Vector3.back; // column axis, pointing from the wheel towards the driver
        public float steeringWheelRadius;
        public Vector3 eyeLocal;
        public bool wheelsDetected;
        public string report = "";
        // light lenses found on the model (renderers), split by position
        public readonly List<Renderer> headLightMeshes = new List<Renderer>();
        public readonly List<Renderer> tailLightMeshes = new List<Renderer>();

        static readonly Regex WheelName = new Regex(@"(wheel|tire|tyre|rim|roda|pneu|llanta|rueda|neumatico|felga|reifen|hub)", RegexOptions.IgnoreCase);
        static readonly Regex NotWheel = new Regex(@"(steer|volante|spare|fifth|5th|quinta|reserve|arch|guard|fender|mud|interior|seat|dash|cabin|cabina|chassis)", RegexOptions.IgnoreCase);
        static readonly Regex SteeringName = new Regex(@"(steering.?wheel|steeringwheel|volante|lenkrad|steer_wheel|wheel_steer|kierownica)", RegexOptions.IgnoreCase);
        static readonly Regex LightName = new Regex(@"(head.?light|headlamp|tail.?light|taillamp|farol|lanterna|hl_|light|lamp|redglass|red_glass|clearglass|vermelho|branco_r|stop|brake|_em_|emiss)", RegexOptions.IgnoreCase);
        static readonly Regex NotLight = new Regex(@"(signal|seta|orange|laranja|indicator|blink|interior|dash|gauge|cab_|roof|beacon|ambient)", RegexOptions.IgnoreCase);

        public static TruckModelRig Build(Transform truckRoot, GameObject prefab, TruckSpec spec)
        {
            var rig = new TruckModelRig();
            var visual = new GameObject("Visual").transform;
            visual.SetParent(truckRoot, false);
            rig.visualRoot = visual;

            var model = Object.Instantiate(prefab, visual, false);
            model.name = spec.id;
            model.transform.localPosition = Vector3.zero;

            foreach (var c in model.GetComponentsInChildren<Collider>(true)) Object.DestroyImmediate(c);
            foreach (var c in model.GetComponentsInChildren<Camera>(true)) Object.DestroyImmediate(c.gameObject);
            foreach (var l in model.GetComponentsInChildren<Light>(true)) Object.DestroyImmediate(l.gameObject);
            FixGlass(model);

            // 1) Length axis: longest horizontal extent.
            Bounds b = WorldBounds(model);
            float yaw = 0f;
            if (b.size.x > b.size.z) yaw = 90f;
            model.transform.localRotation = Quaternion.Euler(0, yaw, 0) * model.transform.localRotation;

            // 2) Scale to real length (re-measured: deep hierarchies with tiny node scales do not scale linearly in one step).
            float scale = 1f;
            for (int it = 0; it < 6; it++)
            {
                b = WorldBounds(model);
                float s = spec.lengthMeters / Mathf.Max(0.0001f, b.size.z);
                if (it == 0) rig.report += $"rawLen={b.size.z:0.000} ";
                if (Mathf.Abs(s - 1f) < 0.005f) break;
                model.transform.localScale *= s; scale *= s;
            }
            rig.report += $"scale={scale:0.000} ";
            b = WorldBounds(model);

            // 3) Front detection: the cab is the tallest part; compare the top height in each end.
            if (spec.yawOverride >= 0f)
                model.transform.localRotation = Quaternion.Euler(0, spec.yawOverride, 0) * model.transform.localRotation;
            else
            {
                float frontTop = TopHeightInSlice(model, b, 0.5f, 1.0f);
                float rearTop = TopHeightInSlice(model, b, 0.0f, 0.5f);
                if (rearTop > frontTop + 0.05f) model.transform.localRotation = Quaternion.Euler(0, 180f, 0) * model.transform.localRotation;
                rig.report += $"frontTop={frontTop:0.00} rearTop={rearTop:0.00} ";
            }
            b = WorldBounds(model);

            // 4) Centre on X/Z, put the lowest point on the ground.
            model.transform.position += new Vector3(-b.center.x, -b.min.y, -b.center.z);
            b = WorldBounds(model);
            rig.localBounds = new Bounds(truckRoot.InverseTransformPoint(b.center), b.size);

            rig.DetectWheels(truckRoot, model, spec);
            rig.FindSteeringWheel(truckRoot, model, spec);
            rig.ComputeEye(truckRoot, model, spec);
            rig.FindLights(truckRoot, model);
            return rig;
        }

        static Bounds WorldBounds(GameObject go)
        {
            bool has = false;
            Bounds b = new Bounds(go.transform.position, Vector3.zero);
            foreach (var r in go.GetComponentsInChildren<Renderer>())
            {
                if (!TryMeshBounds(r, out var rb)) continue;
                if (!has) { b = rb; has = true; } else b.Encapsulate(rb);
            }
            return has ? b : new Bounds(go.transform.position, Vector3.one);
        }

        /// <summary>World bounds computed from the mesh itself, valid right after scaling / rotating.</summary>
        static bool TryMeshBounds(Renderer r, out Bounds wb)
        {
            Mesh m = null;
            if (r is MeshRenderer) { var mf = r.GetComponent<MeshFilter>(); if (mf) m = mf.sharedMesh; }
            else if (r is SkinnedMeshRenderer smr) m = smr.sharedMesh;
            if (m == null) { wb = r.bounds; return wb.size.sqrMagnitude > 0f; }
            Bounds lb = m.bounds;
            Matrix4x4 mtx = r.transform.localToWorldMatrix;
            Vector3 c = lb.center, e = lb.extents;
            wb = new Bounds(mtx.MultiplyPoint3x4(c - e), Vector3.zero);
            for (int i = 1; i < 8; i++)
            {
                var p = c + new Vector3((i & 1) != 0 ? e.x : -e.x, (i & 2) != 0 ? e.y : -e.y, (i & 4) != 0 ? e.z : -e.z);
                wb.Encapsulate(mtx.MultiplyPoint3x4(p));
            }
            return true;
        }

        static Bounds RB(Renderer r) { TryMeshBounds(r, out var b); return b; }

        static readonly Regex GlassName = new Regex(@"(glass|window|windshield|windscreen|vidro|parabrisa|cristal|steklo|sklo|okno)", RegexOptions.IgnoreCase);

        /// <summary>Some Sketchfab exports ship the windshield as an opaque material: make glass see-through.</summary>
        static void FixGlass(GameObject model)
        {
            Shader std = Shader.Find("Standard");
            if (std == null) return;
            var cache = new Dictionary<Material, Material>();
            foreach (var r in model.GetComponentsInChildren<Renderer>(true))
            {
                var mats = r.sharedMaterials; bool changed = false;
                for (int i = 0; i < mats.Length; i++)
                {
                    var m = mats[i];
                    if (m == null || !GlassName.IsMatch(m.name) || m.renderQueue >= 2900) continue;
                    if (Regex.IsMatch(m.name, "(farol|seta|light|lamp|red|orange)", RegexOptions.IgnoreCase)) continue; // lamp lenses stay as they are
                    if (!cache.TryGetValue(m, out var g))
                    {
                        g = new Material(std) { name = m.name + "_seeThrough" };
                        g.SetFloat("_Mode", 3f);
                        g.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.One);
                        g.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                        g.SetInt("_ZWrite", 0);
                        g.DisableKeyword("_ALPHATEST_ON"); g.DisableKeyword("_ALPHABLEND_ON"); g.EnableKeyword("_ALPHAPREMULTIPLY_ON");
                        g.renderQueue = 3000;
                        g.color = new Color(0.55f, 0.62f, 0.66f, 0.2f);
                        g.SetFloat("_Glossiness", 0.95f); g.SetFloat("_Metallic", 0.1f);
                        cache[m] = g;
                    }
                    mats[i] = g; changed = true;
                }
                if (changed) r.sharedMaterials = mats;
            }
        }

        static float TopHeightInSlice(GameObject go, Bounds all, float from, float to)
        {
            float z0 = all.min.z + all.size.z * from, z1 = all.min.z + all.size.z * to;
            float top = all.min.y;
            foreach (var r in go.GetComponentsInChildren<Renderer>())
            {
                var rb = RB(r);
                if (rb.size.z > all.size.z * 0.45f) continue;
                if (rb.center.z < z0 || rb.center.z > z1) continue;
                top = Mathf.Max(top, rb.max.y);
            }
            return top - all.min.y;
        }

        static string NameOf(Renderer r)
        {
            string n = r.name;
            var p = r.transform.parent;
            for (int i = 0; i < 2 && p != null; i++, p = p.parent) n += " " + p.name;
            foreach (var m in r.sharedMaterials) if (m) n += " " + m.name;
            return n;
        }

        void DetectWheels(Transform root, GameObject model, TruckSpec spec)
        {
            Bounds b = localBounds;
            float maxCenterY = b.min.y + 0.95f;
            var candidates = new List<Renderer>();
            foreach (var r in model.GetComponentsInChildren<Renderer>())
            {
                string n = NameOf(r);
                if (!WheelName.IsMatch(n) || NotWheel.IsMatch(n)) continue;
                var rb = RB(r);
                Vector3 c = root.InverseTransformPoint(rb.center);
                if (c.y > maxCenterY || c.y < 0.25f) continue;
                if (rb.size.y > 1.4f || rb.size.y < 0.15f) continue;
                if (rb.size.z > 1.4f) continue;
                if (rb.size.x > 1.2f) continue;          // whole axle or merged left+right wheels
                candidates.Add(r);
            }

            var clusters = new List<(Bounds bounds, List<Renderer> list)>();
            foreach (var r in candidates)
            {
                Bounds rb = RB(r);
                Vector3 c = root.InverseTransformPoint(rb.center);
                int found = -1;
                for (int i = 0; i < clusters.Count; i++)
                {
                    Vector3 cc = root.InverseTransformPoint(clusters[i].bounds.center);
                    if (Mathf.Abs(cc.z - c.z) < 0.5f && Mathf.Abs(cc.x - c.x) < 0.8f && Mathf.Sign(cc.x) == Mathf.Sign(c.x)) { found = i; break; }
                }
                if (found < 0) clusters.Add((rb, new List<Renderer> { r }));
                else { var cl = clusters[found]; cl.bounds.Encapsulate(rb); cl.list.Add(r); clusters[found] = cl; }
            }
            clusters.RemoveAll(c => c.bounds.size.y < 0.7f || c.bounds.size.y > 1.4f);
            int leftCount = 0, rightCount = 0;
            foreach (var c in clusters) { if (root.InverseTransformPoint(c.bounds.center).x < 0) leftCount++; else rightCount++; }
            report += $"wheelCandidates={candidates.Count} clusters={clusters.Count} L={leftCount} R={rightCount} ";

            if (clusters.Count >= 4 && clusters.Count <= 10 && leftCount >= 2 && rightCount >= 2 && leftCount == rightCount)
            {
                wheelsDetected = true;
                float frontZ = float.MinValue;
                foreach (var c in clusters) frontZ = Mathf.Max(frontZ, root.InverseTransformPoint(c.bounds.center).z);
                foreach (var c in clusters)
                {
                    Vector3 center = root.InverseTransformPoint(c.bounds.center);
                    var pivot = new GameObject("WheelPivot").transform;
                    pivot.SetParent(visualRoot, false);
                    pivot.localPosition = center;
                    foreach (var r in c.list) r.transform.SetParent(pivot, true);
                    wheels.Add(new WheelVisual
                    {
                        pivot = pivot, localCenter = center, radius = c.bounds.extents.y,
                        front = center.z > frontZ - 0.6f, left = center.x < 0
                    });
                }
            }
            else
            {
                wheelsDetected = false;
                float r = spec.wheelRadius;
                float frontAxle = b.max.z - (spec.lengthMeters > 7.5f ? 2.05f : 1.45f);
                float rearAxle = frontAxle - spec.wheelbase;
                float halfTrack = Mathf.Min(1.03f, b.extents.x - 0.2f);
                var axleZ = new List<float> { frontAxle, rearAxle };
                if (spec.axles >= 3) axleZ.Add(rearAxle - 1.35f);
                foreach (float z in axleZ)
                    for (int s = -1; s <= 1; s += 2)
                        wheels.Add(new WheelVisual { pivot = null, localCenter = new Vector3(s * halfTrack, r, z), radius = r, front = z == frontAxle, left = s < 0 });
            }
        }

        // ------------------------------------------------------------------ steering wheel

        struct DiscFit { public bool ok; public Vector3 center, normal; public float radius, flat, round, rim, score; }

        /// <summary>Principal-axis fit of a point set: a steering wheel is a flat, round ring about 0.36-0.62 m across.</summary>
        static DiscFit FitDisc(List<Vector3> pts)
        {
            var f = new DiscFit();
            if (pts.Count < 24) return f;
            Vector3 mean = Vector3.zero; foreach (var p in pts) mean += p; mean /= pts.Count;
            double xx = 0, xy = 0, xz = 0, yy = 0, yz = 0, zz = 0;
            foreach (var p in pts) { var d = p - mean; xx += d.x * d.x; xy += d.x * d.y; xz += d.x * d.z; yy += d.y * d.y; yz += d.y * d.z; zz += d.z * d.z; }
            var m = new double[3, 3] { { xx, xy, xz }, { xy, yy, yz }, { xz, yz, zz } };
            Jacobi(m, out var ev, out var vecs);
            // sort eigen values descending
            int[] idx = { 0, 1, 2 }; System.Array.Sort(idx, (a, b) => ev[b].CompareTo(ev[a]));
            double l1 = ev[idx[0]], l2 = ev[idx[1]], l3 = ev[idx[2]];
            if (l1 <= 1e-12) return f;
            var n = new Vector3((float)vecs[0, idx[2]], (float)vecs[1, idx[2]], (float)vecs[2, idx[2]]).normalized;
            // in-plane radii
            float rmax = 0f; var rs = new float[pts.Count];
            for (int i = 0; i < pts.Count; i++) { var d = pts[i] - mean; d -= Vector3.Dot(d, n) * n; rs[i] = d.magnitude; rmax = Mathf.Max(rmax, rs[i]); }
            int rimCount = 0; foreach (var r in rs) if (r > rmax * 0.72f) rimCount++;
            f.center = mean; f.normal = n; f.radius = rmax;
            f.flat = (float)System.Math.Sqrt(l3 / l1);
            f.round = (float)System.Math.Sqrt(l2 / l1);
            f.rim = rimCount / (float)pts.Count;
            f.ok = rmax > 0.17f && rmax < 0.33f && f.flat < 0.42f && f.round > 0.72f && f.rim > 0.3f;
            f.score = f.round * 2f + f.rim - f.flat * 2f - Mathf.Abs(rmax - 0.23f) * 4f;
            return f;
        }

        static void Jacobi(double[,] a, out double[] d, out double[,] v)
        {
            v = new double[3, 3] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
            d = new double[3];
            for (int sweep = 0; sweep < 50; sweep++)
            {
                double off = a[0, 1] * a[0, 1] + a[0, 2] * a[0, 2] + a[1, 2] * a[1, 2];
                if (off < 1e-22) break;
                for (int p = 0; p < 2; p++)
                    for (int q = p + 1; q < 3; q++)
                    {
                        if (System.Math.Abs(a[p, q]) < 1e-30) continue;
                        double theta = (a[q, q] - a[p, p]) / (2 * a[p, q]);
                        double t = System.Math.Sign(theta) / (System.Math.Abs(theta) + System.Math.Sqrt(theta * theta + 1));
                        if (theta == 0) t = 1;
                        double c = 1 / System.Math.Sqrt(t * t + 1), s = t * c;
                        for (int k = 0; k < 3; k++) { double akp = a[k, p], akq = a[k, q]; a[k, p] = c * akp - s * akq; a[k, q] = s * akp + c * akq; }
                        for (int k = 0; k < 3; k++) { double apk = a[p, k], aqk = a[q, k]; a[p, k] = c * apk - s * aqk; a[q, k] = s * apk + c * aqk; }
                        for (int k = 0; k < 3; k++) { double vkp = v[k, p], vkq = v[k, q]; v[k, p] = c * vkp - s * vkq; v[k, q] = s * vkp + c * vkq; }
                    }
            }
            for (int i = 0; i < 3; i++) d[i] = a[i, i];
        }

        static List<Vector3> RootPoints(Renderer r, Transform root, int maxPts = 6000)
        {
            var mf = r.GetComponent<MeshFilter>();
            var res = new List<Vector3>();
            if (mf == null || mf.sharedMesh == null || !mf.sharedMesh.isReadable) return res;
            var v = mf.sharedMesh.vertices;
            int step = Mathf.Max(1, v.Length / maxPts);
            var mtx = root.worldToLocalMatrix * r.transform.localToWorldMatrix;
            for (int i = 0; i < v.Length; i += step) res.Add(mtx.MultiplyPoint3x4(v[i]));
            return res;
        }

        bool PlausibleWheelSpot(Vector3 c)
        {
            Bounds b = localBounds;
            return c.y > 1.15f && c.y < 2.9f && c.x < 0.05f && c.x > b.min.x + 0.15f && c.z > b.min.z + b.size.z * 0.2f && c.z < b.max.z - 0.15f;
        }

        /// <summary>A disc-shaped piece of geometry: either a whole mesh or one connected part of a mesh.</summary>
        public class DiscCandidate
        {
            public Renderer renderer; public MeshFilter mf; public int sub = -1; public List<int> tris; // tris == null -> whole mesh
            public Vector3 center, normal; public float radius, flat, round, rim, score; public int triCount; public string name;
        }

        public static bool strictDisc = true;

        static bool DiscOk(DiscFit f, bool strict)
        {
            if (strict) return f.radius > 0.17f && f.radius < 0.33f && f.flat < 0.42f && f.round > 0.72f && f.rim > 0.3f;
            return f.radius > 0.12f && f.radius < 0.4f && f.flat < 0.6f && f.round > 0.5f;
        }

        /// <summary>Collects steering-wheel shaped geometry inside a region (root space), whole meshes and connected parts.</summary>
        public List<DiscCandidate> CollectDiscs(Transform root, GameObject model, System.Func<Vector3, bool> region, bool strict)
        {
            var res = new List<DiscCandidate>();
            foreach (var mf in model.GetComponentsInChildren<MeshFilter>())
            {
                var mesh = mf.sharedMesh; var r = mf.GetComponent<Renderer>();
                if (mesh == null || r == null || !mesh.isReadable || mesh.vertexCount > 300000) continue;
                var rb = RB(r);
                Vector3 c = root.InverseTransformPoint(rb.center);
                // whole mesh
                if (rb.size.magnitude < 1.4f && rb.size.magnitude > 0.25f && region(c))
                {
                    var fit = FitDisc(RootPoints(r, root));
                    if (fit.radius > 0 && DiscOk(fit, strict))
                        res.Add(new DiscCandidate { renderer = r, mf = mf, center = fit.center, normal = fit.normal, radius = fit.radius, flat = fit.flat, round = fit.round, rim = fit.rim, score = fit.score, triCount = (int)(mesh.triangles.Length / 3), name = r.name });
                }
                if (rb.size.magnitude < 0.35f) continue;
                EnumerateParts(mf, root, region, (sub, tl, wv) =>
                {
                    var bb = new Bounds(wv[tl[0]], Vector3.zero);
                    foreach (int i in tl) bb.Encapsulate(wv[i]);
                    if (bb.size.magnitude > 1.3f || bb.size.magnitude < 0.25f || !region(bb.center)) return;
                    var pts = new List<Vector3>();
                    int stepT = Mathf.Max(1, tl.Count / 9000) * 3;
                    for (int q = 0; q + 2 < tl.Count; q += stepT) { pts.Add(wv[tl[q]]); pts.Add(wv[tl[q + 1]]); pts.Add(wv[tl[q + 2]]); }
                    var fit = FitDisc(pts);
                    if (fit.radius <= 0 || !DiscOk(fit, strict)) return;
                    res.Add(new DiscCandidate { renderer = r, mf = mf, sub = sub, tris = tl, center = fit.center, normal = fit.normal, radius = fit.radius, flat = fit.flat, round = fit.round, rim = fit.rim, score = fit.score, triCount = tl.Count / 3, name = r.name + "#" + sub });
                });
            }
            return res;
        }

        /// <summary>Calls back for every connected part (welded by position) of every sub-mesh: (sub, triangle index list, root-space vertices).</summary>
        static void EnumerateParts(MeshFilter mf, Transform root, System.Func<Vector3, bool> nearRegion, System.Action<int, List<int>, Vector3[]> onPart)
        {
            var mesh = mf.sharedMesh;
            var mtx = root.worldToLocalMatrix * mf.transform.localToWorldMatrix;
            var verts = mesh.vertices;
            var wv = new Vector3[verts.Length];
            bool any = false;
            for (int i = 0; i < verts.Length; i++) { wv[i] = mtx.MultiplyPoint3x4(verts[i]); if (!any && (i % 5 == 0) && nearRegion(wv[i])) any = true; }
            if (!any) return;
            var map = new Dictionary<Vector3Int, int>(); var weld = new int[verts.Length];
            for (int i = 0; i < verts.Length; i++)
            {
                var key = new Vector3Int(Mathf.RoundToInt(wv[i].x * 1000f), Mathf.RoundToInt(wv[i].y * 1000f), Mathf.RoundToInt(wv[i].z * 1000f));
                if (!map.TryGetValue(key, out int w)) { w = map.Count; map[key] = w; }
                weld[i] = w;
            }
            for (int sub = 0; sub < mesh.subMeshCount; sub++)
            {
                var tris = mesh.GetTriangles(sub);
                if (tris.Length < 3) continue;
                var parent = new int[map.Count]; for (int i = 0; i < parent.Length; i++) parent[i] = i;
                int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
                for (int t = 0; t < tris.Length; t += 3)
                {
                    int a = Find(weld[tris[t]]);
                    int b2 = Find(weld[tris[t + 1]]); if (b2 != a) parent[b2] = a;
                    int c2 = Find(weld[tris[t + 2]]); if (c2 != a) parent[c2] = a;
                }
                var comps = new Dictionary<int, List<int>>();
                for (int t = 0; t < tris.Length; t += 3)
                {
                    int k = Find(weld[tris[t]]);
                    if (!comps.TryGetValue(k, out var l)) comps[k] = l = new List<int>();
                    l.Add(tris[t]); l.Add(tris[t + 1]); l.Add(tris[t + 2]);
                }
                foreach (var kv in comps) onPart(sub, kv.Value, wv);
            }
        }

        void FindSteeringWheel(Transform root, GameObject model, TruckSpec spec)
        {
            Bounds b = localBounds;
            bool hinted = spec.steeringWheelHint != Vector3.zero;
            System.Func<Vector3, bool> region = hinted
                ? (System.Func<Vector3, bool>)(c => (c - spec.steeringWheelHint).magnitude < 0.3f)
                : PlausibleWheelSpot;
            var cands = CollectDiscs(root, model, region, !hinted);
            DiscCandidate best = null; float bestScore = float.MinValue;
            foreach (var c in cands)
            {
                float score = c.score + (SteeringName.IsMatch(c.name) ? 3f : 0f) - Mathf.Abs(c.normal.x) * 2f;
                if (hinted) score -= (c.center - spec.steeringWheelHint).magnitude * 6f;
                if (score > bestScore) { bestScore = score; best = c; }
            }
            if (best == null) { report += "steeringWheel=none "; return; }

            Vector3 n = best.normal;
            if (spec.steeringAxisHint != Vector3.zero) n = spec.steeringAxisHint.normalized;
            if (n.z > 0f) n = -n;                 // point from the wheel towards the driver
            Vector3 C = best.center; float R = best.radius;
            if (spec.steeringRadiusHint > 0f) R = spec.steeringRadiusHint;
            var pivot = new GameObject("SteeringWheelPivot").transform;
            pivot.SetParent(visualRoot, false);
            pivot.localPosition = C;
            // everything that lies completely inside the wheel's cylinder turns with it (rim, spokes, hub, logo, buttons)
            System.Func<Vector3, bool> inside = p =>
            {
                Vector3 d = p - C; float ax = Vector3.Dot(d, n);
                return ax > -0.17f && ax < 0.12f && (d - ax * n).magnitude < R * 1.1f + 0.015f;
            };
            int parts = 0;
            foreach (var mf in model.GetComponentsInChildren<MeshFilter>())
            {
                var mesh = mf.sharedMesh; var r = mf.GetComponent<Renderer>();
                if (mesh == null || r == null || !mesh.isReadable || mesh.vertexCount > 300000) continue;
                var rb = RB(r);
                if (rb.size.magnitude < 3f * R && inside(root.InverseTransformPoint(rb.center)) && AllInside(r, root, inside))
                { r.transform.SetParent(pivot, true); parts++; continue; }
                var bySub = new Dictionary<int, List<int>>();
                EnumerateParts(mf, root, inside, (sub, tl, wv) =>
                {
                    int ok = 0; foreach (int i in tl) if (inside(wv[i])) ok++;
                    if (ok < tl.Count * 0.98f) return;
                    if (!bySub.TryGetValue(sub, out var l)) bySub[sub] = l = new List<int>();
                    l.AddRange(tl);
                });
                foreach (var kv in bySub)
                {
                    var part = Extract(new DiscCandidate { renderer = r, mf = mf, sub = kv.Key, tris = kv.Value });
                    part.transform.SetParent(pivot, true); parts++;
                }
            }
            if (parts == 0 && best.tris == null) { best.renderer.transform.SetParent(pivot, true); parts = 1; }
            report += $"wheelParts={parts} ";
            steeringWheel = pivot;
            steeringWheelAxis = n.normalized;
            steeringWheelRadius = best.radius;
            RefineSteeringPivot(root, R);
            report += $"steeringWheel='{best.name}' at {best.center} axis={n} r={best.radius:0.00} flat={best.flat:0.00} round={best.round:0.00} tris={best.triCount} ";
        }

        /// <summary>
        /// Puts the steering-wheel pivot exactly on the rim's centre and its axis exactly on the rim's normal
        /// (circle fit + plane fit on the rim vertices). An off-centre pivot makes the wheel wobble / look distorted when turned.
        /// </summary>
        void RefineSteeringPivot(Transform root, float R)
        {
            if (steeringWheel == null) return;
            var pts = new List<Vector3>();
            foreach (var mf in steeringWheel.GetComponentsInChildren<MeshFilter>())
            {
                var m = mf.sharedMesh; if (m == null || !m.isReadable) continue;
                var v = m.vertices; var mtx = root.worldToLocalMatrix * mf.transform.localToWorldMatrix;
                int stride = Mathf.Max(1, v.Length / 30000);
                for (int i = 0; i < v.Length; i += stride) pts.Add(mtx.MultiplyPoint3x4(v[i]));
            }
            Vector3 C = root.InverseTransformPoint(steeringWheel.position), n = steeringWheelAxis.normalized;
            // only geometry inside the wheel's cylinder (moved sub-hierarchies may carry unrelated children)
            pts.RemoveAll(p => { var d = p - C; float ax = Vector3.Dot(d, n); return ax < -0.17f || ax > 0.12f || (d - ax * n).magnitude > R * 1.1f + 0.015f; });
            if (pts.Count < 60) { report += "pivotRefine=skip "; return; }
            Vector3 C0 = C, n0 = n;
            // The rim is the outermost ring, so the centre of its in-plane extent (robust percentiles) is the true
            // rotation centre; spokes / hub / buttons lie inside the ring and do not affect it. The axis stays as is.
            Vector3 u = Vector3.Cross(n, Mathf.Abs(n.y) < 0.9f ? Vector3.up : Vector3.right).normalized, w = Vector3.Cross(n, u);
            for (int it = 0; it < 3; it++)
            {
                var xs = new List<float>(); var ys = new List<float>();
                foreach (var p in pts)
                {
                    var d = p - C; float ax = Vector3.Dot(d, n); var rad = d - ax * n;
                    if (rad.magnitude > R * 1.15f + 0.02f) continue;
                    xs.Add(Vector3.Dot(rad, u)); ys.Add(Vector3.Dot(rad, w));
                }
                if (xs.Count < 60) break;
                xs.Sort(); ys.Sort();
                float Pc(List<float> l, float q) => l[Mathf.Clamp(Mathf.RoundToInt(q * (l.Count - 1)), 0, l.Count - 1)];
                float cx = 0.5f * (Pc(xs, 0.004f) + Pc(xs, 0.996f)), cy = 0.5f * (Pc(ys, 0.004f) + Pc(ys, 0.996f));
                C += u * cx + w * cy;
                if (Mathf.Abs(cx) + Mathf.Abs(cy) < 0.0005f) break;
            }
            float shift = (C - C0).magnitude, turn = 0f;
            if (shift > R * 0.4f || float.IsNaN(shift)) { report += $"pivotRefine=rejected({shift:0.000}m) "; return; }
            report += $"wobble {RimWobble(pts, C0, n0) * 1000f:0.0}->{RimWobble(pts, C, n) * 1000f:0.0}mm ";
            // move the pivot without moving the wheel parts
            var kids = new List<(Transform t, Vector3 p, Quaternion r)>();
            foreach (Transform k in steeringWheel) kids.Add((k, k.position, k.rotation));
            steeringWheel.position = root.TransformPoint(C);
            foreach (var k in kids) k.t.SetPositionAndRotation(k.p, k.r);
            if (n.z > 0f) n = -n;
            steeringWheelAxis = n.normalized;
            report += $"pivotRefine=shift {shift * 100f:0.0}cm axis {turn:0.0}deg ";
        }

        static float RimWobble(List<Vector3> pts, Vector3 C, Vector3 n)
        {
            float rmax = 0f; foreach (var p in pts) { var d = p - C; d -= Vector3.Dot(d, n) * n; rmax = Mathf.Max(rmax, d.magnitude); }
            double sr = 0, srr = 0, sa = 0, saa = 0; int N = 0;
            foreach (var p in pts)
            {
                var d = p - C; float ax = Vector3.Dot(d, n); float r = (d - ax * n).magnitude;
                if (r < rmax * 0.72f) continue;
                sr += r; srr += r * r; sa += ax; saa += ax * ax; N++;
            }
            if (N < 10) return float.MaxValue;
            double vr = srr / N - (sr / N) * (sr / N), va = saa / N - (sa / N) * (sa / N);
            return (float)(System.Math.Sqrt(System.Math.Max(0, vr)) + System.Math.Sqrt(System.Math.Max(0, va)));
        }

        static bool AllInside(Renderer r, Transform root, System.Func<Vector3, bool> inside)
        {
            var pts = RootPoints(r, root, 4000);
            if (pts.Count == 0) return false;
            int ok = 0; foreach (var p in pts) if (inside(p)) ok++;
            return ok >= pts.Count * 0.98f;
        }

        /// <summary>Moves one connected part out of a mesh into its own object so it can rotate.</summary>
        static Renderer Extract(DiscCandidate c)
        {
            var src = c.mf.sharedMesh;
            var rest = Object.Instantiate(src); rest.name = src.name + "_noWheel";
            var wheelSet = new HashSet<long>();
            for (int i = 0; i < c.tris.Count; i += 3) wheelSet.Add(((long)c.tris[i] << 42) ^ ((long)c.tris[i + 1] << 21) ^ c.tris[i + 2]);
            var orig = src.GetTriangles(c.sub); var keep = new List<int>(orig.Length);
            for (int t = 0; t < orig.Length; t += 3)
                if (!wheelSet.Contains(((long)orig[t] << 42) ^ ((long)orig[t + 1] << 21) ^ orig[t + 2])) { keep.Add(orig[t]); keep.Add(orig[t + 1]); keep.Add(orig[t + 2]); }
            rest.SetTriangles(keep, c.sub);
            c.mf.sharedMesh = rest;
            var wheelMesh = Object.Instantiate(src); wheelMesh.name = "SteeringWheel_split";
            wheelMesh.subMeshCount = 1; wheelMesh.SetTriangles(c.tris, 0, true);
            var go = new GameObject("SteeringWheel_split");
            go.transform.SetParent(c.mf.transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = wheelMesh;
            var mats = c.renderer.sharedMaterials;
            go.AddComponent<MeshRenderer>().sharedMaterial = mats[Mathf.Min(c.sub, mats.Length - 1)];
            return go.GetComponent<Renderer>();
        }

        void ComputeEye(Transform root, GameObject model, TruckSpec spec)
        {
            Bounds b = localBounds;
            if (steeringWheel != null && spec.useSteeringWheelForEye)
            {
                // Driver's eyes: ~0.45 m above the wheel centre, in line with it, and just in front of the seat's headrest.
                Vector3 w = steeringWheel.localPosition;
                float headY = w.y + 0.47f;
                float seatFront = float.MinValue;
                foreach (var r in model.GetComponentsInChildren<Renderer>())
                {
                    if (r.transform.IsChildOf(steeringWheel)) continue;
                    var rb = RB(r);
                    Vector3 lo = root.InverseTransformPoint(rb.min), hi = root.InverseTransformPoint(rb.max);
                    if (Mathf.Max(lo.x, hi.x) < w.x - 0.14f || Mathf.Min(lo.x, hi.x) > w.x + 0.14f) continue;
                    if (Mathf.Max(lo.y, hi.y) < headY - 0.1f || Mathf.Min(lo.y, hi.y) > headY + 0.1f) continue;
                    foreach (var p in RootPoints(r, root, 40000))
                        if (Mathf.Abs(p.x - w.x) < 0.14f && Mathf.Abs(p.y - headY) < 0.1f && p.z < w.z - 0.25f && p.z > w.z - 1.3f)
                            seatFront = Mathf.Max(seatFront, p.z);
                }
                float eyeZ = seatFront > float.MinValue ? seatFront + 0.2f : w.z - 0.55f;
                eyeZ = Mathf.Clamp(eyeZ, w.z - 0.75f, w.z - 0.42f);
                eyeLocal = new Vector3(w.x, w.y + 0.45f, eyeZ);
                report += $"seatFront={(seatFront > float.MinValue ? (w.z - seatFront).ToString("0.00") : "none")} ";
            }
            else
            {
                Vector3 n = spec.eyeNormalized;
                eyeLocal = new Vector3(b.center.x + n.x * b.extents.x, b.min.y + n.y * b.size.y, b.center.z + n.z * b.extents.z);
            }
            eyeLocal += spec.eyeOffset;
        }

        /// <summary>Rotates the 3D steering wheel around its real column axis (+ = clockwise for the driver = right).</summary>
        public void SetSteeringWheel(float degrees)
        {
            if (steeringWheel != null) steeringWheel.localRotation = Quaternion.AngleAxis(degrees, steeringWheelAxis);
        }

        // ------------------------------------------------------------------ lights

        void FindLights(Transform root, GameObject model)
        {
            Bounds b = localBounds;
            foreach (var r in model.GetComponentsInChildren<Renderer>())
            {
                string n = NameOf(r);
                if (!LightName.IsMatch(n) || NotLight.IsMatch(n)) continue;
                var rb = RB(r);
                if (rb.size.magnitude > 1.6f) continue;
                Vector3 c = root.InverseTransformPoint(rb.center);
                if (c.y < 0.3f || c.y > 2.0f) continue;          // roof marker lights / beacons are not head or tail lights
                if (c.z > b.max.z - 0.9f) headLightMeshes.Add(r);
                else if (c.z < b.min.z + 0.9f) tailLightMeshes.Add(r);
            }
            report += $"headLightMeshes={headLightMeshes.Count} tailLightMeshes={tailLightMeshes.Count} ";
        }
    }
}
