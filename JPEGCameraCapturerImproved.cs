using System;
using System.Collections;
using UnityEngine;

/*
 * JPEGCameraCapturerImproved
 * --------------------------
 * Captures a single JPEG frame from a Unity Camera "on demand".
 *
 * Key improvements vs. a naive implementation:
 * 1) Avoids freezing Unity:
 *    - No busy-wait loops like: while(IsCaptureEnable) {}
 *    - Capture is performed in a coroutine at EndOfFrame.
 *
 * 2) Better memory / GC behavior:
 *    - Reuses RenderTexture and Texture2D buffers across captures.
 *    - Only the JPEG byte[] is newly allocated per capture (EncodeToJPG does that).
 *
 * Usage:
 *  - Set IsCaptureEnable = true to request ONE capture.
 *  - The script will capture once, fill 'jpg', then set IsCaptureEnable back to false.
 *  - Optional: subscribe to OnJpegCaptured for event-driven workflows.
 *
 * Notes:
 *  - Unity rendering and ReadPixels must run on the Unity main thread.
 *  - This script is designed to be attached to the same GameObject as the Camera.
 */

public class JPEGCameraCapturerImproved : MonoBehaviour
{
    [Header("Capture control")]
    [Tooltip("When set true, one capture will be performed and then reset to false.")]
    public bool IsCaptureEnable = false;

    [Header("Camera source")]
    public Camera cameraSource;

    [Header("Image settings")]
    [Min(16)] public int resWidth  = 360;
    [Min(16)] public int resHeight = 240;

    [Range(1, 100)]
    [Tooltip("JPEG quality (1..100). Higher = better quality, larger payload.")]
    public int jpgQuality = 70;

    [Header("Output (read-only)")]
    [Tooltip("Last captured JPEG bytes. Replaced on each capture.")]
    public byte[] jpg;

    public event Action<byte[]> OnJpegCaptured;

    // Reusable buffers
    private RenderTexture _rt;
    private Texture2D     _tex;
    private int           _allocatedW = -1;
    private int           _allocatedH = -1;

    private bool _captureInProgress = false;

    private void Awake()
    {
        if (cameraSource == null)
            cameraSource = GetComponent<Camera>();

        if (cameraSource == null)
        {
            Debug.LogError("JPEGCameraCapturerImproved: No Camera assigned/found.");
            enabled = false;
            return;
        }

        EnsureBuffers();
    }

    private void Update()
    {
        // Trigger a single capture when requested.
        if (IsCaptureEnable && !_captureInProgress)
            StartCoroutine(CaptureAtEndOfFrame());
    }

    /// <summary>
    /// Convenience method: request one capture.
    /// </summary>
    public void RequestCapture()
    {
        IsCaptureEnable = true;
    }

    private IEnumerator CaptureAtEndOfFrame()
    {
        _captureInProgress = true;

        // Wait until rendering for this frame is done.
        yield return new WaitForEndOfFrame();

        EnsureBuffers();

        // Perform capture and JPEG encoding.
        byte[] bytes = CaptureJpegInternal();

        // Publish results.
        jpg = bytes;
        IsCaptureEnable = false;

        OnJpegCaptured?.Invoke(bytes);

        _captureInProgress = false;
    }

    private void EnsureBuffers()
    {
        resWidth   = Mathf.Max(16, resWidth);
        resHeight  = Mathf.Max(16, resHeight);
        jpgQuality = Mathf.Clamp(jpgQuality, 1, 100);

        if (_rt != null && _tex != null && _allocatedW == resWidth && _allocatedH == resHeight)
            return;

        ReleaseBuffers();

        _rt = new RenderTexture(resWidth, resHeight, 24, RenderTextureFormat.ARGB32)
        {
            antiAliasing = 1
        };
        _tex = new Texture2D(resWidth, resHeight, TextureFormat.RGB24, false);

        _allocatedW = resWidth;
        _allocatedH = resHeight;
    }

    private void ReleaseBuffers()
    {
        if (_rt != null)
        {
            _rt.Release();
            Destroy(_rt);
            _rt = null;
        }
        if (_tex != null)
        {
            Destroy(_tex);
            _tex = null;
        }
        _allocatedW = -1;
        _allocatedH = -1;
    }

    private byte[] CaptureJpegInternal()
    {
        try
        {
            // Save previous state to restore it later.
            RenderTexture prevActive = RenderTexture.active;
            RenderTexture prevTarget = cameraSource.targetTexture;

            cameraSource.targetTexture = _rt;
            cameraSource.Render();

            RenderTexture.active = _rt;
            _tex.ReadPixels(new Rect(0, 0, resWidth, resHeight), 0, 0, false);
            _tex.Apply(false, false);

            // Restore previous state.
            RenderTexture.active        = prevActive;
            cameraSource.targetTexture  = prevTarget;

            // EncodeToJPG allocates a new byte[] per call (expected).
            return _tex.EncodeToJPG(jpgQuality);
        }
        catch (Exception e)
        {
            Debug.LogError($"JPEGCameraCapturerImproved: capture failed: {e.Message}");
            return null;
        }
    }

    private void OnDestroy()
    {
        ReleaseBuffers();
    }
}
