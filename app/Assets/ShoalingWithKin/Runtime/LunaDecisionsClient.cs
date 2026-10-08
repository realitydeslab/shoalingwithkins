using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace ShoalingWithKin
{
    /// <summary>Posts one touch question to OpenRouter's System One endpoint (Luna Decisions).</summary>
    public sealed class LunaDecisionsClient
    {
        readonly string m_ApiKey;
        readonly int m_TimeoutSeconds;

        public LunaDecisionsClient(string apiKey, float timeoutSeconds)
        {
            m_ApiKey = apiKey;
            m_TimeoutSeconds = Mathf.Max(1, Mathf.CeilToInt(timeoutSeconds));
        }

        /// <summary>Sends <paramref name="requestJson"/>; calls exactly one of the callbacks. Latency is in seconds.</summary>
        public IEnumerator Send(string requestJson, Action<TouchDecision, double> onSuccess, Action<string> onError)
        {
            using var request = new UnityWebRequest(DecisionsProtocol.Endpoint, UnityWebRequest.kHttpVerbPOST);
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(requestJson)) { contentType = "application/json" };
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Authorization", "Bearer " + m_ApiKey);
            request.SetRequestHeader("Content-Type", "application/json");
            request.timeout = m_TimeoutSeconds;

            double start = Time.realtimeSinceStartupAsDouble;
            yield return request.SendWebRequest();
            double latency = Time.realtimeSinceStartupAsDouble - start;

            if (request.result != UnityWebRequest.Result.Success)
            {
                var body = request.downloadHandler?.text;
                var detail = string.IsNullOrEmpty(body) ? request.error : DecisionsProtocol.ParseError(body);
                onError($"HTTP {request.responseCode}: {detail}");
                yield break;
            }

            TouchDecision decision;
            try
            {
                decision = DecisionsProtocol.ParseResponse(request.downloadHandler.text);
            }
            catch (Exception e)
            {
                // Any parse failure is one lost answer, never a stuck request (the caller counts in-flight).
                onError($"{e.GetType().Name}: {e.Message}");
                yield break;
            }
            onSuccess(decision, latency);
        }
    }
}
