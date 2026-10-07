namespace ErganiManager.Core.Interfaces;

/// <summary>
/// One rounding band: when the minutes of a worked duration are between
/// <see cref="FromMinute"/> and <see cref="ToMinute"/> (inclusive) the time is rounded to
/// the whole hour plus <see cref="RoundToMinute"/> (0, 30, or 60 = the next hour).
/// </summary>
public sealed class RoundingBand
{
    public int FromMinute { get; set; }
    public int ToMinute { get; set; }
    public int RoundToMinute { get; set; }
}

public sealed class RoundingRules
{
    public List<RoundingBand> Bands { get; set; } = Defaults();

    public static List<RoundingBand> Defaults() => new()
    {
        new RoundingBand { FromMinute = 1,  ToMinute = 16, RoundToMinute = 0  },
        new RoundingBand { FromMinute = 17, ToMinute = 30, RoundToMinute = 30 },
        new RoundingBand { FromMinute = 31, ToMinute = 46, RoundToMinute = 30 },
        new RoundingBand { FromMinute = 47, ToMinute = 59, RoundToMinute = 60 }
    };

    /// <summary>
    /// Rounds a duration in hours. Whole minutes are used (seconds are ignored); an exact hour stays as is,
    /// and minutes not covered by any band are left unrounded.
    /// e.g. 8:09 → 8:00, 8:20 → 8:30, 8:50 → 9:00 with the default bands.
    /// </summary>
    public double Round(double hours)
    {
        var totalMinutes = (int)Math.Floor(hours * 60 + 1e-6);
        var whole = totalMinutes / 60;
        var minutes = totalMinutes % 60;
        if (minutes == 0) return whole;

        var band = Bands.FirstOrDefault(b => minutes >= b.FromMinute && minutes <= b.ToMinute);
        return band == null ? totalMinutes / 60.0 : whole + band.RoundToMinute / 60.0;
    }
}

/// <summary>Stores the editable rounding rules (a small file in the application data folder).</summary>
public interface IRoundingSettingsService
{
    RoundingRules Current { get; }
    void Save(RoundingRules rules);
}
