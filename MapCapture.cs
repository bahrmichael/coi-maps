using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Mafi;
using Mafi.Core;
using Mafi.Core.Buildings.RuinedBuildings;
using Mafi.Core.Entities;
using Mafi.Core.Entities.Dynamic;
using Mafi.Core.Entities.Static;
using Mafi.Core.Entities.Static.Layout;
using Mafi.Core.GameLoop;
using Mafi.Core.Terrain;
using Mafi.Core.Terrain.Designation;
using Mafi.Core.Vehicles;
using Mafi.Unity.Camera;
using Mafi.Unity.InputControl;
using Mafi.Unity.Utils;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering.PostProcessing;

namespace MapShare;

// Builds a top-down map from small screenshots and uploads them to the relay, when the player
// clicks Upload map. It needs a link (MapSession). Rendering notes (culling, shadows, vignette)
// are in README.md.
// 1. Finds the cells with the player's things (scanCells) and sends the map info, with the zoom
//    levels from the detail setting.
// 2. Takes all pictures of these levels with an offscreen camera pose, a few each frame. The
//    player's view does not change. The shot changes (shadows, culling, vignette) stay applied
//    for all frames. The JPEGs go to a folder, because a large map has tens of thousands of them.
// 3. Uploads them in tar archives of up to 1 MiB, direct to the relay's storage. The relay
//    unpacks them.
// The mod pauses the game for step 2, and resumes it after, if it paused it.
// Zoom levels: a picture at level z covers 2^z x 2^z cells at the same size, so a zoomed-out
// viewer loads few pictures. Each level is a separate shot from higher up. Levels below
// SPARSE_BELOW are sparse: they have pictures only where the player has things. The viewer shows
// the SPARSE_BELOW level everywhere else.
[GlobalDependency(RegistrationMode.AsSelf)]
public sealed class MapCapture : IDisposable
{
    public const int CELL_TILES = 16;
    public const int CELL_PX = 512;
    // Narrow FOV gives a near-orthographic view, so cells join with small seams.
    private const float FOV = 2f;
    private const float UNITS_PER_TILE = 2f;
    // Levels 0 (32 px per tile) and 1 (16 px per tile) are for close zoom. After level 2, each
    // step is 4x fewer pictures, so the viewer loads at most about 4x the screen pixels.
    // The detail slider selects the most detailed level. The levels above it always upload.
    public static readonly int[] LEVELS = { 0, 1, 2, 4, 6 };
    public const int SPARSE_BELOW = 2;
    // Cells next to a marked cell are marked too, so that things at a cell edge are not cut.
    private const int MARGIN_CELLS = 1;

    // Pictures per frame. Each frame has a fixed cost (the player's frame, with all chunks, and the
    // setup of ScreenshotTaker). The capture measures it and the cost of one picture, and takes so
    // many pictures that they use about 80 % of the frame: n = 4 * fixed / picture.
    private const int FIRST_SHOTS = 4;
    // At least 2, because the cost of a picture is measured between two pictures of one frame.
    private const int MIN_SHOTS = 2;
    private const int MAX_SHOTS = 64;
    // The first frames are slow (the shot changes, the adaptation), so the estimate waits.
    private const int MIN_ESTIMATE_PICTURES = 32;
    // Raw pictures (1 MB each) in the thread pool. More pauses the capture.
    private const int MAX_ENCODING = 128;
    // Archives (tar). The relay accepts up to 10 MiB and 512 files. Small archives let the
    // relay unpack each one quickly.
    private const int TAR_BLOCK = 512;
    private const int ARCHIVE_MAX_BYTES = 1024 * 1024;
    private const int ARCHIVE_MAX_FILES = 512;
    private const string ARCHIVE_TYPE = "application/x-tar";
    private const int ARCHIVE_ATTEMPTS = 5;
    // The relay unpacks an archive after a queue message, usually in a few seconds.
    private static readonly TimeSpan ARCHIVE_POLL = TimeSpan.FromSeconds(2);

    private static readonly long SEC = Stopwatch.Frequency;
    // The relay allows a burst of 1000 uploads (map info or archive) for each IP, then 1000
    // each hour. After the burst, the mod waits for Retry-After on 429.
    private static readonly long UPLOAD_INTERVAL = 60 * SEC / 1000;
    private static readonly long RETRY_DELAY = 10 * SEC;
    // Minimum time between two scans for the window.
    private static readonly long SCAN_INTERVAL = 2 * SEC;
    private const string REJECTED = "The relay does not know this link. Delete it and create a new link.";

    // Shader globals of the game's ocean and cloud shaders.
    private static readonly int SHADOW_ATTENUATION_ID = Shader.PropertyToID("_CustomShadowMinAttenuation");
    private static readonly int CLOUD_TEXTURE_ID = Shader.PropertyToID("_Mafi_CookieTexture");

    private enum Phase { Scan, Map, Capture, Upload, Done, Failed }

    // One progress line in the window. Stopped: a later step, or a cancel.
    public enum StepState { Waiting, Running, Stopped, Done, Failed }

    public readonly struct Step
    {
        public readonly string Text;
        public readonly StepState State;

        public Step(string text, StepState state)
        {
            Text = text;
            State = state;
        }
    }

    private sealed class Item
    {
        public Level Level;
        public int X, Y;
        public string Path;
        public volatile bool Uploaded;
    }

