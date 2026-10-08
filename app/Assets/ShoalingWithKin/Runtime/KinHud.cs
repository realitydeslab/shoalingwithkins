using System.Collections.Generic;
using HoloKit;
using UnityEngine;

namespace ShoalingWithKin
{
    /// <summary>
    /// Live touch probabilities. Mono: screen bars, latency/cost stats and the frame Luna saw.
    /// Stereo: world-space bars below the gaze (TRIGGER toggles them) and the stereo info band.
    /// </summary>
    public class KinHud : MonoBehaviour
    {
        [SerializeField] TouchKinDetector m_Detector;
        [Tooltip("Flat material (Fish shader, wiggle and shading 0) for the stereo bars.")]
        [SerializeField] Material m_BarMaterial;
        [SerializeField] Vector3 m_StereoOffset = new(-0.1f, -0.22f, 0.75f);
        [Tooltip("Room for \"nothing 100%\" before the bar starts (metres).")]
        [SerializeField] float m_LabelWidth = 0.15f;
        [SerializeField] float m_BarWidth = 0.16f;
        [SerializeField] float m_RowHeight = 0.03f;

        static readonly Color OtherColor = new(0.85f, 0.85f, 0.85f);
        static readonly Color NothingColor = new(0.5f, 0.5f, 0.5f);
        static readonly int ColorId = Shader.PropertyToID("_Color");

        HoloKitCameraManager m_Camera;
        Transform m_Stereo;
        readonly List<(string id, Transform bar, TextMesh text, Color color)> m_Rows = new();
        readonly string[] m_StereoInfo = new string[4];
        bool m_StereoVisible = true;
        GUIStyle m_Label, m_Big, m_Small, m_Button;

        void Start()
        {
            if (m_Detector == null)
                m_Detector = FindFirstObjectByType<TouchKinDetector>();
            m_Camera = FindFirstObjectByType<HoloKitCameraManager>();
            if (m_Camera != null)
                m_Camera.OnStereoTrigger += ToggleStereoHud;
        }

        void OnDestroy()
        {
            if (m_Camera != null)
                m_Camera.OnStereoTrigger -= ToggleStereoHud;
        }

        void ToggleStereoHud() => m_StereoVisible = !m_StereoVisible;

        Color ColorOf(string id)
        {
            if (id == DecisionsProtocol.OtherId) return OtherColor;
            if (id == DecisionsProtocol.NothingId) return NothingColor;
            return m_Detector.Find(id)?.Color ?? Color.white;
        }

        float P(string id) => m_Detector.Matcher != null && m_Detector.Matcher.Smoothed.TryGetValue(id, out var p) ? p : 0f;

        void LateUpdate()
        {
            if (m_Detector == null || m_Detector.Outcomes.Count == 0)
                return;
            bool stereo = HeadPose.IsStereo;
            if (m_Stereo == null && m_BarMaterial != null)
                BuildStereoHud();
            if (m_Stereo != null)
            {
                m_Stereo.gameObject.SetActive(stereo && m_StereoVisible);
                if (stereo && m_StereoVisible)
                    UpdateStereoHud();
            }
            if (m_Camera != null)
            {
                var matched = m_Detector.Matcher?.Matched;
                m_StereoInfo[0] = matched != null ? $"MATCH {matched.ToUpperInvariant()}" : "touching: —";
                m_StereoInfo[1] = m_Detector.Kin.Count > 0 ? $"{m_Detector.Kin[0].Id} {P(m_Detector.Kin[0].Id):P0}" : "";
                m_StereoInfo[2] = m_Detector.Kin.Count > 1 ? $"{m_Detector.Kin[1].Id} {P(m_Detector.Kin[1].Id):P0}" : "";
                m_StereoInfo[3] = $"{m_Detector.LastLatency * 1000f:F0} ms";
                m_Camera.StereoInfo = m_StereoInfo;
            }
        }

        void BuildStereoHud()
        {
            m_Stereo = new GameObject("Stereo HUD").transform;
            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            var outcomes = m_Detector.Outcomes;
            for (int i = 0; i < outcomes.Count; i++)
            {
                var id = outcomes[i];
                var color = ColorOf(id);
                var bar = GameObject.CreatePrimitive(PrimitiveType.Quad);
                bar.name = $"Bar {id}";
                Destroy(bar.GetComponent<Collider>());
                bar.transform.SetParent(m_Stereo, false);
                var renderer = bar.GetComponent<MeshRenderer>();
                renderer.sharedMaterial = m_BarMaterial;
                var block = new MaterialPropertyBlock();
                block.SetColor(ColorId, color);
                renderer.SetPropertyBlock(block);

                var label = new GameObject($"Label {id}").AddComponent<TextMesh>();
                label.transform.SetParent(m_Stereo, false);
                label.transform.localPosition = new Vector3(0f, -i * m_RowHeight, 0f);
                label.font = font;
                label.GetComponent<MeshRenderer>().sharedMaterial = font.material;
                label.fontSize = 48;
                // TextMesh line height is about fontSize * characterSize / 10 world units.
                label.characterSize = m_RowHeight * 0.7f * 10f / 48f;
                label.anchor = TextAnchor.MiddleLeft;
                label.color = color;
                m_Rows.Add((id, bar.transform, label, color));
            }
        }

