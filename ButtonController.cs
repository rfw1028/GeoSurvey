using System.Diagnostics;

namespace Surveyor.Core;

/// <summary>
/// Monitors the two external panel buttons on the GroundTruth surveyor unit.
/// Uses gpioget CLI for compatibility with libgpiod v2 (Debian Trixie).
///
/// Pin assignments (BCM on gpiochip0) — verified against hardware 2026-06-06:
///   GPIO 7 / Physical Pin 26 — Capture point (short press)
///   GPIO 9 / Physical Pin 21 — Safe shutdown (hold 3 seconds)
/// Note: the capture button is physically wired to GPIO 7, not GPIO 17 as
/// originally assumed. Both buttons confirmed working on hardware.
/// </summary>
public class ButtonController : IDisposable
{
    private const int PinCapture  = 7;
    private const int PinShutdown = 9;
    private const int DebounceMsec = 300;
    private const int ShutdownHoldMsec = 3000;

    private readonly CancellationToken _ct;
    private readonly Func<Task> _onCapture;

    private DateTime _lastCapturePress = DateTime.MinValue;
    private Task? _monitorTask;
    private bool _disposed = false;

    public ButtonController(Func<Task> onCapture, CancellationToken ct)
    {
        _onCapture = onCapture;
        _ct = ct;

        // Verify gpioget works
        try
        {
            ReadPin(PinCapture);
            Console.WriteLine($"[BTN] GPIO initialized — Capture=GPIO{PinCapture}, Shutdown=GPIO{PinShutdown}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[BTN] GPIO init FAILED: {ex.Message}");
            throw;
        }
    }

    private bool ReadPin(int pin)
    {
        using var proc = Process.Start(new ProcessStartInfo
        {
            FileName = "gpioget",
            Arguments = $"--numeric --bias=pull-up --chip gpiochip0 {pin}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        proc!.WaitForExit(2000);
        var output = proc.StandardOutput.ReadToEnd().Trim();
        // With --numeric: "1" = high (not pressed), "0" = low (pressed, active low)
        return output == "0";
    }

    public void Start()
    {
        _monitorTask = Task.Run(MonitorLoop, _ct);
    }

    private async Task MonitorLoop()
    {
        while (!_ct.IsCancellationRequested)
        {
            try
            {
                // --- Capture button ---
                if (ReadPin(PinCapture))
                {
                    Console.WriteLine("[BTN] Capture button pressed");
                    try { await _onCapture(); }
                    catch (Exception ex) { Console.WriteLine($"[BTN] Capture callback error: {ex.Message}"); }

                    // Wait for button release before accepting another press
                    while (ReadPin(PinCapture) && !_ct.IsCancellationRequested)
                        await Task.Delay(200, _ct);
                    await Task.Delay(DebounceMsec, _ct); // debounce after release
                }

                // --- Shutdown button (GPIO 9) ---
                if (ReadPin(PinShutdown))
                {
                    Console.WriteLine("[BTN] Shutdown button held — release within 3s to cancel...");
                    var holdStart = DateTime.UtcNow;

                    while (ReadPin(PinShutdown))
                    {
                        if ((DateTime.UtcNow - holdStart).TotalMilliseconds >= ShutdownHoldMsec)
                        {
                            Console.WriteLine("[BTN] Shutdown confirmed — shutting down now...");
                            await Task.Run(() => Process.Start("sudo", "shutdown -h now"));
                            return;
                        }
                        await Task.Delay(200, _ct);
                    }

                    Console.WriteLine("[BTN] Shutdown cancelled — button released early.");
                }

                await Task.Delay(200, _ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Console.WriteLine($"[BTN] Monitor error: {ex.Message}");
                await Task.Delay(500, _ct);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }
}
