#!/usr/bin/env python3
"""
GroundTruth External LED Test
Cycles through all 6 external panel LEDs, each on for 2 seconds.
Press Ctrl-C to exit cleanly.

GPIO BCM  Physical Pin  Function
-------   ------------  --------------------
  5         29          Power
  6         31          GPS Fix
 12         32          RTK Fixed
 13         33          WiFi / Network
 16         36          NTRIP Active
 26         37          Battery Low
"""

import RPi.GPIO as GPIO
import time

# ---------------------------------------------------------------------------
# Pin definitions
# ---------------------------------------------------------------------------

LEDS = [
    ( 5, "Power          ", 29),
    ( 6, "GPS Fix        ", 31),
    (12, "RTK Fixed      ", 32),
    (13, "WiFi / Network ", 33),
    (16, "NTRIP Active   ", 36),
    (26, "Battery Low    ", 37),
]

DWELL_SECONDS = 2.0

# ---------------------------------------------------------------------------
# Setup
# ---------------------------------------------------------------------------

def setup():
    GPIO.setmode(GPIO.BCM)
    GPIO.setwarnings(False)
    for pin, _, _ in LEDS:
        GPIO.setup(pin, GPIO.OUT)
        GPIO.output(pin, GPIO.LOW)
    print("\n  GroundTruth External LED Test")
    print("  " + "─" * 42)
    print(f"  {len(LEDS)} LEDs  |  {DWELL_SECONDS}s each  |  Ctrl-C to exit")
    print()
    print(f"  {'GPIO':>5}  {'Pin':>4}  Function")
    print(f"  {'─'*5}  {'─'*4}  {'─'*20}")
    for pin, label, phys in LEDS:
        print(f"  {pin:>5}  {phys:>4}  {label.strip()}")
    print()

def all_off():
    for pin, _, _ in LEDS:
        GPIO.output(pin, GPIO.LOW)

# ---------------------------------------------------------------------------
# Main loop
# ---------------------------------------------------------------------------

def run():
    cycle = 0
    try:
        while True:
            cycle += 1
            print(f"  ── Cycle {cycle} " + "─" * 28)
            for pin, label, phys in LEDS:
                all_off()
                GPIO.output(pin, GPIO.HIGH)
                print(f"  ON  GPIO {pin:2d}  Pin {phys:2d}  {label.strip()}", flush=True)
                time.sleep(DWELL_SECONDS)

            all_off()
            print()

    except KeyboardInterrupt:
        print("\n\n  Ctrl-C received — cleaning up.")

    finally:
        all_off()
        GPIO.cleanup()
        print("  All LEDs off. GPIO cleaned up.\n")

# ---------------------------------------------------------------------------

if __name__ == "__main__":
    setup()
    run()
