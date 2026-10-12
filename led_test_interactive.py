#!/usr/bin/env python3
"""Interactive LED test for Surveyor Pi — turns on each LED one at a time,
displays its name, and waits for Enter before moving to the next.
Uses libgpiod v2 (gpiod) with gpiochip4."""

import gpiod
import sys

CHIP = "/dev/gpiochip4"

LEDS = [
    (5,  29, "Power LED"),
    (6,  31, "GPS Fix LED"),
    (12, 32, "RTK Fixed LED"),
    (13, 33, "WiFi / Network LED"),
    (16, 36, "NTRIP Active LED"),
    (26, 37, "Battery Low LED"),
]

def main():
    config = gpiod.request_lines(
        CHIP,
        consumer="led-test",
        config={
            tuple(pin for pin, _, _ in LEDS): gpiod.LineSettings(
                direction=gpiod.line.Direction.OUTPUT,
                output_value=gpiod.line.Value.INACTIVE,
            )
        },
    )

    print("=" * 50)
    print("  Surveyor Pi — Interactive LED Test")
    print("=" * 50)
    print(f"  Testing {len(LEDS)} LEDs. Each will turn on and stay")
    print("  on until you press Enter.\n")

    try:
        for i, (gpio, phys, name) in enumerate(LEDS, 1):
            # All off
            for pin, _, _ in LEDS:
                config.set_value(pin, gpiod.line.Value.INACTIVE)
            # This one on
            config.set_value(gpio, gpiod.line.Value.ACTIVE)
            print(f"  [{i}/{len(LEDS)}]  ON  ->  {name}")
            print(f"         GPIO {gpio}  (physical pin {phys})")
            input("         Press Enter to continue...")
            print()

        # All off
        for pin, _, _ in LEDS:
            config.set_value(pin, gpiod.line.Value.INACTIVE)
        print("  All LEDs off. Test complete.")
    finally:
        config.release()

if __name__ == "__main__":
    try:
        main()
    except KeyboardInterrupt:
        print("\n  Cancelled.")
        sys.exit(0)
