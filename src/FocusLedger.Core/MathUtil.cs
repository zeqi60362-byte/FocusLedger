namespace FocusLedger.Core;

internal static class MathUtil
{
    public static double Clamp01(double value) =>
        value < 0 ? 0 : value > 1 ? 1 : value;
}
