using System.Text.RegularExpressions;

namespace Lustral.UnityLibraries;

/// <summary>
/// A Unity version in Unity's notation, for example <c>6000.0.58f2</c>: major, minor and patch numbers, the release type
/// (<c>a</c>lpha, <c>b</c>eta, <c>f</c>inal, <c>p</c>atch, e<c>x</c>perimental) and its build number.
/// </summary>
internal readonly partial record struct UnityVersion(int Major, int Minor, int Patch, char Type, int Build) : IComparable<UnityVersion>
{
    public bool IsFinal => Type == 'f';

    public static bool TryParse(string? text, out UnityVersion version)
    {
        var match = text is null ? Match.Empty : Pattern().Match(text.Trim());
        if (!match.Success)
        {
            version = default;
            return false;
        }
        version = new UnityVersion(int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value), int.Parse(match.Groups[3].Value),
            match.Groups[4].Value[0], int.Parse(match.Groups[5].Value));
        return true;
    }

    public static UnityVersion Parse(string text) =>
        TryParse(text, out var version) ? version : throw new FormatException($"\"{text}\" is not a Unity version (for example 6000.0.58f2)");

    public int CompareTo(UnityVersion other)
    {
        var order = (Major, Minor, Patch).CompareTo((other.Major, other.Minor, other.Patch));
        if (order != 0)
            return order;
        order = TypeRank(Type).CompareTo(TypeRank(other.Type));
        return order != 0 ? order : Build.CompareTo(other.Build);
    }

    public static bool operator <(UnityVersion left, UnityVersion right) => left.CompareTo(right) < 0;

    public static bool operator >(UnityVersion left, UnityVersion right) => left.CompareTo(right) > 0;

    public static bool operator <=(UnityVersion left, UnityVersion right) => left.CompareTo(right) <= 0;

    public static bool operator >=(UnityVersion left, UnityVersion right) => left.CompareTo(right) >= 0;

    public override string ToString() => $"{Major}.{Minor}.{Patch}{Type}{Build}";

    private static int TypeRank(char type) => type switch
    {
        'x' => 0,
        'a' => 1,
        'b' => 2,
        'f' => 3,
        'p' => 4,
        _ => 5,
    };

    [GeneratedRegex(@"^(\d+)\.(\d+)\.(\d+)([abfpx])(\d+)$")]
    private static partial Regex Pattern();
}
