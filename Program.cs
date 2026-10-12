using Surveyor.Core;
using Surveyor.Core.Models;
using System;
using System.Device.Gpio;
using System.IO.Ports;
using System.Net.Http;
using System.Net.Http.Json;

using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

class Program
{
    // --- Configuration ---
    static string SerialPort = "/dev/ttyAMA0";
    const int BaudRate = 115200;
    static readonly string RtcmHost = Env("SURVEYOR_RTCM_HOST", "base.local");
    const int RtcmPort = 2101;
    static readonly string SurveyApi = Env("SURVEYOR_SURVEY_API", "");
    static readonly string PositionApi = Env("SURVEYOR_POSITION_API", "");
    static readonly string OwnerEmail = Env("SURVEYOR_OWNER_EMAIL", "");
    const int DefaultSessionId = 1;

    static string Env(string name, string fallback)
    {
        var v = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(v) ? fallback : v;
    }

    static double EnvDouble(string name, double fallback) =>
        double.TryParse(Environment.GetEnvironmentVariable(name), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : fallback;

    // Approximate startup position — used ONLY until first GPS fix arrives
    static readonly double ApproxLat = EnvDouble("SURVEYOR_APPROX_LAT", 0);
    static readonly double ApproxLon = EnvDouble("SURVEYOR_APPROX_LON", 0);
    static readonly double ApproxAlt = EnvDouble("SURVEYOR_APPROX_ALT", 0);

    static LedController? _leds;
    static FileStream? _ptyWrite;  // PTY for writing RTCM
    static HttpClient _http = new();
    static CancellationTokenSource _cts = new();
    static GpsStatus _gpsStatus = new();
    static ButtonController? _buttons;

    // Latest parsed GGA data
    static string _fixQuality = "No Fix";
    static string _latitude = "---";
    static string _longitude = "---";
    static string _altitude = "---";
    static string _satellites = "0";
    static string _hdop = "---";
    static DateTime _lastGga = DateTime.MinValue;
    static DateTime _lastFixUtc = DateTime.MinValue; // when the live coords were last updated
    static DateTime _lastRtcmUtc = DateTime.MinValue; // when RTCM bytes last arrived from the base

    // Live decimal degree coordinates (updated from parsed NMEA)
    static double _liveLat = ApproxLat;
    static double _liveLon = ApproxLon;
    static double _liveAlt = ApproxAlt;
    static bool _hasLiveFix = false;

    // Save status
    static string _saveStatus = "Press Ctrl+S to save point";
    static int _savedCount = 0;                       // last used sequence number (persisted across restarts)
    static readonly SemaphoreSlim _saveLock = new(1, 1); // serialize saves (button + Ctrl+S share this)
    static readonly string SeqFilePath = BuildSeqPath();
    static string _ntripStatus = "Disconnected";

    static string BuildSeqPath()
    {
        string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(baseDir)) baseDir = AppContext.BaseDirectory; // fallback: next to the exe
        return Path.Combine(baseDir, "surveyor", "point_seq.txt");
    }
    static string _posStatus = "";

