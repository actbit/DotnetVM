using System.Globalization;

namespace DotnetVM.CoreLib;

/// <summary>
/// Culture-aware formatting/parsing choke points used by the VM CoreLib replacement IL.
///
/// CLR execution keeps the historical invariant implementation so the replacement assembly
/// remains self-contained and deterministic on its own. When the assembly is executed by the VM,
/// these methods are replaced by host bindings that use the VM's configured CultureInfo. Keeping
/// the bridge behind ordinary managed methods means the replacement faces remain traceable IL
/// while avoiding a process-global culture cache.
/// </summary>
public static class CultureSettings {
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static string FormatByte(byte value, string? format) => value.ToString(format, Invariant);
    public static string FormatSByte(sbyte value, string? format) => value.ToString(format, Invariant);
    public static string FormatInt16(short value, string? format) => value.ToString(format, Invariant);
    public static string FormatUInt16(ushort value, string? format) => value.ToString(format, Invariant);
    public static string FormatInt32(int value, string? format) => value.ToString(format, Invariant);
    public static string FormatUInt32(uint value, string? format) => value.ToString(format, Invariant);
    public static string FormatInt64(long value, string? format) => value.ToString(format, Invariant);
    public static string FormatUInt64(ulong value, string? format) => value.ToString(format, Invariant);
    public static string FormatSingle(double value, string? format) => ((float)value).ToString(format, Invariant);
    public static string FormatDouble(double value, string? format) => value.ToString(format, Invariant);
    public static string FormatDecimal(decimal value, string? format) => value.ToString(format, Invariant);

    public static int ParseInt32(string value, int styles) =>
        int.Parse(value, (NumberStyles)styles, Invariant);
    public static long ParseInt64(string value, int styles) =>
        long.Parse(value, (NumberStyles)styles, Invariant);
    public static double ParseSingle(string value, int styles) =>
        float.Parse(value, (NumberStyles)styles, Invariant);
    public static double ParseDouble(string value, int styles) =>
        double.Parse(value, (NumberStyles)styles, Invariant);
}