    private sealed class Job
    {
        public string Session;
        public string Dir;
        // The zoom levels of this upload, for the map info.
        public int[] Levels;
        // Picture indexes (y * cols + x) of the sparse levels, for the map info.
        public readonly Dictionary<int, List<int>> Sparse = new();
        // The capture waits for a scan after this one.
        public int ScanAfter;
        // Pictures to take, the most zoomed-out level first. Main thread only.
        public readonly List<(Level Level, int Index)> Todo = new();
        public int Next;
        // Pictures on disk. Lock it.
        public readonly List<Item> Files = new();
        public int Encoding;
        public int Uploaded;
        public long NextUpload;
        // Archive bytes, for the progress line.
        public long TotalBytes;
        public long SentBytes;
        public long UploadStart;
        public volatile Phase Phase;
        // All pictures are taken, and all archives are sent (or gave up).
        public volatile bool CaptureDone;
        public volatile bool Sent;
        // Cancelled is also true after a failure. Failed is not true after a cancel.
        public volatile bool Failed;
        public volatile bool Cancelled;
        public volatile string Message;
        // Statistics for the log at the end of the capture phase.
        public long CaptureStart;
        public int Frames;
        public float FrameTimeSum;
        public int ShotsPerFrame = FIRST_SHOTS;
        public int MaxShotsPerFrame;
        public int EncoderWaits;
        // Time between two capture callbacks in the same frame: one render and its ReadPixels.
        public long ShotTicks;
        public int ShotSamples;
        public int LastShotFrame = -1;
        public long LastShotAt;
        // Encoder and file write, on the thread pool.
        public long EncodeTicks;
        public int Encoded;
    }

    private readonly MapSession m_session;
    private readonly ScreenshotTaker m_screenshots;
    private readonly CameraController m_camera;
    private readonly Vignette m_vignette;
    private readonly IGameLoopEvents m_gameLoop;
    private readonly TerrainManager m_terrain;
    private readonly GameSpeedController m_speed;
    private readonly IEntitiesManager m_entities;
    private readonly IVehiclesManager m_vehicles;
    private readonly ITerrainDesignationsManager m_designations;

    public readonly int Cols;
    public readonly int Rows;
    // Made on first use. The terrain heights are not ready in the constructor on a new game.
    private Level[] m_levelsOrNull;

    private Job m_job;
    // Cells (y * Cols + x) with the player's things and the margin, from the last scan. Null before
    // the first scan. The scan runs in SyncUpdate, because the simulation thread changes the
    // entities at other times. Main thread only.
    private bool[] m_marked;
    private bool m_scanRequested;
    private long m_scannedAt;
    public int ScanCount { get; private set; }
    // True while the map info request runs.
    private volatile bool m_busy;
    private bool m_shotOverrides;
    // Values that beginShot changed, for endShot.
    private ShadowQuality m_savedShadows;
    private float m_savedShadowAttenuation;
    private Texture m_savedCloudTexture;
    private bool m_savedVignette;
    // No uploads before this time (Stopwatch ticks), after a 429 from the relay. Upload threads write it.
    private long m_rateLimitEnd;
    // True if Start paused the game. The game resumes when the capture phase ends.
    private bool m_pausedByUs;

    public MapCapture(MapSession session, ScreenshotTaker screenshots, IGameLoopEvents gameLoop,
        TerrainManager terrain, CameraController camera, GameSpeedController speed, IEntitiesManager entities,
        IVehiclesManager vehicles, ITerrainDesignationsManager designations)
    {
        m_entities = entities;
        m_vehicles = vehicles;
        m_designations = designations;
        m_speed = speed;
        m_camera = camera;
        // The vignette darkens the edges of each cell. Use the profile that the game changes, if any.
        PostProcessVolume volume = camera.Camera.GetComponentInChildren<PostProcessVolume>();
        PostProcessProfile profile = volume == null ? null : volume.HasInstantiatedProfile() ? volume.profile : volume.sharedProfile;
        if (profile == null || !profile.TryGetSettings(out m_vignette)) m_vignette = null;
        m_session = session;
        m_screenshots = screenshots;
        m_gameLoop = gameLoop;
        m_terrain = terrain;

        Cols = (terrain.TerrainWidth + CELL_TILES - 1) / CELL_TILES;
        Rows = (terrain.TerrainHeight + CELL_TILES - 1) / CELL_TILES;

        m_gameLoop.RenderUpdate.AddNonSaveable(this, onRenderUpdate);
        m_gameLoop.SyncUpdate.AddNonSaveable(this, onSyncUpdate);
    }

    public void Dispose()
    {
        m_gameLoop.RenderUpdate.RemoveNonSaveable(this, onRenderUpdate);
        m_gameLoop.SyncUpdate.RemoveNonSaveable(this, onSyncUpdate);
        Cancel();
    }

    public bool Running
    {
        get
        {
            Phase? phase = m_job?.Phase;
            return phase == Phase.Scan || phase == Phase.Map || phase == Phase.Capture || phase == Phase.Upload;
        }
    }

    private bool Capturing
    {
        get
        {
            Phase? phase = m_job?.Phase;
            return phase == Phase.Scan || phase == Phase.Map || phase == Phase.Capture;
        }
    }

