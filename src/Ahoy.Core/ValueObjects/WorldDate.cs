namespace Ahoy.Core.ValueObjects;

/// <summary>
/// In-game date. The simulation advances one day per tick during normal play.
/// Year range roughly corresponds to the golden age of Caribbean piracy (1650-1730).
/// </summary>
public readonly record struct WorldDate(int Year, int Month, int Day)
    : IComparable<WorldDate>
{
    public static readonly WorldDate Start = new(1680, 1, 1);

    private static readonly int[] DaysInMonth = [31, 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31];

    public WorldDate Advance(int days = 1)
    {
        var (y, m, d) = (Year, Month, Day);
        for (var i = 0; i < days; i++)
        {
            d++;
            if (d > DaysInMonth[m - 1])
            {
                d = 1;
                m++;
                if (m > 12) { m = 1; y++; }
            }
        }
        return new(y, m, d);
    }

    public int DayOfYear
    {
        get
        {
            var total = 0;
            for (var i = 0; i < Month - 1; i++) total += DaysInMonth[i];
            return total + Day;
        }
    }

    /// <summary>True during Caribbean hurricane season (June–November).</summary>
    public bool IsHurricaneSeason => Month is >= 6 and <= 11;

    public int CompareTo(WorldDate other)
    {
        var y = Year.CompareTo(other.Year);
        if (y != 0) return y;
        var m = Month.CompareTo(other.Month);
        if (m != 0) return m;
        return Day.CompareTo(other.Day);
    }

    public override string ToString() => $"{Day:D2}/{Month:D2}/{Year}";
}
