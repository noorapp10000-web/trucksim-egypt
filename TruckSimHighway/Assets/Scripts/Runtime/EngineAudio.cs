using UnityEngine;

namespace TruckSim
{
    /// <summary>Procedural diesel engine sound (no audio files needed): firing pulses + turbo whistle + rumble.</summary>
    [RequireComponent(typeof(AudioSource))]
    public class EngineAudio : MonoBehaviour
    {
        public TruckController truck;
        volatile float rpm = 600f, load = 0f;
        double phase, phase2, turboPhase;
        int sampleRate;
        readonly System.Random rnd = new System.Random();
        float lowpass;

        void Start()
        {
            sampleRate = AudioSettings.outputSampleRate;
            var src = GetComponent<AudioSource>();
            src.spatialBlend = 0f; src.loop = true; src.volume = 0.5f;
            src.Play();
        }

        void Update()
        {
            if (truck == null) return;
            rpm = truck.EngineRpm; load = truck.Throttle;
        }

        void OnAudioFilterRead(float[] data, int channels)
        {
            if (sampleRate == 0) return;
            float r = rpm, l = load;
            double fire = r / 60.0 * 3.0;          // 6 cylinders, 4 stroke
            double turbo = 1800 + r * 1.6;
            for (int i = 0; i < data.Length; i += channels)
            {
                phase += fire / sampleRate; if (phase > 1) phase -= 1;
                phase2 += fire * 0.5 / sampleRate; if (phase2 > 1) phase2 -= 1;
                turboPhase += turbo / sampleRate; if (turboPhase > 1) turboPhase -= 1;
                float pulse = Mathf.Exp(-(float)phase * 9f) * 2f - 0.25f;
                float sub = Mathf.Sin((float)(phase2 * Mathf.PI * 2));
                float noise = (float)(rnd.NextDouble() * 2 - 1);
                float raw = pulse * (0.35f + l * 0.4f) + sub * 0.25f + noise * (0.05f + l * 0.08f);
                lowpass += (raw - lowpass) * 0.18f;
                float whistle = Mathf.Sin((float)(turboPhase * Mathf.PI * 2)) * 0.02f * l * Mathf.InverseLerp(900, 2000, r);
                float s = (lowpass + whistle) * 0.5f;
                for (int c = 0; c < channels; c++) data[i + c] = s;
            }
        }
    }
}
