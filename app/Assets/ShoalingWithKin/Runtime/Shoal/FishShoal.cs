using UnityEngine;
using UnityEngine.Rendering;

namespace ShoalingWithKin
{
    /// <summary>
    /// A shoal of instanced fish circling the wearer. It leans toward the hand as a kin's
    /// probability rises, and on a match makes camp with that kin: Enter dives into the touch
    /// point, Climb spirals up around the trunk. Release sends them back out around the wearer.
    /// </summary>
    public class FishShoal : MonoBehaviour
    {
        [SerializeField] Material m_Material;
        [SerializeField, Range(8, 1023)] int m_Count = 96;
        [Tooltip("Fish length range in metres.")]
        [SerializeField] Vector2 m_Length = new(0.05f, 0.09f);
        [SerializeField] Color[] m_Palette =
        {
            new(1f, 0.78f, 0.3f), new(1f, 0.55f, 0.25f), new(1f, 0.45f, 0.4f), new(0.75f, 0.95f, 1f),
        };

        [Header("Roaming around the wearer")]
        [SerializeField] float m_OrbitRadius = 1.3f;
        [SerializeField] float m_OrbitSpread = 0.45f;
        [SerializeField] float m_BelowEyes = 0.25f;
        [SerializeField] float m_HeightSpread = 0.5f;
        [SerializeField] float m_CruiseSpeed = 0.35f;
        [SerializeField] float m_MaxSpeed = 1.4f;
        [Tooltip("Fish never swim closer than this to the wearer's eyes, even on the way to camp.")]
        [SerializeField] float m_HeadClearance = 0.45f;

        [Header("Boids")]
        [SerializeField] float m_NeighbourRadius = 0.3f;
        [SerializeField] float m_SeparationRadius = 0.09f;
        [SerializeField] float m_Separation = 1.6f;
        [SerializeField] float m_Alignment = 0.6f;
        [SerializeField] float m_Cohesion = 0.3f;

        [Header("Making camp")]
        [SerializeField] float m_CampSpeed = 0.9f;
        [Tooltip("Fish start slipping into the kin this close to the touch point.")]
        [SerializeField] float m_EnterRadius = 0.12f;
        [Tooltip("Fish leave for camp over this many seconds, not all at once.")]
        [SerializeField] float m_Stagger = 1.5f;
        [SerializeField] float m_TrunkRadius = 0.2f;
        [SerializeField] float m_ClimbHeight = 1.4f;
        [SerializeField] float m_ClimbSeconds = 5f;

        static readonly int ColorId = Shader.PropertyToID("_Color");
        static readonly int PhaseId = Shader.PropertyToID("_Phase");

        Mesh m_Mesh;
        MaterialPropertyBlock m_Props;
        Vector3[] m_Pos, m_Vel;
        float[] m_Phase, m_Size, m_Submerge, m_Climb, m_Delay, m_RadiusOffset, m_HeightOffset;
        Color[] m_Base;
        Matrix4x4[] m_Matrices;
        Vector4[] m_Colors;

        bool m_Camping;
        ShoalGesture m_Gesture;
        Vector3 m_CampPoint, m_TrunkAxis, m_LeanPoint;
        float m_CampStart, m_LeanWeight, m_Tint;
        float m_Clock;  // simulation time, so the shoal can be stepped outside Play mode (previews)
        Color m_TintColor = Color.white;

        public bool Camping => m_Camping;

        /// <summary>Pull toward <paramref name="point"/> with <paramref name="weight"/> (0–1), tinting toward <paramref name="color"/>.</summary>
        public void Lean(Vector3 point, Color color, float weight)
        {
            m_LeanPoint = point;
            m_LeanWeight = Mathf.Clamp01(weight);
            if (!m_Camping && weight > 0.05f)
                m_TintColor = color;
        }

        public void Camp(KinDefinition kin, Vector3 point)
        {
            var head = HeadPose.Get();
            var away = head != null ? point - head.position : Vector3.forward;
            away.y = 0f;
            away = away.sqrMagnitude > 1e-4f ? away.normalized : Vector3.forward;
            m_Camping = true;
            m_Gesture = kin.Gesture;
            // A trunk's axis is behind the touched bark; a stone is entered just past the hand.
            m_TrunkAxis = point + away * m_TrunkRadius;
            m_CampPoint = kin.Gesture == ShoalGesture.Enter ? point + away * 0.06f : point;
            m_CampStart = m_Clock;
            m_TintColor = kin.Color;
            for (int i = 0; i < m_Count; i++)
            {
                m_Climb[i] = 0f;
                m_Delay[i] = Random.value * m_Stagger;
            }
        }

