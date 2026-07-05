using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace AgenLink.Ops
{
    /// <summary>
    /// Captures the Game view or the Scene view to a PNG under AgenLink~/screenshots/ and returns its absolute
    /// path, so the CLI can READ the image and see the result itself instead of asking the user. The edit-mode
    /// path renders a camera explicitly, so it works even while the Editor is unfocused; screen-space-overlay
    /// UI only appears in a play-mode game capture.
    /// </summary>
    internal static class ScreenshotOps
    {
        public static string Capture(CommandHandlers.RequestParams p)
        {
            string view = (p.view ?? "game").ToLowerInvariant();
            int w = p.width > 0 ? p.width : 1280;
            int h = p.height > 0 ? p.height : 720;

            string dir = Path.Combine(ConfigBuilder.ProjectRoot(), "AgenLink~", "screenshots");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, $"{view}-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");

            if (view == "game")
            {
                if (EditorApplication.isPlaying)
                {
                    Texture2D shot = ScreenCapture.CaptureScreenshotAsTexture();
                    try { WritePng(shot.EncodeToPNG(), file); w = shot.width; h = shot.height; }
                    finally { UnityEngine.Object.DestroyImmediate(shot); }
                }
                else
                {
                    Camera cam = Camera.main;
                    if (cam == null) cam = FirstEnabledCamera();
                    if (cam == null) throw new Exception("no camera to capture — the scene has no enabled Camera (tag one MainCamera or enter play mode)");
                    RenderToPng(cam, w, h, file);
                }
            }
            else if (view == "scene")
            {
                SceneView sv = SceneView.lastActiveSceneView;
                if (sv == null || sv.camera == null) throw new Exception("no Scene view is open to capture");
                RenderToPng(sv.camera, w, h, file);
            }
            else throw new Exception("view must be 'game' or 'scene'");

            return new JObj().S("view", view).S("path", Path.GetFullPath(file)).N("width", w).N("height", h).Build();
        }

        private static void RenderToPng(Camera cam, int w, int h, string file)
        {
            var rt = new RenderTexture(w, h, 24);
            RenderTexture prevActive = RenderTexture.active;
            RenderTexture prevTarget = cam.targetTexture;
            var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
            try
            {
                cam.targetTexture = rt;
                cam.Render();
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                tex.Apply();
                WritePng(tex.EncodeToPNG(), file);
            }
            finally
            {
                cam.targetTexture = prevTarget;
                RenderTexture.active = prevActive;
                UnityEngine.Object.DestroyImmediate(tex);
                rt.Release();
                UnityEngine.Object.DestroyImmediate(rt);
            }
        }

        private static Camera FirstEnabledCamera()
        {
            foreach (Camera c in Camera.allCameras)
                if (c != null && c.enabled) return c;
            return null;
        }

        private static void WritePng(byte[] png, string file)
        {
            if (png == null || png.Length == 0) throw new Exception("capture produced no image data");
            File.WriteAllBytes(file, png);
        }
    }
}
