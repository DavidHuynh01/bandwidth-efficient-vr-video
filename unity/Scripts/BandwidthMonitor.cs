using UnityEngine;

namespace TileStreaming
{
    // Tracks rolling download throughput so the client can downgrade viewport
    // tiles from "high" to "low" when the network can't keep up.
    public class BandwidthMonitor : MonoBehaviour
    {
        [Tooltip("Below this estimated throughput, request low-quality tiles.")]
        public float lowQualityThresholdKbps = 8000f;

        private float _windowBytes;
        private float _windowSeconds;
        public float EstimatedKbps { get; private set; }

        // Call once per completed chunk download.
        public void RecordDownload(long bytes, float seconds)
        {
            if (seconds <= 0f) return;
            _windowBytes += bytes;
            _windowSeconds += seconds;

            if (_windowSeconds >= 1f)
            {
                EstimatedKbps = (_windowBytes * 8f / 1000f) / _windowSeconds;
                _windowBytes = 0f;
                _windowSeconds = 0f;
            }
        }

        public string PickQuality()
        {
            // Until we have a sample, assume the network is fine.
            if (EstimatedKbps <= 0f) return "high";
            return EstimatedKbps < lowQualityThresholdKbps ? "low" : "high";
        }
    }
}