    static async Task Main(string[] args)
    {
        Console.WriteLine("=== SurveyorDemo - RTK GPS ===");
        Console.WriteLine($"Serial: {SerialPort} @ {BaudRate} baud");
        Console.WriteLine($"RTCM:   {RtcmHost}:{RtcmPort}");
        Console.WriteLine("Press Ctrl+C to exit.\n");

        if (args.Length > 0) SerialPort = args[0];

        if (string.IsNullOrEmpty(SurveyApi) || string.IsNullOrEmpty(PositionApi) || string.IsNullOrEmpty(OwnerEmail))
            Console.WriteLine("[CONFIG] SURVEYOR_SURVEY_API / SURVEYOR_POSITION_API / SURVEYOR_OWNER_EMAIL not set - uploads will fail. See README.md.");

        LoadPointSequence();

        Console.CancelKeyPress += (s, e) => {
            e.Cancel = true;
            _cts.Cancel();
        };

     

        // Initialize LEDs before opening serial port
        try
        {
            _leds = new LedController(_cts.Token);
            _leds.Start();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[LED] Failed to initialize — continuing without LEDs: {ex.Message}");
        }

        try
        {
            _buttons = new ButtonController(SavePointAsync, _cts.Token);
            _buttons.Start();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[BTN] Failed to initialize — continuing without buttons: {ex.Message}");
        }

        // Connect to socat PTY bridge (managed by serial-bridge.service)
        try
        {
            string ptyPath = "/tmp/gps_pty";
            for (int i = 0; i < 40; i++)
            {
                if (File.Exists(ptyPath)) break;
                Thread.Sleep(500);
            }
            if (!File.Exists(ptyPath))
            {
                Console.WriteLine("[GPS] PTY /tmp/gps_pty not found — is serial-bridge.service running?");
                _leds?.Dispose();
                return;
            }
            _ptyWrite = new FileStream(ptyPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            Console.WriteLine($"[GPS] Connected to PTY bridge: {ptyPath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GPS] Failed to open PTY: {ex.Message}");
            _leds?.Dispose();
            return;
        }

        // Start tasks
        var ntripTask    = Task.Run(() => RtcmTcpLoop(_cts.Token));
        var nmeaTask     = Task.Run(() => NmeaReadLoop(_cts.Token));
        var displayTask  = Task.Run(() => DisplayLoop(_cts.Token));
        var keyTask      = Task.Run(() => KeyInputLoop(_cts.Token));
        var positionTask = Task.Run(() => PositionLoop(_cts.Token));

        await Task.WhenAll(ntripTask, nmeaTask, displayTask, keyTask, positionTask);

        _ptyWrite?.Dispose();
        _http.Dispose();
        _leds?.Dispose();
        Console.WriteLine("\n[INFO] Shutdown complete.");
    }

    // -------------------------------------------------------------------------
    // Position loop — saves current location every 3 seconds
    // -------------------------------------------------------------------------
    static async Task PositionLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(3000, ct);

                if (!_hasLiveFix) continue;

                // Use the actual time the GPS fix was parsed, not current time
                var fixAge = DateTime.UtcNow - _lastFixUtc;
                if (fixAge.TotalSeconds > 10)
                {
                    _posStatus = $"Skipped — fix {fixAge.TotalSeconds:F0}s stale";
                    continue;
                }

                var payload = new
                {
                    email = OwnerEmail,
                    latitude = _liveLat,
                    longitude = _liveLon,
                    altitude = _liveAlt,
                    eventDateTime = _lastFixUtc
                };

                var response = await _http.PostAsJsonAsync(PositionApi, payload, ct);
                _posStatus = response.IsSuccessStatusCode
                    ? $"Pos saved {DateTime.Now:HH:mm:ss}"
                    : $"Pos failed: HTTP {(int)response.StatusCode}";
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _posStatus = $"Pos error: {ex.Message[..Math.Min(20, ex.Message.Length)]}";
            }
        }
    }

    // -------------------------------------------------------------------------
    // RTCM TCP client loop — connects to base station, pipes RTCM to GPS
    // -------------------------------------------------------------------------
    static async Task RtcmTcpLoop(CancellationToken ct)
    {
        // The base streams RTCM continuously (~6 frames/sec). If no bytes arrive
        // for this long, the socket is treated as stale (half-open) and we reconnect.
        // This guards against NAT/router idle-timeouts that silently drop the
        // connection without a FIN/RST, which would otherwise wedge ReadAsync forever.
        var staleTimeout = TimeSpan.FromSeconds(15);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                _ntripStatus = "Connecting...";
                _gpsStatus.NtripStatus = _ntripStatus;

                using var tcp = new TcpClient();
                await tcp.ConnectAsync(RtcmHost, RtcmPort, ct);
                // Set keep-alive only AFTER connecting. Touching tcp.Client before
                // ConnectAsync materializes the socket and breaks multi-address
                // (IPv6/IPv4) connect retry on Unix (PlatformNotSupportedException).
                ConfigureKeepAlive(tcp.Client);

                _ntripStatus = "Connected";
                _gpsStatus.NtripStatus = _ntripStatus;
                _leds?.UpdateNtripConnected(true);
                _lastRtcmUtc = DateTime.UtcNow;
                Console.WriteLine($"[RTCM] Connected to {RtcmHost}:{RtcmPort}");

                using var stream = tcp.GetStream();
                byte[] buffer = new byte[4096];
                while (!ct.IsCancellationRequested)
                {
                    // Bound each read so a half-open connection can't block us forever.
                    using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    readCts.CancelAfter(staleTimeout);

                    int n;
                    try
                    {
                        n = await stream.ReadAsync(buffer, readCts.Token);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        Console.WriteLine($"[RTCM] No data for {staleTimeout.TotalSeconds:F0}s — connection stale, reconnecting");
                        break;
                    }

                    if (n == 0) break;
                    try { _ptyWrite?.Write(buffer, 0, n); _ptyWrite?.Flush(); }
                    catch { }
                    _lastRtcmUtc = DateTime.UtcNow;
                    _leds?.PulseNtrip();
                }

                _ntripStatus = "Disconnected";
                _gpsStatus.NtripStatus = _ntripStatus;
                _leds?.UpdateNtripConnected(false);
                Console.WriteLine("[RTCM] Stream ended — reconnecting");
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _ntripStatus = "Error — retry 5s";
                _gpsStatus.NtripStatus = _ntripStatus;
                _leds?.UpdateNtripConnected(false);
                Console.WriteLine($"[RTCM] {ex.GetType().Name}: {ex.Message}");
                await Task.Delay(5000, ct);
            }
        }
    }

    // OS-level backstop: detect a dead peer even if a read happens to be idle.
    // Probes start after 10s idle, repeat every 5s, drop the socket after 3 misses.
    static void ConfigureKeepAlive(Socket sock)
    {
        try
        {
            sock.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            sock.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 10);
            sock.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 5);
            sock.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);
        }
        catch { /* keep-alive tuning is best-effort; the read timeout is the primary guard */ }
    }

    // -------------------------------------------------------------------------
    // NMEA read loop — reads in bulk to avoid falling behind on Pi Zero 2 W
    // -------------------------------------------------------------------------
    static void NmeaReadLoop(CancellationToken ct)
    {
        byte[] readBuf = new byte[4096];
        var sb = new StringBuilder();

        // Wait for PTY to be ready
        while (!File.Exists("/tmp/gps_pty") && !ct.IsCancellationRequested)
            Thread.Sleep(250);

        // Read NMEA via cat subprocess on the PTY (bypasses .NET SerialPort read bugs)
        var catProc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "cat",
            Arguments = "/tmp/gps_pty",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        if (catProc == null) { Console.WriteLine("[NMEA] Failed to start reader"); return; }
        Console.WriteLine("[NMEA] Reading via cat /tmp/gps_pty");
        var readStream = catProc.StandardOutput.BaseStream;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                int bytesRead = readStream.Read(readBuf, 0, readBuf.Length);
                if (bytesRead == 0) { Thread.Sleep(10); continue; }

                for (int i = 0; i < bytesRead; i++)
                {
                    char c = (char)readBuf[i];
                    if (c == '\n')
                    {
                        string line = sb.ToString().Trim();
                        sb.Clear();
                        if (line.StartsWith("$") && line.Contains(","))
                            ParseNmea(line);
                    }
                    else if (c != '\r')
                    {
                        sb.Append(c);
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                Console.WriteLine($"[NMEA] Read error: {ex.Message}");
                Thread.Sleep(500);
            }
        }
    }

    static void ParseNmea(string sentence)
    {
        try
        {
            int star = sentence.IndexOf('*');
            string data = star > 0 ? sentence[..star] : sentence;
            string[] parts = data.Split(',');
            string type = parts[0].TrimStart('$');

            if (type.Length > 3) type = type[^3..];

            if (type == "GGA" && parts.Length >= 10)
            {
                _lastGga = DateTime.UtcNow;
                _fixQuality = parts[6] switch
                {
                    "0" => "No Fix",
                    "1" => "GPS Fix",
                    "2" => "DGPS",
                    "4" => "RTK Fixed",
                    "5" => "RTK Float",
                    _ => $"Fix({parts[6]})"
                };
                _satellites = parts[7];
                _hdop = parts[8];
                _altitude = string.IsNullOrEmpty(parts[9]) ? "---" : $"{parts[9]} m";
                _latitude = FormatLatLon(parts[2], parts[3]);
                _longitude = FormatLatLon(parts[4], parts[5]);

                // Update LED fix quality regardless of whether we have coords
                _leds?.UpdateFixQuality(int.TryParse(parts[6], out int fq2) ? fq2 : 0);

                if (parts[6] != "0" && !string.IsNullOrEmpty(parts[2]))
                {
                    _liveLat = ParseNmeaCoord(parts[2], parts[3], false);
                    _liveLon = ParseNmeaCoord(parts[4], parts[5], true);
                    _liveAlt = double.TryParse(parts[9], out double a) ? a : ApproxAlt;
                    _lastFixUtc = DateTime.UtcNow;
                    _hasLiveFix = true;
                    _gpsStatus.FixQuality = int.TryParse(parts[6], out int fq) ? fq : 0;
                    _gpsStatus.FixDescription = _fixQuality;
                    _gpsStatus.Latitude = _liveLat;
                    _gpsStatus.Longitude = _liveLon;
                    _gpsStatus.AltitudeMeters = _liveAlt;
                    _gpsStatus.SatellitesUsed = int.TryParse(_satellites, out int s) ? s : 0;
                    _gpsStatus.Hdop = double.TryParse(_hdop, out double h) ? h : 0;
                    _gpsStatus.LastUpdateUtc = DateTime.UtcNow;
                }
            }
        }
        catch { }
    }

    static double ParseNmeaCoord(string value, string direction, bool isLon)
    {
        if (string.IsNullOrEmpty(value) || value.Length < 4) return 0;
        int degLen = isLon ? 3 : 2;
        double deg = double.Parse(value[..degLen]);
        double min = double.Parse(value[degLen..]);
        double dd = deg + min / 60.0;
        if (direction == "S" || direction == "W") dd = -dd;
        return dd;
    }

    static string FormatLatLon(string value, string direction)
    {
        if (string.IsNullOrEmpty(value) || value.Length < 4) return "---";
        try
        {
            bool isLon = direction == "E" || direction == "W";
            double dd = ParseNmeaCoord(value, direction, isLon);
            return $"{dd:F7}°";
        }
        catch { return value; }
    }

    // -------------------------------------------------------------------------
    // Keyboard input loop
    // -------------------------------------------------------------------------
    static async Task KeyInputLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(intercept: true);
                    if (key.Key == ConsoleKey.S && key.Modifiers == ConsoleModifiers.Control)
                        await SavePointAsync();
                }
                await Task.Delay(100, ct);
            }
            catch (OperationCanceledException) { break; }
            catch { }
        }
    }

    static async Task SavePointAsync()
    {
        if (!_hasLiveFix)
        {
            _saveStatus = "Cannot save — no GPS fix yet";
            Console.WriteLine($"[SAVE] {_saveStatus}");
            return;
        }

        await _saveLock.WaitAsync();
        try
        {
            _saveStatus = "Saving...";

            double alt = double.TryParse(_altitude.Replace(" m", ""), out double a) ? a : 0;
            int? sats = int.TryParse(_satellites, out int s) ? s : null;
            double hdop = double.TryParse(_hdop, out double h) ? h : 0;

            // Identifier: local date-time + persistent 3-digit sequence + fix quality,
            // e.g. "20260606-171557-001 (RTK Fixed)". The date-time guarantees uniqueness
            // even if the sequence file is lost; the sequence keeps points ordered.
            var capturedLocal = DateTime.Now;
            int seq = _savedCount + 1;
            string identifier = $"{capturedLocal:yyyyMMdd-HHmmss}-{seq:D3} ({_fixQuality})";

            var payload = new
            {
                sessionId = DefaultSessionId,
                ownerEmail = OwnerEmail,
                latitude = _liveLat,
                longitude = _liveLon,
                altitude = alt,
                horizontalAccuracy = hdop,
                fixType = _fixQuality,
                numSatellites = sats,
                label = identifier,
                capturedAt = capturedLocal.ToUniversalTime()
            };

            var response = await _http.PostAsJsonAsync(SurveyApi, payload);

            if (response.IsSuccessStatusCode)
            {
                _savedCount = seq;
                PersistPointSequence();
                _gpsStatus.PointsSaved = _savedCount;
                _saveStatus = $"Saved {identifier}";
                Console.WriteLine($"[SAVE] Saved {identifier}");
            }
            else
            {
                _saveStatus = $"Save failed: HTTP {(int)response.StatusCode}";
                Console.WriteLine($"[SAVE] {_saveStatus}");
            }
        }
        catch (Exception ex)
        {
            _saveStatus = $"Save error: {ex.Message[..Math.Min(30, ex.Message.Length)]}";
            Console.WriteLine($"[SAVE] {_saveStatus}");
        }
        finally
        {
            _saveLock.Release();
        }
    }

    // Persistent point-sequence counter — survives app/service restarts so point
    // numbers never reset or repeat (stored at SeqFilePath).
    static void LoadPointSequence()
    {
        try
        {
            if (File.Exists(SeqFilePath)
                && int.TryParse(File.ReadAllText(SeqFilePath).Trim(), out int n)
                && n > 0)
            {
                _savedCount = n;
                Console.WriteLine($"[SAVE] Restored point sequence — last used #{_savedCount}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SAVE] Could not load sequence ({ex.Message}) — starting at 0");
        }
    }

    static void PersistPointSequence()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SeqFilePath)!);
            File.WriteAllText(SeqFilePath, _savedCount.ToString());
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SAVE] Could not persist sequence: {ex.Message}");
        }
    }


    // -------------------------------------------------------------------------
    // Display loop
    // -------------------------------------------------------------------------
    static void DisplayLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Thread.Sleep(1000);
            Console.Clear();
            Console.WriteLine("╔══════════════════════════════════════╗");
            Console.WriteLine("║       SurveyorDemo - RTK GPS         ║");
            Console.WriteLine("╠══════════════════════════════════════╣");
            Console.WriteLine($"║ Fix Quality : {_fixQuality,-23}║");
            Console.WriteLine($"║ Latitude    : {_latitude,-23}║");
            Console.WriteLine($"║ Longitude   : {_longitude,-23}║");
            Console.WriteLine($"║ Altitude    : {_altitude,-23}║");
            Console.WriteLine($"║ Satellites  : {_satellites,-23}║");
            Console.WriteLine($"║ HDOP        : {_hdop,-23}║");
            string age = _lastGga == DateTime.MinValue ? "Waiting..."
                       : $"{(DateTime.UtcNow - _lastGga).TotalSeconds:F0}s ago";
            Console.WriteLine($"║ Last GGA    : {age,-23}║");
            string ntripLine = _ntripStatus == "Connected" && _lastRtcmUtc != DateTime.MinValue
                ? $"Connected ({(DateTime.UtcNow - _lastRtcmUtc).TotalSeconds:F0}s)"
                : _ntripStatus;
            Console.WriteLine($"║ NTRIP       : {ntripLine,-23}║");
            Console.WriteLine($"║ Live Fix    : {(_hasLiveFix ? "Yes" : "Using approx pos"),-23}║");
            Console.WriteLine($"║ Points Saved: {_savedCount,-23}║");
            Console.WriteLine($"║ Auto Pos    : {_posStatus,-23}║");
            Console.WriteLine("╠══════════════════════════════════════╣");
            Console.WriteLine($"║ {_saveStatus,-38}║");
            Console.WriteLine("╠══════════════════════════════════════╣");
            Console.WriteLine("║  Ctrl+S = Save Point  Ctrl+C = Exit  ║");
            Console.WriteLine("╚══════════════════════════════════════╝");
        }
    }
}
