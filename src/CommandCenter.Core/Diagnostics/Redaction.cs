using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace CommandCenter.Core.Diagnostics
{
    // What a copied report hides before it leaves the PC (spec phase 3): the last part of every IPv4 address (the LAN
    // address of the network check too), all but the first two groups of every IPv6 address, and the Windows user and PC
    // names where they are whole path segments. The support report and the network diagnostics both use it.
    public static class Redaction
    {
        public static string Report(string text) => Report(text, Environment.UserName, Environment.MachineName);

        internal static string Report(string text, string user, string machine)
        {
            // IPv6 first, so an IPv4 address written inside one goes with it
            text = Ipv6.Replace(text, m => IPAddress.TryParse(m.Value, out var address) && address.AddressFamily == AddressFamily.InterNetworkV6
                ? FirstGroups(address) + ":XXXX::"
                : m.Value);
            text = Ipv4.Replace(text, m => m.Groups.Values.Skip(1).All(g => int.Parse(g.Value) <= 255)
                ? $"{m.Groups[1].Value}.{m.Groups[2].Value}.{m.Groups[3].Value}.XXX"
                : m.Value);
            text = Segment(text, user, "<user>");
            return Segment(text, machine, "<pc>");
        }

        // Hexadecimal groups joined by colons, possibly ending in a dotted IPv4 address; IPAddress decides
        private static readonly Regex Ipv6 = new(
            @"(?<![0-9A-Fa-f:.])(?:[0-9A-Fa-f]{0,4}:){2,7}(?:\d{1,3}(?:\.\d{1,3}){3}|[0-9A-Fa-f]{0,4})(?![0-9A-Fa-f:.])",
            RegexOptions.Compiled);

        // Four numbers of up to three digits, not part of a longer dotted number (a version)
        private static readonly Regex Ipv4 = new(@"(?<![\d.])(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})(?!\.?\d)", RegexOptions.Compiled);

        private static string FirstGroups(IPAddress address)
        {
            byte[] bytes = address.GetAddressBytes();
            return $"{bytes[0] << 8 | bytes[1]:x}:{bytes[2] << 8 | bytes[3]:x}";
        }

        // The name between path separators, or at the end of a path before a space, a full stop or punctuation; a longer
        // name that starts with it ("a.b" for "a") stays
        private static string Segment(string text, string name, string placeholder)
        {
            if (string.IsNullOrWhiteSpace(name))
                return text;
            var segment = new Regex(@"(?<=[\\/])" + Regex.Escape(name) + @"(?=[\\/""'),;]|\.?(?:\s|$))", RegexOptions.IgnoreCase);
            return segment.Replace(text, placeholder);
        }
    }
}
