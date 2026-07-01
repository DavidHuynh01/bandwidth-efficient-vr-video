using System;

namespace TileStreaming
{
    // Mirrors manifest.json produced by tile_video.py. JsonUtility fills these
    // by matching field names exactly, so do not rename them.
    [Serializable]
    public class TileInfo
    {
        public int id;
        public int col;
        public int row;
    }

    [Serializable]
    public class TileManifest
    {
        public string source;
        public int video_width;
        public int video_height;
        public int cols;
        public int rows;
        public int tile_width;
        public int tile_height;
        public int segment_seconds;
        public int num_chunks;
        public string[] qualities;
        public TileInfo[] tiles;

        // low-res full-panorama fallback that fills everything outside the viewport
        public int base_tile_id;
        public int base_width;
        public int base_height;
        public int base_chunks;

        public int TileCount => cols * rows;
        public bool HasBase => base_chunks > 0;
    }
}
