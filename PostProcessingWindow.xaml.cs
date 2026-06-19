using System;
using System.Windows;
using System.Windows.Controls;

namespace WhisperTyper
{
    public partial class PostProcessingWindow : Window
    {
        public PostProcessingSettings Settings { get; private set; }

        public PostProcessingWindow(PostProcessingSettings? currentSettings)
        {
            InitializeComponent();
            Settings = currentSettings ?? new PostProcessingSettings();
            LoadSettings();
        }

        private void LoadSettings()
        {
            ChkEnabled.IsChecked = Settings.Enabled;
            ComboProvider.SelectedIndex = (int)Settings.Provider;
            TxtEndpoint.Text = Settings.Endpoint;
            TxtModel.Text = string.IsNullOrEmpty(Settings.Model) ? "Default" : Settings.Model;
            TxtPrompt.Text = Settings.Prompt;
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            Settings.Enabled = ChkEnabled.IsChecked == true;
            Settings.Provider = (LlmProvider)ComboProvider.SelectedIndex;
            Settings.Endpoint = TxtEndpoint.Text;
            Settings.Model = TxtModel.Text == "Default" ? "" : TxtModel.Text;
            Settings.Prompt = TxtPrompt.Text;
            
            DialogResult = true;
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void ComboProvider_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (TxtEndpoint == null) return;

            if (ComboProvider.SelectedIndex == 1) // Ollama
            {
                if (string.IsNullOrEmpty(TxtEndpoint.Text) || TxtEndpoint.Text.Contains("/v1/chat/completions"))
                    TxtEndpoint.Text = "http://localhost:11434/api/generate";
            }
            else if (ComboProvider.SelectedIndex == 2) // LM Studio
            {
                if (string.IsNullOrEmpty(TxtEndpoint.Text) || TxtEndpoint.Text.Contains("/api/generate"))
                    TxtEndpoint.Text = "http://localhost:1234/v1/chat/completions";
            }
        }
    }
}
