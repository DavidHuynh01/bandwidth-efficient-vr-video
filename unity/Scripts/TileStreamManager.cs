using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.Video;

namespace TileStreaming
{
    // Pulls high-res tiles for wherever the user is looking and lays them over a
    // low-res full-panorama base layer. The FoveatedPanoramic shader keeps the
    // gaze centre sharp and blurs the base more the further out you look, so the
    // edges of vision fade off instead of cutting to black.
    //
    // Quest 2 only has a few hardware decoders, so we keep a small tile pool plus
    // one decoder for the base layer.
    public class TileStreamManager : MonoBehaviour
    {
        [Header("Server")]
        [Tooltip("LAN URL of the Flask server, e.g. http://192.168.1.50:8080")]
        public string serverUrl = "http://192.168.1.50:8080";

        [Header("Rendering")]
        [Tooltip("Material using the Skybox/FoveatedPanoramic shader.")]
        public Material skyboxMaterial;
        public int combinedTextureWidth = 4096;
        public int combinedTextureHeight = 2048;

        [Header("Foveated blur")]
        [Tooltip("0 = mostly sharp. 1 = blur reaches almost to the centre. Grows inward from the edges.")]
        [Range(0f, 1f)] public float blurCoverage = 0.4f;
        [Tooltip("How strong the blur gets out at the edges.")]
        [Range(0f, 0.06f)] public float maxBlurStrength = 0.05f;
        [Tooltip("Fetch fewer high-res tiles as coverage grows — more blur, less work.")]
        public bool fetchLessWhenBlurred = true;

        [Header("Decoder pool")]
        [Tooltip("Max simultaneous tile decoders. Keep small on Quest 2.")]
        public int decoderPoolSize = 6;

        [Header("Refs")]
        public ViewportPredictor viewport;
        public BandwidthMonitor bandwidth;

        private TileManifest _manifest;
        private RenderTexture _combined;   // detail layer: tiles where fetched, transparent elsewhere
        private int _currentChunk;
        private int _rebufferEvents;

        // one slot per tile decoder
        private VideoPlayer[] _pool;
        private int[] _slotTile;           // which tile each slot currently holds
        private bool[] _slotReady;
        private RectInt[] _slotRegion;

        private VideoPlayer _basePlayer;

        IEnumerator Start()
        {
            if (viewport == null) viewport = GetComponent<ViewportPredictor>();
            if (bandwidth == null) bandwidth = GetComponent<BandwidthMonitor>();

            yield return LoadManifest();
            if (_manifest == null)
            {
                Debug.LogError("No manifest — is the server running and reachable?");
                yield break;
            }

            SetupRenderTarget();
            SetupDecoders();
            StartCoroutine(StreamLoop());
            StartCoroutine(ReportLoop());
        }

        IEnumerator LoadManifest()
        {
            using var req = UnityWebRequest.Get($"{serverUrl}/manifest");
            yield return req.SendWebRequest();
            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError($"manifest fetch failed: {req.error}");
                yield break;
            }
            _manifest = JsonUtility.FromJson<TileManifest>(req.downloadHandler.text);
            Debug.Log($"Manifest: {_manifest.cols}x{_manifest.rows} grid, " +
                      $"{_manifest.num_chunks} chunks, base={_manifest.HasBase}");
        }

        void SetupRenderTarget()
        {
            _combined = new RenderTexture(combinedTextureWidth, combinedTextureHeight, 0)
            { wrapMode = TextureWrapMode.Repeat };
            _combined.Create();
            ClearCombined();

            if (skyboxMaterial != null)
            {
                skyboxMaterial.mainTexture = _combined;        // _MainTex = detail layer
                RenderSettings.skybox = skyboxMaterial;
                DynamicGI.UpdateEnvironment();
            }
        }

        void SetupDecoders()
        {
            _pool = new VideoPlayer[decoderPoolSize];
            _slotTile = new int[decoderPoolSize];
            _slotReady = new bool[decoderPoolSize];
            _slotRegion = new RectInt[decoderPoolSize];
            for (int i = 0; i < decoderPoolSize; i++)
            {
                _pool[i] = NewPlayer($"Tile_{i}");
                _slotTile[i] = -1;
            }
            if (_manifest.HasBase)
                _basePlayer = NewPlayer("Base");
        }

        VideoPlayer NewPlayer(string label)
        {
            var go = new GameObject(label);
            go.transform.SetParent(transform);
            var vp = go.AddComponent<VideoPlayer>();
            vp.playOnAwake = false;
            vp.isLooping = false;
            vp.renderMode = VideoRenderMode.APIOnly;   // we read .texture ourselves
            vp.audioOutputMode = VideoAudioOutputMode.None;
            return vp;
        }

        // advance one chunk per segment; refetch the viewport tiles and the base
        IEnumerator StreamLoop()
        {
            float segLen = Mathf.Max(1, _manifest.segment_seconds);
            while (true)
            {
                var visible = viewport.VisibleTiles(_manifest);
                string quality = bandwidth.PickQuality();

                int slot = 0;
                foreach (int tileId in visible)
                {
                    if (slot >= _pool.Length) break;
                    StartCoroutine(FetchTile(slot, tileId, quality, _currentChunk));
                    slot++;
                }

                if (_manifest.HasBase)
                    StartCoroutine(FetchBase(_currentChunk));

                _currentChunk = (_currentChunk + 1) % Mathf.Max(1, _manifest.num_chunks);
                yield return new WaitForSeconds(segLen);
            }
        }

