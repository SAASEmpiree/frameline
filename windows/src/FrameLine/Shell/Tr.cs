using System;
using System.Globalization;
using System.Text;
using FrameLine.Core;

namespace FrameLine.Shell;

/// Localisation helpers for the shell. Strings.cs keeps the macOS printf placeholders (%@, %d,
/// %1$@), so formatting is done here rather than with string.Format.
internal static class Tr
{
    public static string L(string key) => Loc.L(key);

    /// For strings the macOS app hard-coded (toasts, alerts): the localised key when the Strings
    /// table has it, otherwise the macOS literal.
    public static string L(string key, string fallback)
    {
        var s = Loc.L(key);
        return string.IsNullOrEmpty(s) || s == key ? fallback : s;
    }

    public static string F(string key, params object[] args) => Format(Loc.L(key), args);

    /// Replaces %@ / %d / %ld / %i (sequential) and %1$@ style (positional) placeholders; "%%" is "%".
    public static string Format(string format, params object[] args)
    {
        var sb = new StringBuilder(format.Length + 16);
        int next = 0;
        for (int i = 0; i < format.Length; i++)
        {
            char c = format[i];
            if (c != '%' || i + 1 >= format.Length) { sb.Append(c); continue; }
            int j = i + 1;
            if (format[j] == '%') { sb.Append('%'); i = j; continue; }
            int index = -1;
            int k = j;
            while (k < format.Length && char.IsDigit(format[k])) k++;
            if (k > j && k < format.Length && format[k] == '$')
            {
                index = int.Parse(format.AsSpan(j, k - j), CultureInfo.InvariantCulture) - 1;
                j = k + 1;
            }
            if (j < format.Length && format[j] == 'l') j++;
            if (j < format.Length && (format[j] == '@' || format[j] == 'd' || format[j] == 'i'))
            {
                int arg = index >= 0 ? index : next++;
                sb.Append(arg >= 0 && arg < args.Length ? Convert.ToString(args[arg], CultureInfo.CurrentCulture) : "");
                i = j;
                continue;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }
}
