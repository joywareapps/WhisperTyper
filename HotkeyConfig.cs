using System.Windows.Input;

namespace WhisperTyper
{
    public class HotkeyConfig
    {
        public string Label { get; set; } = "Caps Lock";
        public int VirtualCode { get; set; } = 0x14;
        public int ModifierCode { get; set; } = 0;
        public bool Swallow { get; set; } = true;

        public static HotkeyConfig Default => new();

        public static string GetKeyLabel(Key key) => key switch
        {
            Key.CapsLock    => "Caps Lock",
            Key.Scroll      => "Scroll Lock",
            Key.NumLock     => "Num Lock",
            Key.LeftAlt     => "Left Alt",
            Key.RightAlt    => "Right Alt",
            Key.LeftCtrl    => "Left Ctrl",
            Key.RightCtrl   => "Right Ctrl",
            Key.LeftShift   => "Left Shift",
            Key.RightShift  => "Right Shift",
            Key.LWin        => "Left Win",
            Key.RWin        => "Right Win",
            Key.Pause       => "Pause",
            Key.Insert      => "Insert",
            Key.Delete      => "Delete",
            Key.Home        => "Home",
            Key.End         => "End",
            Key.PageUp      => "Page Up",
            Key.PageDown    => "Page Down",
            Key.Space       => "Space",
            Key.Back        => "Backspace",
            Key.PrintScreen => "Print Screen",
            Key.OemTilde    => "`",
            Key.OemMinus    => "-",
            Key.OemPlus     => "=",
            Key.OemOpenBrackets => "[",
            Key.OemCloseBrackets => "]",
            Key.OemPipe     => "\\",
            Key.OemSemicolon => ";",
            Key.OemQuotes   => "'",
            Key.OemComma    => ",",
            Key.OemPeriod   => ".",
            Key.OemQuestion => "/",
            _ => key.ToString()
        };
    }
}
