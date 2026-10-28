using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Mafi;
using Mafi.Collections;
using Mafi.Core;
using Mafi.Core.Game;
using Mafi.Serialization;

namespace MapShare;

// The share link, the settings and the relay requests. Create link makes a session on the relay,
// and Delete link deletes it with its pictures. Everything is kept in one machine-level file, never in the save: a shared save must not
// leak the secret. Each game (the save folder name) has its own session; the maps URL is shared.
[GlobalDependency(RegistrationMode.AsSelf)]
public sealed class MapSession
{
    // One server has the website and the API (/v1/*).
    public const string DEFAULT_URL = "https://coimaps.bahr.dev";

    // No redirects: the secret must go only to the server URL. The relay answers are small JSON.
    private static readonly HttpClient s_http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(15),
        MaxResponseContentBufferSize = 1024 * 1024,
    };
    // Batch archives are up to 1 MiB: 8 s at 1 Mbit/s.
    private static readonly HttpClient s_archiveHttp = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromMinutes(10),
        MaxResponseContentBufferSize = 1024 * 1024,
    };
    // Error text from a server goes to the window and the log.
    private const int MAX_ERROR_CHARS = 200;

    private readonly string m_file;
    private readonly object m_lock = new object();

    public string MapsUrl { get; private set; } = DEFAULT_URL;
    // The most detailed zoom level to upload (detail slider). 0 is the most detail. The default
    // 4 is step 2 of 5.
    public int FinestLevel { get; private set; } = 4;
    // The session belongs to the server that created it.
    public string SessionId { get; private set; }
    private string m_sessionUrl;
    private string m_secret;
    // The sessions of all games, so that save() keeps the other games.
    private readonly string m_game;
    private readonly Dict<string, GameEntry> m_games = new Dict<string, GameEntry>();

    private sealed class GameEntry
    {
        public string Url, Session, Secret;
    }

    public MapSession(IFileSystemHelper fs, GameNameConfig gameName)
    {
        m_file = Path.Combine(fs.GameDataRootDirPath, "ModData", "map-share", "state.json");
        m_game = gameName.GameName ?? "";
        load();
    }

    // Pictures wait here for upload.
    public string UploadDir => Path.Combine(Path.GetDirectoryName(m_file), "upload");

    // Null without a link.
    public string ShareLink
    {
        get
        {
            lock (m_lock) return SessionId == null ? null : $"{m_sessionUrl}/m/{SessionId}";
        }
    }

    // --- Settings (advanced section) ----------------------------------------

    // For new links. A link stays on the server that created it.
    public void SetMapsUrl(string url)
    {
        MapsUrl = cleanUrl(url, DEFAULT_URL);
        save();
    }

    public void SetFinestLevel(int z)
    {
        if (z == FinestLevel) return;
        FinestLevel = z;
        save();
    }

    // HTTPS, or HTTP only for a local server: the requests send the secret.
    private static string cleanUrl(string url, string fallback)
    {
        url = (url ?? "").Trim().TrimEnd('/');
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri uri) || uri.UserInfo != "" || uri.Query != "" || uri.Fragment != "")
        {
            return fallback;
        }
        return uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback) ? url : fallback;
    }

    // The relay makes tokens in base64url.
    private static bool isToken(string s)
    {
        if (string.IsNullOrEmpty(s) || s.Length > 256) return false;
        foreach (char ch in s)
        {
            if (!(ch >= 'A' && ch <= 'Z' || ch >= 'a' && ch <= 'z' || ch >= '0' && ch <= '9' || ch == '-' || ch == '_')) return false;
        }
        return true;
    }

    private static string shorten(string text) =>
        text == null || text.Length <= MAX_ERROR_CHARS ? text : text.Substring(0, MAX_ERROR_CHARS) + "...";

    // --- Relay ----------------------------------------------------------------

    // Creates a session on the server at the maps URL. Does nothing if there is a link.
    public async Task CreateLinkAsync()
    {
        string url;
        lock (m_lock)
        {
            if (SessionId != null) return;
            url = MapsUrl;
        }
        HttpResponseMessage res = await s_http.PostAsync(url + "/v1/sessions", null);
        string body = await res.Content.ReadAsStringAsync();
        if (res.StatusCode != HttpStatusCode.Created) throw new Exception($"relay answered {(int)res.StatusCode} {shorten(body)}");
        Dict<string, object> json = parseObject(body);
        string session = get(json, "session"), secret = get(json, "secret");
        if (!isToken(session) || !isToken(secret)) throw new Exception("relay sent an invalid session");
        lock (m_lock)
        {
            SessionId = session;
            m_secret = secret;
            m_sessionUrl = url;
            save();
        }
    }

    // Deletes the session and its pictures on the relay, then forgets the link. If the relay does
    // not know the session (401), it has no pictures of it, so forget the link too.
    public async Task DeleteLinkAsync()
    {
        string session = SessionId;
        if (session == null) return;
        try
        {
            await sendAsync(session, HttpMethod.Delete, "", null);
        }
        catch (SessionRejectedException) { }
        lock (m_lock)
        {
            if (SessionId != session) return;
            SessionId = null;
            m_sessionUrl = null;
            m_secret = null;
            save();
        }
    }

    // Uploads one file below /v1/s/{session}/. Throws SessionRejectedException on 401
    // and RateLimitedException on 429.
    public async Task PutAsync(string session, string path, string contentType, byte[] body)
    {
        var content = new ByteArrayContent(body);
        content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        await sendAsync(session, HttpMethod.Put, path, content);
    }

    // Asks for the URL of one batch archive. Returns the id and the URL. Throws as PutAsync.
    public async Task<(string Id, string Url)> NewArchiveAsync(string session, int size)
    {
        var content = new StringContent("{\"size\":" + size + "}", Encoding.UTF8, "application/json");
        string body = await sendAsync(session, HttpMethod.Post, "uploads", content);
        Dict<string, object> json = parseObject(body);
        string id = get(json, "id"), url = get(json, "url");
        if (!isToken(id)) throw new Exception("relay sent no valid upload id");
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new Exception("relay sent no valid upload URL");
        }
        return (id, url);
    }

    // Status of a batch archive: waiting, processing, done or failed, with the picture
    // count or the reason.
    public async Task<(string Status, int Count, string Error)> ArchiveStatusAsync(string session, string id)
    {
        string body = await sendAsync(session, HttpMethod.Get, "uploads/" + Uri.EscapeDataString(id), null);
        Dict<string, object> json = parseObject(body);
        int count = json.TryGetValue("count", out object n) ? Convert.ToInt32(n) : 0;
        return (get(json, "status"), count, get(json, "error"));
    }

    // PUT to the signed URL from NewArchiveAsync. The URL is the access check, so no secret.
    public static async Task PutArchiveAsync(string url, HttpContent content)
    {
        var req = new HttpRequestMessage(HttpMethod.Put, url) { Content = content };
        HttpResponseMessage res = await s_archiveHttp.SendAsync(req);
        if (!res.IsSuccessStatusCode)
        {
            throw new Exception($"archive upload answered {(int)res.StatusCode} {shorten(await res.Content.ReadAsStringAsync())}");
        }
    }

    private async Task<string> sendAsync(string session, HttpMethod method, string path, HttpContent content)
    {
        string relay = m_sessionUrl, secret = m_secret;
        if (session == null || session != SessionId) throw new InvalidOperationException("session changed");
        string url = $"{relay}/v1/s/{Uri.EscapeDataString(session)}" + (path == "" ? "" : "/" + path);
        var req = new HttpRequestMessage(method, url);
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + secret);
        req.Content = content;
        HttpResponseMessage res = await s_http.SendAsync(req);
        if (res.StatusCode == HttpStatusCode.Unauthorized) throw new SessionRejectedException();
        if ((int)res.StatusCode == 429) throw new RateLimitedException(retryAfter(res));
        string body = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode) throw new Exception($"relay answered {(int)res.StatusCode} {shorten(body)}");
        return body;
    }

    // Retry-After is seconds or a date. Without it, wait the relay's upload interval.
    private static TimeSpan retryAfter(HttpResponseMessage res)
    {
        var header = res.Headers.RetryAfter;
        TimeSpan wait = header?.Delta
            ?? (header?.Date is DateTimeOffset date ? date - DateTimeOffset.UtcNow : TimeSpan.FromSeconds(1));
        return wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
    }

    // --- State file -------------------------------------------------------------

    private void load()
    {
        try
        {
            if (!File.Exists(m_file)) return;
            Dict<string, object> json = parseObject(File.ReadAllText(m_file));
            MapsUrl = cleanUrl(get(json, "url"), DEFAULT_URL);
            if (json.TryGetValue("finestLevel", out object f) && f != null) FinestLevel = Convert.ToInt32(f);
            if (json.TryGetValue("games", out object g) && g is Dict<string, object> games)
            {
                foreach (var kv in games)
                {
                    if (kv.Value is Dict<string, object> e)
                    {
                        m_games[kv.Key] = new GameEntry
                        {
                            Url = get(e, "url"),
                            Session = get(e, "session"),
                            Secret = get(e, "secret"),
                        };
                    }
                }
            }
            if (m_games.TryGetValue(m_game, out GameEntry mine) && isToken(mine.Session) && isToken(mine.Secret) && mine.Url != null)
            {
                SessionId = mine.Session;
                m_secret = mine.Secret;
                m_sessionUrl = mine.Url;
            }
        }
        catch (Exception e)
        {
            Log.Exception(e, "Map Share: cannot read " + m_file);
        }
    }

    private static string get(Dict<string, object> json, string key) => json.TryGetValue(key, out object v) ? v as string : null;

    private void save()
    {
        if (SessionId == null) m_games.Remove(m_game);
        else m_games[m_game] = new GameEntry { Url = m_sessionUrl, Session = SessionId, Secret = m_secret };
        try
        {
            var games = new StringBuilder();
            foreach (var kv in m_games)
            {
                GameEntry e = kv.Value;
                if (games.Length > 0) games.Append(',');
                games.Append($"{str(kv.Key)}:{{\"url\":{str(e.Url)},\"session\":{str(e.Session)},\"secret\":{str(e.Secret)}}}");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(m_file));
            File.WriteAllText(m_file, $"{{\"url\":{str(MapsUrl)},\"finestLevel\":{FinestLevel},\"games\":{{{games}}}}}");
        }
        catch (Exception e)
        {
            Log.Exception(e, "Map Share: cannot write " + m_file);
        }
    }

    // --- JSON -------------------------------------------------------------------

    private static Dict<string, object> parseObject(string text)
    {
        return new JsonParser().Parse(new StringReader(text)) as Dict<string, object>
            ?? throw new Exception("expected a JSON object");
    }

    private static string str(string s)
    {
        if (s == null) return "null";
        var sb = new StringBuilder("\"");
        foreach (char ch in s)
        {
            switch (ch)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                default:
                    if (ch < 0x20) sb.Append($"\\u{(int)ch:x4}");
                    else sb.Append(ch);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }
}

public sealed class SessionRejectedException : Exception
{
    public SessionRejectedException() : base("relay rejected the session") { }
}

// The relay limits uploads for each session. Upload again after RetryAfter.
public sealed class RateLimitedException : Exception
{
    public readonly TimeSpan RetryAfter;

    public RateLimitedException(TimeSpan retryAfter) : base("relay rate limit") => RetryAfter = retryAfter;
}
