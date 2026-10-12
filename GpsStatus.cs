namespace Surveyor.Core.Models;

public class GpsStatus
{
    public int FixQuality { get; set; }
    public string FixDescription { get; set; } = "No Fix";
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public double AltitudeMeters { get; set; }
    public int SatellitesUsed { get; set; }
    public double Hdop { get; set; }
    public bool HasRtkFix => FixQuality == 4;
    public bool HasRtkFloat => FixQuality == 5;
    public DateTime? LastUpdateUtc { get; set; }
    public string NtripStatus { get; set; } = "Disconnected";
    public int PointsSaved { get; set; } = 0;
}