        public void Release()
        {
            if (!m_Camping)
                return;
            m_Camping = false;
            for (int i = 0; i < m_Count; i++)
            {
                if (m_Submerge[i] <= 0f)
                    continue;
                var burst = Random.onUnitSphere;
                burst.y = Mathf.Abs(burst.y) * 0.6f;
                m_Vel[i] = burst * (m_CruiseSpeed * 2.5f);
            }
        }

        void Awake() => EnsureInitialized();

        /// <summary>Allocates and scatters the fish around the head; safe to call more than once.</summary>
        public void EnsureInitialized()
        {
            if (m_Pos != null)
                return;
            m_Count = Mathf.Clamp(m_Count, 1, 1023);
            m_Mesh = FishMesh.Create();
            m_Props = new MaterialPropertyBlock();
            m_Pos = new Vector3[m_Count];
            m_Vel = new Vector3[m_Count];
            m_Phase = new float[m_Count];
            m_Size = new float[m_Count];
            m_Submerge = new float[m_Count];
            m_Climb = new float[m_Count];
            m_Delay = new float[m_Count];
            m_RadiusOffset = new float[m_Count];
            m_HeightOffset = new float[m_Count];
            m_Base = new Color[m_Count];
            m_Matrices = new Matrix4x4[m_Count];
            m_Colors = new Vector4[m_Count];

            var head = HeadPose.Get();
            var centre = head != null ? head.position : Vector3.zero;
            for (int i = 0; i < m_Count; i++)
            {
                float a = Random.value * Mathf.PI * 2f;
                m_RadiusOffset[i] = (Random.value - 0.5f) * m_OrbitSpread;
                m_HeightOffset[i] = (Random.value - 0.5f) * m_HeightSpread;
                float r = m_OrbitRadius + m_RadiusOffset[i];
                m_Pos[i] = centre + new Vector3(Mathf.Cos(a) * r, m_HeightOffset[i] - m_BelowEyes, Mathf.Sin(a) * r);
                m_Vel[i] = new Vector3(-Mathf.Sin(a), 0f, Mathf.Cos(a)) * m_CruiseSpeed;
                m_Phase[i] = Random.value * Mathf.PI * 2f;
                m_Size[i] = Mathf.Lerp(m_Length.x, m_Length.y, Random.value);
                m_Base[i] = m_Palette.Length > 0 ? m_Palette[i % m_Palette.Length] : Color.white;
            }
        }

        void OnDestroy()
        {
            if (m_Mesh != null)
                Destroy(m_Mesh);
        }

        void Update()
        {
            Step(Mathf.Min(Time.deltaTime, 0.05f));
            Draw();
        }

        /// <summary>Advances the shoal by <paramref name="dt"/> seconds.</summary>
        public void Step(float dt)
        {
            m_Clock += dt;
            var head = HeadPose.Get();
            var centre = head != null ? head.position : Vector3.zero;
            float targetTint = m_Camping ? 1f : m_LeanWeight * 0.5f;
            m_Tint = Mathf.MoveTowards(m_Tint, targetTint, dt * 1.5f);

            for (int i = 0; i < m_Count; i++)
            {
                bool waiting = m_Camping && m_Clock - m_CampStart < m_Delay[i];
                Vector3 steer = m_Camping && !waiting ? CampSteer(i, dt) : RoamSteer(i, centre) + Flock(i) + LeanSteer(i);
                // Roaming (or not yet leaving for a new camp after a switch) fish re-emerge.
                if (!m_Camping || waiting)
                    m_Submerge[i] = Mathf.Max(0f, m_Submerge[i] - dt / 0.8f);
                if (m_Submerge[i] >= 1f)
                {
                    m_Vel[i] = Vector3.zero;
                    continue;
                }
                steer += AvoidHead(i, centre);
                m_Vel[i] = Vector3.ClampMagnitude(m_Vel[i] + steer * dt, m_MaxSpeed);
                m_Pos[i] += m_Vel[i] * dt;
            }
        }

        Vector3 RoamSteer(int i, Vector3 centre)
        {
            var rel = m_Pos[i] - centre;
            var flat = new Vector3(rel.x, 0f, rel.z);
            float r = Mathf.Max(flat.magnitude, 0.01f);
            var radial = flat / r;
            var tangent = Vector3.Cross(Vector3.up, radial);
            float targetY = centre.y - m_BelowEyes + m_HeightOffset[i] + 0.1f * Mathf.Sin(m_Clock * 0.5f + m_Phase[i]);
            var desired = tangent * m_CruiseSpeed
                          + radial * ((m_OrbitRadius + m_RadiusOffset[i] - r) * 1.2f)
                          + Vector3.up * ((targetY - m_Pos[i].y) * 1.2f);
            return (desired - m_Vel[i]) * 1.5f;
        }

