using System.Collections.Generic;
using UnityEngine;

namespace TruckSim
{
    /// <summary>
    /// Working truck lights: low / high beam headlights (real spot lights), red tail / position lights,
    /// brighter brake lights when braking, white reverse lights in reverse, and a dashboard glow.
    /// Lamp lenses found on the model glow (unlit material swap); glow sprites are added at the lamp positions.
    /// </summary>
    public class TruckLights : MonoBehaviour
    {
        public enum Mode { Off, Low, High }
        public Mode mode = Mode.Off;
        public TruckController truck;

        readonly List<Light> beams = new List<Light>();
        readonly List<Renderer> headGlow = new List<Renderer>(), tailGlow = new List<Renderer>(), reverseGlow = new List<Renderer>();
        readonly Dictionary<Renderer, Material[]> originalMats = new Dictionary<Renderer, Material[]>();
        TruckModelRig rig;
        Light dashLight;
        Material headOn, tailOn, brakeOn, reverseOn, glowHead, glowTail, glowBrake, glowReverse, glowOff;
        static Texture2D glowTex;
        bool lastBrake, lastReverse, lastNight; Mode lastMode = (Mode)(-1);
        /// <summary>Set by GameManager (day / night). At night the dashboard glow is brighter.</summary>
        public static bool Night;
        public bool IsOn => mode != Mode.Off;
        public bool BrakeLit { get; private set; }
        public bool ReverseLit { get; private set; }
        public float DashIntensity => dashLight ? dashLight.intensity : 0f;
        public int BeamCount => beams.Count;
        public string State => $"mode={mode} beams={(beams.Count > 0 && beams[0].enabled ? "on" : "off")} range={(beams.Count > 0 ? beams[0].range : 0):0} brake={BrakeLit} reverse={ReverseLit} dash={DashIntensity:0.00} lenses(head={(rig != null ? rig.headLightMeshes.Count : 0)},tail={(rig != null ? rig.tailLightMeshes.Count : 0)})";

        public static Texture2D GlowTexture()
        {
            if (glowTex) return glowTex;
            int n = 64; glowTex = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            for (int y = 0; y < n; y++) for (int x = 0; x < n; x++)
            {
                float d = new Vector2(x - n / 2f + 0.5f, y - n / 2f + 0.5f).magnitude / (n / 2f);
                float a = Mathf.Clamp01(1f - d); a = a * a * (3f - 2f * a);
                glowTex.SetPixel(x, y, new Color(1, 1, 1, a));
            }
            glowTex.Apply();
            return glowTex;
        }

        static Material Unlit(Color c)
        {
            var sh = Shader.Find("Unlit/Color") ?? Shader.Find("Standard");
            var m = new Material(sh) { color = c };
            return m;
        }

        static Material Glow(Color c)
        {
            var sh = Shader.Find("Legacy Shaders/Particles/Additive") ?? Shader.Find("Particles/Standard Unlit");
            var m = new Material(sh) { mainTexture = GlowTexture() };
            if (m.HasProperty("_TintColor")) m.SetColor("_TintColor", c); else m.color = c;
            m.renderQueue = 3100;
            return m;
        }

