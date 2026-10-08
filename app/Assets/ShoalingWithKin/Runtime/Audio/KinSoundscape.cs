using System.Collections.Generic;
using UnityEngine;

namespace ShoalingWithKin
{
    /// <summary>
    /// One looping voice per kin, placed at the touch point so the music comes from the stone or
    /// the tree. Fades in on a match and out on release.
    /// </summary>
    public class KinSoundscape : MonoBehaviour
    {
        [SerializeField, Range(0f, 1f)] float m_Volume = 0.9f;
        [SerializeField] float m_FadeIn = 1.2f;
        [SerializeField] float m_FadeOut = 2.5f;
        [Tooltip("0 = heard in the head, 1 = fully from the touch point.")]
        [SerializeField, Range(0f, 1f)] float m_Spatial = 0.75f;

        readonly Dictionary<string, AudioSource> m_Sources = new();
        readonly Dictionary<string, float> m_Targets = new();

        public void Prepare(IEnumerable<KinDefinition> kin)
        {
            foreach (var k in kin)
            {
                if (m_Sources.ContainsKey(k.Id))
                    continue;
                var go = new GameObject($"Voice: {k.Id}");
                go.transform.SetParent(transform, false);
                var source = go.AddComponent<AudioSource>();
                source.clip = k.Music != null ? k.Music : ProceduralMusic.Create(k.Voice, k.Id);
                source.loop = true;
                source.playOnAwake = false;
                source.volume = 0f;
                source.spatialBlend = m_Spatial;
                source.rolloffMode = AudioRolloffMode.Logarithmic;
                source.minDistance = 0.6f;
                source.maxDistance = 10f;
                m_Sources[k.Id] = source;
                m_Targets[k.Id] = 0f;
            }
        }

        public void Play(KinDefinition kin, Vector3 at)
        {
            if (kin == null || !m_Sources.TryGetValue(kin.Id, out var source))
                return;
            source.transform.position = at;
            if (!source.isPlaying)
                source.Play();
            m_Targets[kin.Id] = m_Volume;
        }

        public void Stop(KinDefinition kin)
        {
            if (kin != null && m_Targets.ContainsKey(kin.Id))
                m_Targets[kin.Id] = 0f;
        }

        void Update()
        {
            foreach (var (id, source) in m_Sources)
            {
                float target = m_Targets[id];
                float rate = target > source.volume ? m_Volume / Mathf.Max(0.01f, m_FadeIn) : m_Volume / Mathf.Max(0.01f, m_FadeOut);
                source.volume = Mathf.MoveTowards(source.volume, target, rate * Time.deltaTime);
                if (target <= 0f && source.volume <= 0f && source.isPlaying)
                    source.Stop();
            }
        }
    }
}
