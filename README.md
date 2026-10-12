# SurveyorDemo (GroundTruth RTK GPS)

.NET 9 console app for a Raspberry Pi (linux-arm64) RTK GPS survey unit: parses NMEA from a GPS
receiver, forwards RTCM corrections from a base station, drives status LEDs and buttons over
GPIO, and posts captured survey points and live position to an HTTP API.

## Configuration

Settings are read from environment variables (no personal endpoints are stored in the code):

| Variable | Purpose |
|---|---|
| `SURVEYOR_SURVEY_API` | URL that receives captured survey points |
| `SURVEYOR_POSITION_API` | URL that receives periodic position updates |
| `SURVEYOR_OWNER_EMAIL` | Account email sent with uploads |
| `SURVEYOR_RTCM_HOST` | RTCM base station host (default `base.local`) |
| `SURVEYOR_APPROX_LAT` / `_LON` / `_ALT` | Optional start position used until the first GPS fix (default 0) |

On the Pi, put them in `/etc/surveyor/surveyor.env` (`KEY=value` lines, root-readable only);
`deploy/surveyor.service` loads that file.

## Deploy (example)

```bash
dotnet publish -c Release -r linux-arm64 -o ./publish --self-contained
scp -r ./publish/* pi@<your-pi>:/home/pi/groundtruth/
ssh pi@<your-pi> "sudo systemctl restart surveyor"
```

The unit files assume user `pi` and `/home/pi/groundtruth`; edit `User=`/paths if yours differ.

## License

PolyForm Noncommercial 1.0.0 (see `License`).