    // The three progress lines (capture, upload, processing on the relay), or null.
    public Step[] Steps
    {
        get
        {
            Job j = m_job;
            if (j == null) return null;
            bool stopped = j.Phase == Phase.Failed;
            // After a stop, the first step that did not finish shows why. The steps after it stop.
            StepState current = stopped ? (j.Failed ? StepState.Failed : StepState.Stopped) : StepState.Running;
            StepState later = stopped ? StepState.Stopped : StepState.Waiting;
            var steps = new Step[3];

            if (j.CaptureDone) steps[0] = new Step($"Capturing data: {j.Files.Count} pictures.", StepState.Done);
            else if (j.Phase == Phase.Scan) steps[0] = new Step("Capturing data: finding buildings.", current);
            else if (j.Phase == Phase.Map) steps[0] = new Step("Capturing data: sending map info.", current);
            else
            {
                // The average rate since the start. All pictures have the same size, so the
                // levels cost about the same.
                double elapsed = j.CaptureStart == 0 ? 0 : (double)(Stopwatch.GetTimestamp() - j.CaptureStart) / SEC;
                string remaining = j.Next < MIN_ESTIMATE_PICTURES || elapsed < 1 ? "unknown"
                    : "ca. " + MapWindow.FormatDuration((long)((j.Todo.Count - j.Next) * elapsed / j.Next));
                steps[0] = new Step($"Capturing data: {j.Next}/{j.Todo.Count} pictures."
                    + (stopped ? "" : $" Time left: {remaining}."), current);
            }

            long sent = Volatile.Read(ref j.SentBytes);
            if (!j.CaptureDone) steps[1] = new Step("Uploading data: waiting.", later);
            else if (j.Sent)
            {
                steps[1] = sent == j.TotalBytes
                    ? new Step($"Uploading data: {j.TotalBytes / 1e6:F1} MB.", StepState.Done)
                    : new Step($"Uploading data: {sent / 1e6:F1}/{j.TotalBytes / 1e6:F1} MB. The game log has the errors.", StepState.Failed);
            }
            else
            {
                double seconds = (double)(Stopwatch.GetTimestamp() - j.UploadStart) / SEC;
                string left = sent <= 0 || j.UploadStart == 0 ? "unknown"
                    : "ca. " + MapWindow.FormatDuration((long)((j.TotalBytes - sent) * seconds / sent));
                steps[1] = new Step($"Uploading data: {sent / 1e6:F1}/{j.TotalBytes / 1e6:F1} MB."
                    + (stopped ? "" : $" Time left: {left}."), current);
            }

            // The relay unpacks each archive while the next one uploads.
            int stored = Volatile.Read(ref j.Uploaded);
            string publishedText = $"Server-side processing: {stored}/{j.Todo.Count} pictures published.";
            if (!j.CaptureDone) steps[2] = new Step("Server-side processing: waiting.", later);
            else if (j.Phase == Phase.Done)
            {
                steps[2] = stored == j.Todo.Count ? new Step(publishedText, StepState.Done)
                    : new Step(publishedText + " The game log has the errors.", StepState.Failed);
            }
            else steps[2] = new Step(publishedText, j.Sent ? current : stopped ? later : StepState.Running);
            return steps;
        }
    }

    // The result of the last upload, or null while it runs.
    public string Message
    {
        get
        {
            Job j = m_job;
            return j != null && (j.Phase == Phase.Done || j.Phase == Phase.Failed) ? j.Message : null;
        }
    }

    // Pictures at this detail: all levels from finestLevel up. Sparse: the pictures of the sparse
    // levels, from the last scan, and sparseFull the same levels without the scan.
    public int PictureCount(int finestLevel, out int sparse, out int sparseFull)
    {
        int n = 0;
        sparse = sparseFull = 0;
        foreach (Level level in Levels)
        {
            if (level.Z < finestLevel) continue;
            int all = level.Cols * level.Rows;
            if (level.Z >= SPARSE_BELOW)
            {
                n += all;
                continue;
            }
            int marked = 0;
            for (int i = 0; i < all; i++)
            {
                if (hasMarked(level, i % level.Cols, i / level.Cols)) marked++;
            }
            n += marked;
            sparse += marked;
            sparseFull += all;
        }
        return n;
    }

    // Main thread. Asks for a new scan if the last one is old. The window calls it while it is open.
    public void RefreshScan()
    {
        if (Stopwatch.GetTimestamp() - m_scannedAt > SCAN_INTERVAL) m_scanRequested = true;
    }

    // Rough average size of one 512 px JPEG at quality 85, for the size estimate. Ocean pictures
    // are smaller, factory and forest pictures are larger.
    public const int AVG_PICTURE_BYTES = 77_000;

    // Main thread.
    public void Start()
    {
        if (Running || m_session.SessionId == null) return;
        // A finest level that is not in LEVELS falls back to the most detail.
        int finest = Array.IndexOf(LEVELS, m_session.FinestLevel) < 0 ? LEVELS[0] : m_session.FinestLevel;
        var j = new Job { Dir = m_session.UploadDir, Session = m_session.SessionId, Levels = Array.FindAll(LEVELS, z => z >= finest) };
        try
        {
            // Files of an upload that did not finish, for example after a crash.
            Directory.CreateDirectory(j.Dir);
            foreach (string file in Directory.GetFiles(j.Dir, "*.jpg")) File.Delete(file);
        }
        catch (Exception e)
        {
            Log.Warning("Map Share: cannot prepare the upload folder: " + e.Message);
            j.Message = "Upload failed: cannot use the folder " + j.Dir;
            j.Failed = true;
            j.Phase = Phase.Failed;
            m_job = j;
            return;
        }
        // The pictures come after the scan (addPictures).
        j.Phase = Phase.Scan;
        j.ScanAfter = ScanCount;
        m_scanRequested = true;
        // Vehicles move while the pictures are taken, so they would be cut at picture edges.
        if (!m_speed.IsPaused)
        {
            m_speed.RequestPause();
            m_pausedByUs = true;
        }
        m_job = j;
    }

