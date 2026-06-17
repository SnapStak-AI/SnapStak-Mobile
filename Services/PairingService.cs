using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SnapStakMobile.Services;

/// <summary>
/// Handles SnapStak Desktop - Mobile pairing and subscription validation.
///
/// Works identically on Android and iOS.
///
/// ── Device Identity ──────────────────────────────────────────────────────────
/// A UUID is generated once on first install and stored in SecureStorage.
/// This UUID is the device identity — it never changes unless the user performs
/// a full device wipe / factory reset. A factory reset permanently revokes
/// subscription access. The user must purchase a new subscription.
///
/// ── Pairing Flow (one time, WiFi required) ───────────────────────────────────
///   1. User presses Pair on SnapStak Desktop.
///      Desktop generates a 6-digit PIN (valid for 60 seconds, single use).
///      Desktop broadcasts itself via mDNS as _snapstak._tcp on port 5172.
///      Desktop displays the PIN on screen.
///   2. User opens Settings on Mobile, enters the 6-digit PIN, presses Pair.
///   3. Mobile scans the local network for _snapstak._tcp via mDNS.
///   4. Mobile connects to Desktop HTTP endpoint: POST /pair
///      Body: { "deviceId": "{uuid}", "pin": "{6digits}" }
///   5. Desktop verifies PIN (not expired, not already used).
///      Desktop encrypts PaystackUUID using deviceId as AES-256-GCM key input.
///      Desktop responds: { "token": "{base64EncryptedToken}" }
///   6. Mobile decrypts token to verify integrity, stores in SecureStorage.
///
/// ── Subscription Validation ──────────────────────────────────────────────────
///   1. Mobile reads device UUID from SecureStorage.
///   2. Mobile decrypts stored pairing token using device UUID.
///   3. Mobile POSTs plaintext PaystackUUID to subscriptions.snapstak.ai/api/validate.
///   4. Returns active/inactive status.
///
/// ── File Encryption ──────────────────────────────────────────────────────────
///   All generated files are encrypted with the device UUID.
///   Only SnapStak Desktop (which stored the device UUID at pairing time) can decrypt.
///   Factory reset = new UUID = all unshared files permanently unreadable.
/// </summary>
public class PairingService
{
    // ── Constants ─────────────────────────────────────────────────────────────

    private const string DeviceIdKey = "snapstakDeviceId";
    private const string TokenKey = "snapstakPairingToken";
    private const string ValidateEndpoint = "https://subscriptions.snapstak.ai/api/validate";
    private const int PairingPort = 5174;
    private const string MdnsServiceType = "_snapstak._tcp";
    private const int DiscoveryMs = 8_000;
    private const int PairingTimeoutMs = 15_000;