        void UpdateStereoHud()
        {
            var head = HeadPose.Get();
            if (head == null)
                return;
            var yaw = Quaternion.Euler(0f, head.eulerAngles.y, 0f);
            var target = head.position + yaw * m_StereoOffset;
            float follow = 1f - Mathf.Exp(-Time.deltaTime * 4f);
            m_Stereo.position = Vector3.Lerp(m_Stereo.position, target, follow);
            m_Stereo.rotation = Quaternion.Slerp(m_Stereo.rotation, Quaternion.LookRotation(m_Stereo.position - head.position), follow);

            var matched = m_Detector.Matcher?.Matched;
            for (int i = 0; i < m_Rows.Count; i++)
            {
                var (id, bar, text, _) = m_Rows[i];
                float p = P(id);
                float w = Mathf.Max(0.002f, m_BarWidth * p);
                bar.localPosition = new Vector3(m_LabelWidth + w * 0.5f, -i * m_RowHeight, 0f);
                bar.localScale = new Vector3(w, m_RowHeight * (id == matched ? 0.8f : 0.5f), 1f);
                text.text = $"{id} {p * 100f:F0}%";
            }
        }

        void OnGUI()
        {
            if (m_Detector == null || HeadPose.IsStereo)
                return;
            float s = Screen.height / 1000f;
            m_Label ??= new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(26 * s) };
            m_Big ??= new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(40 * s), fontStyle = FontStyle.Bold };
            m_Small ??= new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(20 * s), wordWrap = true };
            m_Button ??= new GUIStyle(GUI.skin.button) { fontSize = Mathf.RoundToInt(26 * s) };

            float x = 30f * s, y = 190f * s, w = 640f * s, row = 40f * s;
            GUI.color = new Color(0f, 0f, 0f, 0.55f);
            GUI.DrawTexture(new Rect(x - 12f * s, y - 10f * s, w + 24f * s, row * (m_Detector.Outcomes.Count + 4.6f)), Texture2D.whiteTexture);
            GUI.color = Color.white;

            var matched = m_Detector.Matcher?.Matched;
            m_Big.normal.textColor = matched != null ? ColorOf(matched) : Color.white;
            GUI.Label(new Rect(x, y, w, row * 1.3f), matched != null ? $"Touching {matched}" : "Touching: —", m_Big);
            y += row * 1.4f;

            foreach (var id in m_Detector.Outcomes)
            {
                float p = P(id);
                m_Label.normal.textColor = ColorOf(id);
                GUI.Label(new Rect(x, y, 170f * s, row), id, m_Label);
                GUI.color = ColorOf(id);
                GUI.DrawTexture(new Rect(x + 170f * s, y + row * 0.2f, (w - 290f * s) * p, row * 0.6f), Texture2D.whiteTexture);
                GUI.color = Color.white;
                GUI.Label(new Rect(x + w - 110f * s, y, 110f * s, row), $"{p * 100f:F0}%", m_Label);
                y += row;
            }

            m_Small.normal.textColor = Color.white;
            GUI.Label(new Rect(x, y, w, row), $"{m_Detector.Model}  ·  {m_Detector.LastLatency * 1000f:F0} ms (avg {m_Detector.MeanLatency * 1000f:F0})  ·  {m_Detector.AnswerRate:F1} Hz", m_Small);
            y += row * 0.8f;
            GUI.Label(new Rect(x, y, w, row), $"{m_Detector.Requests} sent · {m_Detector.Answers} answered · {m_Detector.InFlight} in flight · {m_Detector.Errors} errors · ${m_Detector.TotalCost:F4} · hand {(m_Detector.HandVisible ? "seen" : "not seen")}", m_Small);
            y += row * 0.8f;
            if (!string.IsNullOrEmpty(m_Detector.LastError))
            {
                m_Small.normal.textColor = new Color(1f, 0.5f, 0.45f);
                GUI.Label(new Rect(x, y, w, row * 2f), m_Detector.LastError, m_Small);
            }

            var frame = m_Detector.LastFrame;
            if (frame != null)
            {
                float fw = 360f * s, fh = fw * frame.height / frame.width;
                GUI.DrawTexture(new Rect(Screen.width - fw - 30f * s, Screen.height - fh - 170f * s, fw, fh), frame);
                GUI.Label(new Rect(Screen.width - fw - 30f * s, Screen.height - 165f * s, fw, row), "what Luna sees", m_Small);
            }

            if (GUI.Button(new Rect(x, Screen.height - 170f * s, 260f * s, 80f * s), m_Detector.Running ? "Pause Luna" : "Resume Luna", m_Button))
                m_Detector.Running = !m_Detector.Running;
        }
    }
}