    // After the scan. Grouped by level, because each frame takes pictures of one level.
    private void addPictures(Job j)
    {
        int finest = j.Levels[0];
        for (int l = Levels.Length - 1; l >= 0; l--)
        {
            Level level = Levels[l];
            if (level.Z < finest) continue;
            List<int> sparse = null;
            if (level.Z < SPARSE_BELOW) j.Sparse[level.Z] = sparse = new List<int>();
            for (int i = 0; i < level.Cols * level.Rows; i++)
            {
                if (sparse != null)
                {
                    if (!hasMarked(level, i % level.Cols, i / level.Cols)) continue;
                    sparse.Add(i);
                }
                j.Todo.Add((level, i));
            }
        }
    }

    // True if a cell of this picture is marked. Before the first scan, all cells are marked.
    private bool hasMarked(Level level, int px, int py)
    {
        if (m_marked == null) return true;
        for (int y = py * level.Span; y < Math.Min(Rows, (py + 1) * level.Span); y++)
        {
            for (int x = px * level.Span; x < Math.Min(Cols, (px + 1) * level.Span); x++)
            {
                if (m_marked[y * Cols + x]) return true;
            }
        }
        return false;
    }

    // --- Scan (main thread, in sync with the simulation) ---------------------------

    private void onSyncUpdate(GameTime time)
    {
        if (!m_scanRequested) return;
        m_scanRequested = false;
        try
        {
            m_marked = scanCells();
        }
        catch (Exception e)
        {
            // All cells then, so that nothing is missing.
            Log.Warning("Map Share: cannot find the buildings: " + e.Message);
            m_marked = null;
        }
        m_scannedAt = Stopwatch.GetTimestamp();
        ScanCount++;
    }

    // Marks the cells with buildings and transports (not ruins), vehicles, and mining and dumping
    // designations. Then adds the margin.
    private bool[] scanCells()
    {
        var found = new bool[Cols * Rows];
        void mark(int tileX, int tileY)
        {
            int x = tileX / CELL_TILES, y = tileY / CELL_TILES;
            if (tileX >= 0 && tileY >= 0 && x < Cols && y < Rows) found[y * Cols + x] = true;
        }
        foreach (IStaticEntity e in m_entities.GetAllEntitiesOfType<IStaticEntity>())
        {
            if (e.IsDestroyed || e is Ruins) continue;
            Tile3i center = e.CenterTile;
            foreach (OccupiedTileRelative t in e.OccupiedTiles) mark(center.X + t.RelativeX, center.Y + t.RelativeY);
        }
        foreach (Vehicle v in m_vehicles.AllVehicles)
        {
            if (v.IsSpawned) mark(v.Position2f.X.ToIntFloored(), v.Position2f.Y.ToIntFloored());
        }
        foreach (TerrainDesignation d in m_designations.Designations)
        {
            if (d.IsForestry) continue;
            RectangleTerrainArea2i area = d.Area;
            for (int y = 0; y < area.Size.Y; y++)
            {
                for (int x = 0; x < area.Size.X; x++) mark(area.Origin.X + x, area.Origin.Y + y);
            }
        }

        var marked = new bool[Cols * Rows];
        for (int y = 0; y < Rows; y++)
        {
            for (int x = 0; x < Cols; x++)
            {
                if (!found[y * Cols + x]) continue;
                for (int my = Math.Max(0, y - MARGIN_CELLS); my <= Math.Min(Rows - 1, y + MARGIN_CELLS); my++)
                {
                    for (int mx = Math.Max(0, x - MARGIN_CELLS); mx <= Math.Min(Cols - 1, x + MARGIN_CELLS); mx++) marked[my * Cols + mx] = true;
                }
            }
        }
        return marked;
    }

    // Main thread.
    public void Cancel()
    {
        Job j = m_job;
        if (!Running) return;
        bool capturing = Capturing;
        j.Message = "Upload cancelled.";
        j.Cancelled = true;
        // The uploader cleans up when its uploads end.
        if (j.Phase == Phase.Upload) return;
        j.Phase = Phase.Failed;
        if (capturing)
        {
            endShot();
            logCaptureStats(j);
        }
        cleanUp(j);
    }

    // --- Capture (main thread) ------------------------------------------------

    private void onRenderUpdate(GameTime time)
    {
        if (Capturing)
        {
            update();
            return;
        }
        // Normally the capture phase resets it. This is only a safety net.
        if (m_shotOverrides) endShot();
        // After the capture, a cancel or a failure. If the player resumed the game, do nothing.
        if (m_pausedByUs)
        {
            m_pausedByUs = false;
            if (m_speed.IsPaused) m_speed.RequestResume();
        }
    }

