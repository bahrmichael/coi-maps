# Map Share mod

Source code for https://coigame.com/Mod/1255. Requires a server that can store and serve the screenshots as maps. That server code is not available, but I can give you a spec if you ask.

Mod ID `map-share`. Build and install:

    dotnet build -c Release     # copies MapShare.dll and manifest.json to Mods/map-share

The default paths are for Steam on Linux (Proton). On other systems, set them:

    dotnet build -c Release -p:GameDir="C:/Program Files (x86)/Steam/steamapps/common/Captain of Industry" -p:ModsDir="%APPDATA%/Captain of Industry/Mods"

## Security and privacy

Report security problems privately to the author, not in a public issue.

## Files

| File | Purpose |
|------|---------|
| `MapShareMod.cs` | Entry point (`IMod`, `IsUiOnly = true`). |
| `MapSession.cs` | Share link, settings, create and delete the session, relay requests. |
| `MapCapture.cs` | Offscreen shots of all pictures, tar archives, upload. |
| `MapWindow.cs` | Toolbar window: upload warning, Setup, Detail and Upload sections, Advanced section. |

## Modding notes

Based on research and decompiling on 4.3. Might break with game updates.

### Threads

| Thread | Hook | Use |
|--------|------|-----|
| Main (render) | `IGameLoopEvents.RenderUpdate`, `RenderUpdateEnd` | Unity objects, cameras, screenshots. |
| Thread pool | `Task.Run` | Network, JPEG encoding, file writes. |

### Coordinates

- One tile is 2 Unity units. Tile `(x, y, z)` is Unity `(x * 2, z * 2, y * 2)`.
- A camera with `Quaternion.Euler(90, 0, 0)` looks straight down. The top of
  the image is +tile y, and the right side is +tile x.

### Offscreen screenshots

`ScreenshotTaker.ScheduleScreenshotCapture` is public. It takes a camera
pose, FOV, size, supersampling and `ScreenshotConfigFlags`. In
`RenderUpdateEnd`, it moves the main camera, calls `camera.Render()` into a
`RenderTexture`, and restores the camera in a `finally` block. All of this
happens before Unity renders the frame for the screen, so the player does not
see the shot. The callback runs on the main thread in the same frame, and the
texture is shared: copy it (`GetRawTextureData`) and encode on the thread pool.

Some problems and their fixes:

| Problem | Cause | Fix |
|---------|-------|-----|
| No terrain or ocean far from the player (only trees) | "Always visible" renderers (terrain, ocean) draw with bounds around the **player's** camera, only ±500 units deep (`ChunkBasedRenderingManager.renderChunks`). Unity culls them for our pose. | Set `Camera.cullingMatrix` to a box around the whole map for the shot. `ForceNoChunkCulling` does not help here. |
| Ocean bands and blotches | The ocean samples shadows itself (`_CustomShadowDistance`, `_CustomShadowMinAttenuation`) and reads the cloud texture (`_Mafi_CookieTexture`). | No shadows in shots: `QualitySettings.shadows = Disable`, attenuation `1`, white cloud texture. |
| Cloud shadows on terrain | `SkyboxController` adds a light command buffer at `AfterScreenspaceMask`. `DisableWeather` does not touch it. | Shadows off (above) also removes it. |
| Dark grid at cell edges | Post-processing vignette. | Turn off `Vignette` in the camera's `PostProcessVolume` profile for the shot. |
| Seams between cells | Perspective camera: tall objects lean differently at the edges. | One camera height for each level and a 2° FOV (near-orthographic). |

Known limits:

- `ScreenshotTaker` applies `ForceLod0` and `ForceNoChunkCulling` before the
  frame is scheduled and resets them after the shot, so the player's frame
  also gets them: trees change, and all chunks of the map draw. The mod must
  use `ForceNoChunkCulling`: without it, far cells have no terrain.

