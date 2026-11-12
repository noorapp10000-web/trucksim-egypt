using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace TruckSim
{
    /// <summary>Bootstraps the whole game: world, highway, truck garage, HUD and touch controls.</summary>
    public class GameManager : MonoBehaviour
    {
        public static GameManager Instance;
        public HighwayBuilder highway;
        public TruckController truck;
        public TruckCameraRig camRig;
        public int truckIndex;
        public bool inGarage = true;

        GameObject garagePanel, drivePanel;
        Text garageName, garageSpecs, garageTier, speedText, gearText, rpmText, truckNameText, camText, gearBtnText;
        Image rpmFill;
        MobileControls controls;
        Light sun, cabinLight;
        const int SpawnIndex = 10;
        int roadHint = SpawnIndex;
        // route picker / loading overlay
        GameObject routePanel, loadingPanel;
        Text loadingText, routeTitle, lightsBtnText, nightBtnText, routeBtnText;
        RectTransform routeListContent;
        readonly List<Image> regionTabs = new List<Image>();
        int shownRegion = -1;
        bool busy;                      // a truck / route switch is running: ignore further taps (no queued work = no lag)
        bool night;
        public string CurrentRouteId => highway != null && highway.data != null ? highway.data.id : "";
        public bool Busy => busy;

        void Awake()
        {
            Instance = this;
            Application.targetFrameRate = 60;
            Time.fixedDeltaTime = 0.01f;
            QualitySettings.shadowDistance = 160f;
            QualitySettings.shadowCascades = 2;
            Screen.sleepTimeout = SleepTimeout.NeverSleep;
        }

        void Start()
        {
            BuildEnvironment();
            BuildUI();
            SpawnTruck(0);
            EnterGarage();
            Application.logMessageReceived += CountErrors;
            if (HasArg("-autotest")) StartCoroutine(AutoTest());
        }

        static bool HasArg(string a) { foreach (var s in System.Environment.GetCommandLineArgs()) if (s == a) return true; return false; }
        static string ArgValue(string a, string def) { var args = System.Environment.GetCommandLineArgs(); for (int i = 0; i < args.Length - 1; i++) if (args[i] == a) return args[i + 1]; return def; }

        void BuildEnvironment()
        {
            var sunGo = new GameObject("Sun");
            sun = sunGo.AddComponent<Light>();
            sun.type = LightType.Directional; sun.intensity = 1.2f; sun.color = new Color(1f, 0.96f, 0.88f);
            sun.shadows = LightShadows.Soft; sun.shadowStrength = 0.8f;
            sunGo.transform.rotation = Quaternion.Euler(42f, -35f, 0f);
            var skyShader = Shader.Find("Skybox/Procedural");
            if (skyShader)
            {
                var sky = new Material(skyShader);
                sky.SetFloat("_SunSize", 0.04f); sky.SetFloat("_AtmosphereThickness", 0.9f); sky.SetFloat("_Exposure", 1.2f);
                sky.SetColor("_GroundColor", new Color(0.37f, 0.36f, 0.33f));
                RenderSettings.skybox = sky;
            }
            RenderSettings.sun = sun;
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.62f, 0.7f, 0.82f);
            RenderSettings.ambientEquatorColor = new Color(0.55f, 0.57f, 0.55f);
            RenderSettings.ambientGroundColor = new Color(0.28f, 0.27f, 0.24f);
            RenderSettings.fog = true; RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogStartDistance = 350f; RenderSettings.fogEndDistance = 4200f;
            RenderSettings.fogColor = new Color(0.72f, 0.79f, 0.86f);
            DynamicGI.UpdateEnvironment();

            highway = new GameObject("Highway").AddComponent<HighwayBuilder>();
            highway.Build();

            var camGo = new GameObject("MainCamera");
            camGo.tag = "MainCamera";
            var cam = camGo.AddComponent<Camera>();
            cam.farClipPlane = 6000f;
            cam.clearFlags = CameraClearFlags.Skybox;
            camGo.AddComponent<AudioListener>();
            camRig = camGo.AddComponent<TruckCameraRig>();
        }

        // ---------------- trucks ----------------
        public void SpawnTruck(int index)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            truckIndex = (index + TruckDatabase.All.Length) % TruckDatabase.All.Length;
            var spec = TruckDatabase.All[truckIndex];
            if (truck) { truck.gameObject.SetActive(false); Destroy(truck.gameObject); truck = null; }

            var prefab = Resources.Load<GameObject>("Trucks/" + spec.id);
            var root = new GameObject("Truck_" + spec.id);
            root.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            var rb = root.AddComponent<Rigidbody>();
            rb.isKinematic = true;
            TruckModelRig rig;
            if (prefab) rig = TruckModelRig.Build(root.transform, prefab, spec);
            else
            {
                Debug.LogError("Missing truck model " + spec.id);
                var placeholder = GameObject.CreatePrimitive(PrimitiveType.Cube);
                placeholder.transform.localScale = new Vector3(2.5f, 3.5f, spec.lengthMeters);
                rig = TruckModelRig.Build(root.transform, placeholder, spec);
                Destroy(placeholder);
            }
            truck = root.AddComponent<TruckController>();
            truck.Init(spec, rig);

            var cl = new GameObject("CabinLight").AddComponent<Light>();
            cl.transform.SetParent(root.transform, false);
            cl.transform.localPosition = rig.eyeLocal + new Vector3(0.45f, 0.3f, 0.3f);
            cl.type = LightType.Point; cl.range = 3.2f; cl.intensity = 0.7f; cl.color = new Color(1f, 0.95f, 0.85f); cl.shadows = LightShadows.None;
            cabinLight = cl;

            var audioGo = new GameObject("EngineAudio");
            audioGo.transform.SetParent(root.transform, false);
            audioGo.AddComponent<AudioSource>();
            audioGo.AddComponent<EngineAudio>().truck = truck;

            Vector3 pos = highway.LanePosition(SpawnIndex, 0);
            Quaternion rot = Quaternion.LookRotation(highway.tangents[SpawnIndex]);
            root.transform.SetPositionAndRotation(pos + Vector3.up * 0.3f, rot);
            roadHint = SpawnIndex;
            rb.isKinematic = inGarage;
            camRig.target = truck;
            camRig.Snap();
            truck.lights.SetMode(night ? TruckLights.Mode.Low : TruckLights.Mode.Off);
            UpdateLightsLabel();
            Debug.Log($"[Truck] Spawned {spec.displayName} in {sw.ElapsedMilliseconds} ms: size={rig.localBounds.size} wheelsDetected={rig.wheelsDetected} wheels={rig.wheels.Count} steeringWheel={(rig.steeringWheel != null)} eye={rig.eyeLocal} | {rig.report}");
            RefreshGarageText();
        }

        public void EnterGarage()
        {
            inGarage = true;
            garagePanel.SetActive(true); drivePanel.SetActive(false);
            if (routePanel) routePanel.SetActive(false);
            // back to the route start: re-centre the world there (floating origin)
            if (highway.ox != 0 || highway.oz != 0) { highway.SetOrigin(0); highway.BuildAround(SpawnIndex); }
            if (truck)
            {
                truck.GetComponent<Rigidbody>().isKinematic = true;
                Vector3 pos = highway.LanePosition(SpawnIndex, 0);
                truck.transform.SetPositionAndRotation(pos + Vector3.up * 0.25f, Quaternion.LookRotation(highway.tangents[SpawnIndex]));
            }
            camRig.SetMode(CamMode.Orbit);
            camRig.SetOrbitAngles(215f, 10f);
            RefreshGarageText();
        }

        public void StartDriving()
        {
            if (busy || truck == null) return;
            inGarage = false;
            garagePanel.SetActive(false); drivePanel.SetActive(true);
            if (routePanel) routePanel.SetActive(false);
            var rb = truck.GetComponent<Rigidbody>();
            rb.isKinematic = false;
            truck.ResetOnRoad(highway.LanePosition(SpawnIndex, 0), Quaternion.LookRotation(highway.tangents[SpawnIndex]));
            roadHint = SpawnIndex;
            camRig.SetMode(CamMode.Interior);
            UpdateCamLabel();
            UpdateLightsLabel();
        }

        public void ResetTruck()
        {
            if (!truck || inGarage) return;
            int best = highway.ClosestIndex(truck.transform.position, roadHint);
            truck.ResetOnRoad(highway.LanePosition(best, 0), Quaternion.LookRotation(highway.tangents[best]));
        }

        // ---------------- truck / route switching (one at a time, with a loading overlay) ----------------
        public void RequestTruck(int index)
        {
            if (busy) return;
            StartCoroutine(SwitchTruckRoutine(index));
        }

        IEnumerator SwitchTruckRoutine(int index)
        {
            busy = true;
            ShowLoading("Loading truck...");
            yield return null; yield return null;          // let the overlay render before the heavy work
            SpawnTruck(index);
            EnterGarage();
            HideLoading();
            busy = false;
        }

        public void RequestRoute(string id)
        {
            if (busy || id == CurrentRouteId) { if (routePanel) routePanel.SetActive(false); return; }
            StartCoroutine(SwitchRouteRoutine(id));
        }

        void SwitchRoute(int delta)
        {
            var ids = RoadCatalog.Ids;
            if (ids.Length == 0) return;
            int i = Mathf.Max(0, RoadCatalog.IndexOf(CurrentRouteId));
            RequestRoute(ids[(i + delta + ids.Length) % ids.Length]);
        }

        IEnumerator SwitchRouteRoutine(string id)
        {
            busy = true;
            if (routePanel) routePanel.SetActive(false);
            var e = RoadCatalog.Get(id);
            ShowLoading("Loading road...\n" + (e != null ? e.name : id));
            yield return null; yield return null;
            LoadRouteNow(id);
            HideLoading();
            busy = false;
        }

        /// <summary>Loads a road synchronously and puts the truck at its start (used by the UI coroutine and the tests).</summary>
        public bool LoadRouteNow(string id)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            if (!highway.Load(id)) { Debug.LogError("[Route] could not load " + id); return false; }
            highway.BuildAround(0);
            if (truck)
            {
                var rb = truck.GetComponent<Rigidbody>();
                Vector3 pos = highway.LanePosition(SpawnIndex, 0);
                Quaternion rot = Quaternion.LookRotation(highway.tangents[SpawnIndex]);
                if (inGarage) truck.transform.SetPositionAndRotation(pos + Vector3.up * 0.25f, rot);
                else truck.ResetOnRoad(pos, rot);
                roadHint = SpawnIndex;
                camRig.Snap();
            }
            RefreshGarageText();
            if (routeBtnText) routeBtnText.text = "ROUTE: " + Short(highway.data.name, 34);
            Debug.Log($"[Route] {id} ready in {sw.ElapsedMilliseconds} ms");
            return true;
        }

        static string Short(string s, int n) => string.IsNullOrEmpty(s) || s.Length <= n ? s : s.Substring(0, n - 1) + "…";

        void ShowLoading(string msg) { if (loadingPanel) { loadingText.text = msg; loadingPanel.SetActive(true); loadingPanel.transform.SetAsLastSibling(); } }
        void HideLoading() { if (loadingPanel) loadingPanel.SetActive(false); }

        // ---------------- lights / day-night ----------------
        public void CycleLights() { if (truck && truck.lights) { truck.lights.Cycle(); UpdateLightsLabel(); } }

        void UpdateLightsLabel()
        {
            if (lightsBtnText == null || truck == null || truck.lights == null) return;
            var m = truck.lights.mode;
            lightsBtnText.text = m == TruckLights.Mode.Off ? "LIGHTS OFF" : m == TruckLights.Mode.Low ? "LOW BEAM" : "HIGH BEAM";
        }

        public void SetNight(bool on)
        {
            night = on;
            TruckLights.Night = on;
            sun.intensity = on ? 0.03f : 1.2f;
            sun.shadows = on ? LightShadows.None : LightShadows.Soft;
            RenderSettings.ambientSkyColor = on ? new Color(0.05f, 0.06f, 0.1f) : new Color(0.62f, 0.7f, 0.82f);
            RenderSettings.ambientEquatorColor = on ? new Color(0.04f, 0.045f, 0.06f) : new Color(0.55f, 0.57f, 0.55f);
            RenderSettings.ambientGroundColor = on ? new Color(0.02f, 0.02f, 0.025f) : new Color(0.28f, 0.27f, 0.24f);
            RenderSettings.fogColor = on ? new Color(0.03f, 0.035f, 0.05f) : new Color(0.72f, 0.79f, 0.86f);
            if (RenderSettings.skybox && RenderSettings.skybox.HasProperty("_Exposure")) RenderSettings.skybox.SetFloat("_Exposure", on ? 0.05f : 1.2f);
            highway.SetNight(on);
            if (on && truck && truck.lights && truck.lights.mode == TruckLights.Mode.Off) truck.lights.SetMode(TruckLights.Mode.Low);
            if (nightBtnText) nightBtnText.text = on ? "NIGHT" : "DAY";
            UpdateLightsLabel();
        }

        // ---------------- UI ----------------
        void BuildUI()
        {
            if (!FindFirstObjectByType<EventSystem>())
                new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
            var cgo = new GameObject("Canvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            var canvas = cgo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = cgo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.6f;
            var safe = UIFactory.Rect("Safe", cgo.transform, Vector2.zero, Vector2.zero, Vector2.zero, Vector2.zero);
            safe.anchorMin = Vector2.zero; safe.anchorMax = Vector2.one; safe.offsetMin = Vector2.zero; safe.offsetMax = Vector2.zero;
            safe.gameObject.AddComponent<SafeArea>();
            BuildGarageUI(safe);
            BuildDriveUI(safe);
            BuildRoutePicker(safe);
            BuildLoading(safe);
        }

        static RectTransform FullPanel(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rt = go.GetComponent<RectTransform>();
            rt.SetParent(parent, false); rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.offsetMin = rt.offsetMax = Vector2.zero;
            return rt;
        }

        void BuildGarageUI(Transform parent)
        {
            var rt = FullPanel("Garage", parent);
            garagePanel = rt.gameObject;
            UIFactory.Text("Title", rt, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0, -24), new Vector2(1400, 80), "HIGHWAY TRUCK SIMULATOR - EGYPT", 54, TextAnchor.MiddleCenter, new Color(1f, 0.78f, 0.15f), FontStyle.Bold);

            var card = UIFactory.Image("SpecCard", rt, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(40, 10), new Vector2(660, 640), new Color(0.03f, 0.05f, 0.08f, 0.8f));
            garageTier = UIFactory.Text("Tier", card.transform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(34, -26), new Vector2(600, 40), "", 26, TextAnchor.UpperLeft, new Color(1f, 0.78f, 0.15f), FontStyle.Bold);
            garageName = UIFactory.Text("Name", card.transform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(34, -66), new Vector2(600, 110), "", 44, TextAnchor.UpperLeft, Color.white, FontStyle.Bold);
            garageSpecs = UIFactory.Text("Specs", card.transform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(34, -186), new Vector2(600, 440), "", 26, TextAnchor.UpperLeft, new Color(0.88f, 0.9f, 0.93f));
            garageSpecs.lineSpacing = 1.12f;

            UIFactory.Button("Prev", rt, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(-360, 50), new Vector2(150, 110), "<", () => RequestTruck(truckIndex - 1), 60);
            UIFactory.Button("Next", rt, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(360, 50), new Vector2(150, 110), ">", () => RequestTruck(truckIndex + 1), 60);
            UIFactory.Button("PrevRoute", rt, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(-470, 180), new Vector2(110, 80), "<", () => SwitchRoute(-1), 40);
            var rb = UIFactory.Button("Routes", rt, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0, 180), new Vector2(800, 80), "ROUTE", () => OpenRoutePicker(), 28);
            routeBtnText = rb.GetComponentInChildren<Text>();
            UIFactory.Button("NextRoute", rt, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(470, 180), new Vector2(110, 80), ">", () => SwitchRoute(1), 40);
            var drive = UIFactory.Button("Drive", rt, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0, 50), new Vector2(460, 110), "DRIVE", () => StartDriving(), 46);
            drive.GetComponent<Image>().color = new Color(0.95f, 0.62f, 0.05f, 0.95f);
            var nb = UIFactory.Button("DayNight", rt, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-40, -40), new Vector2(200, 80), "DAY", () => SetNight(!night), 28);
            nightBtnText = nb.GetComponentInChildren<Text>();
            UIFactory.Text("Hint", rt, new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(-40, 290), new Vector2(560, 60), "Drag to rotate the truck", 24, TextAnchor.LowerRight, new Color(1, 1, 1, 0.7f));
        }

        // ---- route picker: regions on the left, the region's roads on the right (scroll list)
        void BuildRoutePicker(Transform parent)
        {
            var rt = FullPanel("RoutePicker", parent);
            routePanel = rt.gameObject;
            var bg = rt.gameObject.AddComponent<Image>(); bg.color = new Color(0.01f, 0.02f, 0.04f, 0.93f);
            int total = RoadCatalog.All.Length; float km = 0; foreach (var e in RoadCatalog.All) km += e.km;
            routeTitle = UIFactory.Text("Title", rt, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0, -24), new Vector2(1600, 70), $"SELECT ROAD   -   {total} roads, {km:0} km of Egyptian highways", 40, TextAnchor.MiddleCenter, new Color(1f, 0.78f, 0.15f), FontStyle.Bold);
            UIFactory.Button("Close", rt, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-30, -24), new Vector2(170, 70), "CLOSE", () => routePanel.SetActive(false), 26);

            var regs = RoadCatalog.Regions;
            regionTabs.Clear();
            for (int r = 0; r < regs.Length; r++)
            {
                int rr = r;
                var b = UIFactory.Button("Region_" + regs[r].id, rt, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(30, -120 - r * 104), new Vector2(560, 94), "", () => ShowRegion(rr), 24);
                var label = b.GetComponentInChildren<Text>();
                float rkm = 0; foreach (var e in regs[r].routes) rkm += e.km;
                label.text = $"{regs[r].name}\n<size=19>{regs[r].routes.Length} roads · {rkm:0} km</size>";
                label.supportRichText = true;
                regionTabs.Add(b.GetComponent<Image>());
            }

            // scroll list
            var view = UIFactory.Image("List", rt, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(620, -120), new Vector2(1270, 900), new Color(1, 1, 1, 0.04f));
            var vrt = view.rectTransform;
            vrt.anchorMin = new Vector2(0f, 0f); vrt.anchorMax = new Vector2(1f, 1f);
            vrt.offsetMin = new Vector2(620, 40); vrt.offsetMax = new Vector2(-30, -120);
            view.gameObject.AddComponent<RectMask2D>();
            var sr = view.gameObject.AddComponent<ScrollRect>();
            sr.horizontal = false; sr.movementType = ScrollRect.MovementType.Clamped; sr.scrollSensitivity = 40f; sr.inertia = true;
            var content = new GameObject("Content", typeof(RectTransform)).GetComponent<RectTransform>();
            content.SetParent(vrt, false);
            content.anchorMin = new Vector2(0, 1); content.anchorMax = new Vector2(1, 1); content.pivot = new Vector2(0.5f, 1f);
            content.offsetMin = content.offsetMax = Vector2.zero;
            var vl = content.gameObject.AddComponent<VerticalLayoutGroup>();
            vl.spacing = 12; vl.padding = new RectOffset(14, 14, 14, 14); vl.childControlHeight = true; vl.childControlWidth = true; vl.childForceExpandHeight = false;
            content.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            sr.content = content; sr.viewport = vrt;
            routeListContent = content;
            routePanel.SetActive(false);
        }

        void OpenRoutePicker()
        {
            if (busy) return;
            routePanel.SetActive(true);
            routePanel.transform.SetAsLastSibling();
            ShowRegion(RoadCatalog.RegionOf(CurrentRouteId));
        }

        /// <summary>Fills the list with the roads of one region (only ~10 cards: cheap, no frame spike).</summary>
        public void ShowRegion(int r)
        {
            var regs = RoadCatalog.Regions;
            if (regs.Length == 0) return;
            r = Mathf.Clamp(r, 0, regs.Length - 1);
            for (int i = 0; i < regionTabs.Count; i++) regionTabs[i].color = i == r ? new Color(0.95f, 0.62f, 0.05f, 0.95f) : new Color(0.05f, 0.07f, 0.1f, 0.75f);
            if (r == shownRegion && routeListContent.childCount > 0) { RefreshRouteCards(); return; }
            shownRegion = r;
            for (int i = routeListContent.childCount - 1; i >= 0; i--) Destroy(routeListContent.GetChild(i).gameObject);
            foreach (var e in regs[r].routes)
            {
                var ent = e;
                var card = UIFactory.Image("Road_" + e.id, routeListContent, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), Vector2.zero, new Vector2(1200, 112), new Color(0.05f, 0.07f, 0.1f, 0.85f));
                card.gameObject.AddComponent<LayoutElement>().preferredHeight = 112;
                var btn = card.gameObject.AddComponent<Button>();
                btn.onClick.AddListener(() => RequestRoute(ent.id));
                var ol = card.gameObject.AddComponent<Outline>(); ol.effectDistance = new Vector2(2, -2);
                UIFactory.Text("Name", card.transform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(24, -10), new Vector2(1150, 50), e.name, 32, TextAnchor.UpperLeft, Color.white, FontStyle.Bold);
                UIFactory.Text("Info", card.transform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(24, -60), new Vector2(1150, 44),
                    $"{e.origin}  >  {e.dest}     {e.km:0} km     lanes {e.lanes}     elevation {e.elevation}     bridges {e.bridges}", 24, TextAnchor.UpperLeft, new Color(0.8f, 0.85f, 0.9f));
            }
            RefreshRouteCards();
            routeListContent.anchoredPosition = Vector2.zero;
        }

        void RefreshRouteCards()
        {
            string cur = CurrentRouteId;
            for (int i = 0; i < routeListContent.childCount; i++)
            {
                var c = routeListContent.GetChild(i);
                bool sel = c.name == "Road_" + cur;
                c.GetComponent<Outline>().effectColor = sel ? new Color(1f, 0.72f, 0.1f, 1f) : new Color(1f, 1f, 1f, 0.15f);
                c.GetComponent<Image>().color = sel ? new Color(0.2f, 0.14f, 0.02f, 0.95f) : new Color(0.05f, 0.07f, 0.1f, 0.85f);
            }
        }

        void BuildLoading(Transform parent)
        {
            var rt = FullPanel("Loading", parent);
            loadingPanel = rt.gameObject;
            var bg = rt.gameObject.AddComponent<Image>(); bg.color = new Color(0f, 0f, 0f, 0.55f);   // also blocks taps while loading
            loadingText = UIFactory.Text("Msg", rt, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(1400, 200), "Loading...", 46, TextAnchor.MiddleCenter, new Color(1f, 0.85f, 0.3f), FontStyle.Bold);
            loadingPanel.SetActive(false);
        }

        void BuildDriveUI(Transform parent)
        {
            var rt = FullPanel("Drive", parent);
            drivePanel = rt.gameObject;
            controls = drivePanel.AddComponent<MobileControls>();

            var wheelImg = UIFactory.Image("SteeringWheel", rt, Vector2.zero, new Vector2(0.5f, 0.5f), new Vector2(250, 250), new Vector2(420, 420), Color.white, UIFactory.LoadSprite("UI/steering_wheel"));
            wheelImg.gameObject.AddComponent<WheelTouchArea>().controls = controls;
            controls.wheel = wheelImg.rectTransform;

            var brakeImg = UIFactory.Image("BrakePedal", rt, new Vector2(1, 0), new Vector2(0.5f, 0f), new Vector2(-410, 40), new Vector2(250, 210), Color.white, UIFactory.LoadSprite("UI/brake_pedal"));
            controls.brake = brakeImg.gameObject.AddComponent<PedalButton>();
            var gasImg = UIFactory.Image("GasPedal", rt, new Vector2(1, 0), new Vector2(0.5f, 0f), new Vector2(-150, 40), new Vector2(180, 270), Color.white, UIFactory.LoadSprite("UI/gas_pedal"));
            controls.gas = gasImg.gameObject.AddComponent<PedalButton>();

            var cluster = UIFactory.Image("Cluster", rt, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0, 20), new Vector2(520, 190), new Color(0.02f, 0.03f, 0.05f, 0.72f));
            speedText = UIFactory.Text("Speed", cluster.transform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(-10, -8), new Vector2(260, 110), "0", 96, TextAnchor.MiddleCenter, Color.white, FontStyle.Bold);
            UIFactory.Text("Unit", cluster.transform, new Vector2(0.5f, 1f), new Vector2(0f, 1f), new Vector2(120, -50), new Vector2(120, 40), "km/h", 28, TextAnchor.MiddleLeft, new Color(1, 1, 1, 0.7f));
            gearText = UIFactory.Text("Gear", cluster.transform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(26, -20), new Vector2(120, 90), "D1", 54, TextAnchor.MiddleLeft, new Color(1f, 0.78f, 0.15f), FontStyle.Bold);
            var rpmBg = UIFactory.Image("RpmBg", cluster.transform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0, 30), new Vector2(460, 22), new Color(1, 1, 1, 0.12f));
            rpmFill = UIFactory.Image("RpmFill", rpmBg.transform, new Vector2(0, 0.5f), new Vector2(0, 0.5f), Vector2.zero, new Vector2(460, 22), new Color(0.2f, 0.85f, 0.35f));
            rpmText = UIFactory.Text("Rpm", cluster.transform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0, 56), new Vector2(460, 34), "600 rpm", 24, TextAnchor.MiddleCenter, new Color(1, 1, 1, 0.75f));

            UIFactory.Button("Garage", rt, new Vector2(0, 1), new Vector2(0, 1), new Vector2(30, -30), new Vector2(200, 84), "GARAGE", () => EnterGarage(), 28);
            var camB = UIFactory.Button("Camera", rt, new Vector2(0, 1), new Vector2(0, 1), new Vector2(250, -30), new Vector2(220, 84), "CAB VIEW", () => { camRig.Next(); UpdateCamLabel(); }, 28);
            camText = camB.GetComponentInChildren<Text>();
            UIFactory.Button("Reset", rt, new Vector2(0, 1), new Vector2(0, 1), new Vector2(490, -30), new Vector2(200, 84), "RESET", () => ResetTruck(), 28);
            var lb = UIFactory.Button("Lights", rt, new Vector2(0, 1), new Vector2(0, 1), new Vector2(710, -30), new Vector2(230, 84), "LIGHTS OFF", () => CycleLights(), 26);
            lightsBtnText = lb.GetComponentInChildren<Text>();
            var nb2 = UIFactory.Button("DayNightDrive", rt, new Vector2(0, 1), new Vector2(0, 1), new Vector2(960, -30), new Vector2(170, 84), "DAY", () => SetNight(!night), 26);
            var nbt = nb2.GetComponentInChildren<Text>();
            var gearB = UIFactory.Button("GearMode", rt, new Vector2(1, 0), new Vector2(0.5f, 0f), new Vector2(-280, 340), new Vector2(150, 90), "D", () => truck.CycleGearMode(), 46);
            gearBtnText = gearB.GetComponentInChildren<Text>();
            truckNameText = UIFactory.Text("TruckName", rt, new Vector2(1, 1), new Vector2(1, 1), new Vector2(-30, -30), new Vector2(700, 50), "", 28, TextAnchor.UpperRight, new Color(1, 1, 1, 0.85f), FontStyle.Bold);
            dayNightDriveText = nbt;
        }
        Text dayNightDriveText;

        void UpdateCamLabel()
        {
            if (camText) camText.text = camRig.mode == CamMode.Interior ? "CAB VIEW" : "OUTSIDE";
        }

        void RefreshGarageText()
        {
            if (garageName == null || truck == null) return;
            var s = truck.spec;
            garageTier.text = $"TRUCK {truckIndex + 1} / {TruckDatabase.All.Length}   POWER CLASS " + new string('|', (truckIndex + 1) * 2);
            garageName.text = s.displayName;
            string road = "-";
            if (highway != null && highway.data != null)
            {
                var d = highway.data;
                var e = RoadCatalog.Get(d.id);
                road = $"{d.name}\n{(highway.totalLength / 1000f):0} km" + (e != null ? $"  ·  lanes {e.lanes}  ·  {e.elevation}" : "");
            }
            garageSpecs.text =
                $"Engine:  {s.engine}\n" +
                $"Power:  {s.horsePower:0} hp  ({s.PowerKw:0} kW)\n" +
                $"Torque:  {s.maxTorque:0} Nm\n" +
                $"Gearbox:  {s.gearbox}\n" +
                $"Mass:  {s.massKg / 1000f:0.0} t   ({s.horsePower / (s.massKg / 1000f):0} hp/t)\n" +
                $"Top speed:  {s.topSpeedKmh:0} km/h\n" +
                $"Length:  {s.lengthMeters:0.00} m\n\n" +
                $"EGYPT HIGHWAY:\n{road}";
            if (truckNameText) truckNameText.text = s.displayName;
            if (routeBtnText && highway != null && highway.data != null) routeBtnText.text = "ROUTE: " + Short(highway.data.name, 34);
            if (routePanel && routePanel.activeSelf) RefreshRouteCards();
        }

        void Update()
        {
            if (dayNightDriveText) dayNightDriveText.text = night ? "NIGHT" : "DAY";
            if (truck == null || inGarage) return;
            speedText.text = truck.SpeedKmh.ToString("0");
            gearText.text = truck.gearMode == GearMode.Reverse ? "R" : truck.gearMode == GearMode.Neutral ? "N" : "D" + truck.Gear;
            gearBtnText.text = truck.gearMode == GearMode.Reverse ? "R" : truck.gearMode == GearMode.Neutral ? "N" : "D";
            float rpm01 = Mathf.InverseLerp(0, truck.spec.redlineRpm, truck.EngineRpm);
            rpmFill.rectTransform.sizeDelta = new Vector2(460f * rpm01, 22f);
            rpmFill.color = truck.EngineRpm > 1900 ? new Color(0.95f, 0.3f, 0.2f) : truck.EngineRpm > 1000 ? new Color(0.2f, 0.85f, 0.35f) : new Color(1f, 0.78f, 0.15f);
            rpmText.text = $"{truck.EngineRpm:0} rpm";
            if (cabinLight) cabinLight.enabled = camRig.mode == CamMode.Interior && !night;
            if (Input.GetKeyDown(KeyCode.L) || Input.GetKeyDown(KeyCode.H)) UpdateLightsLabel();
            if (Input.GetKeyDown(KeyCode.N)) SetNight(!night);
            roadHint = highway.ClosestIndex(truck.transform.position, roadHint);
            if (Input.GetKeyDown(KeyCode.Backspace)) ResetTruck();
            if (truck.transform.position.y < highway.P(roadHint).y - 30f || (Vector3.Dot(truck.transform.up, Vector3.up) < 0.3f && truck.SpeedKmh < 3f)) ResetTruck();
            // floating origin: keep the truck within ~1.5 km of (0,0,0) so physics stays precise on 300 km roads
            var shift = highway.RecenterIfNeeded(truck.transform.position);
            if (shift != Vector3.zero)
            {
                var rb = truck.GetComponent<Rigidbody>();
                truck.transform.position -= shift;
                rb.position = truck.transform.position;
                Physics.SyncTransforms();
                camRig.Shift(shift);
            }
        }

        // ---------------- automated test (verifies the build end-to-end) ----------------
        int errorCount, exceptionCount;
        readonly List<string> firstErrors = new List<string>();
        void CountErrors(string msg, string stack, LogType type)
        {
            if (type == LogType.Error || type == LogType.Assert) errorCount++;
            else if (type == LogType.Exception) exceptionCount++;
            else return;
            if (firstErrors.Count < 20) firstErrors.Add(type + ": " + msg);
        }

        IEnumerator AutoTest()
        {
            string outDir = ArgValue("-shots", Path.Combine(Application.persistentDataPath, "autotest"));
            Directory.CreateDirectory(outDir);
            float driveSeconds = float.Parse(ArgValue("-drive", "14"), CultureInfo.InvariantCulture);
            string only = ArgValue("-truck", "");
            string phases = ArgValue("-phases", "trucks,lights,steer,routes,ui");
            yield return new WaitForSeconds(1f);
            if (phases.Contains("trucks"))
            for (int t = 0; t < TruckDatabase.All.Length; t++)
            {
                if (only != "" && only != t.ToString()) continue;
                string id = $"{t + 1}_{TruckDatabase.All[t].id}";
                SpawnTruck(t);
                EnterGarage();
                camRig.autoOrbit = false;
                camRig.SetOrbitAngles(215f, 10f);
                yield return new WaitForSeconds(0.5f);
                yield return Shot(outDir, id + "_garage.png");
                camRig.SetOrbitAngles(325f, 8f);
                yield return new WaitForSeconds(0.3f);
                yield return Shot(outDir, id + "_garage_front.png");
                camRig.autoOrbit = true;

                StartDriving();
                truck.externalInput = true;
                yield return new WaitForSeconds(0.6f);
                yield return Shot(outDir, id + "_cab.png");
                camRig.SetLook(-60f, 10f);
                yield return new WaitForSeconds(0.1f);
                yield return Shot(outDir, id + "_cab_left.png");
                camRig.SetLook(0f, 0f);

                // Accelerate using the on-screen gas pedal.
                controls.gas.simulated = true; controls.gas.pressed = true;
                float t0 = Time.time; float t60 = -1; float maxSpeed = 0; Vector3 start = truck.transform.position; float shifted = 0f;
                double ox0 = highway.ox, oz0 = highway.oz;
                bool midLogged = false;
                while (Time.time - t0 < driveSeconds)
                {
                    truck.throttleInput = controls.Throttle; truck.brakeInput = controls.Brake;
                    truck.steerInput = LaneKeep();
                    if (t60 < 0 && truck.SpeedKmh >= 60f) t60 = Time.time - t0;
                    maxSpeed = Mathf.Max(maxSpeed, truck.SpeedKmh);
                    if (!midLogged && Time.time - t0 > driveSeconds * 0.5f) { midLogged = true; Debug.Log($"[AutoTest] {truck.spec.id} mid: speed={truck.SpeedKmh:0.0} gear={truck.Gear} rpm={truck.EngineRpm:0}"); }
                    yield return null;
                }
                yield return Shot(outDir, id + "_cab_driving.png");
                camRig.SetMode(CamMode.Chase);
                for (int f = 0; f < 20; f++) { truck.throttleInput = controls.Throttle; truck.steerInput = LaneKeep(); yield return null; }
                yield return Shot(outDir, id + "_chase.png");
                // Brake test with the on-screen brake pedal.
                controls.gas.pressed = false; controls.brake.simulated = true; controls.brake.pressed = true;
                float vb = truck.SpeedKmh; float tb = Time.time;
                while (truck.SpeedKmh > 1f && Time.time - tb < 20f) { truck.throttleInput = 0; truck.brakeInput = controls.Brake; truck.steerInput = LaneKeep(); yield return null; }
                float brakeTime = Time.time - tb;
                controls.brake.pressed = false;
                // distance in route coordinates (the world may have been re-centred on the way)
                Vector3 endRoute = truck.transform.position + new Vector3((float)(highway.ox - ox0), 0f, (float)(highway.oz - oz0));
                float dist = Vector3.Distance(start, endRoute);
                shifted = (float)System.Math.Sqrt((highway.ox - ox0) * (highway.ox - ox0) + (highway.oz - oz0) * (highway.oz - oz0));
                int idx = highway.ClosestIndex(truck.transform.position, -1);
                float lateral = Vector3.Distance(truck.transform.position, highway.LanePosition(idx, 0));
                Debug.Log($"[AutoTest] RESULT {truck.spec.id} hp={truck.spec.horsePower} 0-60={(t60 < 0 ? "n/a" : t60.ToString("0.0") + "s")} maxSpeed={maxSpeed:0.0}km/h distance={dist:0}m brake {vb:0}->0 in {brakeTime:0.0}s laneError={lateral:0.0}m upright={Vector3.Dot(truck.transform.up, Vector3.up):0.00} originShift={shifted:0}m");
                truck.externalInput = false;
                controls.gas.simulated = controls.brake.simulated = false;
            }
            if (phases.Contains("lights")) yield return LightsTest(outDir);
            if (phases.Contains("steer")) yield return SteeringTest(outDir);
            if (phases.Contains("routes")) yield return RoutesTest(outDir);
            if (phases.Contains("ui")) yield return UiTest(outDir);
            // Garage UI screenshot with the full HUD
            SetNight(false);
            EnterGarage(); yield return new WaitForSeconds(0.3f);
            yield return Shot(outDir, "0_garage_ui.png");
            Debug.Log($"[AutoTest] ERRORS errors={errorCount} exceptions={exceptionCount}" + (firstErrors.Count > 0 ? "\n  " + string.Join("\n  ", firstErrors) : ""));
            Debug.Log("[AutoTest] DONE");
            yield return new WaitForSeconds(0.3f);
            Application.Quit(errorCount + exceptionCount == 0 ? 0 : 3);
        }

        // lights: every truck, low / high beam, brake, reverse and dashboard glow, at night
        IEnumerator LightsTest(string outDir)
        {
            SetNight(true);
            int fails = 0;
            for (int t = 0; t < TruckDatabase.All.Length; t++)
            {
                SpawnTruck(t); StartDriving(); truck.externalInput = true;
                truck.throttleInput = 0; truck.brakeInput = 0; truck.steerInput = 0;
                var L = truck.lights;
                string id = $"{t + 1}_{truck.spec.id}";
                L.SetMode(TruckLights.Mode.Low); yield return null;
                bool lowOk = L.IsOn && L.State.Contains("beams=on") && L.State.Contains("range=70") && L.DashIntensity > 0.5f;
                camRig.SetMode(CamMode.Orbit); camRig.autoOrbit = false; camRig.SetOrbitAngles(335f, 6f);
                yield return new WaitForSeconds(0.2f);
                yield return Shot(outDir, id + "_night_lowbeam.png");
                L.SetMode(TruckLights.Mode.High); yield return null;
                bool highOk = L.State.Contains("range=160");
                yield return Shot(outDir, id + "_night_highbeam.png");
                camRig.SetMode(CamMode.Interior); yield return new WaitForSeconds(0.1f);
                yield return Shot(outDir, id + "_night_cab.png");
                truck.brakeInput = 1f; yield return null; yield return null;
                bool brakeOk = L.BrakeLit;
                camRig.SetMode(CamMode.Orbit); camRig.SetOrbitAngles(160f, 8f); yield return new WaitForSeconds(0.1f);
                yield return Shot(outDir, id + "_night_brake.png");
                truck.brakeInput = 1f;
                for (int f = 0; f < 30 && truck.SpeedKmh > 0.5f; f++) yield return null;
                truck.CycleGearMode(); truck.CycleGearMode(); truck.brakeInput = 0f; yield return null; yield return null;
                bool revOk = L.ReverseLit && truck.gearMode == GearMode.Reverse;
                yield return Shot(outDir, id + "_night_reverse.png");
                truck.CycleGearMode(); yield return null;
                L.SetMode(TruckLights.Mode.Off); yield return null;
                bool offOk = !L.IsOn && L.State.Contains("beams=off") && L.DashIntensity == 0f;
                bool ok = lowOk && highOk && brakeOk && revOk && offOk && L.BeamCount == 2;
                if (!ok) fails++;
                Debug.Log($"[AutoTest] LIGHTS {truck.spec.id} low={lowOk} high={highOk} brake={brakeOk} reverse={revOk} off={offOk} beams={L.BeamCount} -> {(ok ? "PASS" : "FAIL")} | {L.State}");
                camRig.autoOrbit = true; truck.externalInput = false;
            }
            Debug.Log($"[AutoTest] LIGHTS SUMMARY fails={fails}");
            SetNight(false);
        }

        // steering: on-screen wheel angle == 3D cab wheel angle in the same frame, for every truck
        IEnumerator SteeringTest(string outDir)
        {
            float[] inputs = { 0f, 0.25f, -0.5f, 1f, -1f, 0.1f };
            float worstAll = 0f;
            for (int t = 0; t < TruckDatabase.All.Length; t++)
            {
                SpawnTruck(t); StartDriving(); truck.externalInput = false;
                var rig = truck.rig;
                Vector3 scale0 = rig.steeringWheel ? rig.steeringWheel.lossyScale : Vector3.one;
                float worst = 0f, worstScale = 0f;
                foreach (var v in inputs)
                {
                    controls.SimulateWheel(v);
                    yield return null;   // one frame: MobileControls (early) -> TruckController -> rig
                    float screen = controls.wheel.localEulerAngles.z; if (screen > 180f) screen -= 360f;   // UI z: + = counter-clockwise
                    float expectCab = Mathf.Clamp(-controls.WheelAngle, -TruckController.SteeringWheelLockDeg, TruckController.SteeringWheelLockDeg);
                    float err = Mathf.Abs(truck.SteeringWheelAngle - expectCab);
                    if (rig.steeringWheel)
                    {
                        var want = Quaternion.AngleAxis(expectCab, rig.steeringWheelAxis);
                        err = Mathf.Max(err, Quaternion.Angle(rig.steeringWheel.localRotation, want));
                        worstScale = Mathf.Max(worstScale, (rig.steeringWheel.lossyScale - scale0).magnitude);
                    }
                    // the on-screen sprite shows the same angle modulo 360 (450 deg = 1.25 turns)
                    err = Mathf.Max(err, Mathf.Abs(Mathf.DeltaAngle(screen, controls.WheelAngle)));
                    worst = Mathf.Max(worst, err);
                    if (Mathf.Approximately(v, 0.25f) || Mathf.Approximately(v, 1f))
                    {
                        camRig.SetMode(CamMode.Interior); camRig.SetLook(0f, -18f);
                        yield return Shot(outDir, $"{t + 1}_{truck.spec.id}_wheel_{(int)(v * 450)}deg.png");
                    }
                }
                controls.ReleaseWheel();
                worstAll = Mathf.Max(worstAll, worst);
                Debug.Log($"[AutoTest] STEERSYNC {truck.spec.id} wheel3D={(rig.steeringWheel != null)} maxError={worst:0.000}deg scaleDrift={worstScale:0.0000} lock={TruckController.SteeringWheelLockDeg} -> {(worst < 0.5f && worstScale < 1e-4f ? "PASS" : "FAIL")}");
            }
            // drive test: turn the on-screen wheel and check the truck turns the right way
            SpawnTruck(0); StartDriving(); truck.externalInput = true;
            controls.gas.simulated = true; controls.gas.pressed = true;
            float ts = Time.time; while (Time.time - ts < 5f) { truck.throttleInput = controls.Throttle; truck.brakeInput = 0; truck.steerInput = LaneKeep(); yield return null; }
            controls.gas.pressed = false;
            float yaw0 = truck.transform.eulerAngles.y;
            controls.SimulateWheel(0.5f);
            ts = Time.time; while (Time.time - ts < 2.0f) { truck.throttleInput = 0.3f; truck.brakeInput = 0; truck.steerInput = controls.Steer; yield return null; }
            yield return Shot(outDir, "7_steering_test_cab.png");
            float dyaw = Mathf.DeltaAngle(yaw0, truck.transform.eulerAngles.y);
            Debug.Log($"[AutoTest] STEER wheelInput={controls.Steer:0.00} yawChange={dyaw:0.0}deg (positive = right turn) -> {(dyaw > 1f ? "PASS" : "FAIL")}");
            controls.ReleaseWheel();
            camRig.SetMode(CamMode.Chase); yield return new WaitForSeconds(0.4f);
            yield return Shot(outDir, "8_steering_test_chase.png");
            controls.brake.simulated = true; controls.brake.pressed = true;
            ts = Time.time; while (truck.SpeedKmh > 0.5f && Time.time - ts < 10f) { truck.brakeInput = controls.Brake; truck.throttleInput = 0; truck.steerInput = 0; yield return null; }
            controls.brake.pressed = false;
            truck.CycleGearMode(); truck.CycleGearMode();
            Vector3 p0 = truck.transform.position;
            ts = Time.time; while (Time.time - ts < 3f) { truck.throttleInput = 0.6f; truck.brakeInput = 0; truck.steerInput = 0; yield return null; }
            float back = Vector3.Dot(truck.transform.position - p0, truck.transform.forward);
            Debug.Log($"[AutoTest] REVERSE mode={truck.gearMode} movedAlongForward={back:0.0}m (negative = backwards) -> {(back < -0.5f ? "PASS" : "FAIL")}");
            truck.externalInput = false; controls.gas.simulated = controls.brake.simulated = false;
            Debug.Log($"[AutoTest] STEERSYNC SUMMARY worst={worstAll:0.000}deg");
        }

        // every road of the catalog: load time, geometry sanity and a short drive on it
        IEnumerator RoutesTest(string outDir)
        {
            SpawnTruck(0);
            int fails = 0; long worstMs = 0; string worstId = "";
            var ids = RoadCatalog.Ids;
            int every = int.Parse(ArgValue("-routeShotsEvery", "1"));
            for (int r = 0; r < ids.Length; r++)
            {
                EnterGarage();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                bool ok = LoadRouteNow(ids[r]);
                long ms = sw.ElapsedMilliseconds;
                if (ms > worstMs) { worstMs = ms; worstId = ids[r]; }
                if (!ok) { fails++; Debug.Log($"[AutoTest] ROUTE {ids[r]} FAIL (load)"); continue; }
                var d = highway.data;
                bool geo = highway.Count > 100 && !float.IsNaN(highway.LanePosition(SpawnIndex, 0).x);
                StartDriving(); truck.externalInput = true;
                float t0 = Time.time; float vmax = 0f;
                while (Time.time - t0 < 3.5f) { truck.throttleInput = 1f; truck.brakeInput = 0; truck.steerInput = LaneKeep(); vmax = Mathf.Max(vmax, truck.SpeedKmh); yield return null; }
                if (r % every == 0) { camRig.SetMode(CamMode.Chase); yield return null; yield return null; yield return Shot(outDir, $"route_{ids[r]}.png"); }
                int idx = highway.ClosestIndex(truck.transform.position, -1);
                float h = truck.transform.position.y - highway.P(idx).y;
                bool onRoad = Mathf.Abs(h) < 3f && Vector3.Dot(truck.transform.up, Vector3.up) > 0.8f && vmax > 15f;
                bool pass = geo && onRoad;
                if (!pass) fails++;
                int jersey = 0, bridges = 0; if (d.HasJersey) foreach (var j in d.jersey) jersey += j; for (int k = 1; k < d.count; k++) if (d.bridge[k] == 1 && d.bridge[k - 1] == 0) bridges++;
                Debug.Log($"[AutoTest] ROUTE {ids[r]} {d.lengthKm:0.0}km load={ms}ms lanes={RoadCatalog.Get(ids[r])?.lanes} bridges={bridges} jersey={(d.HasJersey ? (jersey * 100 / d.count) + "%" : "legacy")} towns={(d.towns != null ? d.towns.Length : 0)} vmax={vmax:0} height={h:0.00} -> {(pass ? "PASS" : "FAIL")}");
                truck.externalInput = false;
            }
            Debug.Log($"[AutoTest] ROUTES SUMMARY roads={ids.Length} fails={fails} slowestLoad={worstMs}ms ({worstId})");
            LoadRouteNow(ids[0]);
        }

        // garage UI: route picker + truck selection through the real buttons, measuring the longest frame
        IEnumerator UiTest(string outDir)
        {
            EnterGarage(); yield return new WaitForSeconds(0.3f);
            float worst = 0f;
            OpenRoutePicker(); yield return null; yield return null;
            for (int r = 0; r < RoadCatalog.Regions.Length; r++)
            {
                float f0 = Time.realtimeSinceStartup;
                ShowRegion(r);
                yield return null;
                worst = Mathf.Max(worst, Time.realtimeSinceStartup - f0);
                if (r == 1 || r == 4) yield return Shot(outDir, $"ui_route_picker_{RoadCatalog.Regions[r].id}.png");
            }
            // pick a road by clicking its card
            var card = routeListContent.GetChild(Mathf.Min(1, routeListContent.childCount - 1)).GetComponent<Button>();
            string want = card.name.Substring(5);
            card.onClick.Invoke();
            yield return null;
            bool overlay = loadingPanel.activeSelf;
            yield return Shot(outDir, "ui_route_loading.png");
            // taps while busy are ignored (no queued work)
            RequestTruck(truckIndex + 1); RequestRoute(RoadCatalog.Ids[0]);
            float tw = Time.time; while (busy && Time.time - tw < 20f) yield return null;
            Debug.Log($"[AutoTest] UI route click -> {CurrentRouteId} (wanted {want}) overlay={overlay} busyGuard={(CurrentRouteId == want ? "PASS" : "FAIL")} worstRegionFrame={worst * 1000f:0}ms");
            yield return Shot(outDir, "ui_garage_after_route.png");
            // truck selection with the arrow buttons
            float worstTruck = 0f;
            for (int i = 0; i < TruckDatabase.All.Length; i++)
            {
                int before = truckIndex;
                float f0 = Time.realtimeSinceStartup;
                RequestTruck(truckIndex + 1);
                while (busy) yield return null;
                worstTruck = Mathf.Max(worstTruck, Time.realtimeSinceStartup - f0);
                Debug.Log($"[AutoTest] UI truck {before}->{truckIndex} ({truck.spec.id}) {(truckIndex == (before + 1) % TruckDatabase.All.Length ? "PASS" : "FAIL")}");
            }
            yield return Shot(outDir, "ui_garage_truck.png");
            Debug.Log($"[AutoTest] UI SUMMARY worstRegionSwitchFrame={worst * 1000f:0}ms worstTruckSwitch={worstTruck * 1000f:0}ms (with loading overlay)");
        }

        float LaneKeep()
        {
            roadHint = highway.ClosestIndex(truck.transform.position, roadHint);
            Vector3 target = highway.LanePosition(roadHint + 6 + (int)(truck.SpeedKmh / 10f), 0);
            Vector3 local = truck.transform.InverseTransformPoint(target);
            return Mathf.Clamp(Mathf.Atan2(local.x, local.z) * 2.5f, -1f, 1f);
        }

        IEnumerator Shot(string dir, string name)
        {
            yield return new WaitForEndOfFrame();
            var tex = ScreenCapture.CaptureScreenshotAsTexture();
            File.WriteAllBytes(Path.Combine(dir, name), tex.EncodeToPNG());
            Destroy(tex);
            Debug.Log("[AutoTest] screenshot " + name);
        }
    }

    /// <summary>Keeps UI inside the phone safe area (notches).</summary>
    public class SafeArea : MonoBehaviour
    {
        Rect last;
        void Update()
        {
            var sa = Screen.safeArea;
            if (sa == last || Screen.width == 0) return;
            last = sa;
            var rt = (RectTransform)transform;
            rt.anchorMin = new Vector2(sa.xMin / Screen.width, sa.yMin / Screen.height);
            rt.anchorMax = new Vector2(sa.xMax / Screen.width, sa.yMax / Screen.height);
        }
    }

    public static class Bootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Init()
        {
            if (Object.FindFirstObjectByType<GameManager>() == null)
                new GameObject("GameManager").AddComponent<GameManager>();
        }
    }
}