    // Each frame in the map and capture phases.
    private void update()
    {
        Job j = m_job;
        if (m_busy) return;
        if (j.Phase == Phase.Scan)
        {
            if (ScanCount == j.ScanAfter) return;
            addPictures(j);
            j.Phase = Phase.Map;
            return;
        }
        if (j.Phase == Phase.Map)
        {
            if (Stopwatch.GetTimestamp() < Volatile.Read(ref m_rateLimitEnd)) return;
            m_busy = true;
            _ = sendMapAsync(j);
            return;
        }

        if (j.CaptureStart == 0) j.CaptureStart = Stopwatch.GetTimestamp();
        else
        {
            // The previous frame, with its shots.
            j.Frames++;
            j.FrameTimeSum += Time.unscaledDeltaTime;
            adaptShotsPerFrame(j);
        }
        if (j.Next >= j.Todo.Count)
        {
            if (Volatile.Read(ref j.Encoding) > 0) return;
            endShot();
            logCaptureStats(j);
            j.CaptureDone = true;
            j.Phase = Phase.Upload;
            _ = Task.Run(() => uploadAsync(j));
            return;
        }
        if (Volatile.Read(ref j.Encoding) >= MAX_ENCODING)
        {
            j.EncoderWaits++;
            return;
        }
        // One level per frame: the culling box of beginShot is for one camera height.
        Level level = j.Todo[j.Next].Level;
        j.MaxShotsPerFrame = Math.Max(j.MaxShotsPerFrame, j.ShotsPerFrame);
        for (int n = 0; n < j.ShotsPerFrame && j.Next < j.Todo.Count && j.Todo[j.Next].Level == level; n++)
        {
            int index = j.Todo[j.Next++].Index;
            int px = index % level.Cols;
            int py = index / level.Cols;
            Interlocked.Increment(ref j.Encoding);
            shoot(level, px, py, (texture, _, error) => onCaptured(j, level, px, py, texture, error));
        }
    }

    private static void logCaptureStats(Job j)
    {
        if (j.CaptureStart == 0) return;
        double seconds = (double)(Stopwatch.GetTimestamp() - j.CaptureStart) / SEC;
        double ms(long ticks, int n) => n == 0 ? 0 : ticks * 1000.0 / SEC / n;
        Log.Info($"Map Share: took {j.Next} pictures in {seconds:F1} s ({j.Next / Math.Max(seconds, 0.001):F1} per s), "
            + $"{j.Frames} frames, average frame {(j.Frames == 0 ? 0 : j.FrameTimeSum * 1000 / j.Frames):F0} ms, "
            + $"render + ReadPixels ca. {ms(j.ShotTicks, j.ShotSamples):F0} ms per picture, "
            + $"JPEG + write {ms(Volatile.Read(ref j.EncodeTicks), Volatile.Read(ref j.Encoded)):F0} ms per picture, "
            + $"{j.EncoderWaits} frames waited for the encoder, {j.MaxShotsPerFrame} pictures per frame at most.");
    }

    // Averages since the start of the capture. The fixed cost is the frame time without the pictures.
    private static void adaptShotsPerFrame(Job j)
    {
        if (j.ShotSamples == 0) return;
        double shot = (double)j.ShotTicks / SEC / j.ShotSamples;
        double frame = j.FrameTimeSum / j.Frames;
        double shotsPerFrame = (double)j.Next / j.Frames;
        double fixedCost = Math.Max(0, frame - shotsPerFrame * shot);
        int n = (int)Math.Ceiling(4 * fixedCost / Math.Max(shot, 0.0001));
        j.ShotsPerFrame = Math.Max(MIN_SHOTS, Math.Min(MAX_SHOTS, n));
    }

    private async Task sendMapAsync(Job j)
    {
        var sparse = new List<string>();
        foreach (KeyValuePair<int, List<int>> level in j.Sparse) sparse.Add($"\"{level.Key}\":[{string.Join(",", level.Value)}]");
        try
        {
            string json = $"{{\"cols\":{Cols},\"rows\":{Rows},\"cellTiles\":{CELL_TILES},\"cellPx\":{CELL_PX},"
                + $"\"levels\":[{string.Join(",", j.Levels)}],\"sparse\":{{{string.Join(",", sparse)}}}}}";
            await m_session.PutAsync(j.Session, "map", "application/json", System.Text.Encoding.UTF8.GetBytes(json));
            if (!j.Cancelled) j.Phase = Phase.Capture;
        }
        catch (RateLimitedException e)
        {
            // The next frame tries again.
            waitForRelay(e.RetryAfter);
        }
        catch (Exception e)
        {
            fail(j, e);
        }
        finally
        {
            m_busy = false;
        }
    }

    // Schedules one picture. The callback runs at the end of this frame, before the player's frame.
    private void shoot(Level level, int px, int py, Action<Option<Texture2D>, UnityEngine.Camera, string> callback)
    {
        float size = level.Span * CELL_TILES * UNITS_PER_TILE;
        // Looks straight down. Image top is +tile Y, image right is +tile X.
        var position = new Vector3((px + 0.5f) * size, level.CameraY, (py + 0.5f) * size);
        Quaternion rotation = Quaternion.Euler(90f, 0f, 0f);

        // ForceLod0 and ForceNoChunkCulling also apply to the player's frame (trees change, all
        // chunks draw). ForceNoChunkCulling is necessary: without it, far cells have no terrain.
        ScreenshotConfigFlags flags = ScreenshotConfigFlags.DisableWeather | ScreenshotConfigFlags.DisableFog
            | ScreenshotConfigFlags.DisableIcons | ScreenshotConfigFlags.DisableTerrainOverlays
            | ScreenshotConfigFlags.DisableTerrainGrid | ScreenshotConfigFlags.DisableHighlights
            | ScreenshotConfigFlags.DisableResourceBars | ScreenshotConfigFlags.ForceNoChunkCulling
            | ScreenshotConfigFlags.ForceLod0;

        beginShot(position, level.CameraY, rotation);
        m_screenshots.ScheduleScreenshotCapture(
            encodedDataCallback: callback,
            customWidth: CELL_PX,
            customHeight: CELL_PX,
            cameraPosition: position,
            cameraRotation: rotation,
            verticalFieldOfView: FOV,
            nearFarClipPlane: level.Clip,
            superSample: 2,
            configFlags: flags);
    }

