using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace TruckSim
{
    /// <summary>
    /// Streams a real Egyptian intercity road (see RoadData) around the truck in 500 m chunks:
    /// asphalt with the real number of lanes, shoulders, markings, median (concrete barrier or wide sand median),
    /// bridges, guardrails on high embankments, median street lights, km posts, distance / speed signs, towns,
    /// and SRTM terrain with cut / fill slopes. Uses a floating origin so long routes stay precise.
    /// Lane 0 = right lane (Egypt drives on the right).
    /// </summary>
    public class HighwayBuilder : MonoBehaviour
    {
        public const float LaneWidth = 3.65f;
        public const float Shoulder = 2.5f;
        public const float InnerShoulder = 0.6f;
        public const float Step = 5f;            // runtime sample spacing (m)
        const int ChunkSamples = 100;            // 500 m
        const int TerrainEvery = 4;              // terrain rows every 20 m (data: every 2 x 10 m)

        public RoadData data;
        public int Count { get; private set; }
        public float totalLength;
        double[] X, Y, Z;                        // route coordinates (m), Z = north
        float[] medianM, carriageW, curvature;
        byte[] lanes; bool[] bridge, jerseyS;
        int streamHint = -1;
        Vector3[] tan;
        public double ox, oy, oz;                // floating origin (route coords at world 0,0,0)
        readonly Dictionary<int, GameObject> chunks = new Dictionary<int, GameObject>();
        Transform chunkRoot, farGround;
        int dataN; float[] terrOff; int nOff;

        Material asphaltMat, markingMat, yellowMat, concreteMat, steelMat, sandMat, medianMat, poleMat, signMat, signWhiteMat, redMat, buildingMat, buildingMat2, farMat;
        public Material lampMat;                 // shared by all street lamps (switched on at night)
        Font font;

        public string RouteName => data != null ? data.name : "";
        public Vector3 P(int i) { i = Mathf.Clamp(i, 0, Count - 1); return new Vector3((float)(X[i] - ox), (float)(Y[i] - oy), (float)(Z[i] - oz)); }
        public Vector3 Tangent(int i) => tan[Mathf.Clamp(i, 0, Count - 1)];
        public Vector3[] tangents => tan;
        public void Build() { Load("R1_CairoAlex"); BuildAround(0); }
        public Vector3 Right(int i) { var t = Tangent(i); return new Vector3(t.z, 0f, -t.x).normalized; }
        public int LaneCount(int i) => lanes[Mathf.Clamp(i, 0, Count - 1)];
        public float KmAt(int i) => i * Step / 1000f;
        public float ElevationAt(int i) => (float)Y[Mathf.Clamp(i, 0, Count - 1)];
        public float GradeAt(int i) { int a = Mathf.Clamp(i - 4, 0, Count - 1), b = Mathf.Clamp(i + 4, 0, Count - 1); return b > a ? (float)((Y[b] - Y[a]) / ((b - a) * Step)) * 100f : 0f; }
        float HalfMedian(int i) => medianM[i] * 0.5f;
        bool Divided(int i) => medianM[i] > 0.4f;
        float CarriageInner(int i) => Divided(i) ? HalfMedian(i) + InnerShoulder : 0.1f;
        float CarriageOuter(int i) => CarriageInner(i) + carriageW[i];
        float EdgeOuter(int i) => CarriageOuter(i) + Shoulder;

        /// <summary>Centre of a lane on the right-hand carriageway (lane 0 = rightmost / slow lane).</summary>
        public Vector3 LanePosition(int i, int lane)
        {
            i = Mathf.Clamp(i, 0, Count - 1);
            int n = Mathf.Max(1, Mathf.RoundToInt(carriageW[i] / LaneWidth));
            lane = Mathf.Clamp(lane, 0, n - 1);
            float off = CarriageInner(i) + LaneWidth * (n - 0.5f - lane);
            return P(i) + Right(i) * off;
        }

        // ------------------------------------------------------------------ setup

        /// <summary>Loads a route (returns false and keeps the current one when the file is missing / invalid).</summary>
        public bool Load(string routeId)
        {
            var d = RoadData.Load(routeId);
            if (d == null) return false;
            ClearChunks();
            if (asphaltMat == null) CreateMaterials();
            data = d;
            streamHint = -1;
            int n0 = data.count;
            dataN = n0;
            Count = (n0 - 1) * 2 + 1;
            X = new double[Count]; Y = new double[Count]; Z = new double[Count];
            medianM = new float[Count]; carriageW = new float[Count]; lanes = new byte[Count]; bridge = new bool[Count]; jerseyS = new bool[Count]; curvature = new float[Count];
            Vector3d Dp(int k) { k = Mathf.Clamp(k, 0, n0 - 1); return new Vector3d(data.x[k] * 0.01, data.y[k] * 0.01, data.zN[k] * 0.01); }
            for (int k = 0; k < n0; k++)
            {
                int i = k * 2;
                var p = Dp(k);
                X[i] = p.x; Y[i] = p.y; Z[i] = p.z;
                if (k < n0 - 1)
                {
                    // Catmull-Rom half-way point (smooth curves at 5 m spacing)
                    var p0 = Dp(k - 1); var p1 = p; var p2 = Dp(k + 1); var p3 = Dp(k + 2);
                    X[i + 1] = 0.5 * (p1.x + p2.x) + (-p0.x + p1.x + p2.x - p3.x) * 0.0625 * 1.0 + 0 * 0; // t=0.5 Catmull-Rom
                    X[i + 1] = CR(p0.x, p1.x, p2.x, p3.x); Z[i + 1] = CR(p0.z, p1.z, p2.z, p3.z); Y[i + 1] = 0.5 * (p1.y + p2.y);
                }
            }
            for (int i = 0; i < Count; i++)
            {
                int k = Mathf.Min(i / 2, n0 - 1), k2 = Mathf.Min(k + 1, n0 - 1); float f = (i % 2) * 0.5f;
                medianM[i] = Mathf.Lerp(data.median[k], data.median[k2], f) * 0.01f;
                lanes[i] = (byte)Mathf.Clamp(data.lanes[k], 1, 6);
                bridge[i] = data.bridge[k] != 0;
                // concrete New-Jersey barrier: explicit in v3 data, derived from a narrow (paved) median in older files
                jerseyS[i] = data.HasJersey ? (data.jersey[k] != 0 || data.jersey[k2] != 0) : medianM[i] <= 8f;
            }
            // carriageway width with ~120 m tapers where the number of lanes changes
            var raw = new float[Count];
            for (int i = 0; i < Count; i++) raw[i] = lanes[i] * LaneWidth;
            int w = 12; double acc = 0; int cnt = 0;
            for (int i = -w; i < Count + w; i++)
            {
                int add = i + w; if (add >= 0 && add < Count) { acc += raw[add]; cnt++; }
                int rem = i - w - 1; if (rem >= 0 && rem < Count) { acc -= raw[rem]; cnt--; }
                if (i >= 0 && i < Count) carriageW[i] = (float)(acc / Mathf.Max(1, cnt));
            }
            for (int i = 0; i < Count; i++) carriageW[i] = Mathf.Max(carriageW[i], Mathf.Min(raw[i], carriageW[i] + LaneWidth)) ;
            tan = new Vector3[Count];
            for (int i = 0; i < Count; i++)
            {
                int a = Mathf.Max(0, i - 1), b = Mathf.Min(Count - 1, i + 1);
                tan[i] = new Vector3((float)(X[b] - X[a]), 0f, (float)(Z[b] - Z[a])).normalized;
            }
            // signed curvature (1/m), + = turning right
            for (int i = 0; i < Count; i++)
            {
                int a = Mathf.Max(0, i - 4), b = Mathf.Min(Count - 1, i + 4);
                float ang = Vector3.SignedAngle(tan[a], tan[b], Vector3.up) * Mathf.Deg2Rad;
                curvature[i] = b > a ? ang / ((b - a) * Step) : 0f;
            }
            totalLength = (Count - 1) * Step;
            terrOff = new float[data.terrainOffsets.Length]; nOff = terrOff.Length;
            for (int i = 0; i < nOff; i++) terrOff[i] = data.terrainOffsets[i];
            SetOrigin(0);
            Debug.Log($"[Road] {data.name}: {totalLength / 1000f:0.0} km, {Count} samples, elevation {MinMaxY()}");
            return true;
        }

        static double CR(double p0, double p1, double p2, double p3) => 0.5 * (2 * p1 + (-p0 + p2) * 0.5 + (2 * p0 - 5 * p1 + 4 * p2 - p3) * 0.25 + (-p0 + 3 * p1 - 3 * p2 + p3) * 0.125);

        string MinMaxY() { double a = 1e9, b = -1e9; foreach (var y in Y) { a = System.Math.Min(a, y); b = System.Math.Max(b, y); } return $"{a:0}..{b:0} m"; }

        struct Vector3d { public double x, y, z; public Vector3d(double a, double b, double c) { x = a; y = b; z = c; } }

        /// <summary>Puts route sample i at the world origin (used when loading a route).</summary>
        public void SetOrigin(int i)
        {
            ox = X[i]; oy = Y[i]; oz = Z[i];
            foreach (var kv in chunks) PlaceChunk(kv.Key, kv.Value);
        }

        /// <summary>Floating origin: move the world by -delta (the caller moves the truck and camera).</summary>
        public void ShiftOrigin(Vector3 delta)
        {
            ox += delta.x; oy += delta.y; oz += delta.z;
            foreach (var kv in chunks) PlaceChunk(kv.Key, kv.Value);
        }

        void PlaceChunk(int c, GameObject go)
        {
            int a = c * ChunkSamples;
            go.transform.position = new Vector3((float)(X[a] - ox), (float)(Y[a] - oy), (float)(Z[a] - oz));
        }

        void ClearChunks()
        {
            foreach (var kv in chunks) if (kv.Value) Destroy(kv.Value);
            chunks.Clear();
        }

        
        void Update()
        {
            var cam = Camera.main;
            if (cam != null && Count > 0 && tan != null)
            {
                // local search around the last index (a full-route scan every frame stalls on 300 km roads)
                streamHint = ClosestIndex(cam.transform.position, streamHint);
                UpdateStreaming(streamHint, cam.transform.position);
            }
        }

        /// <summary>Floating origin: when the player is far from (0,0,0) move the world so the player is back near it.
        /// Returns the world-space shift that the caller must subtract from the truck / camera (zero when not needed).</summary>
        public Vector3 RecenterIfNeeded(Vector3 playerPos, float threshold = 1500f)
        {
            if (new Vector2(playerPos.x, playerPos.z).magnitude < threshold) return Vector3.zero;
            var delta = new Vector3(playerPos.x, 0f, playerPos.z);
            ShiftOrigin(delta);
            return delta;
        }

        // ------------------------------------------------------------------ streaming

        int ChunkCount => (Count - 2) / ChunkSamples + 1;

        /// <summary>Builds every chunk around sample index synchronously (route start / reset).</summary>
        public void BuildAround(int index)
        {
            int c0 = index / ChunkSamples;
            for (int c = c0 - 2; c <= c0 + 4; c++) EnsureChunk(c);
            Trim(c0);
        }

        /// <summary>Called every frame with the truck's sample index: builds at most one missing chunk.</summary>
        public void UpdateStreaming(int index, Vector3 playerPos)
        {
            int c0 = index / ChunkSamples;
            int[] order = { c0, c0 + 1, c0 + 2, c0 - 1, c0 + 3, c0 + 4, c0 - 2 };
            foreach (int c in order) if (c >= 0 && c < ChunkCount && !chunks.ContainsKey(c)) { EnsureChunk(c); break; }
            Trim(c0);
            if (farGround) farGround.position = new Vector3(playerPos.x, P(index).y - 35f, playerPos.z);
        }

        void Trim(int c0)
        {
            List<int> drop = null;
            foreach (var kv in chunks) if (kv.Key < c0 - 3 || kv.Key > c0 + 5) (drop ??= new List<int>()).Add(kv.Key);
            if (drop != null) foreach (int c in drop) { Destroy(chunks[c]); chunks.Remove(c); }
        }

        void EnsureChunk(int c)
        {
            if (c < 0 || c >= ChunkCount || chunks.ContainsKey(c)) return;
            var go = BuildChunk(c);
            chunks[c] = go;
            PlaceChunk(c, go);
        }

        // ------------------------------------------------------------------ materials

        static Material Mat(Color c, float smooth = 0.2f, float metal = 0f, Texture tex = null)
        {
            var m = new Material(Shader.Find("Standard")) { color = c };
            m.SetFloat("_Glossiness", smooth);
            m.SetFloat("_Metallic", metal);
            if (tex) m.mainTexture = tex;
            m.enableInstancing = true;
            return m;
        }

        static Texture2D SandTexture(int seed, Color a, Color b)
        {
            int n = 256; var t = new Texture2D(n, n, TextureFormat.RGB24, true) { wrapMode = TextureWrapMode.Repeat, anisoLevel = 4 };
            var px = new Color[n * n];
            for (int y = 0; y < n; y++) for (int x = 0; x < n; x++)
            {
                float u = x / (float)n, v = y / (float)n, h = 0f, amp = 0.5f, f = 4f;
                for (int o = 0; o < 4; o++) { h += amp * TileNoise(u * f, v * f, (int)f, seed + o); amp *= 0.5f; f *= 2f; }
                float grain = Mathf.PerlinNoise(x * 0.9f + seed, y * 0.9f) * 0.15f;
                px[y * n + x] = Color.Lerp(a, b, Mathf.Clamp01(h + grain - 0.1f));
            }
            t.SetPixels(px); t.Apply(true);
            return t;
        }

        static float TileNoise(float x, float y, int period, int seed)
        {
            // tileable value noise
            int x0 = Mathf.FloorToInt(x), y0 = Mathf.FloorToInt(y); float fx = x - x0, fy = y - y0;
            float H(int i, int j) { i = ((i % period) + period) % period; j = ((j % period) + period) % period; uint hsh = (uint)(i * 374761393 + j * 668265263 + seed * 2147483647); hsh = (hsh ^ (hsh >> 13)) * 1274126177u; return (hsh & 0xffff) / 65535f; }
            fx = fx * fx * (3 - 2 * fx); fy = fy * fy * (3 - 2 * fy);
            return Mathf.Lerp(Mathf.Lerp(H(x0, y0), H(x0 + 1, y0), fx), Mathf.Lerp(H(x0, y0 + 1), H(x0 + 1, y0 + 1), fx), fy);
        }

        void CreateMaterials()
        {
            var asphaltTex = Resources.Load<Texture2D>("Textures/asphalt");
            asphaltMat = Mat(new Color(0.78f, 0.77f, 0.76f), 0.12f, 0f, asphaltTex);
            markingMat = Mat(new Color(0.95f, 0.95f, 0.92f), 0.3f);
            yellowMat = Mat(new Color(0.95f, 0.75f, 0.1f), 0.3f);
            concreteMat = Mat(new Color(0.78f, 0.76f, 0.72f), 0.08f);
            steelMat = Mat(new Color(0.78f, 0.8f, 0.83f), 0.75f, 0.8f);
            var sandTex = SandTexture(7, new Color(0.74f, 0.6f, 0.42f), new Color(0.9f, 0.79f, 0.6f));
            sandMat = Mat(Color.white, 0.04f, 0f, sandTex);
            medianMat = Mat(new Color(0.85f, 0.8f, 0.72f), 0.04f, 0f, SandTexture(3, new Color(0.55f, 0.48f, 0.38f), new Color(0.78f, 0.7f, 0.56f)));
            poleMat = Mat(new Color(0.6f, 0.62f, 0.64f), 0.5f, 0.6f);
            lampMat = new Material(Shader.Find("Unlit/Color")) { color = new Color(0.55f, 0.55f, 0.52f) };
            signMat = Mat(new Color(0.03f, 0.33f, 0.16f), 0.4f);
            signWhiteMat = Mat(new Color(0.95f, 0.95f, 0.95f), 0.4f);
            redMat = Mat(new Color(0.8f, 0.08f, 0.06f), 0.4f);
            buildingMat = Mat(new Color(0.82f, 0.74f, 0.62f), 0.05f);
            buildingMat2 = Mat(new Color(0.72f, 0.6f, 0.5f), 0.05f);
            farMat = Mat(new Color(0.8f, 0.68f, 0.5f), 0.02f);
            font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

            // big ground plane under everything (beyond the streamed terrain)
            var fg = GameObject.CreatePrimitive(PrimitiveType.Plane);
            Destroy(fg.GetComponent<Collider>());
            fg.name = "FarGround"; fg.transform.SetParent(transform, false); fg.transform.localScale = new Vector3(1200f, 1f, 1200f);
            fg.GetComponent<Renderer>().sharedMaterial = farMat;
            fg.GetComponent<Renderer>().shadowCastingMode = ShadowCastingMode.Off;
            farGround = fg.transform;
            chunkRoot = new GameObject("Chunks").transform; chunkRoot.SetParent(transform, false);
        }

        public void SetNight(bool night)
        {
            lampMat.color = night ? new Color(1f, 0.86f, 0.55f) : new Color(0.55f, 0.55f, 0.52f);
        }

        // ------------------------------------------------------------------ mesh helper

        class MB
        {
            public readonly List<Vector3> v = new List<Vector3>(); public readonly List<Vector2> uv = new List<Vector2>(); public readonly List<int> t = new List<int>();
            public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector2 ua, Vector2 ub, Vector2 uc, Vector2 ud)
            {
                int i = v.Count; v.Add(a); v.Add(b); v.Add(c); v.Add(d); uv.Add(ua); uv.Add(ub); uv.Add(uc); uv.Add(ud);
                t.Add(i); t.Add(i + 1); t.Add(i + 2); t.Add(i); t.Add(i + 2); t.Add(i + 3);
            }
            public void Box(Vector3 c, Vector3 size, Quaternion r)
            {
                Vector3 e = size * 0.5f;
                Vector3 P(float x, float y, float z) => c + r * new Vector3(x * e.x, y * e.y, z * e.z);
                Vector2 u0 = Vector2.zero, u1 = Vector2.right, u2 = Vector2.one, u3 = Vector2.up;
                Quad(P(-1, -1, -1), P(-1, 1, -1), P(1, 1, -1), P(1, -1, -1), u0, u3, u2, u1);
                Quad(P(1, -1, 1), P(1, 1, 1), P(-1, 1, 1), P(-1, -1, 1), u0, u3, u2, u1);
                Quad(P(-1, -1, 1), P(-1, 1, 1), P(-1, 1, -1), P(-1, -1, -1), u0, u3, u2, u1);
                Quad(P(1, -1, -1), P(1, 1, -1), P(1, 1, 1), P(1, -1, 1), u0, u3, u2, u1);
                Quad(P(-1, 1, -1), P(-1, 1, 1), P(1, 1, 1), P(1, 1, -1), u0, u3, u2, u1);
                Quad(P(-1, -1, 1), P(-1, -1, -1), P(1, -1, -1), P(1, -1, 1), u0, u3, u2, u1);
            }
            public GameObject Make(string name, Transform parent, Material m, bool collider, bool shadows = true)
            {
                if (v.Count == 0) return null;
                var go = new GameObject(name);
                go.transform.SetParent(parent, false);
                var mesh = new Mesh { indexFormat = v.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
                mesh.SetVertices(v); mesh.SetUVs(0, uv); mesh.SetTriangles(t, 0);
                mesh.RecalculateNormals(); mesh.RecalculateBounds();
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var r = go.AddComponent<MeshRenderer>(); r.sharedMaterial = m;
                if (!shadows) r.shadowCastingMode = ShadowCastingMode.Off;
                if (collider) go.AddComponent<MeshCollider>().sharedMesh = mesh;
                return go;
            }
        }

        // ------------------------------------------------------------------ chunk

        Vector3 L(int i, Vector3 anchor, float offset, float dy = 0f)
        {
            // local position (relative to chunk anchor) of a point at lateral offset (+ = right) from the centreline
            var p = new Vector3((float)(X[i] - X[anchor_i]), (float)(Y[i] - Y[anchor_i]) + dy, (float)(Z[i] - Z[anchor_i]));
            return p + Right(i) * offset;
        }
        int anchor_i;

        float TerrainDem(int row, float offsetSigned)
        {
            // DEM height (m) at a lateral offset from the data cross-section of a terrain row (row = data index / 2)
            int rows = data.terrainL.Length / nOff;
            row = Mathf.Clamp(row, 0, rows - 1);
            int[] arr = offsetSigned < 0 ? data.terrainL : data.terrainR;
            float o = Mathf.Abs(offsetSigned);
            if (o <= terrOff[0]) return arr[row * nOff] * 0.1f;
            for (int k = 1; k < nOff; k++)
                if (o <= terrOff[k]) return Mathf.Lerp(arr[row * nOff + k - 1], arr[row * nOff + k], (o - terrOff[k - 1]) / (terrOff[k] - terrOff[k - 1])) * 0.1f;
            return arr[row * nOff + nOff - 1] * 0.1f;
        }

        /// <summary>Ground height (route coords) next to the road at sample i: SRTM blended into cut / fill slopes.</summary>
        float GroundHeight(int i, float offsetSigned)
        {
            int row = (i / TerrainEvery);
            float roadY = (float)Y[i];
            float dem = TerrainDem(row, offsetSigned);
            float e = EdgeOuter(i);
            float o = Mathf.Abs(offsetSigned);
            if (bridge[i]) return Mathf.Min(dem, roadY - 2f);
            if (o <= e + 0.3f) return roadY - 0.12f;
            float maxDelta = 0.25f + (o - e) * 0.55f;      // ~1:1.8 cut / fill slopes
            return roadY - 0.15f + Mathf.Clamp(dem - roadY, -maxDelta, maxDelta);
        }

        GameObject BuildChunk(int c)
        {
            int a = c * ChunkSamples, b = Mathf.Min(Count - 1, a + ChunkSamples);
            anchor_i = a;
            var root = new GameObject("Chunk_" + c);
            root.transform.SetParent(chunkRoot, false);
            var rnd = new System.Random(c * 7919 + data.count);

            var asphalt = new MB(); var median = new MB(); var marks = new MB(); var yellow = new MB(); var concrete = new MB();
            var steel = new MB(); var poles = new MB(); var lamps = new MB(); var terrain = new MB(); var signs = new MB(); var signWhite = new MB(); var reds = new MB();
            var houses = new MB(); var houses2 = new MB();

            // ---- road surface, median, markings
            for (int i = a; i < b; i++)
            {
                int j = i + 1;
                float sA = i * Step, sB = j * Step;
                bool paved = !Divided(i) || jerseyS[i];
                for (int side = -1; side <= 1; side += 2)
                {
                    float inA = paved ? 0f : HalfMedian(i), inB = paved ? 0f : HalfMedian(j);
                    float outA = EdgeOuter(i), outB = EdgeOuter(j);
                    Vector3 p0 = L(i, default, side * inA), p1 = L(j, default, side * inB), p2 = L(j, default, side * outB), p3 = L(i, default, side * outA);
                    Vector2 u0 = new Vector2(inA / 6f, sA / 6f), u1 = new Vector2(inB / 6f, sB / 6f), u2 = new Vector2(outB / 6f, sB / 6f), u3 = new Vector2(outA / 6f, sA / 6f);
                    if (side > 0) asphalt.Quad(p0, p1, p2, p3, u0, u1, u2, u3); else asphalt.Quad(p3, p2, p1, p0, u3, u2, u1, u0);
                    // asphalt edge (vertical face down to the verge, hides the seam to the terrain)
                    Vector3 q0 = L(i, default, side * outA, -0.35f), q1 = L(j, default, side * outB, -0.35f);
                    if (side > 0) asphalt.Quad(p3, p2, q1, q0, Vector2.zero, Vector2.right, Vector2.one, Vector2.up); else asphalt.Quad(q0, q1, p2, p3, Vector2.zero, Vector2.right, Vector2.one, Vector2.up);

                    // markings: solid edge lines, dashed lane lines (6 m line / 9 m gap approximated on 5 m samples)
                    float ciA = CarriageInner(i), ciB = CarriageInner(j), coA = CarriageOuter(i), coB = CarriageOuter(j);
                    Line(marks, i, j, side, coA - 0.1f, coB - 0.1f, 0.2f);
                    if (Divided(i)) Line(yellow, i, j, side, ciA + 0.08f, ciB + 0.08f, 0.15f);
                    else if (side > 0) { Line(yellow, i, j, 1, 0.12f, 0.12f, 0.12f); Line(yellow, i, j, -1, 0.12f, 0.12f, 0.12f); }
                    if (i % 3 == 0)
                    {
                        int n = Mathf.RoundToInt(carriageW[i] / LaneWidth);
                        for (int k = 1; k < n; k++)
                        {
                            float oA = ciA + k * LaneWidth, oB = ciB + k * LaneWidth;
                            if (oA > coA - 0.6f || oB > coB - 0.6f) continue;
                            Line(marks, i, j, side, oA, oB, 0.15f);
                        }
                    }
                }
                if (Divided(i))
                {
                    if (paved)
                    {
                        // concrete New-Jersey barrier on the centreline
                        Barrier(concrete, i, j);
                    }
                    else
                    {
                        // wide desert median: sand strip slightly lower than the road + kerbs
                        float hA = HalfMedian(i), hB = HalfMedian(j);
                        median.Quad(L(i, default, -hA, -0.15f), L(j, default, -hB, -0.15f), L(j, default, hB, -0.15f), L(i, default, hA, -0.15f),
                            new Vector2((float)(X[i] % 1000) / 8f, (float)(Z[i] % 1000) / 8f), new Vector2((float)(X[j] % 1000) / 8f, (float)(Z[j] % 1000) / 8f),
                            new Vector2((float)(X[j] % 1000) / 8f + hB / 4f, (float)(Z[j] % 1000) / 8f), new Vector2((float)(X[i] % 1000) / 8f + hA / 4f, (float)(Z[i] % 1000) / 8f));
                        for (int side = -1; side <= 1; side += 2)
                            Kerb(concrete, i, j, side * hA, side * hB, side);
                    }
                }
                // bridges: concrete parapets; guardrails where the road is on a high embankment
                for (int side = -1; side <= 1; side += 2)
                {
                    float oA = EdgeOuter(i), oB = EdgeOuter(j);
                    if (bridge[i] && bridge[j]) Wall(concrete, i, j, side * (oA - 0.25f), side * (oB - 0.25f), 0.35f, 1.05f);
                    else if (GroundHeight(i, side * (oA + 6f)) < Y[i] - 2.2f) Guardrail(steel, i, j, side * (oA - 0.4f), side * (oB - 0.4f), i % 2 == 0);
                }
                if (bridge[i] && i % 6 == 0)
                {
                    float ground = TerrainDem(i / TerrainEvery, 0f);
                    float h = (float)Y[i] - 1.2f - ground;
                    if (h > 1f)
                        for (int side = -1; side <= 1; side += 2)
                        {
                            float off = side * (CarriageInner(i) + carriageW[i] * 0.5f);
                            concrete.Box(L(i, default, off, -1.2f - h * 0.5f), new Vector3(1.4f, h, 1.4f), Quaternion.LookRotation(Tangent(i)));
                            concrete.Box(L(i, default, off, -0.75f), new Vector3(carriageW[i] + 2f, 0.9f, 1.6f), Quaternion.LookRotation(Tangent(i)));
                        }
                }
                // median street lights every 50 m (typical on Egyptian desert highways)
                if (Divided(i) && i % 10 == 0 && medianM[i] > 1.2f)
                {
                    Vector3 basePos = L(i, default, 0f, paved ? 0.8f : -0.15f);
                    var rot = Quaternion.LookRotation(Tangent(i));
                    poles.Box(basePos + Vector3.up * 5.5f, new Vector3(0.22f, 11f, 0.22f), rot);
                    for (int side = -1; side <= 1; side += 2)
                    {
                        Vector3 r = Right(i) * side;
                        poles.Box(basePos + Vector3.up * 10.9f + r * 1.1f, new Vector3(0.12f, 0.12f, 2.2f), Quaternion.LookRotation(r));
                        lamps.Box(basePos + Vector3.up * 10.8f + r * 2.2f, new Vector3(0.35f, 0.14f, 0.8f), Quaternion.LookRotation(r));
                    }
                }
                // km posts every 1 km and signs
                if (i % 200 == 0 && i > 0) KmPost(root, signWhite, i);
                if (i % 1000 == 100) DistanceSign(root, signs, poles, i);
                if (i % 1000 == 600) SpeedSign(root, signWhite, reds, poles, i);
            }

            // ---- terrain (SRTM cross-sections every 20 m, with cut / fill slopes)
            var rowsList = new List<int>();
            for (int i = a - (a % TerrainEvery); i <= b; i += TerrainEvery) rowsList.Add(Mathf.Clamp(i, a, b));
            if (rowsList[rowsList.Count - 1] != b) rowsList.Add(b);
            var cols = new List<float>();
            for (int k = nOff - 1; k >= 0; k--) cols.Add(-terrOff[k]);
            cols.Add(-1f); cols.Add(0f); cols.Add(1f); // replaced per row by -edge, 0, +edge
            for (int k = 0; k < nOff; k++) cols.Add(terrOff[k]);
            int nc = cols.Count + 2; // + skirts
            int v0 = terrain.v.Count;
            foreach (int i in rowsList)
            {
                float e = EdgeOuter(i);
                float k = curvature[i];
                float innerLimit = Mathf.Abs(k) > 1e-5f ? 0.85f / Mathf.Abs(k) : 1e9f;
                int innerSide = k > 0 ? 1 : -1;
                var row = new List<Vector3>(nc);
                for (int ci = 0; ci < cols.Count; ci++)
                {
                    float o = cols[ci];
                    if (ci == nOff) o = -(e + 0.3f); else if (ci == nOff + 1) o = 0f; else if (ci == nOff + 2) o = e + 0.3f;
                    else if (Mathf.Abs(o) < e + 1.5f) o = Mathf.Sign(o) * (e + 1.5f + (Mathf.Abs(o) * 0.01f));
                    if (Mathf.Sign(o) == innerSide && Mathf.Abs(o) > innerLimit) o = Mathf.Sign(o) * Mathf.Max(innerLimit, e + 2f);
                    float h = (ci == nOff + 1) ? (bridge[i] ? TerrainDem(i / TerrainEvery, 0f) : (float)Y[i] - 0.3f) : GroundHeight(i, o);
                    row.Add(L(i, default, o, h - (float)Y[i]));
                }
                // skirts below the outer ends
                row.Insert(0, row[0] + Vector3.down * 60f); row.Add(row[row.Count - 1] + Vector3.down * 60f);
                foreach (var p in row)
                {
                    terrain.v.Add(p);
                    double wx = X[anchor_i] + p.x, wz = Z[anchor_i] + p.z;
                    terrain.uv.Add(new Vector2((float)(wx % 2000.0) / 9f, (float)(wz % 2000.0) / 9f));
                }
            }
            for (int r = 0; r < rowsList.Count - 1; r++)
                for (int ci = 0; ci < nc - 1; ci++)
                {
                    if (ci + 1 == nOff + 2 || ci + 1 == nOff + 3) { /* under-road strip kept (needed under bridges) */ }
                    int i0 = v0 + r * nc + ci, i1 = i0 + 1, i2 = i0 + nc, i3 = i2 + 1;
                    terrain.t.Add(i0); terrain.t.Add(i2); terrain.t.Add(i1);
                    terrain.t.Add(i1); terrain.t.Add(i2); terrain.t.Add(i3);
                }

            // ---- towns: houses near the road around each town
            if (data.towns != null)
                foreach (var town in data.towns)
                {
                    int ti = Mathf.RoundToInt(town.km * 1000f / Step);
                    if (ti < a - 300 || ti > b + 300) continue;
                    for (int q = 0; q < 26; q++)
                    {
                        int i = a + rnd.Next(0, b - a);
                        if (Mathf.Abs(i - ti) > 300) continue;
                        int side = rnd.Next(0, 2) == 0 ? -1 : 1;
                        float off = side * (EdgeOuter(i) + 60f + (float)rnd.NextDouble() * 320f);
                        float gh = GroundHeight(i, off);
                        float w = 10f + (float)rnd.NextDouble() * 16f, d = 10f + (float)rnd.NextDouble() * 16f, h = 6f + (float)rnd.NextDouble() * 15f;
                        var p = L(i, default, off, gh - (float)Y[i] + h * 0.5f - 0.5f);
                        (q % 2 == 0 ? houses : houses2).Box(p, new Vector3(w, h, d), Quaternion.LookRotation(Tangent(i)) * Quaternion.Euler(0, rnd.Next(-8, 8), 0));
                    }
                }

            // end of the route: barrier
            if (b >= Count - 1)
                for (int side = -1; side <= 1; side += 2)
                    reds.Box(L(Count - 3, default, side * (CarriageInner(Count - 3) + carriageW[Count - 3] * 0.5f), 0.6f), new Vector3(carriageW[Count - 3] + 3f, 1.2f, 0.6f), Quaternion.LookRotation(Tangent(Count - 3)));

            asphalt.Make("Asphalt", root.transform, asphaltMat, true);
            median.Make("Median", root.transform, medianMat, true);
            marks.Make("Markings", root.transform, markingMat, false, false);
            yellow.Make("MarkingsYellow", root.transform, yellowMat, false, false);
            concrete.Make("Concrete", root.transform, concreteMat, true);
            steel.Make("Guardrails", root.transform, steelMat, true);
            poles.Make("Poles", root.transform, poleMat, false);
            lamps.Make("Lamps", root.transform, lampMat, false, false);
            terrain.Make("Terrain", root.transform, sandMat, true);
            signs.Make("Signs", root.transform, signMat, false);
            signWhite.Make("SignsWhite", root.transform, signWhiteMat, false);
            reds.Make("Red", root.transform, redMat, true);
            houses.Make("Houses", root.transform, buildingMat, false);
            houses2.Make("Houses2", root.transform, buildingMat2, false);
            return root;
        }

        void Line(MB mb, int i, int j, int side, float oA, float oB, float w)
        {
            float h = 0.025f;
            Vector3 a0 = L(i, default, side * (oA - w * 0.5f), h), a1 = L(i, default, side * (oA + w * 0.5f), h);
            Vector3 b0 = L(j, default, side * (oB - w * 0.5f), h), b1 = L(j, default, side * (oB + w * 0.5f), h);
            if (side > 0) mb.Quad(a0, b0, b1, a1, Vector2.zero, Vector2.up, Vector2.one, Vector2.right);
            else mb.Quad(a1, b1, b0, a0, Vector2.zero, Vector2.up, Vector2.one, Vector2.right);
        }

        void Barrier(MB mb, int i, int j)
        {
            // New-Jersey profile: (offset, height)
            Vector2[] prof = { new Vector2(-0.3f, 0f), new Vector2(-0.22f, 0.08f), new Vector2(-0.1f, 0.33f), new Vector2(-0.075f, 0.82f), new Vector2(0.075f, 0.82f), new Vector2(0.1f, 0.33f), new Vector2(0.22f, 0.08f), new Vector2(0.3f, 0f) };
            for (int k = 0; k < prof.Length - 1; k++)
            {
                Vector3 a0 = L(i, default, prof[k].x, prof[k].y), a1 = L(i, default, prof[k + 1].x, prof[k + 1].y);
                Vector3 b0 = L(j, default, prof[k].x, prof[k].y), b1 = L(j, default, prof[k + 1].x, prof[k + 1].y);
                mb.Quad(a0, a1, b1, b0, Vector2.zero, Vector2.right, Vector2.one, Vector2.up);
            }
        }

        void Kerb(MB mb, int i, int j, float oA, float oB, int side)
        {
            float w = 0.25f * side;
            Vector3 a0 = L(i, default, oA, -0.15f), a1 = L(i, default, oA, 0.18f), a2 = L(i, default, oA - w, 0.18f), a3 = L(i, default, oA - w, -0.15f);
            Vector3 b0 = L(j, default, oB, -0.15f), b1 = L(j, default, oB, 0.18f), b2 = L(j, default, oB - w, 0.18f), b3 = L(j, default, oB - w, -0.15f);
            if (side > 0) { mb.Quad(a1, b1, b2, a2, Vector2.zero, Vector2.up, Vector2.one, Vector2.right); mb.Quad(a2, b2, b3, a3, Vector2.zero, Vector2.up, Vector2.one, Vector2.right); mb.Quad(a0, b0, b1, a1, Vector2.zero, Vector2.up, Vector2.one, Vector2.right); }
            else { mb.Quad(a2, b2, b1, a1, Vector2.zero, Vector2.up, Vector2.one, Vector2.right); mb.Quad(a3, b3, b2, a2, Vector2.zero, Vector2.up, Vector2.one, Vector2.right); mb.Quad(a1, b1, b0, a0, Vector2.zero, Vector2.up, Vector2.one, Vector2.right); }
        }

        void Wall(MB mb, int i, int j, float oA, float oB, float w, float h)
        {
            float s = Mathf.Sign(oA);
            Vector3 a0 = L(i, default, oA - s * w * 0.5f, 0f), a1 = L(i, default, oA - s * w * 0.5f, h), a2 = L(i, default, oA + s * w * 0.5f, h), a3 = L(i, default, oA + s * w * 0.5f, -1.2f);
            Vector3 b0 = L(j, default, oB - s * w * 0.5f, 0f), b1 = L(j, default, oB - s * w * 0.5f, h), b2 = L(j, default, oB + s * w * 0.5f, h), b3 = L(j, default, oB + s * w * 0.5f, -1.2f);
            if (s > 0) { mb.Quad(a0, b0, b1, a1, Vector2.zero, Vector2.up, Vector2.one, Vector2.right); mb.Quad(a1, b1, b2, a2, Vector2.zero, Vector2.up, Vector2.one, Vector2.right); mb.Quad(a2, b2, b3, a3, Vector2.zero, Vector2.up, Vector2.one, Vector2.right); }
            else { mb.Quad(a1, b1, b0, a0, Vector2.zero, Vector2.up, Vector2.one, Vector2.right); mb.Quad(a2, b2, b1, a1, Vector2.zero, Vector2.up, Vector2.one, Vector2.right); mb.Quad(a3, b3, b2, a2, Vector2.zero, Vector2.up, Vector2.one, Vector2.right); }
        }

        void Guardrail(MB mb, int i, int j, float oA, float oB, bool post)
        {
            float s = Mathf.Sign(oA);
            // W-beam (both faces) + posts every 10 m
            Vector3 a0 = L(i, default, oA, 0.45f), a1 = L(i, default, oA, 0.8f), b0 = L(j, default, oB, 0.45f), b1 = L(j, default, oB, 0.8f);
            mb.Quad(a0, b0, b1, a1, Vector2.zero, Vector2.up, Vector2.one, Vector2.right);
            mb.Quad(a1, b1, b0, a0, Vector2.zero, Vector2.up, Vector2.one, Vector2.right);
            if (post) mb.Box(L(i, default, oA + s * 0.12f, 0.35f), new Vector3(0.1f, 0.9f, 0.1f), Quaternion.LookRotation(Tangent(i)));
        }

        TextMesh Text(Transform parent, Vector3 localPos, Quaternion rot, string text, float size, Color col, TextAnchor anchor = TextAnchor.MiddleCenter)
        {
            var go = new GameObject("Text");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos; go.transform.localRotation = rot;
            var tm = go.AddComponent<TextMesh>();
            tm.font = font; tm.text = text; tm.fontSize = 64; tm.characterSize = size / 64f * 10f * 0.1f; tm.anchor = anchor; tm.alignment = TextAlignment.Left; tm.color = col;
            go.GetComponent<MeshRenderer>().sharedMaterial = font.material;
            return tm;
        }

        void KmPost(GameObject root, MB white, int i)
        {
            float off = EdgeOuter(i) + 1.2f;
            var rot = Quaternion.LookRotation(Tangent(i));
            Vector3 p = L(i, default, off, GroundHeight(i, off) - (float)Y[i]);
            white.Box(p + Vector3.up * 0.6f, new Vector3(0.45f, 1.2f, 0.12f), rot);
            int km = Mathf.RoundToInt(KmAt(i));
            // facing the oncoming driver
            Text(root.transform, p + Vector3.up * 0.75f - Tangent(i) * 0.07f, rot, km.ToString(), 0.3f, Color.black);
        }

        void DistanceSign(GameObject root, MB green, MB poles, int i)
        {
            float off = EdgeOuter(i) + 2.5f;
            var rot = Quaternion.LookRotation(Tangent(i));
            Vector3 p = L(i, default, off, GroundHeight(i, off) - (float)Y[i]);
            float w = 5.2f, h = 2.6f;
            poles.Box(p + Right(i) * (-w * 0.35f) + Vector3.up * 3f, new Vector3(0.2f, 6f, 0.2f), rot);
            poles.Box(p + Right(i) * (w * 0.35f) + Vector3.up * 3f, new Vector3(0.2f, 6f, 0.2f), rot);
            Vector3 c = p + Vector3.up * (3.4f + h * 0.5f);
            green.Box(c, new Vector3(w, h, 0.1f), rot);
            float kmHere = KmAt(i);
            var lines = new List<string>();
            if (data.towns != null)
                foreach (var t in data.towns)
                    if (t.km > kmHere + 0.5f && lines.Count < 2 && !lines.Exists(s => s.StartsWith(t.name))) lines.Add($"{t.name}  {Mathf.RoundToInt(t.km - kmHere)} km");
            lines.Add($"{data.dest}  {Mathf.RoundToInt(data.lengthKm - kmHere)} km");
            Text(root.transform, c - Tangent(i) * 0.07f + Right(i) * (-w * 0.45f) + Vector3.up * (h * 0.38f), rot, string.Join("\n", lines), 0.42f, Color.white, TextAnchor.UpperLeft);
        }

        void SpeedSign(GameObject root, MB white, MB red, MB poles, int i)
        {
            float off = EdgeOuter(i) + 1.8f;
            var rot = Quaternion.LookRotation(Tangent(i));
            Vector3 p = L(i, default, off, GroundHeight(i, off) - (float)Y[i]);
            poles.Box(p + Vector3.up * 1.4f, new Vector3(0.1f, 2.8f, 0.1f), rot);
            Vector3 c = p + Vector3.up * 3.1f;
            red.Box(c, new Vector3(0.9f, 0.9f, 0.05f), rot * Quaternion.Euler(0, 0, 45f));
            white.Box(c - Tangent(i) * 0.03f, new Vector3(0.68f, 0.68f, 0.05f), rot * Quaternion.Euler(0, 0, 45f));
            Text(root.transform, c - Tangent(i) * 0.07f, rot, "80", 0.5f, Color.black);
            Text(root.transform, c - Tangent(i) * 0.07f + Vector3.down * 0.75f, rot, "TRUCKS", 0.18f, Color.white);
        }

        // ------------------------------------------------------------------ queries

        /// <summary>Closest route sample to a world position, searched around a hint (or the whole route when hint &lt; 0).</summary>
        public int ClosestIndex(Vector3 pos, int hint)
        {
            int best = 0; float bestD = float.MaxValue;
            if (hint < 0)
            {
                for (int i = 0; i < Count; i += 4) { float d = (P(i) - pos).sqrMagnitude; if (d < bestD) { bestD = d; best = i; } }
                hint = best; bestD = float.MaxValue;
            }
            int lo = Mathf.Max(0, hint - 80), hi = Mathf.Min(Count - 1, hint + 80);
            for (int i = lo; i <= hi; i++) { float d = (P(i) - pos).sqrMagnitude; if (d < bestD) { bestD = d; best = i; } }
            // stale hint after a teleport (reset / route change): fall back to a full search once
            if (bestD > 250f * 250f && (lo > 0 || hi < Count - 1)) return ClosestIndex(pos, -1);
            return best;
        }
    }
}