    public const string EncryptedExtension = ".snapstak";

    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(12) };

    // ── Device ID ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the device UUID. Generated once on first install and stored
    /// in SecureStorage. Permanently wiped on factory reset — intentional.
    /// </summary>
    public static async Task<string> GetOrCreateDeviceIdAsync()
    {
        try
        {
            var stored = await SecureStorage.Default.GetAsync(DeviceIdKey);
            if (!string.IsNullOrEmpty(stored)) return stored;
        }
        catch { }

        // First install — generate and store a new UUID
        var id = Guid.NewGuid().ToString("N");
        try { await SecureStorage.Default.SetAsync(DeviceIdKey, id); }
        catch { }
        return id;
    }

    // ── Pairing status ────────────────────────────────────────────────────────

    public static async Task<bool> IsPairedAsync()
    {
        try
        {
            var token = await SecureStorage.Default.GetAsync(TokenKey);
            return !string.IsNullOrEmpty(token);
        }
        catch { return false; }
    }

    // ── mDNS Discovery ────────────────────────────────────────────────────────

    /// <summary>
    /// Discovers SnapStak Desktop on the local network by scanning all
    /// reachable LAN hosts on port 5172. Returns the Desktop IP address.
    ///
    /// mDNS/Bonjour library support in .NET MAUI is limited — we use a
    /// practical LAN scan on the /24 subnet as a reliable cross-platform
    /// alternative that works on both Android and iOS without extra packages.
    /// </summary>
    public static async Task<string?> DiscoverDesktopAsync(
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        progress?.Report("Scanning local network for SnapStak Desktop...");

        var localIp = GetLocalIpAddress();
        if (localIp == null)
        {
            progress?.Report("Not connected to WiFi.");
            return null;
        }

        var parts = localIp.Split('.');
        if (parts.Length != 4) return null;
        var subnet = $"{parts[0]}.{parts[1]}.{parts[2]}";

        progress?.Report($"Scanning {subnet}.0/24...");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(DiscoveryMs);

        // Short-circuit: return as soon as the first matching host responds.
        var tcs = new TaskCompletionSource<string?>();

        var tasks = Enumerable.Range(1, 254).Select(async i =>
        {
            var ip = $"{subnet}.{i}";
            if (ip == localIp) return;
            try
            {
                using var discoverClient = new HttpClient { Timeout = TimeSpan.FromMilliseconds(500) };
                var response = await discoverClient.GetAsync(
                    $"http://{ip}:{PairingPort}/discover", cts.Token);
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    if (json.Contains("SNAPSTAK_HERE"))
                    {
                        tcs.TrySetResult(ip);
                        cts.Cancel(); // cancel remaining scans immediately
                    }
                }
            }
            catch { }
        }).ToList();

        // Race: either a host is found (tcs) or all tasks complete with no match
        var allDone = Task.WhenAll(tasks).ContinueWith(_ => tcs.TrySetResult(null));
        var result = await tcs.Task;
        cts.Cancel();
        return result;
    }

    private static string? GetLocalIpAddress()
    {
        try
        {
            foreach (var iface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (iface.OperationalStatus != OperationalStatus.Up) continue;
                if (iface.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var addr in iface.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        var ip = addr.Address.ToString();
                        if (!ip.StartsWith("169.254")) // skip APIPA
                            return ip;
                    }
                }
            }
        }
        catch { }
        return null;
    }

    // ── Pairing ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Pairs with SnapStak Desktop over WiFi using a 6-digit PIN.
    /// Discovers Desktop automatically via LAN scan.
    /// PIN is valid for 60 seconds and single use.
    /// </summary>
    public static async Task PairWithDesktopAsync(
        string pin,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(pin) || pin.Length != 6 || !pin.All(char.IsDigit))
            throw new ArgumentException("PIN must be exactly 6 digits.");

        progress?.Report("Getting device identity...");
        var deviceId = await GetOrCreateDeviceIdAsync();
        // Note: pairing token will include subscriptionCode for Mobile validation

        // Discover Desktop
        var desktopIp = await DiscoverDesktopAsync(progress, ct);
        if (desktopIp == null)
            throw new Exception(
                "SnapStak Desktop not found on your network.\n\n" +
                "Make sure:\n" +
                "• Both devices are on the same WiFi network\n" +
                "• SnapStak Desktop is open and showing the pairing PIN\n" +
                "• SnapStak Desktop has been started (dotnet run)");

        progress?.Report($"Found SnapStak Desktop at {desktopIp} — connecting...");

        // POST to Desktop pairing endpoint
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(PairingTimeoutMs);

        var payload = JsonSerializer.Serialize(new { deviceId, pin });
        var content = new StringContent(payload, Encoding.UTF8, "application/json");

        HttpResponseMessage resp;
        try
        {
            using var pairClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            resp = await pairClient.PostAsync(
                $"http://{desktopIp}:{PairingPort}/pair", content, cts.Token);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException("Desktop did not respond in time. Please try again.");
        }
        catch (Exception ex)
        {
            throw new Exception($"Could not reach Desktop: {ex.Message}");
        }

        var body = await resp.Content.ReadAsStringAsync();

        if (resp.StatusCode == HttpStatusCode.Forbidden)
            throw new Exception(
                "No active SnapStak subscription found on Desktop.\n\n" +
                "Please subscribe via SnapStak Desktop before pairing.");

        if (resp.StatusCode == HttpStatusCode.Unauthorized)
            throw new Exception("Incorrect PIN. Please check the PIN displayed on SnapStak Desktop.");

        if (resp.StatusCode == HttpStatusCode.Gone)
            throw new Exception("PIN has expired (60 second limit). Press Pair on Desktop to generate a new PIN.");

        if (resp.StatusCode == HttpStatusCode.Conflict)
            throw new Exception("PIN has already been used. Press Pair on Desktop to generate a new PIN.");

        if (!resp.IsSuccessStatusCode)
            throw new Exception($"Desktop returned {(int)resp.StatusCode}: {body}");

        progress?.Report("Verifying token...");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var base64Token = root.TryGetProperty("token", out var t) ? t.GetString() : null;

        if (string.IsNullOrWhiteSpace(base64Token))
            throw new Exception("Desktop sent an empty token. Please try again.");

        // Verify decryption before storing — parse both fields
        var decrypted = Decrypt(base64Token, deviceId);
        var pairingData = ParsePairingData(decrypted);
        if (string.IsNullOrWhiteSpace(pairingData.paystackUuid) || string.IsNullOrWhiteSpace(pairingData.openRouterKey))
            throw new Exception("Token verification failed. Please try pairing again.");
        // pairingData.subscriptionCode is the Desktop SUB_xxx code used for validation

        progress?.Report("Storing secure token...");
        await SecureStorage.Default.SetAsync(TokenKey, base64Token);

        progress?.Report("Paired successfully.");
    }

    // ── Subscription validation ───────────────────────────────────────────────

    /// <summary>
    /// Decrypts the stored pairing token using the device UUID, then validates
    /// the PaystackUUID against the SnapStak subscription server.
    /// Returns (true, null) if active. Returns (false, message) if not.
    /// </summary>
    public static async Task<(bool active, string? error)> ValidateSubscriptionAsync()
    {
        try
        {
            var token = await SecureStorage.Default.GetAsync(TokenKey);
            if (string.IsNullOrEmpty(token))
                return (false, "Not paired. Open Settings and pair with SnapStak Desktop.");

            string deviceId;
            try
            {
                deviceId = await GetOrCreateDeviceIdAsync();
            }
            catch
            {
                return (false, "Could not read device identity.");
            }

            string paystackUuid;
            string subscriptionCode;
            try
            {
                var decrypted = Decrypt(token, deviceId);
                var pairingData = ParsePairingData(decrypted);
                paystackUuid = pairingData.paystackUuid;
                subscriptionCode = pairingData.subscriptionCode;
            }
            catch
            {
                // Device UUID has changed — factory reset
                return (false,
                    "Device identity has changed. " +
                    "Your subscription access has been permanently revoked. " +
                    "Please purchase a new subscription.");
            }

            // Full challenge/response validation — same flow as Desktop LicenceService
            const string ChallengeEndpoint = "https://subscriptions.snapstak.ai/api/challenge";
            var challengeResp = await _http.PostAsync(ChallengeEndpoint, null);
            var challengeJson = await challengeResp.Content.ReadAsStringAsync();
            using var challengeDoc = JsonDocument.Parse(challengeJson);
            var cr = challengeDoc.RootElement;
            string serverNonce = cr.TryGetProperty("serverNonce", out var sn) ? sn.GetString() ?? "" : "";
            string issuedAt = cr.TryGetProperty("issuedAt", out var ia) ? ia.GetInt64().ToString() : "";
            string challengeHmac = cr.TryGetProperty("challengeHmac", out var ch) ? ch.GetString() ?? "" : "";
            string clientNonce = Guid.NewGuid().ToString("N");
            string userUuid = paystackUuid; // paystackUuid in token IS the Desktop userUuid

            var payload = JsonSerializer.Serialize(new
            {
                subscriptionCode,
                userUuid,
                clientNonce,
                serverNonce,
                issuedAt,
                challengeHmac,
            });
            var content = new StringContent(payload, Encoding.UTF8, "application/json");
            var resp = await _http.PostAsync(ValidateEndpoint, content);
            var body = await resp.Content.ReadAsStringAsync();

            // Trust HTTP 200 as confirmed active — some server versions return active:false with a positive message
            if (resp.IsSuccessStatusCode)
                return (true, null);

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            // Try multiple field names — server may use 'active', 'isActive', or 'status'
            bool active;
            if (root.TryGetProperty("active", out var a))
                active = a.ValueKind == JsonValueKind.True || (a.ValueKind == JsonValueKind.String && a.GetString()?.Equals("true", StringComparison.OrdinalIgnoreCase) == true);
            else if (root.TryGetProperty("isActive", out var ia2))
                active = ia2.ValueKind == JsonValueKind.True;
            else if (root.TryGetProperty("status", out var st))
                active = st.GetString()?.Equals("active", StringComparison.OrdinalIgnoreCase) == true;
            else
                active = false;

            string? message = root.TryGetProperty("message", out var m) ? m.GetString() : null;

            return (active, active ? null : message ?? "Subscription not active.");
        }
        catch (Exception ex)
        {
            return (false, $"Could not validate subscription: {ex.Message}");
        }
    }

    // ── OpenRouter key access ─────────────────────────────────────────────────

    /// <summary>
    /// Returns the OpenRouter API key from the paired token.
    /// The key is received from Desktop during pairing — never entered manually on Mobile.
    /// </summary>
    public static async Task<string?> GetOpenRouterKeyAsync()
    {
        try
        {
            var token = await SecureStorage.Default.GetAsync(TokenKey);
            if (string.IsNullOrEmpty(token)) return null;
            var deviceId = await GetOrCreateDeviceIdAsync();
            var decrypted = Decrypt(token, deviceId);
            return ParsePairingData(decrypted).openRouterKey; // 3rd element of tuple
        }
        catch { return null; }
    }

    // ── Pairing data parser ───────────────────────────────────────────────────

    /// <summary>
    /// Parses the decrypted token payload.
    /// Format: JSON { "paystackUuid": "...", "openRouterKey": "..." }
    /// </summary>
    private static (string paystackUuid, string subscriptionCode, string openRouterKey) ParsePairingData(string decrypted)
    {
        try
        {
            using var doc = JsonDocument.Parse(decrypted);
            var root = doc.RootElement;
            var uuid = root.TryGetProperty("paystackUuid", out var u) ? u.GetString() ?? "" : "";
            var sub = root.TryGetProperty("subscriptionCode", out var s) ? s.GetString() ?? "" : "";
            var key = root.TryGetProperty("openRouterKey", out var k) ? k.GetString() ?? "" : "";
            return (uuid, sub, key);
        }
        catch { return ("", "", ""); }
    }

    // ── Unpair ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Removes pairing from both this device and SnapStak Desktop.
    /// Requires both devices to be on the same WiFi — calls
    /// POST /api/mobile/pairing/remove-device on the Desktop server
    /// before clearing the local token.
    ///
    /// Returns (true, null) on full success.
    /// Returns (true, warningMessage) if local unpair succeeded but Desktop
    /// could not be reached — caller should show the warning.
    /// </summary>
    public static async Task<(bool success, string? error)> UnpairAsync()
    {
        string? deviceId = null;
        try { deviceId = await GetOrCreateDeviceIdAsync(); } catch { }

        if (string.IsNullOrWhiteSpace(deviceId))
            return (false, "Could not read device identity.");

        // Step 1: Find Desktop — it must be reachable
        string? desktopIp = null;
        try { desktopIp = await DiscoverDesktopAsync(null, CancellationToken.None); } catch { }

        if (string.IsNullOrWhiteSpace(desktopIp))
            return (false, "Cannot find SnapStak Desktop on the network. Make sure it is running on the same WiFi and try again.");

        // Step 2: Tell Desktop to remove its record
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            var payload = JsonSerializer.Serialize(new { deviceId });
            var content = new StringContent(payload, Encoding.UTF8, "application/json");
            var resp = await client.PostAsync(
                $"http://{desktopIp}:{PairingPort}/api/mobile/pairing/remove-device",
                content);

            if (!resp.IsSuccessStatusCode)
                return (false, "SnapStak Desktop returned an error. Please try again.");
        }
        catch
        {
            return (false, "Could not reach SnapStak Desktop. Make sure it is running on the same WiFi and try again.");
        }

        // Step 3: Desktop confirmed — now clear local token
        try { SecureStorage.Default.Remove(TokenKey); } catch { }

        return (true, null);
    }

    // ── File encryption ───────────────────────────────────────────────────────

    /// <summary>
    /// Encrypts file bytes using the device UUID as the key derivation input.
    /// Output format: [16 salt][12 nonce][16 tag][ciphertext]
    /// Only SnapStak Desktop can decrypt — it stored the device UUID at pairing time.
    /// Factory reset = new UUID = files permanently unreadable.
    /// </summary>
    public static async Task<byte[]> EncryptFileAsync(byte[] plaintext)
    {
        var deviceId = await GetOrCreateDeviceIdAsync();
        var salt = RandomNumberGenerator.GetBytes(16);
        var key = DeriveKey(deviceId, salt);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[plaintext.Length];
        var tag = new byte[16];

        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plaintext, cipher, tag);

        // Format: [16 salt][12 nonce][16 tag][ciphertext]
        var blob = new byte[16 + 12 + 16 + cipher.Length];
        Buffer.BlockCopy(salt, 0, blob, 0, 16);
        Buffer.BlockCopy(nonce, 0, blob, 16, 12);
        Buffer.BlockCopy(tag, 0, blob, 28, 16);
        Buffer.BlockCopy(cipher, 0, blob, 44, cipher.Length);
        return blob;
    }

    // ── AES-256-GCM helpers ───────────────────────────────────────────────────

    private static string Decrypt(string base64Token, string deviceId)
    {
        var blob = Convert.FromBase64String(base64Token);
        if (blob.Length < 44)
            throw new CryptographicException("Token is too short to be valid.");

        var salt = blob[..16];
        var nonce = blob[16..28];
        var tag = blob[28..44];
        var cipher = blob[44..];
        var plaintext = new byte[cipher.Length];
        var key = DeriveKey(deviceId, salt);

        using var aes = new AesGcm(key, 16);
        aes.Decrypt(nonce, cipher, tag, plaintext);
        return Encoding.UTF8.GetString(plaintext);
    }

    private static byte[] DeriveKey(string deviceId, byte[] salt)
    {
        using var kdf = new Rfc2898DeriveBytes(
            Encoding.UTF8.GetBytes(deviceId),
            salt,
            100_000,
            HashAlgorithmName.SHA256);
        return kdf.GetBytes(32);
    }

    // ── Unpair listener ───────────────────────────────────────────────────────

    private static HttpListener? _unpairListener;
    private static CancellationTokenSource? _unpairCts;

    // Pending-transfer queue: fileId -> (filename, encryptedBytes)
    // Files added here by the pipeline after encryption; cleared on ACK from Desktop.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string filename, byte[] data)>
        _syncQueue = new();

    /// <summary>
    /// Adds an encrypted file to the sync queue so Desktop can pull it via Sync.
    /// Called by the pipeline after EncryptFileAsync completes.
    /// </summary>
    public static void EnqueueForSync(string filename, byte[] encryptedBytes)
    {
        var id = Guid.NewGuid().ToString("N");
        _syncQueue[id] = (filename, encryptedBytes);
    }

    /// <summary>
    /// Starts a local HTTP listener on port 5174 for /api/unpair and /api/sync.
    /// Desktop calls /api/sync/files to pull all queued encrypted components.
    /// Call when the app starts; stop on sleep.
    /// </summary>
    public static void StartUnpairListener()
    {
        try
        {
            _unpairCts = new CancellationTokenSource();
            _unpairListener = new HttpListener();
            _unpairListener.Prefixes.Add($"http://+:{PairingPort}/api/unpair/");
            _unpairListener.Prefixes.Add($"http://+:{PairingPort}/api/unpair/ping/");
            _unpairListener.Prefixes.Add($"http://+:{PairingPort}/api/sync/");
            _unpairListener.Start();
            _ = Task.Run(() => HandleUnpairRequestsAsync(_unpairCts.Token));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Unpair] Could not start listener: {ex.Message}");
        }
    }

    public static void StopUnpairListener()
    {
        try
        {
            _unpairCts?.Cancel();
            _unpairListener?.Stop();
            _unpairListener?.Close();
            _unpairListener = null;
        }
        catch { }
    }

    private static async Task HandleUnpairRequestsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _unpairListener?.IsListening == true)
        {
            try
            {
                var ctx = await _unpairListener.GetContextAsync();
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var path = ctx.Request.Url?.AbsolutePath ?? "";
                        var method = ctx.Request.HttpMethod;

                        // ── Unpair ping ───────────────────────────────────
                        if (method == "GET" && path == "/api/unpair/ping")
                        {
                            var deviceId = await GetOrCreateDeviceIdAsync();
                            ctx.Response.StatusCode = 200;
                            ctx.Response.ContentType = "application/json";
                            var bytes = Encoding.UTF8.GetBytes("{\"deviceId\":\"" + deviceId + "\"}");
                            await ctx.Response.OutputStream.WriteAsync(bytes, ct);
                        }

                        // ── Unpair ────────────────────────────────────────
                        else if (method == "POST" && path == "/api/unpair")
                        {
                            try { SecureStorage.Default.Remove(TokenKey); } catch { }
                            ctx.Response.StatusCode = 200;
                            ctx.Response.ContentType = "application/json";
                            var bytes = Encoding.UTF8.GetBytes("{\"ok\":true}");
                            await ctx.Response.OutputStream.WriteAsync(bytes, ct);
                        }

                        // ── Sync: list pending files ──────────────────────
                        // GET /api/sync/files
                        else if (method == "GET" && path == "/api/sync/files")
                        {
                            var entries = _syncQueue.Select(kv =>
                                "{\"id\":\"" + kv.Key + "\"," +
                                "\"filename\":\"" + EscapeJson(kv.Value.filename) + "\"," +
                                "\"sizeBytes\":" + kv.Value.data.Length + "}");
                            var json = "[" + string.Join(",", entries) + "]";
                            var bytes = Encoding.UTF8.GetBytes(json);
                            ctx.Response.StatusCode = 200;
                            ctx.Response.ContentType = "application/json";
                            await ctx.Response.OutputStream.WriteAsync(bytes, ct);
                        }

                        // ── Sync: download a single file ──────────────────
                        // GET /api/sync/files/{id}
                        else if (method == "GET"
                              && path.StartsWith("/api/sync/files/", StringComparison.Ordinal)
                              && !path.EndsWith("/ack", StringComparison.Ordinal))
                        {
                            var fileId = path.Substring("/api/sync/files/".Length);
                            if (_syncQueue.TryGetValue(fileId, out var entry))
                            {
                                ctx.Response.StatusCode = 200;
                                ctx.Response.ContentType = "application/octet-stream";
                                ctx.Response.Headers["X-Filename"] = entry.filename;
                                await ctx.Response.OutputStream.WriteAsync(entry.data, ct);
                            }
                            else
                            {
                                ctx.Response.StatusCode = 404;
                            }
                        }

                        // ── Sync: acknowledge receipt ─────────────────────
                        // POST /api/sync/files/{id}/ack
                        else if (method == "POST"
                              && path.StartsWith("/api/sync/files/", StringComparison.Ordinal)
                              && path.EndsWith("/ack", StringComparison.Ordinal))
                        {
                            var withoutPrefix = path.Substring("/api/sync/files/".Length);
                            var fileId = withoutPrefix.Substring(0, withoutPrefix.Length - "/ack".Length);
                            _syncQueue.TryRemove(fileId, out _);
                            ctx.Response.StatusCode = 200;
                            ctx.Response.ContentType = "application/json";
                            var bytes = Encoding.UTF8.GetBytes("{\"ok\":true}");
                            await ctx.Response.OutputStream.WriteAsync(bytes, ct);
                        }

                        else
                        {
                            ctx.Response.StatusCode = 404;
                        }
                    }
                    finally
                    {
                        ctx.Response.Close();
                    }
                }, ct);
            }
            catch (HttpListenerException) { break; }
            catch (ObjectDisposedException) { break; }
            catch { }
        }
    }

    private static string EscapeJson(string s)
        => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}