using System.Windows;
using System.Windows.Input;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;

namespace WhisperTyper
{
    public partial class HotkeyRecorderDialog : Window
    {
        public HotkeyConfig? Result { get; private set; }

        public HotkeyRecorderDialog(HotkeyConfig current)
        {
            InitializeComponent();
            TxtCaptured.Text = current.Label;
            Result = current;
            BtnOk.IsEnabled = true;
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;

            if (key == Key.Escape) { e.Handled = true; DialogResult = false; Close(); return; }
            if (key == Key.Enter)  { e.Handled = true; if (BtnOk.IsEnabled) { DialogResult = true; Close(); } return; }
            if (key == Key.Tab)    { return; }

            e.Handled = true;
            RecordKey(key, e.KeyboardDevice.Modifiers);
        }

        private void RecordKey(Key key, ModifierKeys mods)
        {
            int vkCode = KeyInterop.VirtualKeyFromKey(key);
            string label;
            int modifierCode = 0;

            bool keyIsModifier = key is Key.LeftCtrl or Key.RightCtrl
                                     or Key.LeftAlt  or Key.RightAlt
                                     or Key.LeftShift or Key.RightShift
                                     or Key.LWin     or Key.RWin;

            if (keyIsModifier)
            {
                // Modifier pressed alone — it IS the primary hotkey (e.g. "Left Alt", "Left Ctrl")
                label = HotkeyConfig.GetKeyLabel(key);
                modifierCode = 0;
            }
            else
            {
                // Non-modifier key — check which modifiers are currently held
                string modLabel = "";
                if (mods.HasFlag(ModifierKeys.Control)) { modifierCode = 0x11; modLabel = "Ctrl + "; }
                else if (mods.HasFlag(ModifierKeys.Alt)) { modifierCode = 0x12; modLabel = "Alt + "; }

                label = modLabel + HotkeyConfig.GetKeyLabel(key);
            }

            // F1–F24 don't need to be swallowed (they don't have side effects from passing through)
            bool swallow = !(vkCode >= 0x70 && vkCode <= 0x87);

            Result = new HotkeyConfig { Label = label, VirtualCode = vkCode, ModifierCode = modifierCode, Swallow = swallow };
            TxtCaptured.Text = label;
            BtnOk.IsEnabled = true;
        }

        private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            int vkCode;
            string label;

            switch (e.ChangedButton)
            {
                case MouseButton.Middle:   vkCode = 0x04; label = "Middle Mouse Button"; break;
                case MouseButton.XButton1: vkCode = 0x05; label = "Mouse Button 4"; break;
                case MouseButton.XButton2: vkCode = 0x06; label = "Mouse Button 5"; break;
                default: return;
            }

            e.Handled = true;
            Result = new HotkeyConfig { Label = label, VirtualCode = vkCode, ModifierCode = 0, Swallow = true };
            TxtCaptured.Text = label;
            BtnOk.IsEnabled = true;
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