        Vector3 AvoidHead(int i, Vector3 centre)
        {
            var away = m_Pos[i] - centre;
            float d = away.magnitude;
            if (d >= m_HeadClearance)
                return Vector3.zero;
            var dir = d > 1e-4f ? away / d : Vector3.up;
            return dir * ((m_HeadClearance - d) * 40f);
        }

        Vector3 LeanSteer(int i)
        {
            if (m_LeanWeight <= 0.05f || m_Camping)
                return Vector3.zero;
            var desired = (m_LeanPoint - m_Pos[i]).normalized * (m_CruiseSpeed * 2f);
            return (desired - m_Vel[i]) * (m_LeanWeight * 1.2f);
        }

        Vector3 Flock(int i)
        {
            Vector3 separation = default, alignment = default, centreSum = default;
            int neighbours = 0;
            float nr2 = m_NeighbourRadius * m_NeighbourRadius, sr2 = m_SeparationRadius * m_SeparationRadius;
            for (int j = 0; j < m_Count; j++)
            {
                if (j == i || m_Submerge[j] >= 1f)
                    continue;
                var d = m_Pos[i] - m_Pos[j];
                float d2 = d.sqrMagnitude;
                if (d2 > nr2)
                    continue;
                neighbours++;
                alignment += m_Vel[j];
                centreSum += m_Pos[j];
                if (d2 < sr2 && d2 > 1e-8f)
                    separation += d / d2;
            }
            if (neighbours == 0)
                return Vector3.zero;
            alignment = alignment / neighbours - m_Vel[i];
            var cohesion = centreSum / neighbours - m_Pos[i];
            return separation * (m_Separation * 0.01f) + alignment * m_Alignment + cohesion * m_Cohesion;
        }

        Vector3 CampSteer(int i, float dt)
        {
            if (m_Gesture == ShoalGesture.Climb)
            {
                m_Climb[i] = Mathf.Min(1f, m_Climb[i] + dt / m_ClimbSeconds);
                float a = m_Phase[i] + m_Clock * 1.6f;
                float radius = m_TrunkRadius + 0.12f;
                var target = m_TrunkAxis + new Vector3(Mathf.Cos(a) * radius, m_Climb[i] * m_ClimbHeight, Mathf.Sin(a) * radius);
                float fade = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.75f, 1f, m_Climb[i]));
                m_Submerge[i] = Mathf.MoveTowards(m_Submerge[i], fade, dt / 0.6f);
                return (Vector3.ClampMagnitude((target - m_Pos[i]) * 4f, m_MaxSpeed) - m_Vel[i]) * 4f;
            }

            var to = m_CampPoint - m_Pos[i];
            float d = to.magnitude;
            var dir = d > 1e-4f ? to / d : Vector3.zero;
            var swirl = Vector3.Cross(Vector3.up, dir) * (Mathf.Min(d, 0.3f) * 1.5f);
            var desired = dir * (m_CampSpeed * Mathf.Clamp01(d / 0.25f + 0.2f)) + swirl;
            // Slip in at the touch point; anywhere else (e.g. hidden at a treetop before a switch) re-emerge.
            m_Submerge[i] = d < m_EnterRadius
                ? Mathf.Min(1f, m_Submerge[i] + dt / 0.5f)
                : Mathf.Max(0f, m_Submerge[i] - dt / 0.8f);
            return (desired - m_Vel[i]) * 3f;
        }

        void Draw()
        {
            if (m_Material == null || !PrepareInstances())
                return;
            Graphics.DrawMeshInstanced(m_Mesh, 0, m_Material, m_Matrices, m_Count, m_Props,
                ShadowCastingMode.Off, false, gameObject.layer);
        }

        /// <summary>Draws the shoal into a command buffer (offline previews render it with a camera).</summary>
        public void Record(CommandBuffer commands)
        {
            if (m_Material != null && PrepareInstances())
                commands.DrawMeshInstanced(m_Mesh, 0, m_Material, 0, m_Matrices, m_Count, m_Props);
        }

        bool PrepareInstances()
        {
            if (m_Pos == null)
                return false;
            for (int i = 0; i < m_Count; i++)
            {
                float scale = m_Size[i] * (1f - Mathf.SmoothStep(0f, 1f, m_Submerge[i]));
                var rotation = m_Vel[i].sqrMagnitude > 1e-6f ? Quaternion.LookRotation(m_Vel[i]) : Quaternion.identity;
                m_Matrices[i] = Matrix4x4.TRS(m_Pos[i], rotation, Vector3.one * scale);
                // SetVectorArray skips the sRGB→linear conversion SetColor does (the project is Linear).
                m_Colors[i] = Color.Lerp(m_Base[i], m_TintColor, m_Tint * 0.85f).linear;
            }
            m_Props.SetVectorArray(ColorId, m_Colors);
            m_Props.SetFloatArray(PhaseId, m_Phase);
            return true;
        }
    }
}
