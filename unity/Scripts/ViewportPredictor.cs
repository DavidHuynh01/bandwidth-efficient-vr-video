using System.Collections.Generic;
using UnityEngine;

namespace TileStreaming
{
    // Decides which tiles fall inside the user's (optionally predicted) viewport.
    //
    // The 360 sphere is mapped equirectangularly: longitude -> column,
    // latitude -> row. Given the headset's forward direction we compute the
    // center lon/lat, then mark every tile whose angular extent overlaps the
    // field of view (plus a margin so tiles are ready before they rotate in).
    public class ViewportPredictor : MonoBehaviour
    {
        [Tooltip("Headset camera. Defaults to Camera.main.")]
        public Transform headTransform;

        [Tooltip("Horizontal FOV to cover, in degrees (Quest 2 ~ 90).")]
        public float horizontalFovDeg = 100f;

        [Tooltip("Vertical FOV to cover, in degrees.")]
        public float verticalFovDeg = 90f;

        [Tooltip("Extra degrees fetched around the viewport as a safety margin.")]
        public float marginDeg = 15f;

        [Tooltip("Seconds of head motion to extrapolate ahead (0 = no prediction).")]
        public float predictionSeconds = 0.12f;

        private Vector3 _lastForward;
        private Vector3 _angularVelDeg; // crude yaw/pitch rate estimate

        void Awake()
        {
            if (headTransform == null && Camera.main != null)
                headTransform = Camera.main.transform;
        }

        void Update()
        {
            if (headTransform == null) return;
            Vector3 fwd = headTransform.forward;
            if (_lastForward != Vector3.zero && Time.deltaTime > 0f)
            {
                // Approximate angular velocity from the change in look direction.
                float yawNow = Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg;
                float yawPrev = Mathf.Atan2(_lastForward.x, _lastForward.z) * Mathf.Rad2Deg;
                float pitchNow = Mathf.Asin(Mathf.Clamp(fwd.y, -1f, 1f)) * Mathf.Rad2Deg;
                float pitchPrev = Mathf.Asin(Mathf.Clamp(_lastForward.y, -1f, 1f)) * Mathf.Rad2Deg;
                _angularVelDeg.x = Mathf.DeltaAngle(yawPrev, yawNow) / Time.deltaTime;
                _angularVelDeg.y = (pitchNow - pitchPrev) / Time.deltaTime;
            }
            _lastForward = fwd;
        }

        // where the (predicted) gaze centre sits in lon/lat
        void GazeCenter(out float yaw, out float pitch)
        {
            Vector3 fwd = headTransform.forward;
            yaw = Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg
                  + _angularVelDeg.x * predictionSeconds;
            pitch = Mathf.Asin(Mathf.Clamp(fwd.y, -1f, 1f)) * Mathf.Rad2Deg
                    + _angularVelDeg.y * predictionSeconds;
        }

        // angular distance in degrees from the gaze centre to a tile's centre.
        // lets the stream manager rank tiles and pick a quality per tile.
        public float GazeAngleTo(TileInfo t, TileManifest m)
        {
            if (headTransform == null || m == null) return 0f;
            GazeCenter(out float yaw, out float pitch);
            float tileYaw = -180f + (t.col + 0.5f) * (360f / m.cols);
            float tilePitch = 90f - (t.row + 0.5f) * (180f / m.rows);
            float dYaw = Mathf.DeltaAngle(yaw, tileYaw);
            float dPitch = pitch - tilePitch;
            return Mathf.Sqrt(dYaw * dYaw + dPitch * dPitch);
        }

        // Returns the set of tile ids that should be fetched this frame.
        public HashSet<int> VisibleTiles(TileManifest m)
        {
            var result = new HashSet<int>();
            if (headTransform == null || m == null) return result;

            GazeCenter(out float centerYaw, out float centerPitch);

            float halfH = horizontalFovDeg * 0.5f + marginDeg;
            float halfV = verticalFovDeg * 0.5f + marginDeg;

            float tileYawSpan = 360f / m.cols;
            float tilePitchSpan = 180f / m.rows;

            foreach (var t in m.tiles)
            {
                // Tile center in lon/lat. Column 0 starts at -180 longitude;
                // row 0 is the top of the sphere (+90 latitude).
                float tileYaw = -180f + (t.col + 0.5f) * tileYawSpan;
                float tilePitch = 90f - (t.row + 0.5f) * tilePitchSpan;

                float dYaw = Mathf.Abs(Mathf.DeltaAngle(centerYaw, tileYaw));
                float dPitch = Mathf.Abs(centerPitch - tilePitch);

                if (dYaw <= halfH + tileYawSpan * 0.5f &&
                    dPitch <= halfV + tilePitchSpan * 0.5f)
                {
                    result.Add(t.id);
                }
            }
            return result;
        }
    }
}
