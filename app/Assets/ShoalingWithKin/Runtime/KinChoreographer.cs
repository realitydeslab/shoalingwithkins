using UnityEngine;

namespace ShoalingWithKin
{
    /// <summary>
    /// Wires touch detection to the shoal and the music: as a kin's probability rises the shoal
    /// leans toward the hand; on a match the music starts at the touch point and the fish make
    /// camp with the kin; on release both let go.
    /// </summary>
    public class KinChoreographer : MonoBehaviour
    {
        [SerializeField] TouchKinDetector m_Detector;
        [SerializeField] FishShoal m_Shoal;
        [SerializeField] KinSoundscape m_Soundscape;

        void Awake()
        {
            if (m_Detector == null) m_Detector = FindFirstObjectByType<TouchKinDetector>();
            if (m_Shoal == null) m_Shoal = FindFirstObjectByType<FishShoal>();
            if (m_Soundscape == null) m_Soundscape = FindFirstObjectByType<KinSoundscape>();
        }

        void Start()
        {
            if (m_Detector == null)
            {
                Debug.LogError("[KinChoreographer] No TouchKinDetector in the scene.");
                enabled = false;
                return;
            }
            m_Soundscape?.Prepare(m_Detector.Kin);
            m_Detector.KinMatched += OnMatched;
            m_Detector.KinReleased += OnReleased;
            m_Detector.Updated += OnUpdated;
        }

        void OnDestroy()
        {
            if (m_Detector == null)
                return;
            m_Detector.KinMatched -= OnMatched;
            m_Detector.KinReleased -= OnReleased;
            m_Detector.Updated -= OnUpdated;
        }

        void OnMatched(KinDefinition kin, Vector3 point)
        {
            m_Soundscape?.Play(kin, point);
            m_Shoal?.Camp(kin, point);
        }

        void OnReleased(KinDefinition kin)
        {
            m_Soundscape?.Stop(kin);
            m_Shoal?.Release();
        }

        void OnUpdated()
        {
            var matcher = m_Detector.Matcher;
            if (m_Shoal == null || matcher.Matched != null)
                return;
            var leading = m_Detector.Find(matcher.Leading);
            float weight = leading != null ? Mathf.InverseLerp(0.15f, matcher.Settings.EnterThreshold, matcher.LeadingProbability) : 0f;
            m_Shoal.Lean(m_Detector.ObservedPoint, leading?.Color ?? Color.white, weight);
        }
    }
}