    // Main thread, at the end of the frame. The texture is shared, so copy it now. Encodes and
    // writes on the thread pool.
    private void onCaptured(Job j, Level level, int px, int py, Option<Texture2D> texture, string error)
    {
        if (j.Cancelled || texture.IsNone)
        {
            if (!j.Cancelled) Log.Warning($"Map Share: picture {level.Z}/{px}/{py} capture failed: {error}");
            Interlocked.Decrement(ref j.Encoding);
            return;
        }
        long shotAt = Stopwatch.GetTimestamp();
        if (j.LastShotFrame == Time.frameCount)
        {
            j.ShotTicks += shotAt - j.LastShotAt;
            j.ShotSamples++;
        }
        j.LastShotFrame = Time.frameCount;
        j.LastShotAt = shotAt;
        Texture2D tex = texture.Value;
        byte[] raw = tex.GetRawTextureData();
        GraphicsFormat format = tex.graphicsFormat;
        int w = tex.width, h = tex.height;
        var item = new Item { Level = level, X = px, Y = py, Path = Path.Combine(j.Dir, $"{level.Z}_{px}_{py}.jpg") };
        _ = Task.Run(() =>
        {
            try
            {
                long encodeStart = Stopwatch.GetTimestamp();
                File.WriteAllBytes(item.Path, ImageConversion.EncodeArrayToJPG(raw, format, (uint)w, (uint)h, 0, 85));
                Interlocked.Add(ref j.EncodeTicks, Stopwatch.GetTimestamp() - encodeStart);
                Interlocked.Increment(ref j.Encoded);
                lock (j.Files)
                {
                    // A cancel after the write: cleanUp already ran, so clean up this one here.
                    if (j.Cancelled) delete(item);
                    else j.Files.Add(item);
                }
            }
            catch (Exception e)
            {
                Log.Warning($"Map Share: picture {level.Z}/{px}/{py} not saved: {e.Message}");
                delete(item);
            }
            finally
            {
                Interlocked.Decrement(ref j.Encoding);
            }
        });
    }

    // Changes that apply from now until the end of the capture phase. The capture callbacks run
    // before the player's frame renders, so the player does not see them. A second call changes
    // only the culling.
    private void beginShot(Vector3 position, float cameraY, Quaternion rotation)
    {
        // The terrain, the ocean and other "always visible" renderers draw with bounds around the
        // player's camera (only +-500 units deep), so Unity culls them for a far-away shot. This
        // culling frustum covers the whole map.
        float half = Math.Max(m_terrain.TerrainWidth, m_terrain.TerrainHeight) * UNITS_PER_TILE;
        Matrix4x4 view = Matrix4x4.Scale(new Vector3(1, 1, -1)) * Matrix4x4.TRS(position, rotation, Vector3.one).inverse;
        Matrix4x4 proj = Matrix4x4.Ortho(-half, half, -half, half, 0.1f, cameraY + 5000f);
        m_camera.Camera.cullingMatrix = proj * view;
        if (m_shotOverrides) return;

        // No shadows: they change with the time of the shot, and cloud shadows move. The ocean
        // samples shadows and the cloud texture itself, so also neutralize its inputs.
        m_savedShadows = QualitySettings.shadows;
        QualitySettings.shadows = ShadowQuality.Disable;
        m_savedShadowAttenuation = Shader.GetGlobalFloat(SHADOW_ATTENUATION_ID);
        Shader.SetGlobalFloat(SHADOW_ATTENUATION_ID, 1f);
        m_savedCloudTexture = Shader.GetGlobalTexture(CLOUD_TEXTURE_ID);
        Shader.SetGlobalTexture(CLOUD_TEXTURE_ID, Texture2D.whiteTexture);

        if (m_vignette != null)
        {
            m_savedVignette = m_vignette.enabled.value;
            m_vignette.enabled.value = false;
        }
        m_shotOverrides = true;
    }

    private void endShot()
    {
        // A second call would restore stale values.
        if (!m_shotOverrides) return;
        m_camera.Camera.ResetCullingMatrix();
        QualitySettings.shadows = m_savedShadows;
        Shader.SetGlobalFloat(SHADOW_ATTENUATION_ID, m_savedShadowAttenuation);
        Shader.SetGlobalTexture(CLOUD_TEXTURE_ID, m_savedCloudTexture);
        if (m_vignette != null) m_vignette.enabled.value = m_savedVignette;
        m_shotOverrides = false;
    }

    // --- Upload (thread pool) ---------------------------------------------------

