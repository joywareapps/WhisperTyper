# Implementation Plan — Dual Distribution (Installer & Portable) + Core settings

We will set up a automated dual distribution pipeline on GitHub Actions to generate both:
1. **`WhisperTyperSetup.exe`** — A standard Inno Setup installer that supports a Setup Wizard, desktop shortcuts, and auto-registration on startup (fully silent-run compatible for `winget` installation).
2. **`WhisperTyper-portable-win-x64.zip`** — A standalone ZIP containing the self-contained app files (portable mode).

To make these options fully functional, we will also implement two missing table-stakes features inside the WPF application:
* **Start with Windows** (via a registry run key, ensuring both the installer and portable version can run on login).
* **Always Copy to Clipboard** (auto-copying final transcriptions).

---

## Proposed Changes

We will modify or create the following files:

### ⚙️ WPF Application Updates

#### [MODIFY] [MainWindow.xaml](file:///c:/Source/Repos/WhisperTyper/MainWindow.xaml)
* Add a row with two new CheckBoxes:
  * `ChkStartup` — "Start with Windows"
  * `ChkCopyToClipboard` — "Always Copy to Clipboard"
* Position these right below the instructions box and above the Filler Words expander.

#### [MODIFY] [MainWindow.xaml.cs](file:///c:/Source/Repos/WhisperTyper/MainWindow.xaml.cs)
* Update `AppSettings` record to store:
  * `StartWithWindows` (bool)
  * `AlwaysCopyToClipboard` (bool)
* Wire up `ChkStartup_Checked` and `ChkCopyToClipboard_Checked` events.
* Implement startup shortcut registry modifications:
  * Write path to `Software\Microsoft\Windows\CurrentVersion\Run` on check.
  * Delete path on uncheck.
* Wire up `AlwaysCopyToClipboard` settings check inside `OnTranscriptionCompleted` to set the clipboard contents.

---

### 📦 Installer Packaging

#### [NEW] [WhisperTyper.iss](file:///c:/Source/Repos/WhisperTyper/WhisperTyper.iss)
* Create the Inno Setup script config file in the repository root.
* Configure it for:
  * `PrivilegesRequired=lowest` (User-level installation inside Local AppData, avoiding administrator requirements).
  * Desktop and Startup shortcuts optional tasks.
  * Multi-directory scanning of published DLLs.

---

### 🤖 GitHub Actions Workflow Updates

#### [MODIFY] [.github/workflows/release.yml](file:///c:/Source/Repos/WhisperTyper/.github/workflows/release.yml)
* Modify the existing build-and-release workflow.
* Update publish steps:
  1. Build self-contained app (`--self-contained true` to `./publish`).
  2. Zip files in `./publish` as `WhisperTyper-portable-v*.zip` (Portable).
  3. Compile `WhisperTyper.iss` using `Minionguyjpro/Inno-Setup-Action@v1.2.2` (Installer).
  4. Upload both the Setup executable and the Portable ZIP to the GitHub Release.

---

## Detailed Registry Implementation (C#)

We will use the following helper class or methods to handle startup keys:

```csharp
using Microsoft.Win32;

public static class StartupManager
{
    private const string RegistryKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "WhisperTyper";

    public static bool IsStartupEnabled()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, false);
        return key?.GetValue(AppName) != null;
    }

    public static void SetStartup(bool enable)
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, true);
        if (key == null) return;
        if (enable)
        {
            string exePath = Environment.ProcessPath ?? System.Reflection.Assembly.GetExecutingAssembly().Location;
            key.SetValue(AppName, $"\"{exePath}\"");
        }
        else
        {
            key.DeleteValue(AppName, false);
        }
    }
}
```

---

## Verification Plan

### Automated Tests
* We will verify the build compiles locally using `dotnet build`.
* We will verify the code lints and is syntax-error free.

### Manual Verification
* Run the built application locally and toggle the "Start with Windows" CheckBox. Check the Windows registry (`regedit` at `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`) to verify the key `WhisperTyper` is created and points to the correct executable.
* Toggle the "Always Copy to Clipboard" CheckBox, run a test transcription, and verify the text is in the clipboard.
* Verify the generated Inno Setup `.iss` file syntax.
