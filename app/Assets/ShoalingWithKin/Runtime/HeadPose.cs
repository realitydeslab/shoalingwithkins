using HoloKit;
using UnityEngine;

namespace ShoalingWithKin
{
    /// <summary>The wearer's head: HoloKit's centre-eye pose in Stereo, the phone camera in Mono.</summary>
    public static class HeadPose
    {
        static HoloKitCameraManager s_Camera;

        public static Transform Get()
        {
            if (s_Camera == null)
                s_Camera = Object.FindFirstObjectByType<HoloKitCameraManager>();
            if (s_Camera != null && s_Camera.CenterEyePose != null)
                return s_Camera.CenterEyePose;
            return Camera.main != null ? Camera.main.transform : null;
        }

        public static bool IsStereo
        {
            get
            {
                Get();
                return s_Camera != null && s_Camera.ScreenRenderMode == ScreenRenderMode.Stereo;
            }
        }

        /// <summary>Where a reaching hand usually is: 0.55 m ahead, 25° below the gaze.</summary>
        public static Vector3 ReachPoint()
        {
            var head = Get();
            if (head == null)
                return new Vector3(0f, -0.25f, 0.5f);
            var forward = Quaternion.AngleAxis(25f, head.right) * head.forward;
            return head.position + forward * 0.55f;
        }
    }
}