    // Groups the files into tar archives and uploads each one direct to the relay's storage.
    // While the relay unpacks one archive, the next one uploads.
    private async Task uploadAsync(Job j)
    {
        Item[] items;
        lock (j.Files) items = j.Files.ToArray();
        List<List<Item>> archives = groupArchives(j, items);
        j.UploadStart = Stopwatch.GetTimestamp();
        var checks = new List<Task>();
        try
        {
            for (int i = 0; i < archives.Count && !j.Cancelled; i++) checks.Add(await uploadArchiveAsync(j, archives[i]));
            if (!j.Cancelled) j.Sent = true;
        }
        catch (Exception e) when (isFatal(e))
        {
            fail(j, e);
        }
        await Task.WhenAll(checks);
        cleanUp(j);
        if (j.Cancelled)
        {
            j.Phase = Phase.Failed;
        }
        else
        {
            int uploaded = Volatile.Read(ref j.Uploaded);
            j.Message = uploaded == j.Todo.Count
                ? $"Upload done: {uploaded} pictures. The map is online."
                : $"Upload done: {uploaded} of {j.Todo.Count} pictures. The game log has the errors.";
            j.Phase = Phase.Done;
        }
    }

    // Archive size as the relay counts it: a 512-byte header per file, the data padded to
    // 512 bytes, and two empty blocks at the end.
    private static List<List<Item>> groupArchives(Job j, Item[] items)
    {
        var archives = new List<List<Item>>();
        List<Item> current = null;
        long size = 0;
        foreach (Item item in items)
        {
            long entry = TAR_BLOCK + (new FileInfo(item.Path).Length + TAR_BLOCK - 1) / TAR_BLOCK * TAR_BLOCK;
            if (current == null || current.Count >= ARCHIVE_MAX_FILES || size + entry + 2 * TAR_BLOCK > ARCHIVE_MAX_BYTES)
            {
                current = new List<Item>();
                archives.Add(current);
                size = 0;
            }
            current.Add(item);
            size += entry;
            j.TotalBytes += entry;
        }
        j.TotalBytes += archives.Count * 2 * TAR_BLOCK;
        return archives;
    }

    // Returns the task that waits for the relay to unpack the archive. Throws fatal errors.
    private async Task<Task> uploadArchiveAsync(Job j, List<Item> archive)
    {
        byte[] tar;
        try
        {
            tar = buildTar(archive);
        }
        catch (Exception e)
        {
            Log.Warning("Map Share: archive not built: " + e.Message);
            return Task.CompletedTask;
        }
        int failures = 0;
        while (!j.Cancelled)
        {
            await paceAsync(j);
            long sent = 0;
            try
            {
                (string id, string url) = await m_session.NewArchiveAsync(j.Session, tar.Length);
                await MapSession.PutArchiveAsync(url, new ProgressContent(tar, ARCHIVE_TYPE, n =>
                {
                    sent += n;
                    Interlocked.Add(ref j.SentBytes, n);
                }));
                return waitForArchiveAsync(j, id, archive);
            }
            catch (RateLimitedException e)
            {
                waitForRelay(e.RetryAfter);
            }
            catch (Exception e) when (!isFatal(e))
            {
                Interlocked.Add(ref j.SentBytes, -sent);
                Log.Warning($"Map Share: archive upload failed ({++failures}/{ARCHIVE_ATTEMPTS}): {e.Message}");
                if (failures >= ARCHIVE_ATTEMPTS) break;
                await Task.Delay(TimeSpan.FromSeconds((double)RETRY_DELAY / SEC));
            }
        }
        return Task.CompletedTask;
    }

    // Polls the relay until it stored the pictures of the archive, or refused them, or the
    // player cancels. There is no time limit: a full queue can take a long time.
    private async Task waitForArchiveAsync(Job j, string id, List<Item> archive)
    {
        bool warned = false;
        while (!j.Cancelled)
        {
            await Task.Delay(ARCHIVE_POLL);
            try
            {
                (string status, int count, string error) = await m_session.ArchiveStatusAsync(j.Session, id);
                if (status == "done")
                {
                    foreach (Item item in archive) markUploaded(j, item);
                    return;
                }
                if (status == "failed")
                {
                    Log.Warning($"Map Share: the relay refused an archive: {error}");
                    return;
                }
            }
            catch (Exception e) when (isFatal(e))
            {
                fail(j, e);
                return;
            }
            catch (Exception e)
            {
                if (!warned) Log.Warning("Map Share: archive status not read, retrying: " + e.Message);
                warned = true;
            }
        }
    }

    private static byte[] buildTar(List<Item> archive)
    {
        using var ms = new MemoryStream();
        foreach (Item item in archive)
        {
            byte[] data = File.ReadAllBytes(item.Path);
            ms.Write(tarHeader($"{item.Level.Z}/{item.X}/{item.Y}.jpg", data.Length), 0, TAR_BLOCK);
            ms.Write(data, 0, data.Length);
            int pad = (TAR_BLOCK - data.Length % TAR_BLOCK) % TAR_BLOCK;
            ms.Write(new byte[pad], 0, pad);
        }
        ms.Write(new byte[2 * TAR_BLOCK], 0, 2 * TAR_BLOCK);
        return ms.ToArray();
    }

