using System.Diagnostics;
using System.IO;

namespace Surveyor.Core;

/// <summary>
/// Controls the 6 external panel LEDs on the GroundTruth surveyor unit.
/// Uses gpioset/gpiod CLI for compatibility with libgpiod v2 (Debian Trixie).
/// All pin numbers are BCM GPIO numbers on gpiochip0.
/// </summary>
public class LedController : IDisposable
{
    // --- Pin assignments (BCM) ---
    private const int PinPower      = 5;
    private const int PinGpsFix     = 6;
    private const int PinRtkFixed   = 12;
    private const int PinWifi       = 13;
    private const int PinNtrip      = 16;
    private const int PinBatteryLow = 26;

    // Network interface backing the WiFi LED (GPIO 13)
    private const string WifiInterface = "wlan0";

    private static readonly int[] AllPins = { PinPower, PinGpsFix, PinRtkFixed, PinWifi, PinNtrip, PinBatteryLow };

    private readonly CancellationToken _ct;

    private int _fixQuality      = 0;
    private bool _ntripConnected = false;
    private bool _ntripPulse     = false;
    private bool _disposed       = false;

    // Persistent gpioset process that holds the lines
    private Process? _gpioProc;
    private readonly Dictionary<int, int> _pinState = new();

    private Task? _blinkTask;

    public LedController(CancellationToken ct)
    {
        _ct = ct;
        try
        {
            foreach (var pin in AllPins)
                _pinState[pin] = 0;

            // Power LED on immediately
            _pinState[PinPower] = 1;
            ApplyState();

            Console.WriteLine("[LED] GPIO initialized OK — Power LED on.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[LED] GPIO init FAILED: {ex.Message}");
            throw;
        }
    }

    private void ApplyState()
    {
        _gpioProc?.Kill();
        _gpioProc?.Dispose();

        // gpioset --chip gpiochip0 --hold-period 1000000s 5=1 6=0 12=0 ...
        var args = string.Join(" ", AllPins.Select(p => $"{p}={_pinState[p]}"));
        _gpioProc = Process.Start(new ProcessStartInfo
        {
            FileName = "gpioset",
            Arguments = $"--chip gpiochip0 --hold-period 1000000s {args}",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        });
    }

    public void Start()
    {
        _blinkTask = Task.Run(BlinkLoop, _ct);
    }

    public void UpdateFixQuality(int fixQuality) => _fixQuality = fixQuality;

    // NTRIP link state now drives the NTRIP LED (GPIO 16) via BlinkLoop.
    public void UpdateNtripConnected(bool connected) => _ntripConnected = connected;

    public void PulseNtrip() => _ntripPulse = true;

    // WiFi LED (GPIO 13) reflects wlan link state: operstate == "up" means associated.
    private static int ReadWifiLinkState()
    {
        try
        {
            var path = $"/sys/class/net/{WifiInterface}/operstate";
            if (!File.Exists(path)) return 0;
            return File.ReadAllText(path).Trim() == "up" ? 1 : 0;
        }
        catch { return 0; }
    }

    private void SetPin(int pin, int value)
    {
        if (_pinState[pin] != value)
        {
            _pinState[pin] = value;
            ApplyState();
        }
    }

    private async Task BlinkLoop()
    {
        bool slowBlink = false;

        while (!_ct.IsCancellationRequested)
        {
            try
            {
                slowBlink = !slowBlink;
                bool changed = false;

                // GPS Fix LED
                int gpsFix = (_fixQuality == 0) ? (slowBlink ? 1 : 0) : 1;
                if (_pinState[PinGpsFix] != gpsFix) { _pinState[PinGpsFix] = gpsFix; changed = true; }

                // RTK Fixed LED — solid only on fix quality 4
                int rtk = _fixQuality == 4 ? 1 : 0;
                if (_pinState[PinRtkFixed] != rtk) { _pinState[PinRtkFixed] = rtk; changed = true; }

                // WiFi LED — solid when wlan0 is associated
                int wifi = ReadWifiLinkState();
                if (_pinState[PinWifi] != wifi) { _pinState[PinWifi] = wifi; changed = true; }

                // NTRIP LED — solid while connected; blinks while RTCM data is flowing
                int ntrip;
                if (!_ntripConnected)  ntrip = 0;
                else if (_ntripPulse)  ntrip = slowBlink ? 1 : 0;
                else                   ntrip = 1;
                _ntripPulse = false;
                if (_pinState[PinNtrip] != ntrip) { _pinState[PinNtrip] = ntrip; changed = true; }

                // Battery Low — always off
                if (_pinState[PinBatteryLow] != 0) { _pinState[PinBatteryLow] = 0; changed = true; }

                if (changed) ApplyState();

                await Task.Delay(1000, _ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Console.WriteLine($"[LED] Blink error: {ex.Message}");
                await Task.Delay(1000, _ct);
            }
        }

        AllOff();
    }

    private void AllOff()
    {
        try
        {
            foreach (var pin in AllPins)
                _pinState[pin] = 0;
            ApplyState();
        }
        catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        AllOff();
        _gpioProc?.Kill();
        _gpioProc?.Dispose();
    }
}
