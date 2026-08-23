using System;
using System.IO;
using System.Threading.Tasks;
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
                // Always the explicit camera render here. ScreenCapture.CaptureScreenshotAsTexture only
                // produces a valid texture at the END of a frame, and the bridge dispatches mid-frame from
                // EditorApplication.update, where it fails with "Passed in texture is invalid (null)". The
                // play-mode capture that includes screen-space-overlay UI has to span frames, so it lives in
                // CaptureAsync instead.
                Camera cam = Camera.main;
                if (cam == null) cam = FirstEnabledCamera();
                if (cam == null) throw new Exception("no camera to capture — the scene has no enabled Camera (tag one MainCamera or enter play mode)");
                RenderToPng(cam, w, h, file);
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

        /// <summary>How long to wait for Unity to actually write a play-mode screenshot.</summary>
        private const double PlayModeCaptureTimeoutSec = 10.0;

        /// <summary>
        /// Async entry point used by the bridge. Everything except a play-mode game capture finishes on the
        /// calling frame and is wrapped in a completed Task; a play-mode game capture must span frames.
        /// </summary>
        public static Task<string> CaptureAsync(CommandHandlers.RequestParams p)
        {
            string view = (p.view ?? "game").ToLowerInvariant();
            if (view == "game" && EditorApplication.isPlaying) return CapturePlayModeGameView();
            return Task.FromResult(Capture(p));
        }

        /// <summary>
        /// Capture the live game framebuffer, screen-space-overlay UI included. ScreenCapture.CaptureScreenshot
        /// writes the real frame but only at the end of one, and it returns before the file exists — so the
        /// request is completed from a later editor tick, once the PNG is on disk and readable.
        /// </summary>
        private static Task<string> CapturePlayModeGameView()
        {
            string dir = Path.Combine(ConfigBuilder.ProjectRoot(), "AgenLink~", "screenshots");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, $"game-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");

            ScreenCapture.CaptureScreenshot(file);

            var tcs = new TaskCompletionSource<string>();
            double deadline = EditorApplication.timeSinceStartup + PlayModeCaptureTimeoutSec;
            EditorApplication.CallbackFunction tick = null;
            tick = () =>
            {
                int w, h;
                if (TryReadPngSize(file, out w, out h))
                {
                    EditorApplication.update -= tick;
                    tcs.SetResult(new JObj().S("view", "game").S("path", Path.GetFullPath(file))
                                            .N("width", w).N("height", h).Build());
                    return;
                }
                if (EditorApplication.timeSinceStartup < deadline) return;
                EditorApplication.update -= tick;
                tcs.SetException(new Exception(
                    $"Unity did not write the play-mode screenshot within {PlayModeCaptureTimeoutSec:0}s. The " +
                    "Game view has to render a frame for this to work — if play mode is paused, step a frame or " +
                    "unpause, then retry."));
            };
            EditorApplication.update += tick;
            return tcs.Task;
        }

        /// <summary>
        /// True once <paramref name="file"/> is a complete-enough PNG to report, reading its dimensions out of
        /// the IHDR header. The file appears before it is fully written, so a partial read simply means
        /// "not yet" and the caller ticks again.
        /// </summary>
        private static bool TryReadPngSize(string file, out int w, out int h)
        {
            w = 0;
            h = 0;
            try
            {
                if (!File.Exists(file)) return false;
                var header = new byte[24];
                using (var fs = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    if (fs.Length < header.Length) return false;
                    if (fs.Read(header, 0, header.Length) < header.Length) return false;
                }
                // PNG IHDR stores width and height as big-endian uint32 at byte offsets 16 and 20.
                w = (header[16] << 24) | (header[17] << 16) | (header[18] << 8) | header[19];
                h = (header[20] << 24) | (header[21] << 16) | (header[22] << 8) | header[23];
                return w > 0 && h > 0;
            }
            catch (IOException) { return false; }       // still being written
            catch (UnauthorizedAccessException) { return false; }
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
