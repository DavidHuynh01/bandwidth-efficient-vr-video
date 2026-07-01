# Unity client (Meta Quest)

The files in this folder are the VR client — four C# scripts and the
`FoveatedPanoramic` skybox shader. Unity projects can't be scaffolded from
loose files, so create a project once and drop these in. Built and tested on
**Unity 6.3 LTS** with the Meta XR SDK on a Quest 2.

## 1. Tooling
- **Unity Hub** + **Unity 6.3 LTS**. In the install modules, check
  **Android Build Support** > **Android SDK & NDK Tools** + **OpenJDK**.
- Put the headset in **Developer Mode** (Meta Horizon phone app > your device >
  Headset settings > Developer Mode). Note this only works from the headset's
  **owner** account and needs a free developer org.

## 2. Create the project
1. New project > **Universal 3D (URP)** template.
2. `File > Build Profiles` > **Android** > **Switch Platform**.
3. Install the **Meta XR All-in-One SDK**: add it to your assets on the Unity
   Asset Store (free), then `Window > Package Manager > My Assets > Install`.
4. `Edit > Project Settings > XR Plug-in Management > Android tab` > enable
   **OpenXR** (not Oculus — it's deprecated on Unity 6). Accept the prompt to
   enable the Meta XR feature set.
5. Under **OpenXR**, add the **Oculus Touch Controller Profile** to Interaction
   Profiles, then run **Project Validation** and **Fix All**. If both the Oculus
   and OpenXR plugins end up installed, remove the Oculus one in Package Manager.

## 3. Allow plain HTTP (the two easy-to-miss steps)
The client talks to the Flask server over `http://` on your LAN, which both
Unity and Android block by default:
- `Project Settings > Player > Other Settings` > set **Allow downloads over
  HTTP** to **Always allowed**, then **save the project** so it sticks.
- Add to your `Assets/Plugins/Android/AndroidManifest.xml`:
  `android:usesCleartextTraffic="true"` on the `<application>` tag, plus
  `<uses-permission android:name="android.permission.INTERNET" />`.

## 4. Scene setup
1. Delete the default **Main Camera**.
2. `Meta > Tools > Building Blocks` > drag in the **Camera Rig** block (gives you
   head tracking via `CenterEyeAnchor`).
3. Create a **Material**, set its **Shader** to **Skybox > FoveatedPanoramic**.
   Leave the textures empty — the client fills them at runtime. No sphere object
   is needed; the video renders as the skybox.
4. Create an empty GameObject `StreamController` and add three components:
   `TileStreamManager`, `ViewportPredictor`, `BandwidthMonitor`
   (`TileManifest` is plain data, not a component).
5. On **Tile Stream Manager**:
   - **Server Url** = `http://<your-PC-LAN-IP>:8080`
   - **Skybox Material** = the FoveatedPanoramic material from step 3
   - **Combined Texture Width/Height** = match your source video (e.g. 4096 × 2048)
   - **Viewport** / **Bandwidth** = the components on this same object
6. On **Viewport Predictor**, set **Head Transform** = `CenterEyeAnchor`
   (or leave empty to fall back to `Camera.main`).

## 5. Test in the editor first
Your PC can reach its own server, so you don't need the headset to test:
1. Start the server (`server/start_server.bat`).
2. Press **Play**. The video should wrap around the Game view, sharp where the
   camera points and blurred toward the edges.
3. Tune the **Foveated blur** section on `StreamController` live:
   - **Blur Coverage** — grows the blur inward from the edges (0 = mostly sharp).
   - **Max Blur Strength** — how heavy the edge blur gets.
   - **Fetch Less When Blurred** — fetches fewer high-res tiles as coverage
     rises, so more blur means less bandwidth and fewer decoders.

## 6. Build to the headset
- Both PC and Quest on the **same Wi-Fi** (not a guest network); the server
  binds `0.0.0.0` already. Allow Python through the Windows Firewall on port 8080.
- Connect by USB, accept **Allow USB debugging** in-headset.
- `File > Build Profiles > Run Device` > your Quest > **Build And Run**.

## How it renders
Only the tiles inside the viewport are fetched at full quality and composited
into one equirectangular render texture. A small low-res base layer streams
underneath to fill everything else, and the shader blurs that base by the angle
from your gaze — sharp centre, soft edges, no black gaps. Watch the server's
`GET /tiles/...` log to see only a handful of tiles requested per segment.

## Known limitation
The Quest's hardware can only decode a few video streams at once, so the tile
pool is kept small. To scale to many tiles, stitch the selected tiles into one
frame on the server and decode that single stream (tiled HEVC / MPEG-DASH SRD).
