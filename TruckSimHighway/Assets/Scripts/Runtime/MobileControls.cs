using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace TruckSim
{
    /// <summary>On-screen steering wheel (drag to rotate) and accelerator / brake pedals.
    /// Runs before the truck scripts (execution order) so the 3D cab wheel shows the same angle in the same frame.</summary>
    [DefaultExecutionOrder(-50)]
    public class MobileControls : MonoBehaviour
    {
        public static MobileControls Instance;
        public float Throttle { get; private set; }
        public float Brake { get; private set; }
        public float Steer { get; private set; }

        public RectTransform wheel;
        public PedalButton gas, brake;
        public float maxWheelAngle = 450f;   // +-1.25 turns on screen = full lock

        float wheelAngle;
        /// <summary>On-screen wheel angle in degrees (UI convention: + = counter-clockwise = left).</summary>
        public float WheelAngle => wheelAngle;
        bool wheelHeld;
        float lastPointerAngle;
        int wheelPointer = -99;

        void Awake() { Instance = this; }

        void Update()
        {
            Throttle = Mathf.MoveTowards(Throttle, gas && gas.pressed ? 1f : 0f, Time.deltaTime * 3.5f);
            Brake = Mathf.MoveTowards(Brake, brake && brake.pressed ? 1f : 0f, Time.deltaTime * 5f);
            if (!wheelHeld) wheelAngle = Mathf.MoveTowards(wheelAngle, 0f, Time.deltaTime * 520f);
            Steer = Mathf.Clamp(-wheelAngle / maxWheelAngle, -1f, 1f);
            if (wheel) wheel.localRotation = Quaternion.Euler(0, 0, wheelAngle);
            if (gas) gas.Visual(Throttle);
            if (brake) brake.Visual(Brake);
        }

        float PointerAngle(Vector2 screenPos, Camera uiCam)
        {
            Vector2 center = RectTransformUtility.WorldToScreenPoint(uiCam, wheel.position);
            Vector2 d = screenPos - center;
            return Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg;
        }

        public void WheelDown(PointerEventData e)
        {
            wheelHeld = true; wheelPointer = e.pointerId;
            lastPointerAngle = PointerAngle(e.position, e.pressEventCamera);
        }

        public void WheelDrag(PointerEventData e)
        {
            if (e.pointerId != wheelPointer) return;
            float a = PointerAngle(e.position, e.pressEventCamera);
            float delta = Mathf.DeltaAngle(lastPointerAngle, a);
            lastPointerAngle = a;
            wheelAngle = Mathf.Clamp(wheelAngle + delta, -maxWheelAngle, maxWheelAngle);
        }

        public void WheelUp(PointerEventData e)
        {
            if (e.pointerId != wheelPointer) return;
            wheelHeld = false; wheelPointer = -99;
        }

        /// <summary>Used by the automated test to simulate turning the on-screen wheel (+ = right).</summary>
        public void SimulateWheel(float normalized) { wheelHeld = true; wheelAngle = -normalized * maxWheelAngle; }
        public void ReleaseWheel() { wheelHeld = false; }
    }

    public class WheelTouchArea : MonoBehaviour, IPointerDownHandler, IDragHandler, IPointerUpHandler
    {
        public MobileControls controls;
        public void OnPointerDown(PointerEventData e) => controls.WheelDown(e);
        public void OnDrag(PointerEventData e) => controls.WheelDrag(e);
        public void OnPointerUp(PointerEventData e) => controls.WheelUp(e);
    }

    public class PedalButton : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IPointerExitHandler
    {
        public bool pressed;
        public bool simulated;
        RectTransform rt;
        Vector3 baseScale = Vector3.one;
        Image img;
        void Awake() { rt = GetComponent<RectTransform>(); img = GetComponent<Image>(); baseScale = rt.localScale; }
        public void OnPointerDown(PointerEventData e) { pressed = true; }
        public void OnPointerUp(PointerEventData e) { if (!simulated) pressed = false; }
        public void OnPointerExit(PointerEventData e) { if (!simulated) pressed = false; }
        public void Visual(float amount)
        {
            if (!rt) return;
            rt.localScale = new Vector3(baseScale.x * (1f - amount * 0.04f), baseScale.y * (1f - amount * 0.12f), 1f);
            if (img) img.color = Color.Lerp(Color.white, new Color(0.72f, 0.72f, 0.72f), amount);
        }
    }
}