    // ustar header of a regular file.
    private static byte[] tarHeader(string name, int size)
    {
        var h = new byte[TAR_BLOCK];
        void put(int at, string s)
        {
            for (int i = 0; i < s.Length; i++) h[at + i] = (byte)s[i];
        }
        put(0, name);
        put(100, "0000644");
        put(108, "0000000");
        put(116, "0000000");
        put(124, Convert.ToString(size, 8).PadLeft(11, '0'));
        put(136, "00000000000");
        h[156] = (byte)'0';
        put(257, "ustar");
        put(263, "00");
        // The checksum counts its own field as spaces.
        put(148, "        ");
        int sum = 0;
        foreach (byte x in h) sum += x;
        put(148, Convert.ToString(sum, 8).PadLeft(6, '0'));
        h[154] = 0;
        return h;
    }

    // Writes the body in parts and counts the bytes, for the progress line.
    private sealed class ProgressContent : HttpContent
    {
        private const int PART = 64 * 1024;
        private readonly byte[] m_data;
        private readonly Action<int> m_progress;

        public ProgressContent(byte[] data, string contentType, Action<int> progress)
        {
            m_data = data;
            m_progress = progress;
            Headers.TryAddWithoutValidation("Content-Type", contentType);
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext context)
        {
            for (int at = 0; at < m_data.Length; at += PART)
            {
                int n = Math.Min(PART, m_data.Length - at);
                await stream.WriteAsync(m_data, at, n);
                m_progress(n);
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = m_data.Length;
            return true;
        }
    }

    private void markUploaded(Job j, Item item)
    {
        item.Uploaded = true;
        Interlocked.Increment(ref j.Uploaded);
        delete(item);
    }

    // Archive requests start at the fastest interval that the relay allows (one archive is one upload).
    private async Task paceAsync(Job j)
    {
        while (true)
        {
            long now = Stopwatch.GetTimestamp();
            long at;
            lock (j)
            {
                at = Math.Max(j.NextUpload, Volatile.Read(ref m_rateLimitEnd));
                if (at <= now)
                {
                    j.NextUpload = now + UPLOAD_INTERVAL;
                    return;
                }
            }
            await Task.Delay(TimeSpan.FromSeconds((double)(at - now) / SEC));
        }
    }

    // Moves m_rateLimitEnd later, never earlier. Parallel uploads can call it at the same time.
    private void waitForRelay(TimeSpan wait)
    {
        long end = Stopwatch.GetTimestamp() + (long)(wait.TotalSeconds * SEC);
        long current;
        while ((current = Volatile.Read(ref m_rateLimitEnd)) < end
            && Interlocked.CompareExchange(ref m_rateLimitEnd, end, current) != current) { }
    }

    private static bool isFatal(Exception e) => e is SessionRejectedException || e is InvalidOperationException;

    // Any thread. Stops the job; the phase owner cleans up.
    private void fail(Job j, Exception e)
    {
        j.Message = e switch
        {
            SessionRejectedException => REJECTED,
            // sendAsync: the link changed.
            InvalidOperationException => "Upload stopped: the link changed.",
            _ => "Upload failed: " + e.Message,
        };
        if (!j.Cancelled)
        {
            Log.Warning("Map Share: " + j.Message);
            j.Failed = true;
        }
        j.Cancelled = true;
        if (j.Phase == Phase.Scan || j.Phase == Phase.Map) j.Phase = Phase.Failed;
    }

    // Deletes the files that did not upload.
    private static void cleanUp(Job j)
    {
        lock (j.Files)
        {
            foreach (Item item in j.Files)
            {
                if (!item.Uploaded) delete(item);
            }
        }
    }

    private static void delete(Item item)
    {
        try
        {
            File.Delete(item.Path);
        }
        catch (Exception e)
        {
            Log.Warning("Map Share: cannot delete " + item.Path + ": " + e.Message);
        }
    }

    private Level[] Levels
    {
        get
        {
            if (m_levelsOrNull != null) return m_levelsOrNull;
            float groundY = averageHeight() * UNITS_PER_TILE;
            var levels = new Level[LEVELS.Length];
            for (int i = 0; i < LEVELS.Length; i++) levels[i] = new Level(LEVELS[i], Cols, Rows, groundY);
            return m_levelsOrNull = levels;
        }
    }

    private float averageHeight()
    {
        float sum = 0;
        int n = 0;
        for (int y = 0; y < m_terrain.TerrainHeight; y += CELL_TILES / 2)
        {
            for (int x = 0; x < m_terrain.TerrainWidth; x += CELL_TILES / 2)
            {
                sum += m_terrain.GetHeight(new Tile2i(x, y)).Value.ToFloat();
                n++;
            }
        }
        return n == 0 ? 0 : sum / n;
    }

    // One zoom level. Picture (x, y) covers cells [x * Span, (x + 1) * Span) and the same for y.
    private sealed class Level
    {
        public readonly int Z;
        public readonly int Span;
        public readonly int Cols;
        public readonly int Rows;
        // One camera height per level, so that all pictures of a level have the same scale.
        public readonly float CameraY;
        public readonly Vector2 Clip;

        public Level(int z, int cols, int rows, float groundY)
        {
            Z = z;
            Span = 1 << z;
            Cols = (cols + Span - 1) / Span;
            Rows = (rows + Span - 1) / Span;
            float distance = Span * CELL_TILES / 2 * UNITS_PER_TILE / Mathf.Tan(FOV / 2 * Mathf.Deg2Rad);
            CameraY = groundY + distance;
            // Same near/far ratio on all levels, so the depth precision stays the same.
            Clip = new Vector2(26f * Span, CameraY + 2000f * Span);
        }
    }
}