        public void Init(TruckController t, TruckModelRig r)
        {
            truck = t; rig = r;
            headOn = Unlit(new Color(1f, 0.97f, 0.88f));
            tailOn = Unlit(new Color(0.55f, 0.02f, 0.02f));
            brakeOn = Unlit(new Color(1f, 0.08f, 0.05f));
            reverseOn = Unlit(new Color(1f, 1f, 1f));
            glowHead = Glow(new Color(1f, 0.95f, 0.8f, 0.55f));
            glowTail = Glow(new Color(0.8f, 0.05f, 0.03f, 0.35f));
            glowBrake = Glow(new Color(1f, 0.1f, 0.05f, 0.85f));
            glowReverse = Glow(new Color(1f, 1f, 1f, 0.6f));
            glowOff = Glow(new Color(0, 0, 0, 0));

            Bounds b = r.localBounds;
            foreach (var m in r.headLightMeshes) originalMats[m] = m.sharedMaterials;
            foreach (var m in r.tailLightMeshes) originalMats[m] = m.sharedMaterials;

            // lamp positions: front corners at bumper height (cab-over) or at the grille ends (bonneted trucks)
            float frontZ = b.max.z - 0.05f, rearZ = b.min.z + 0.15f;
            float headY = Mathf.Clamp(b.min.y + 0.95f, 0.8f, 1.3f);
            float halfW = b.extents.x - 0.35f;
            if (r.headLightMeshes.Count >= 2)
            {
                // use the real lamp lenses' positions
                Vector3 l = Vector3.zero, rr = Vector3.zero; int nl = 0, nr = 0;
                foreach (var m in r.headLightMeshes)
                {
                    var c = transform.InverseTransformPoint(m.bounds.center);
                    if (c.x < 0) { l += c; nl++; } else { rr += c; nr++; }
                }
                if (nl > 0 && nr > 0) { l /= nl; rr /= nr; headY = (l.y + rr.y) * 0.5f; halfW = (rr.x - l.x) * 0.5f; frontZ = Mathf.Max(l.z, rr.z) + 0.05f; }
            }
            for (int s = -1; s <= 1; s += 2)
            {
                var go = new GameObject(s < 0 ? "HeadlightL" : "HeadlightR");
                go.transform.SetParent(transform, false);
                go.transform.localPosition = new Vector3(s * halfW, headY, frontZ + 0.1f);
                go.transform.localRotation = Quaternion.Euler(4f, s * 2f, 0);
                var li = go.AddComponent<Light>();
                li.type = LightType.Spot; li.color = new Color(1f, 0.95f, 0.85f);
                li.shadows = LightShadows.None; li.renderMode = LightRenderMode.ForcePixel;
                beams.Add(li);
                headGlow.Add(Quad(go.transform, Vector3.forward * 0.02f, 0.55f, Quaternion.identity));
            }
            for (int s = -1; s <= 1; s += 2)
            {
                var p = new Vector3(s * (b.extents.x - 0.2f), 0.85f, rearZ);
                tailGlow.Add(Quad(transform, p, 0.45f, Quaternion.Euler(0, 180f, 0)));
                reverseGlow.Add(Quad(transform, p + new Vector3(-s * 0.25f, 0f, -0.01f), 0.35f, Quaternion.Euler(0, 180f, 0)));
            }
            dashLight = new GameObject("DashGlow").AddComponent<Light>();
            dashLight.transform.SetParent(transform, false);
            // dashboard glow just in front of the steering wheel (wheel position converted to truck space: the wheel's
            // own localPosition is relative to its parent inside the model hierarchy)
            Vector3 wheelLocal = r.steeringWheel ? transform.InverseTransformPoint(r.steeringWheel.position) : r.eyeLocal + new Vector3(0f, -0.35f, 0.55f);
            dashLight.transform.localPosition = wheelLocal + new Vector3(0.2f, 0.12f, 0.22f);
            dashLight.type = LightType.Point; dashLight.range = 1.4f; dashLight.intensity = 0f; dashLight.color = new Color(0.5f, 0.75f, 1f);
            dashLight.shadows = LightShadows.None;
            Apply(true);
        }

        Renderer Quad(Transform parent, Vector3 localPos, float size, Quaternion rot)
        {
            var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
            Destroy(q.GetComponent<Collider>());
            q.name = "LampGlow";
            q.transform.SetParent(parent, false);
            q.transform.localPosition = localPos; q.transform.localRotation = rot; q.transform.localScale = Vector3.one * size;
            var rr = q.GetComponent<Renderer>();
            rr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; rr.receiveShadows = false;
            rr.sharedMaterial = glowOff;
            return rr;
        }

        public void Cycle() { SetMode(mode == Mode.Off ? Mode.Low : mode == Mode.Low ? Mode.High : Mode.Off); }
        public void ToggleHigh() { SetMode(mode == Mode.High ? Mode.Low : Mode.High); }
        public void SetMode(Mode m) { mode = m; Apply(true); }

        void LateUpdate()
        {
            if (truck == null) return;
            bool brake = truck.Braking, rev = truck.Reversing;
            if (brake != lastBrake || rev != lastReverse || mode != lastMode || Night != lastNight) Apply(false);
        }

        void Apply(bool force)
        {
            bool brake = truck != null && truck.Braking, rev = truck != null && truck.Reversing;
            lastBrake = brake; lastReverse = rev; lastMode = mode; lastNight = Night;
            BrakeLit = brake; ReverseLit = rev;
            bool on = mode != Mode.Off;
            foreach (var l in beams)
            {
                l.enabled = on;
                l.range = mode == Mode.High ? 160f : 70f;
                l.spotAngle = mode == Mode.High ? 38f : 62f;
                l.intensity = mode == Mode.High ? 4.2f : 2.8f;
                l.transform.localRotation = Quaternion.Euler(mode == Mode.High ? 1.5f : 5f, l.transform.localPosition.x < 0 ? -2f : 2f, 0);
            }
            foreach (var g in headGlow) g.sharedMaterial = on ? glowHead : glowOff;
            foreach (var g in tailGlow) g.sharedMaterial = brake ? glowBrake : on ? glowTail : glowOff;
            foreach (var g in reverseGlow) g.sharedMaterial = rev ? glowReverse : glowOff;
            // instrument / dashboard illumination comes on with the lights (brighter at night)
            if (dashLight) { dashLight.intensity = on ? (Night ? 0.9f : 0.35f) : 0f; dashLight.enabled = on; }
            if (rig == null) return;
            foreach (var m in rig.headLightMeshes) SetLens(m, on ? headOn : null);
            foreach (var m in rig.tailLightMeshes) SetLens(m, brake ? brakeOn : on ? tailOn : null);
        }

        void SetLens(Renderer r, Material m)
        {
            if (r == null || !originalMats.TryGetValue(r, out var orig)) return;
            if (m == null) { r.sharedMaterials = orig; return; }
            var mats = new Material[orig.Length];
            for (int i = 0; i < mats.Length; i++) mats[i] = m;
            r.sharedMaterials = mats;
        }
    }
}
