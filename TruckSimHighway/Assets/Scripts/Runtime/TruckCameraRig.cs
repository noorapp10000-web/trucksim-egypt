using UnityEngine;
using UnityEngine.EventSystems;

namespace TruckSim
{
    public enum CamMode { Interior, Chase, Orbit }

    /// <summary>Interior cockpit camera with free look, exterior chase camera and garage orbit camera.</summary>
    public class TruckCameraRig : MonoBehaviour
    {
        public CamMode mode = CamMode.Interior;
        public TruckController target;
        Camera cam;
        float yaw, pitch;
        float orbitYaw = 210f, orbitPitch = 12f;
        public bool autoOrbit = true;
        Vector3 chaseVel;
        int dragFinger = -1;
        Vector2 lastPointer;
        bool mouseDragging;

        void Awake() { cam = GetComponent<Camera>(); }

        public void SetMode(CamMode m)
        {
            mode = m; yaw = 0; pitch = 0;
            if (!cam) cam = GetComponent<Camera>();
            cam.nearClipPlane = m == CamMode.Interior ? 0.05f : 0.25f;
            cam.fieldOfView = m == CamMode.Interior ? 68f : 55f;
            if (target) Snap();
        }

        public void Next() => SetMode(mode == CamMode.Interior ? CamMode.Chase : CamMode.Interior);

        void HandleLook()
        {
            Vector2 delta = Vector2.zero;
            for (int i = 0; i < Input.touchCount; i++)
            {
                var t = Input.GetTouch(i);
                if (t.phase == TouchPhase.Began && dragFinger < 0 && !(EventSystem.current && EventSystem.current.IsPointerOverGameObject(t.fingerId)))
                { dragFinger = t.fingerId; lastPointer = t.position; }
                if (t.fingerId == dragFinger)
                {
                    if (t.phase == TouchPhase.Moved) { delta += t.position - lastPointer; lastPointer = t.position; }
                    if (t.phase == TouchPhase.Ended || t.phase == TouchPhase.Canceled) dragFinger = -1;
                }
            }
            if (Input.touchCount == 0)
            {
                if (Input.GetMouseButtonDown(0) && !(EventSystem.current && EventSystem.current.IsPointerOverGameObject())) { mouseDragging = true; lastPointer = Input.mousePosition; }
                if (Input.GetMouseButtonUp(0)) mouseDragging = false;
                if (mouseDragging) { Vector2 p = Input.mousePosition; delta += p - lastPointer; lastPointer = p; }
            }
            float k = 0.18f;
            if (mode == CamMode.Orbit) { orbitYaw += delta.x * k; orbitPitch = Mathf.Clamp(orbitPitch - delta.y * k, 2f, 60f); }
            else
            {
                yaw += delta.x * k; pitch = Mathf.Clamp(pitch - delta.y * k, -35f, 35f);
                yaw = mode == CamMode.Interior ? Mathf.Clamp(yaw, -130f, 130f) : Mathf.Repeat(yaw + 180f, 360f) - 180f;
                if (delta.sqrMagnitude < 0.01f && dragFinger < 0 && !mouseDragging && mode == CamMode.Chase) yaw = Mathf.Lerp(yaw, 0, Time.deltaTime * 0.8f);
            }
            if (Input.GetKey(KeyCode.Q)) yaw -= 90f * Time.deltaTime;
            if (Input.GetKey(KeyCode.E)) yaw += 90f * Time.deltaTime;
            if (Input.GetKeyDown(KeyCode.C)) Next();
        }

        public void Snap()
        {
            Place(true);
            chaseVel = Vector3.zero;
        }

        void LateUpdate()
        {
            if (target == null || target.rig == null) return;
            HandleLook();
            Place(false);
        }

        void Place(bool snap)
        {
            if (target == null || target.rig == null) return;
            var tr = target.transform;
            var b = target.rig.localBounds;
            switch (mode)
            {
                case CamMode.Interior:
                {
                    Vector3 eye = tr.TransformPoint(target.rig.eyeLocal);
                    // Head stays fixed in the seat: only the player's drag turns the view (no automatic turning with the steering).
                    Quaternion look = tr.rotation * Quaternion.Euler(pitch + 6f, yaw, 0);
                    transform.SetPositionAndRotation(eye, look);
                    break;
                }
                case CamMode.Chase:
                {
                    float dist = b.size.z * 1.25f + 5f;
                    Quaternion rot = Quaternion.Euler(0, tr.eulerAngles.y + yaw, 0) * Quaternion.Euler(9f + pitch, 0, 0);
                    Vector3 pivot = tr.position + Vector3.up * (b.size.y * 0.7f);
                    Vector3 desired = pivot - rot * Vector3.forward * dist;
                    // keep the camera above the ground on hills and embankments
                    if (Physics.Raycast(desired + Vector3.up * 60f, Vector3.down, out var hit, 120f, ~0, QueryTriggerInteraction.Ignore) && !hit.transform.IsChildOf(tr))
                        desired.y = Mathf.Max(desired.y, hit.point.y + 1.2f);
                    transform.position = snap ? desired : Vector3.SmoothDamp(transform.position, desired, ref chaseVel, 0.06f);
                    transform.rotation = Quaternion.LookRotation(pivot + Vector3.up * 0.5f - transform.position);
                    break;
                }
                case CamMode.Orbit:
                {
                    if (autoOrbit && !snap) orbitYaw += Time.deltaTime * 8f;
                    float dist = b.size.z * 1.0f + 3.5f;
                    Vector3 pivot = tr.TransformPoint(b.center) - Vector3.up * 0.2f;
                    Quaternion rot = Quaternion.Euler(orbitPitch, tr.eulerAngles.y + orbitYaw, 0);
                    transform.position = pivot - rot * Vector3.forward * dist;
                    transform.rotation = Quaternion.LookRotation(pivot - transform.position);
                    break;
                }
            }
        }

        /// <summary>Called when the world is re-centred (floating origin) so smoothing does not see a jump.</summary>
        public void Shift(Vector3 delta) { transform.position -= delta; }

        public void SetOrbitAngles(float y, float p) { orbitYaw = y; orbitPitch = p; Place(true); }
        public void SetLook(float y, float p) { yaw = y; pitch = p; Place(true); }
    }
}
