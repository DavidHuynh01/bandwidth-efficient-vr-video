"""
tile_video.py — Spatially divide a 360 equirectangular video into a grid of
independent tiles, encode each tile at multiple quality levels, and segment
each tile-stream into short chunks for adaptive HTTP streaming.

This is the offline preparation step. Run it once per source video; the Flask
server (app.py) then serves the output it produces.

Requires ffmpeg + ffprobe on PATH:  https://ffmpeg.org/download.html

Example:
    python tile_video.py input_360.mp4 --cols 4 --rows 2 --seg 2
"""

import argparse
import json
import os
import shutil
import subprocess
import sys

# Quality ladder. Each entry: (label, crf, max-height-per-tile).
# Lower CRF = higher quality / bigger files. The client picks "high" for
# viewport tiles and "low" when bandwidth is tight.
QUALITIES = {
    "high": {"crf": 23, "preset": "medium"},
    "low": {"crf": 34, "preset": "fast"},
}


def run(cmd):
    print("  $", " ".join(cmd))
    subprocess.run(cmd, check=True)


def probe_dimensions(path):
    """Return (width, height) of the first video stream."""
    out = subprocess.check_output([
        "ffprobe", "-v", "error",
        "-select_streams", "v:0",
        "-show_entries", "stream=width,height",
        "-of", "json", path,
    ])
    stream = json.loads(out)["streams"][0]
    return int(stream["width"]), int(stream["height"])


def tile_video(src, out_dir, cols, rows, seg_seconds):
    width, height = probe_dimensions(src)
    if width % cols or height % rows:
        print(f"warning: {width}x{height} not evenly divisible by "
              f"{cols}x{rows}; ffmpeg will round tile sizes.")

    tile_w = width // cols
    tile_h = height // rows

    if os.path.isdir(out_dir):
        shutil.rmtree(out_dir)
    os.makedirs(out_dir)

    tiles = []
    for row in range(rows):
        for col in range(cols):
            tile_id = row * cols + col
            x = col * tile_w
            y = row * tile_h
            tiles.append({"id": tile_id, "col": col, "row": row})

            for qlabel, q in QUALITIES.items():
                tdir = os.path.join(out_dir, f"tile_{tile_id}", qlabel)
                os.makedirs(tdir, exist_ok=True)

                # Crop this tile out of every frame, encode at this quality,
                # and split into fixed-length segments named chunk_000.mp4 ...
                run([
                    "ffmpeg", "-y", "-i", src,
                    "-vf", f"crop={tile_w}:{tile_h}:{x}:{y}",
                    "-c:v", "libx264", "-crf", str(q["crf"]),
                    "-preset", q["preset"], "-an",
                    "-pix_fmt", "yuv420p",  # required by Android/Quest decoders
                    "-f", "segment",
                    "-segment_time", str(seg_seconds),
                    "-reset_timestamps", "1",
                    "-g", str(seg_seconds * 30),
                    os.path.join(tdir, "chunk_%03d.mp4"),
                ])

    # Count how many chunks were produced (same for every tile/quality).
    sample = os.path.join(out_dir, "tile_0", "high")
    num_chunks = len([f for f in os.listdir(sample) if f.endswith(".mp4")])

    manifest = {
        "source": os.path.basename(src),
        "video_width": width,
        "video_height": height,
        "cols": cols,
        "rows": rows,
        "tile_width": tile_w,
        "tile_height": tile_h,
        "segment_seconds": seg_seconds,
        "num_chunks": num_chunks,
        "qualities": list(QUALITIES.keys()),
        "tiles": tiles,
    }
    with open(os.path.join(out_dir, "manifest.json"), "w") as f:
        json.dump(manifest, f, indent=2)

    print(f"\nDone. {len(tiles)} tiles x {len(QUALITIES)} qualities x "
          f"{num_chunks} chunks -> {out_dir}")
    print(f"Manifest: {os.path.join(out_dir, 'manifest.json')}")


def main():
    p = argparse.ArgumentParser(description="Tile a 360 video for streaming.")
    p.add_argument("src", help="path to source equirectangular 360 video")
    p.add_argument("--out", default="tiles_out", help="output directory")
    p.add_argument("--cols", type=int, default=4, help="grid columns")
    p.add_argument("--rows", type=int, default=2, help="grid rows")
    p.add_argument("--seg", type=int, default=2, help="segment length (sec)")
    args = p.parse_args()

    if not shutil.which("ffmpeg") or not shutil.which("ffprobe"):
        sys.exit("ffmpeg/ffprobe not found on PATH. Install ffmpeg first.")
    if not os.path.isfile(args.src):
        sys.exit(f"source not found: {args.src}")

    tile_video(args.src, args.out, args.cols, args.rows, args.seg)


if __name__ == "__main__":
    main()
