using System;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace GoingMedieval.LLM_NPCs
{
    /// <summary>
    /// Loads medieval UI art assets that are embedded inside the DLL as resources.
    /// Every public method falls back gracefully to null on any error so the caller
    /// can substitute a MakeTex solid-colour fallback.
    /// </summary>
    internal static class UIAssetLoader
    {
        // Base namespace prefix registered in the .csproj <LogicalName> tags
        private const string Prefix = "GoingMedieval.LLM_NPCs.UI.";

        private static readonly Assembly _asm = Assembly.GetExecutingAssembly();

        /// <summary>
        /// Loads a PNG embedded as a resource and returns a ready-to-use Texture2D,
        /// or null if the resource is missing or corrupt.
        /// </summary>
        public static Texture2D Load(string filename)
        {
            var resourceName = Prefix + filename;
            try
            {
                using (var stream = _asm.GetManifestResourceStream(resourceName))
                {
                    if (stream == null)
                    {
                        LLMNPCsPlugin.LogToFile($"[UIAssetLoader] Resource not found: {resourceName}");
                        return null;
                    }

                    var bytes = new byte[stream.Length];
                    stream.Read(bytes, 0, bytes.Length);

                    var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    if (!tex.LoadImage(bytes))
                    {
                        LLMNPCsPlugin.LogToFile($"[UIAssetLoader] LoadImage failed for: {resourceName}");
                        UnityEngine.Object.Destroy(tex);
                        return null;
                    }

                    tex.filterMode = FilterMode.Bilinear;
                    tex.wrapMode   = TextureWrapMode.Clamp;
                    tex.name       = filename;
                    return tex;
                }
            }
            catch (Exception ex)
            {
                LLMNPCsPlugin.LogToFile($"[UIAssetLoader] Exception loading {resourceName}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Returns <paramref name="loaded"/> if non-null, otherwise the result of
        /// <paramref name="fallback"/>. Mirrors the pattern used in EnsureTextures().
        /// </summary>
        public static Texture2D OrFallback(Texture2D loaded, Func<Texture2D> fallback)
            => loaded != null ? loaded : fallback();
    }
}
