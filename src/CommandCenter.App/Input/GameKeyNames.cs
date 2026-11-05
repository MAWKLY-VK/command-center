using System.Windows.Input;

namespace CommandCenter.Input
{
    // The game's KEY_ name for a key pressed in the hotkey editor
    public static class GameKeyNames
    {
        // Null when the game has no such key
        public static string? Of(Key key)
        {
            if (key >= Key.A && key <= Key.Z)
                return "KEY_" + key;
            if (key >= Key.D0 && key <= Key.D9)
                return "KEY_" + (key - Key.D0);
            if (key >= Key.F1 && key <= Key.F12)
                return "KEY_" + key;
            if (key >= Key.NumPad0 && key <= Key.NumPad9)
                return "KEY_KP" + (key - Key.NumPad0);
            return key switch
            {
                Key.Space => "KEY_SPACE",
                Key.Tab => "KEY_TAB",
                Key.Enter => "KEY_ENTER",
                Key.Back => "KEY_BACKSPACE",
                Key.Delete => "KEY_DEL",
                Key.Insert => "KEY_INS",
                Key.Home => "KEY_HOME",
                Key.End => "KEY_END",
                Key.PageUp => "KEY_PGUP",
                Key.PageDown => "KEY_PGDN",
                Key.Up => "KEY_UP",
                Key.Down => "KEY_DOWN",
                Key.Left => "KEY_LEFT",
                Key.Right => "KEY_RIGHT",
                Key.OemMinus => "KEY_MINUS",
                Key.OemPlus => "KEY_EQUAL",
                Key.OemOpenBrackets => "KEY_LBRACKET",
                Key.OemCloseBrackets => "KEY_RBRACKET",
                Key.OemSemicolon => "KEY_SEMICOLON",
                Key.OemQuotes => "KEY_APOSTROPHE",
                Key.OemTilde => "KEY_TICK",
                Key.OemBackslash or Key.Oem5 => "KEY_BACKSLASH",
                Key.OemComma => "KEY_COMMA",
                Key.OemPeriod => "KEY_PERIOD",
                Key.OemQuestion => "KEY_SLASH",
                Key.Add => "KEY_KPPLUS",
                Key.Subtract => "KEY_KPMINUS",
                _ => null,
            };
        }
    }
}
