"""
app.py — Flask server for tile-based adaptive bitrate 360 VR streaming.

Serves the output of tile_video.py:
  GET /manifest                              -> grid layout + tile/chunk info
  GET /tiles/<tile_id>/<quality>/<chunk>     -> a single tile-chunk .mp4
  GET /stats                                 -> lightweight server-side metrics
  POST /report                               -> client posts viewport/bandwidth logs

The client (Unity, running on the Quest) reads the manifest, decides which
tiles fall inside the predicted viewport, and requests ONLY those chunks at
high quality. Peripheral tiles are simply never requested -> bandwidth saved.

Run:
    python app.py --tiles tiles_out --host 0.0.0.0 --port 8080
"""

import argparse
import json
import os
import time

from flask import Flask, jsonify, request, send_from_directory, abort
from flask_cors import CORS

app = Flask(__name__)
CORS(app)

# Populated in main().
TILES_DIR = "tiles_out"

# In-memory metrics so you can sanity-check what the headset is doing.
STATS = {
    "started_at": time.time(),
    "chunks_served": 0,
    "bytes_served": 0,
    "reports": [],  # most recent client-side logs
}


def manifest_path():
    return os.path.join(TILES_DIR, "manifest.json")


@app.get("/manifest")
def manifest():
    if not os.path.isfile(manifest_path()):
        abort(404, "manifest.json not found — run tile_video.py first")
    with open(manifest_path()) as f:
        return jsonify(json.load(f))


@app.get("/tiles/<int:tile_id>/<quality>/<chunk>")
def tile_chunk(tile_id, quality, chunk):
    # Basic path-traversal guard.
    if quality not in ("high", "low") or not chunk.endswith(".mp4"):
        abort(400, "bad request")

    tdir = os.path.join(TILES_DIR, f"tile_{tile_id}", quality)
    fpath = os.path.join(tdir, chunk)
    if not os.path.isfile(fpath):
        abort(404)

    STATS["chunks_served"] += 1
    STATS["bytes_served"] += os.path.getsize(fpath)
    # conditional/range requests handled by send_from_directory
    return send_from_directory(tdir, chunk, mimetype="video/mp4")


@app.post("/report")
def report():
    """Client posts {viewport_tiles, bandwidth_kbps, rebuffer_events, ...}."""
    data = request.get_json(silent=True) or {}
    data["_ts"] = time.time()
    STATS["reports"].append(data)
    STATS["reports"] = STATS["reports"][-50:]  # keep last 50
    return jsonify({"ok": True})


@app.get("/stats")
def stats():
    uptime = time.time() - STATS["started_at"]
    return jsonify({
        "uptime_seconds": round(uptime, 1),
        "chunks_served": STATS["chunks_served"],
        "mb_served": round(STATS["bytes_served"] / 1_048_576, 2),
        "recent_reports": STATS["reports"][-5:],
    })


def main():
    global TILES_DIR
    p = argparse.ArgumentParser(description="Tile streaming server.")
    p.add_argument("--tiles", default="tiles_out", help="tiled output dir")
    p.add_argument("--host", default="0.0.0.0",
                   help="0.0.0.0 so the Quest can reach it over Wi-Fi")
    p.add_argument("--port", type=int, default=8080)
    args = p.parse_args()

    TILES_DIR = args.tiles
    if not os.path.isfile(manifest_path()):
        print(f"warning: {manifest_path()} missing — run tile_video.py first.")

    print(f"Serving tiles from '{TILES_DIR}' on http://{args.host}:{args.port}")
    print("From the Quest, use this PC's LAN IP (e.g. http://192.168.1.50:8080)")
    app.run(host=args.host, port=args.port, threaded=True)


if __name__ == "__main__":
    main()
