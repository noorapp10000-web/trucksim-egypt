using System.Collections.Generic;
using UnityEngine;

namespace TruckSim
{
    public enum GearMode { Drive, Neutral, Reverse }

    /// <summary>
    /// Heavy truck physics: WheelColliders, diesel torque curve, automated manual gearbox,
    /// air brakes, engine brake and speed limiter.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public class TruckController : MonoBehaviour
    {
        public TruckSpec spec;
        public TruckModelRig rig;

        [HideInInspector] public float throttleInput, brakeInput, steerInput;
        public bool externalInput;              // set by the automated test
        public TruckLights lights;
        float keySteer;
        public GearMode gearMode = GearMode.Drive;

        public float SpeedKmh { get; private set; }
        public float ForwardSpeed { get; private set; }
        public float EngineRpm { get; private set; }
        public int Gear { get; private set; } = 1;
        public float SteerAngle { get; private set; }
        public float Throttle { get; private set; }
        public bool Shifting => shiftTimer > 0f;
        public float FinalDrive => finalDrive;
        /// <summary>3D steering wheel rotation in degrees (+ = clockwise seen by the driver = turning right).</summary>
        public float SteeringWheelAngle => steeringWheelAngle;
        public bool Braking => brakeInput > 0.05f;
        public bool Reversing => gearMode == GearMode.Reverse;
        /// <summary>Same lock-to-lock as the on-screen wheel (MobileControls.maxWheelAngle), so both wheels turn together.</summary>
        public const float SteeringWheelLockDeg = 450f;

        Rigidbody rb;
        readonly List<WheelCollider> all = new List<WheelCollider>();
        readonly List<WheelCollider> steerWheels = new List<WheelCollider>();
        readonly List<WheelCollider> driveWheels = new List<WheelCollider>();
        readonly List<(WheelCollider wc, TruckModelRig.WheelVisual vis)> visuals = new List<(WheelCollider, TruckModelRig.WheelVisual)>();
        readonly Dictionary<WheelCollider, float> spin = new Dictionary<WheelCollider, float>();
        float finalDrive;
        float shiftTimer;
        float smoothSteer;
        float steeringWheelAngle;
        const float MaxSteerDeg = 34f;

        public void Init(TruckSpec s, TruckModelRig r)
        {
            spec = s; rig = r;
            rb = GetComponent<Rigidbody>();
            rb.mass = spec.massKg;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            rb.linearDamping = 0.02f;
            rb.angularDamping = 0.6f;
            lights = gameObject.AddComponent<TruckLights>();
            lights.Init(this, rig);
            lights.SetMode(TruckLights.Mode.Low);

            Bounds b = rig.localBounds;
            var chassis = new GameObject("ChassisCollider").AddComponent<BoxCollider>();
            chassis.transform.SetParent(transform, false);
            chassis.center = new Vector3(0, 1.1f, b.center.z);
            chassis.size = new Vector3(b.size.x * 0.9f, 0.6f, b.size.z * 0.98f);
            var cab = new GameObject("CabCollider").AddComponent<BoxCollider>();
            cab.transform.SetParent(transform, false);
            float cabLen = Mathf.Min(2.4f, b.size.z * 0.4f);
            float cabFront = b.max.z - (spec.lengthMeters > 7.5f ? 1.7f : 0f);
            cab.center = new Vector3(0, (1.4f + b.max.y) * 0.5f, cabFront - cabLen * 0.5f);
            cab.size = new Vector3(b.size.x * 0.95f, Mathf.Max(0.5f, b.max.y - 1.4f), cabLen);

            var wheelsParent = new GameObject("WheelColliders").transform;
            wheelsParent.SetParent(transform, false);
            float suspension = 0.28f;
            var rearZs = new List<float>();
            foreach (var w in rig.wheels) if (!w.front && !rearZs.Exists(z => Mathf.Abs(z - w.localCenter.z) < 0.3f)) rearZs.Add(w.localCenter.z);
            rearZs.Sort((a, c) => c.CompareTo(a)); // front-most rear axle first

            foreach (var w in rig.wheels)
            {
                var go = new GameObject(w.front ? "WC_Front" : "WC_Rear");
                go.transform.SetParent(wheelsParent, false);
                go.transform.localPosition = w.localCenter + Vector3.up * (suspension * 0.45f);
                var wc = go.AddComponent<WheelCollider>();
                float radius = Mathf.Clamp(w.radius, 0.45f, 0.6f);
                wc.radius = radius;
                wc.mass = 140f;
                wc.wheelDampingRate = 0.5f;
                wc.suspensionDistance = suspension;
                wc.forceAppPointDistance = 0.4f;
                int wheelCount = rig.wheels.Count;
                float sprung = spec.massKg / wheelCount;
                wc.suspensionSpring = new JointSpring { spring = sprung * 110f, damper = sprung * 9f, targetPosition = 0.45f };
                wc.forwardFriction = new WheelFrictionCurve { extremumSlip = 0.35f, extremumValue = 1.0f, asymptoteSlip = 0.9f, asymptoteValue = 0.75f, stiffness = 1.6f };
                wc.sidewaysFriction = new WheelFrictionCurve { extremumSlip = 0.22f, extremumValue = 1.0f, asymptoteSlip = 0.6f, asymptoteValue = 0.8f, stiffness = 1.8f };
                all.Add(wc);
                spin[wc] = 0f;
                if (w.front) steerWheels.Add(wc);
                else if (rearZs.Count == 0 || Mathf.Abs(w.localCenter.z - rearZs[0]) < 0.3f || (spec.drivenRearAxles >= 2 && rearZs.Count >= 2 && Mathf.Abs(w.localCenter.z - rearZs[1]) < 0.3f)) driveWheels.Add(wc);
                visuals.Add((wc, w));
            }
            if (driveWheels.Count == 0) foreach (var wc in all) if (!steerWheels.Contains(wc)) driveWheels.Add(wc);

            rb.centerOfMass = new Vector3(0, 0.95f, b.center.z + b.size.z * 0.12f);
            all[0].ConfigureVehicleSubsteps(5f, 12, 15);

            // Final drive chosen so top gear at ~1750 rpm reaches the limiter speed (+3%).
            float r0 = all[0].radius;
            float topMs = spec.topSpeedKmh / 3.6f * 1.03f;
            float topGear = spec.gearRatios[spec.gearRatios.Length - 1];
            finalDrive = (1750f / 60f * 2f * Mathf.PI * r0) / (topMs * topGear);
            EngineRpm = spec.idleRpm;
        }

        public float EngineTorqueAt(float rpm)
        {
            float peakT = spec.maxTorque;
            float powerTorque = spec.PowerKw * 1000f / (spec.peakPowerRpm * 2f * Mathf.PI / 60f);
            float t;
            if (rpm < spec.peakTorqueRpmLow) t = Mathf.Lerp(peakT * 0.55f, peakT, Mathf.InverseLerp(spec.idleRpm, spec.peakTorqueRpmLow, rpm));
            else if (rpm <= spec.peakTorqueRpmHigh) t = peakT;
            else if (rpm <= spec.peakPowerRpm) t = Mathf.Lerp(peakT, powerTorque, Mathf.InverseLerp(spec.peakTorqueRpmHigh, spec.peakPowerRpm, rpm));
            else t = Mathf.Lerp(powerTorque, 0f, Mathf.InverseLerp(spec.peakPowerRpm, spec.redlineRpm, rpm));
            return Mathf.Max(0f, t);
        }

        float CurrentRatio()
        {
            if (gearMode == GearMode.Reverse) return -spec.gearRatios[0] * 0.95f;
            if (gearMode == GearMode.Neutral) return 0f;
            return spec.gearRatios[Mathf.Clamp(Gear - 1, 0, spec.gearRatios.Length - 1)];
        }

        void Update()
        {
            if (!externalInput) ReadInput();
            // The cab steering wheel is locked to the driver's input in the same frame (no smoothing = no lag):
            // it shows exactly the on-screen wheel angle, full lock = 450 degrees each way.
            // (Keyboard steering is ramped in ReadInput, so it still turns smoothly.)
            steeringWheelAngle = Mathf.Clamp(steerInput, -1f, 1f) * SteeringWheelLockDeg;
            UpdateVisuals();
        }

        void ReadInput()
        {
            float kThrottle = (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow)) ? 1f : 0f;
            float kBrake = (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow) || Input.GetKey(KeyCode.Space)) ? 1f : 0f;
            float kTarget = 0f;
            if (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow)) kTarget -= 1f;
            if (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow)) kTarget += 1f;
            keySteer = Mathf.MoveTowards(keySteer, kTarget, Time.deltaTime * (kTarget == 0f ? 3.5f : 1.6f));
            float kSteer = keySteer;
            var mc = MobileControls.Instance;
            throttleInput = Mathf.Max(kThrottle, mc ? mc.Throttle : 0f);
            brakeInput = Mathf.Max(kBrake, mc ? mc.Brake : 0f);
            steerInput = Mathf.Clamp(kSteer + (mc ? mc.Steer : 0f), -1f, 1f);
            if (Input.GetKeyDown(KeyCode.R)) CycleGearMode();
            if (lights)
            {
                if (Input.GetKeyDown(KeyCode.L)) lights.Cycle();
                if (Input.GetKeyDown(KeyCode.H)) lights.ToggleHigh();
            }
        }

        public void CycleGearMode()
        {
            if (SpeedKmh > 3f) return; // only change direction when (almost) stopped
            gearMode = gearMode == GearMode.Drive ? GearMode.Neutral : gearMode == GearMode.Neutral ? GearMode.Reverse : GearMode.Drive;
            Gear = 1;
        }

        void FixedUpdate()
        {
            if (rb == null || all.Count == 0 || rb.isKinematic) return;
            float dt = Time.fixedDeltaTime;
            Vector3 localVel = transform.InverseTransformDirection(rb.linearVelocity);
            ForwardSpeed = localVel.z;
            SpeedKmh = Mathf.Abs(ForwardSpeed) * 3.6f;

            // Steering: smooth and speed-sensitive.
            float speedFactor = Mathf.InverseLerp(10f, 110f, SpeedKmh);
            float maxSteer = Mathf.Lerp(MaxSteerDeg, 7f, speedFactor);
            smoothSteer = Mathf.MoveTowards(smoothSteer, steerInput, dt * 1.8f);
            SteerAngle = smoothSteer * maxSteer;
            foreach (var wc in steerWheels) wc.steerAngle = SteerAngle;

            float wheelRpm = 0f;
            foreach (var wc in driveWheels) wheelRpm += wc.rpm;
            wheelRpm /= Mathf.Max(1, driveWheels.Count);
            float ratio = CurrentRatio();
            float totalRatio = ratio * finalDrive;
            float throttle = throttleInput;
            float brake = brakeInput;

            // Speed limiter.
            if (SpeedKmh > spec.topSpeedKmh) throttle = 0f;
            else if (SpeedKmh > spec.topSpeedKmh - 4f) throttle *= Mathf.InverseLerp(spec.topSpeedKmh, spec.topSpeedKmh - 4f, SpeedKmh);

            // Engine rpm with a slipping clutch at low speed.
            float coupledRpm = Mathf.Abs(wheelRpm * totalRatio);
            float clutch = gearMode == GearMode.Neutral ? 0f : Mathf.Clamp01((coupledRpm - spec.idleRpm * 0.6f) / (spec.idleRpm * 0.9f));
            float freeRpm = Mathf.Lerp(spec.idleRpm, spec.redlineRpm * 0.85f, throttle);
            float launchRpm = Mathf.Lerp(spec.idleRpm, 1150f, throttle);
            float targetRpm = clutch >= 1f ? coupledRpm : Mathf.Lerp(gearMode == GearMode.Neutral ? freeRpm : launchRpm, Mathf.Max(coupledRpm, launchRpm), clutch);
            if (Shifting) targetRpm = Mathf.Lerp(EngineRpm, coupledRpm, 0.5f);
            EngineRpm = Mathf.Clamp(Mathf.Lerp(EngineRpm, targetRpm, dt * 8f), spec.idleRpm * 0.95f, spec.redlineRpm);

            // Automatic shifting (forward only).
            if (gearMode == GearMode.Drive && !Shifting)
            {
                int maxGear = spec.gearRatios.Length;
                float upRpm = Mathf.Lerp(1350f, 1800f, throttle);
                if (EngineRpm > upRpm && Gear < maxGear && clutch >= 1f && ForwardSpeed > 1f)
                {
                    int step = (Gear < 6 && throttle < 0.6f) ? 2 : 1;
                    Gear = Mathf.Min(maxGear, Gear + step);
                    shiftTimer = spec.gearRatios.Length > 12 ? 0.25f : 0.35f;
                }
                else if (Gear > 1)
                {
                    float lowerRpm = Mathf.Abs(wheelRpm * spec.gearRatios[Gear - 2] * finalDrive);
                    if (EngineRpm < 950f && lowerRpm < spec.redlineRpm * 0.85f) { Gear--; shiftTimer = 0.3f; }
                }
                if (ForwardSpeed < 0.5f && throttle < 0.05f) Gear = 1;
            }
            if (shiftTimer > 0f) shiftTimer -= dt;

            // Drive torque.
            float engineTorque = EngineTorqueAt(EngineRpm) * throttle;
            if (Shifting) engineTorque = 0f;
            float driveTorque = engineTorque * totalRatio * 0.88f;
            if (clutch < 1f && gearMode != GearMode.Neutral) driveTorque = EngineTorqueAt(launchRpm) * throttle * totalRatio * 0.88f;
            Throttle = throttle;

            // Engine brake (exhaust brake / retarder) when coasting.
            float engineBrake = (throttle < 0.05f && gearMode == GearMode.Drive && SpeedKmh > 5f) ? 900f + 120f * Gear : 0f;
            if (gearMode == GearMode.Reverse && SpeedKmh > 18f) driveTorque = 0f;

            float perDrive = driveTorque / Mathf.Max(1, driveWheels.Count);
            float brakeTorquePerWheel = brake * 16000f;
            bool parked = throttle < 0.02f && SpeedKmh < 1.2f;
            foreach (var wc in all)
            {
                bool isDrive = driveWheels.Contains(wc);
                wc.motorTorque = isDrive ? perDrive : 0f;
                float bt = brakeTorquePerWheel + (isDrive ? engineBrake / driveWheels.Count * finalDrive : 0f);
                if (parked) bt = Mathf.Max(bt, 6000f);
                wc.brakeTorque = bt;
            }

            AntiRoll();
            float v = rb.linearVelocity.magnitude;
            if (v > 0.1f) rb.AddForce(-rb.linearVelocity.normalized * (0.5f * 1.2f * 0.6f * 10f * v * v), ForceMode.Force);
        }

        void AntiRoll()
        {
            float antiRoll = spec.massKg * 30f;
            for (int i = 0; i < visuals.Count; i++)
            {
                var a = visuals[i];
                if (!a.vis.left) continue;
                for (int j = 0; j < visuals.Count; j++)
                {
                    var c = visuals[j];
                    if (c.vis.left || Mathf.Abs(c.vis.localCenter.z - a.vis.localCenter.z) > 0.3f) continue;
                    float travelL = 1f, travelR = 1f;
                    bool gl = a.wc.GetGroundHit(out var hitL);
                    if (gl) travelL = (-a.wc.transform.InverseTransformPoint(hitL.point).y - a.wc.radius) / a.wc.suspensionDistance;
                    bool gr = c.wc.GetGroundHit(out var hitR);
                    if (gr) travelR = (-c.wc.transform.InverseTransformPoint(hitR.point).y - c.wc.radius) / c.wc.suspensionDistance;
                    float force = (travelL - travelR) * antiRoll;
                    if (gl) rb.AddForceAtPosition(a.wc.transform.up * -force, a.wc.transform.position);
                    if (gr) rb.AddForceAtPosition(c.wc.transform.up * force, c.wc.transform.position);
                    break;
                }
            }
        }

        void UpdateVisuals()
        {
            foreach (var (wc, vis) in visuals)
            {
                if (vis.pivot == null) continue;
                spin[wc] = (spin[wc] + wc.rpm * 6f * Time.deltaTime) % 360f;
                float steer = vis.front ? wc.steerAngle : 0f;
                vis.pivot.localRotation = Quaternion.Euler(0, steer, 0) * Quaternion.Euler(spin[wc], 0, 0);
            }
            if (rig != null) rig.SetSteeringWheel(steeringWheelAngle);
        }

        public void ResetOnRoad(Vector3 pos, Quaternion rot)
        {
            rb.linearVelocity = Vector3.zero; rb.angularVelocity = Vector3.zero;
            transform.SetPositionAndRotation(pos + Vector3.up * 0.4f, rot);
            Physics.SyncTransforms();
            rb.position = transform.position; rb.rotation = rot;
            Gear = 1; gearMode = GearMode.Drive; EngineRpm = spec.idleRpm; smoothSteer = 0f; steeringWheelAngle = 0f;
        }
    }
}
