using UnityEngine;

namespace ShoalingWithKin
{
    /// <summary>
    /// Seamless 8-second loops for kin without a Music clip. Sustained tones use frequencies with a
    /// whole number of cycles in the loop; struck notes decay and wrap around the loop's end.
    /// </summary>
    public static class ProceduralMusic
    {
        const int SampleRate = 44100;
        const float Seconds = 8f;

        public static AudioClip Create(KinVoice voice, string name)
        {
            int n = (int)(SampleRate * Seconds);
            var data = new float[n];
            var rng = new System.Random(name.GetHashCode());
            switch (voice)
            {
                case KinVoice.Stone:
                    Stone(data);
                    break;
                case KinVoice.Leaves:
                    Leaves(data, rng);
                    break;
                default:
                    Water(data, rng);
                    break;
            }
            Normalise(data, 0.8f);
            var clip = AudioClip.Create($"{name} ({voice})", n, 1, SampleRate, false);
            clip.SetData(data, 0);
            return clip;
        }

        /// <summary>A low open fifth that breathes, with a deep struck stone every two seconds.</summary>
        static void Stone(float[] d)
        {
            float[] partials = { 55f, 110f, 165f, 247.5f };
            float[] gains = { 0.5f, 0.35f, 0.25f, 0.1f };
            for (int i = 0; i < d.Length; i++)
            {
                float t = i / (float)SampleRate;
                float swell = 0.65f + 0.35f * Mathf.Sin(2f * Mathf.PI * 0.25f * t);
                float s = 0f;
                for (int p = 0; p < partials.Length; p++)
                    s += gains[p] * Mathf.Sin(2f * Mathf.PI * partials[p] * t + p);
                d[i] += s * swell * 0.5f;
            }
            for (int k = 0; k < 4; k++)
                Strike(d, k * 2f + 0.05f, 82.5f, 0.6f, 2.5f, inharmonic: 2.2f);
        }

        /// <summary>Pentatonic chimes over a soft pad, a note every half second.</summary>
        static void Leaves(float[] d, System.Random rng)
        {
            float[] scale = { 587.33f, 659.25f, 739.99f, 880f, 987.77f, 1174.66f };
            for (int i = 0; i < d.Length; i++)
            {
                float t = i / (float)SampleRate;
                d[i] += 0.12f * (Mathf.Sin(2f * Mathf.PI * 147f * t) + Mathf.Sin(2f * Mathf.PI * 220f * t))
                        * (0.7f + 0.3f * Mathf.Sin(2f * Mathf.PI * 0.125f * t));
            }
            for (int k = 0; k < 16; k++)
            {
                if (rng.NextDouble() < 0.2)
                    continue;
                float f = scale[rng.Next(scale.Length)];
                Strike(d, k * 0.5f + (float)rng.NextDouble() * 0.06f, f, 0.25f + 0.2f * (float)rng.NextDouble(), 1.4f, inharmonic: 2.76f);
            }
        }

        /// <summary>Soft bubbles rising.</summary>
        static void Water(float[] d, System.Random rng)
        {
            for (int k = 0; k < 40; k++)
            {
                float start = (float)rng.NextDouble() * Seconds;
                float f0 = 400f + (float)rng.NextDouble() * 500f;
                int begin = (int)(start * SampleRate), length = (int)(0.08f * SampleRate);
                for (int j = 0; j < length; j++)
                {
                    float t = j / (float)SampleRate;
                    float env = Mathf.Sin(Mathf.PI * j / length);
                    d[(begin + j) % d.Length] += 0.3f * env * Mathf.Sin(2f * Mathf.PI * f0 * (1f + 6f * t) * t);
                }
            }
        }

        /// <summary>A struck note with an exponential decay and one inharmonic partial, wrapped around the loop.</summary>
        static void Strike(float[] d, float at, float frequency, float gain, float decay, float inharmonic)
        {
            int begin = (int)(at * SampleRate);
            int length = Mathf.Min(d.Length, (int)(decay * 4f * SampleRate));
            for (int j = 0; j < length; j++)
            {
                float t = j / (float)SampleRate;
                float env = Mathf.Exp(-t / decay * 3f) * Mathf.Min(1f, t * 200f);
                float s = Mathf.Sin(2f * Mathf.PI * frequency * t) + 0.3f * Mathf.Sin(2f * Mathf.PI * frequency * inharmonic * t) * Mathf.Exp(-t * 4f);
                d[(begin + j) % d.Length] += gain * env * s;
            }
        }

        static void Normalise(float[] d, float peak)
        {
            float max = 1e-6f;
            foreach (var v in d)
                max = Mathf.Max(max, Mathf.Abs(v));
            float g = peak / max;
            for (int i = 0; i < d.Length; i++)
                d[i] *= g;
        }
    }
}
