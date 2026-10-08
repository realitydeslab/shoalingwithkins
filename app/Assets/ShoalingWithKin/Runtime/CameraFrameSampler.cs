using System;
using System.Collections;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace ShoalingWithKin
{
    /// <summary>
    /// The latest AR camera frame as a small upright JPEG. ARKit's frame is the raw sensor image
    /// (landscape); HoloKit runs in landscape left, so it is upright for the wearer. The CPU image's
    /// rows run top-down and Unity textures bottom-up, so MirrorX (which reverses the rows) keeps
    /// the encoded JPEG upright. Downsampling is nearest neighbour — measured as accurate as an
    /// antialiased resize at 512 px.
    /// </summary>
    public sealed class CameraFrameSampler : IDisposable
    {
        readonly ARCameraManager m_Camera;
        readonly int m_Width;
        readonly int m_Quality;
        Texture2D m_Texture;

        public CameraFrameSampler(ARCameraManager camera, int width, int quality)
        {
            m_Camera = camera;
            m_Width = width;
            m_Quality = quality;
        }

        /// <summary>The frame last sent, for the HUD's "what Luna sees" preview.</summary>
        public Texture2D LastFrame => m_Texture;

        /// <summary>
        /// Calls <paramref name="onJpeg"/> exactly once: with the encoded frame, or null when no frame
        /// is ready or the conversion failed. Never throws, so the caller's bookkeeping always resumes.
        /// </summary>
        public IEnumerator Capture(Action<byte[]> onJpeg)
        {
            if (m_Camera == null || !m_Camera.TryAcquireLatestCpuImage(out var image))
            {
                onJpeg(null);
                yield break;
            }

            int width, height;
            XRCpuImage.AsyncConversion conversion;
            // Disposing the image before the conversion finishes is allowed.
            using (image)
            {
                width = Mathf.Min(m_Width, image.width);
                height = Mathf.RoundToInt(width * image.height / (float)image.width);
                try
                {
                    conversion = image.ConvertAsync(new XRCpuImage.ConversionParams
                    {
                        inputRect = new RectInt(0, 0, image.width, image.height),
                        outputDimensions = new Vector2Int(width, height),
                        outputFormat = TextureFormat.RGB24,
                        transformation = XRCpuImage.Transformation.MirrorX,
                    });
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[CameraFrameSampler] Conversion refused: {e.Message}");
                    onJpeg(null);
                    yield break;
                }
            }

            using (conversion)
            {
                while (!conversion.status.IsDone())
                    yield return null;
                if (conversion.status != XRCpuImage.AsyncConversionStatus.Ready)
                {
                    onJpeg(null);
                    yield break;
                }
                try
                {
                    if (m_Texture == null || m_Texture.width != width || m_Texture.height != height)
                    {
                        if (m_Texture != null)
                            UnityEngine.Object.Destroy(m_Texture);
                        m_Texture = new Texture2D(width, height, TextureFormat.RGB24, false) { name = "Luna Frame" };
                    }
                    m_Texture.LoadRawTextureData(conversion.GetData<byte>());
                    m_Texture.Apply(false);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[CameraFrameSampler] Frame copy failed: {e.Message}");
                    onJpeg(null);
                    yield break;
                }
            }
            onJpeg(m_Texture.EncodeToJPG(m_Quality));
        }

        public void Dispose()
        {
            if (m_Texture != null)
                UnityEngine.Object.Destroy(m_Texture);
            m_Texture = null;
        }
    }
}
