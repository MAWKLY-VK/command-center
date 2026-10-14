using System.IO;
using System.Text;

namespace CommandCenter.Services
{
    // The game's string table (generals.csf). Button hotkeys are the letter after '&' in a label's text.
    public sealed class CsfFile
    {
        private sealed class Label
        {
            public required string Name;
            public List<(bool Wide, string Text, byte[] Extra)> Strings = new();
        }

        private byte[] _header = Array.Empty<byte>();
        private readonly List<Label> _labels = new();
        private readonly Dictionary<string, Label> _byName = new(StringComparer.OrdinalIgnoreCase);

        public static CsfFile Load(byte[] data)
        {
            var csf = new CsfFile();
            using var reader = new BinaryReader(new MemoryStream(data));

            csf._header = reader.ReadBytes(24);
            int labelCount = BitConverter.ToInt32(csf._header, 8);

            for (int i = 0; i < labelCount; i++)
            {
                reader.ReadBytes(4); // " LBL"
                int stringCount = reader.ReadInt32();
                int nameLength = reader.ReadInt32();
                var label = new Label { Name = Encoding.ASCII.GetString(reader.ReadBytes(nameLength)) };

                for (int s = 0; s < stringCount; s++)
                {
                    bool wide = Encoding.ASCII.GetString(reader.ReadBytes(4)) == "WRTS";
                    int length = reader.ReadInt32();
                    byte[] text = reader.ReadBytes(length * 2);
                    for (int k = 0; k < text.Length; k++)
                        text[k] = (byte)~text[k];
                    byte[] extra = wide ? reader.ReadBytes(reader.ReadInt32()) : Array.Empty<byte>();
                    label.Strings.Add((wide, Encoding.Unicode.GetString(text), extra));
                }

                csf._labels.Add(label);
                csf._byName.TryAdd(label.Name, label);
            }

            return csf;
        }

        public string? Get(string name) =>
            _byName.TryGetValue(name, out var label) && label.Strings.Count > 0 ? label.Strings[0].Text : null;

        public void Set(string name, string text)
        {
            if (_byName.TryGetValue(name, out var label) && label.Strings.Count > 0)
                label.Strings[0] = (label.Strings[0].Wide, text, label.Strings[0].Extra);
        }

        public bool Contains(string name) => _byName.ContainsKey(name);

        public IEnumerable<string> Labels => _labels.Select(l => l.Name);

        // A label the table does not have yet, at the end (the game sorts the labels itself when it loads them)
        public void Add(string name, string text)
        {
            if (_byName.ContainsKey(name))
            {
                Set(name, text);
                return;
            }
            var label = new Label { Name = name };
            label.Strings.Add((false, text, Array.Empty<byte>()));
            _labels.Add(label);
            _byName[name] = label;
        }

        public bool Remove(string name)
        {
            if (!_byName.Remove(name, out var label))
                return false;
            _labels.Remove(label);
            return true;
        }

        // The label's text with the given button key: the original when that is its own key, else the marker moved
        // (or added as " (&X)"), or taken away for no key
        public static string WithKey(string original, char key) =>
            key == HotkeyOf(original) ? original
            : key == '\0' ? WithoutHotkey(original)
            : WithHotkey(original, key);

        public byte[] ToBytes()
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            // The header holds the number of labels and of strings; both change when labels are added or removed
            byte[] header = (byte[])_header.Clone();
            BitConverter.GetBytes(_labels.Count).CopyTo(header, 8);
            BitConverter.GetBytes(_labels.Sum(l => l.Strings.Count)).CopyTo(header, 12);
            writer.Write(header);

            foreach (var label in _labels)
            {
                writer.Write(Encoding.ASCII.GetBytes(" LBL"));
                writer.Write(label.Strings.Count);
                writer.Write(label.Name.Length);
                writer.Write(Encoding.ASCII.GetBytes(label.Name));

                foreach (var (wide, text, extra) in label.Strings)
                {
                    writer.Write(Encoding.ASCII.GetBytes(wide ? "WRTS" : " RTS"));
                    writer.Write(text.Length);
                    byte[] bytes = Encoding.Unicode.GetBytes(text);
                    for (int k = 0; k < bytes.Length; k++)
                        bytes[k] = (byte)~bytes[k];
                    writer.Write(bytes);
                    if (wide)
                    {
                        writer.Write(extra.Length);
                        writer.Write(extra);
                    }
                }
            }

            writer.Flush();
            return stream.ToArray();
        }

        // "Hum&vee" -> 'V'. Returns '\0' when the text has no hotkey marker.
        public static char HotkeyOf(string? text)
        {
            if (text == null)
                return '\0';
            for (int i = 0; i < text.Length - 1; i++)
            {
                if (text[i] != '&')
                    continue;
                if (text[i + 1] == '&')
                {
                    i++;
                    continue;
                }
                return char.ToUpperInvariant(text[i + 1]);
            }
            return '\0';
        }

        // The label without any hotkey marker ("&&" stays, it is a literal ampersand)
        public static string WithoutHotkey(string originalText)
        {
            var plain = new StringBuilder(originalText.Length);
            for (int i = 0; i < originalText.Length; i++)
            {
                if (originalText[i] == '&' && i + 1 < originalText.Length && originalText[i + 1] == '&')
                {
                    plain.Append("&&");
                    i++;
                }
                else if (originalText[i] != '&')
                {
                    plain.Append(originalText[i]);
                }
            }
            return plain.ToString();
        }

        // Builds the label from the game's original text: the marker moves to the first matching
        // letter, or " (&X)" is appended when the name has no such letter.
        public static string WithHotkey(string originalText, char key)
        {
            var plain = new StringBuilder(originalText.Length);
            for (int i = 0; i < originalText.Length; i++)
            {
                if (originalText[i] == '&' && i + 1 < originalText.Length && originalText[i + 1] == '&')
                {
                    plain.Append("&&");
                    i++;
                }
                else if (originalText[i] != '&')
                {
                    plain.Append(originalText[i]);
                }
            }

            string text = plain.ToString();
            int index = text.IndexOf(key.ToString(), StringComparison.OrdinalIgnoreCase);
            return index >= 0 ? text.Insert(index, "&") : $"{text} (&{char.ToUpperInvariant(key)})";
        }
    }
}
