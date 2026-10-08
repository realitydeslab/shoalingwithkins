using System;
using System.IO;
using UnityEngine;

namespace ShoalingWithKin
{
    /// <summary>
    /// The OpenRouter API key: <c>StreamingAssets/openrouter_api_key.txt</c> (written by the setup
    /// script from <c>OPENROUTER_API_KEY</c>, gitignored), or the environment variable in the Editor.
    /// The file ships inside the app bundle: use a dedicated, credit-limited key.
    /// </summary>
    public static class OpenRouterKey
    {
        public const string FileName = "openrouter_api_key.txt";
        public const string EnvironmentVariable = "OPENROUTER_API_KEY";

        public static string Load()
        {
            var path = Path.Combine(Application.streamingAssetsPath, FileName);
            try
            {
                if (File.Exists(path))
                {
                    var key = File.ReadAllText(path).Trim();
                    if (key.Length > 0)
                        return key;
                }
            }
            catch (IOException e)
            {
                Debug.LogWarning($"[OpenRouterKey] Could not read {path}: {e.Message}");
            }
#if UNITY_EDITOR
            return Environment.GetEnvironmentVariable(EnvironmentVariable);
#else
            return null;
#endif
        }
    }
}