        IEnumerator FetchTile(int slot, int tileId, string quality, int chunk)
        {
            string url = $"{serverUrl}/tiles/{tileId}/{quality}/chunk_{chunk:000}.mp4";
            string localPath = Path.Combine(Application.persistentDataPath, $"t{tileId}_{chunk}.mp4");

            float t0 = Time.realtimeSinceStartup;
            using var req = UnityWebRequest.Get(url);
            req.downloadHandler = new DownloadHandlerFile(localPath);
            yield return req.SendWebRequest();
            if (req.result != UnityWebRequest.Result.Success)
            {
                _rebufferEvents++;
                yield break;
            }
            bandwidth.RecordDownload((long)req.downloadedBytes, Time.realtimeSinceStartup - t0);

            var vp = _pool[slot];
            vp.source = VideoSource.Url;
            vp.url = "file://" + localPath;
            vp.Prepare();
            while (!vp.isPrepared) yield return null;
            vp.Play();

            // remember where this tile belongs; LateUpdate does the actual blit
            TileInfo tile = _manifest.tiles[tileId];
            int rw = _combined.width / _manifest.cols;
            int rh = _combined.height / _manifest.rows;
            int dstY = (_manifest.rows - 1 - tile.row) * rh;   // row 0 is the top
            _slotRegion[slot] = new RectInt(tile.col * rw, dstY, rw, rh);
            _slotTile[slot] = tileId;
            _slotReady[slot] = true;
        }

        IEnumerator FetchBase(int chunk)
        {
            string url = $"{serverUrl}/tiles/{_manifest.base_tile_id}/high/chunk_{chunk:000}.mp4";
            string localPath = Path.Combine(Application.persistentDataPath, $"base_{chunk}.mp4");

            using var req = UnityWebRequest.Get(url);
            req.downloadHandler = new DownloadHandlerFile(localPath);
            yield return req.SendWebRequest();
            if (req.result != UnityWebRequest.Result.Success) yield break;

            _basePlayer.source = VideoSource.Url;
            _basePlayer.url = "file://" + localPath;
            _basePlayer.Prepare();
            while (!_basePlayer.isPrepared) yield return null;
            _basePlayer.Play();
        }

        // rebuild the detail layer every frame and push gaze + blur to the shader
        void LateUpdate()
        {
            if (_combined == null || _manifest == null) return;

            ClearCombined();
            var visible = viewport.VisibleTiles(_manifest);
            for (int i = 0; i < _pool.Length; i++)
            {
                if (!_slotReady[i] || _slotTile[i] < 0) continue;
                if (!visible.Contains(_slotTile[i])) continue;   // looked away -> fall back to base
                var tex = _pool[i].texture;
                if (tex == null) continue;
                var rgn = _slotRegion[i];
                Graphics.CopyTexture(tex, 0, 0, 0, 0,
                    Mathf.Min(tex.width, rgn.width), Mathf.Min(tex.height, rgn.height),
                    _combined, 0, 0, rgn.x, rgn.y);
            }

            if (skyboxMaterial != null)
            {
                Vector3 fwd = viewport != null && viewport.headTransform != null
                    ? viewport.headTransform.forward
                    : (Camera.main != null ? Camera.main.transform.forward : Vector3.forward);
                // one knob: as coverage rises, the sharp cone shrinks and the
                // blur ramps in faster, so the soft area creeps toward the centre
                float focus = Mathf.Lerp(50f, 8f, blurCoverage);
                float falloff = Mathf.Lerp(70f, 18f, blurCoverage);
                skyboxMaterial.SetVector("_GazeDir", fwd);
                skyboxMaterial.SetFloat("_BlurStrength", maxBlurStrength);
                skyboxMaterial.SetFloat("_FocusAngle", focus);
                skyboxMaterial.SetFloat("_FalloffAngle", falloff);

                // the base player swaps its texture each chunk, so keep the
                // shader pointed at the current one (skip null gaps to avoid flicker)
                if (_basePlayer != null && _basePlayer.texture != null)
                    skyboxMaterial.SetTexture("_BaseTex", _basePlayer.texture);
            }

            // the more we blur the edges, the smaller the area we bother fetching
            // sharp tiles for — fewer downloads and decoders when blur is high
            if (fetchLessWhenBlurred && viewport != null)
            {
                viewport.horizontalFovDeg = Mathf.Lerp(110f, 35f, blurCoverage);
                viewport.verticalFovDeg = Mathf.Lerp(100f, 35f, blurCoverage);
            }
        }

        void ClearCombined()
        {
            var prev = RenderTexture.active;
            RenderTexture.active = _combined;
            GL.Clear(true, true, new Color(0, 0, 0, 0));   // transparent = "no tile here"
            RenderTexture.active = prev;
        }

        IEnumerator ReportLoop()
        {
            var wait = new WaitForSeconds(2f);
            while (true)
            {
                yield return wait;
                var visible = viewport.VisibleTiles(_manifest);
                var payload = new ClientReport
                {
                    viewport_tile_count = visible.Count,
                    total_tiles = _manifest.TileCount,
                    bandwidth_kbps = Mathf.RoundToInt(bandwidth.EstimatedKbps),
                    rebuffer_events = _rebufferEvents,
                    quality = bandwidth.PickQuality(),
                };
                yield return PostReport(payload);
            }
        }

        IEnumerator PostReport(ClientReport payload)
        {
            string json = JsonUtility.ToJson(payload);
            using var req = new UnityWebRequest($"{serverUrl}/report", "POST")
            {
                uploadHandler = new UploadHandlerRaw(System.Text.Encoding.UTF8.GetBytes(json)),
                downloadHandler = new DownloadHandlerBuffer(),
            };
            req.SetRequestHeader("Content-Type", "application/json");
            yield return req.SendWebRequest();
        }
    }

    [System.Serializable]
    public class ClientReport
    {
        public int viewport_tile_count;
        public int total_tiles;
        public int bandwidth_kbps;
        public int rebuffer_events;
        public string quality;
    }
}
