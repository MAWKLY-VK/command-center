namespace CommandCenter.Tests
{
    // What the copied support report and network diagnostics hide (seam 8), on report text with a made-up user "a" and
    // PC "PC7"
    public sealed class RedactionTests
    {
        private static string Hide(string text) => Redaction.Report(text, user: "a", machine: "PC7");

        [Theory]
        [InlineData("STUN:         OK — 203.0.113.45:3478 (41ms)", "STUN:         OK — 203.0.113.XXX:3478 (41ms)")]
        [InlineData("DNS: 104.18.2.7, 104.18.3.7", "DNS: 104.18.2.XXX, 104.18.3.XXX")]
        public void Ipv4_addresses_lose_their_last_part(string text, string hidden) => Assert.Equal(hidden, Hide(text));

        [Theory]
        [InlineData("STUN: 2001:db8:85a3::8a2e:370:7334", "STUN: 2001:db8:XXXX::")]
        [InlineData("STUN: [2001:db8::1]:3478", "STUN: [2001:db8:XXXX::]:3478")]
        [InlineData("fe80::1c2b:3a4d%12", "fe80:0:XXXX::%12")]
        [InlineData("::ffff:192.0.2.128", "0:0:XXXX::")]
        public void Ipv6_addresses_keep_only_their_first_two_groups(string text, string hidden) => Assert.Equal(hidden, Hide(text));

        [Fact]
        public void The_lan_address_of_the_network_check_is_hidden()
        {
            Assert.Equal("[Warning] Saved network address is old: Options.ini has 192.168.1.XXX, this PC is now 192.168.1.XXX. LAN games may not find each other",
                Hide("[Warning] Saved network address is old: Options.ini has 192.168.1.20, this PC is now 192.168.1.35. LAN games may not find each other"));
        }

        [Theory]
        [InlineData(@"Saved in C:\Users\a\Documents\Command and Conquer Generals Zero Hour Data\Maps",
                    @"Saved in C:\Users\<user>\Documents\Command and Conquer Generals Zero Hour Data\Maps")]
        [InlineData(@"C:/Users/a/AppData", @"C:/Users/<user>/AppData")]
        [InlineData(@"It is in C:\Users\a. Move it", @"It is in C:\Users\<user>. Move it")]
        [InlineData(@"C:\Users\a.b\Documents and a map", @"C:\Users\a.b\Documents and a map")]
        [InlineData(@"\\PC7\share\Maps", @"\\<pc>\share\Maps")]
        public void The_user_and_pc_names_are_replaced_only_as_whole_path_segments(string text, string hidden) =>
            Assert.Equal(hidden, Hide(text));

        [Theory]
        [InlineData("Windows 10.0.26300.0 · 64-bit")]
        [InlineData("Time:         2026-11-05 21:04:13")]
        [InlineData("Client 102725_QFE1 · anti-cheat on · launcher 0.5")]
        [InlineData("Driver 31.0.21921.1000, NAT aa:bb:cc:dd:ee:ff")]
        public void Versions_times_and_other_numbers_stay(string text) => Assert.Equal(text, Hide(text));
    }
}
