using System;
using UnityEngine;

namespace TruckSim
{
    /// <summary>Real-world inspired technical data for one truck.</summary>
    [Serializable]
    public class TruckSpec
    {
        public string id;              // Resources/Trucks/<id>
        public string displayName;
        public string engine;
        public float horsePower;       // hp
        public float maxTorque;        // Nm
        public float massKg;           // tractor curb mass
        public float topSpeedKmh;      // speed limiter
        public float lengthMeters;     // overall tractor length used to scale the model
        public float wheelbase;        // used when wheel meshes cannot be detected
        public int axles = 2;
        public int drivenRearAxles = 1;  // 6x2 = 1, 6x4 = 2
        public string credit = "";       // model author / licence (Sketchfab)
        public string gearbox;
        public float[] gearRatios;
        public float idleRpm = 600f;
        public float redlineRpm = 2300f;
        public float peakTorqueRpmLow = 1000f;
        public float peakTorqueRpmHigh = 1400f;
        public float peakPowerRpm = 1900f;
        public float wheelRadius = 0.53f;
        // Orientation fix (degrees around Y) applied instead of automatic front detection. -1 = auto.
        public float yawOverride = -1f;
        // Driver eye position, normalised to the model bounds (x: -1 left..1 right, y: 0..1, z: -1 rear..1 front).
        public Vector3 eyeNormalized = new Vector3(-0.45f, 0.66f, 0.62f);
        // Optional fine offset added to the computed eye position (metres).
        public Vector3 eyeOffset = Vector3.zero;
        public bool useSteeringWheelForEye = true;
        // Approximate steering wheel centre in truck space (after normalisation) and optional column axis, from QA analysis.
        public Vector3 steeringWheelHint = Vector3.zero;
        public Vector3 steeringAxisHint = Vector3.zero;
        public float steeringRadiusHint = 0f;

        public float PowerKw => horsePower * 0.7457f;
    }

    public static class TruckDatabase
    {
        static readonly float[] Ratio12 = { 14.93f, 11.64f, 9.02f, 7.04f, 5.64f, 4.40f, 3.39f, 2.65f, 2.05f, 1.60f, 1.28f, 1.00f };
        static readonly float[] Ratio14 = { 16.41f, 13.80f, 11.28f, 9.49f, 7.76f, 6.53f, 5.43f, 4.57f, 3.74f, 3.14f, 2.53f, 2.13f, 1.19f, 1.00f };
        static readonly float[] Ratio13 = { 12.57f, 8.38f, 6.22f, 4.57f, 3.40f, 2.46f, 2.08f, 1.83f, 1.54f, 1.35f, 1.14f, 1.00f, 0.84f };
        static readonly float[] Ratio16 = { 16.69f, 13.91f, 11.37f, 9.48f, 7.78f, 6.49f, 5.30f, 4.42f, 3.77f, 3.14f, 2.57f, 2.14f, 1.75f, 1.46f, 1.19f, 1.00f };

        /// <summary>Five trucks ordered from the weakest to the strongest.</summary>
        public static readonly TruckSpec[] All =
        {
            new TruckSpec {
                id = "T1_ScaniaR420", displayName = "Scania R420 Highline", engine = "Scania DC12 11.7L inline-6",
                horsePower = 420, maxTorque = 2100, massKg = 7200, topSpeedKmh = 115, lengthMeters = 5.95f, wheelbase = 3.70f,
                gearbox = "Scania Opticruise GRS905 12+2", gearRatios = Ratio12, redlineRpm = 2300, peakPowerRpm = 1900,
                steeringWheelHint = new Vector3(-0.55f, 2.15f, 2.10f) },
            new TruckSpec {
                id = "T3_VolvoFH460", displayName = "Volvo FH 460 Globetrotter", engine = "Volvo D13K 12.8L inline-6",
                horsePower = 460, maxTorque = 2300, massKg = 8900, topSpeedKmh = 125, lengthMeters = 6.95f, wheelbase = 3.90f, axles = 3,
                gearbox = "Volvo I-Shift AT2612F 12-speed", gearRatios = Ratio12, redlineRpm = 2200, peakPowerRpm = 1800,
                steeringWheelHint = new Vector3(-0.64f, 2.16f, 2.69f), steeringRadiusHint = 0.25f },
            new TruckSpec {
                id = "T4_DafXF95", displayName = "DAF XF 95.480 Super Space Cab", engine = "PACCAR XE 12.6L inline-6",
                horsePower = 480, maxTorque = 2300, massKg = 8700, topSpeedKmh = 130, lengthMeters = 6.80f, wheelbase = 3.80f, axles = 3,
                gearbox = "ZF 16S2220 TD 16-speed", gearRatios = Ratio16, redlineRpm = 2200, peakPowerRpm = 1900,
                steeringWheelHint = new Vector3(-0.72f, 1.93f, 2.79f) },
            new TruckSpec {
                id = "T5_CatCT680", displayName = "Caterpillar CT680 Day Cab", engine = "Cat CT15 15.2L inline-6",
                horsePower = 550, maxTorque = 2508, massKg = 9100, topSpeedKmh = 135, lengthMeters = 7.60f, wheelbase = 5.30f, axles = 3,
                gearbox = "Eaton Fuller UltraShift PLUS 13-speed", gearRatios = Ratio13, redlineRpm = 2100, peakPowerRpm = 1800,
                drivenRearAxles = 2, eyeNormalized = new Vector3(-0.40f, 0.66f, 0.26f) },
            new TruckSpec {
                id = "T6_ScaniaS730", displayName = "Scania S730 V8 Highline 6x4", engine = "Scania DC16 16.4L V8",
                horsePower = 730, maxTorque = 3500, massKg = 9800, topSpeedKmh = 140, lengthMeters = 6.85f, wheelbase = 3.90f, axles = 3, drivenRearAxles = 2,
                gearbox = "Scania Opticruise GRSO926R 14-speed", gearRatios = Ratio14, redlineRpm = 2300, peakPowerRpm = 1900,
                steeringWheelHint = new Vector3(-0.61f, 2.15f, 2.62f),
                credit = "\"SCANIA S730 V8\" by Rick_modding (sketchfab.com/Rick_modding_), CC-BY-4.0" },
        };
    }
}
