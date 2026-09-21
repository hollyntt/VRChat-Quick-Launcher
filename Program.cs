using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using ImGuiNET;
using Raylib_cs;
using rlImGui_cs;
using Valve.VR;

namespace VRCQuickLauncher
{
    // A single launchable "slot". Each profile maps 1:1 to VRChat's own
    // --profile=X launch flag, which is what actually gives each instance
    // its own isolated credentials/settings so multiple accounts can run
    // side by side. See: https://wiki.vrchat.com/wiki/Launch_Options
    public class LaunchProfile
    {
        public bool Selected { get; set; }
        public int Number { get; set; }
        public string Description { get; set; } = "";
        public bool VR { get; set; }
    }

    public enum InstanceMode { None, Create, Join }

    // The four theme presets offered in the Themes menu.
    public enum AppTheme { Light, ColourfulLight, Dark, ColourfulDark }

    // Everything that gets persisted to disk between runs.
    public class LauncherConfig
    {
        public string VRChatPath { get; set; } = "";

        // Launch options
        public bool DebugGui { get; set; }
        public bool SdkLog { get; set; }
        public bool UdonLog { get; set; }
        public bool WatchWorlds { get; set; }
        public bool WatchAvatars { get; set; }
        public string MidiDevice { get; set; } = "";
        public string OscConfig { get; set; } = "";
        public string MaxFps { get; set; } = "";
        public bool CustomParametersEnabled { get; set; }
        public string CustomParameters { get; set; } = "";

        // Instance info
        public InstanceMode InstanceMode { get; set; } = InstanceMode.None;
        public string InstanceWorldId { get; set; } = "";
        public string InstanceOwnerId { get; set; } = "";
        public string InstanceJoinLink { get; set; } = "";
        public int InstanceAccessIndex { get; set; }
        public int InstanceRegionIndex { get; set; }

        public bool AutoLayout { get; set; }
        public bool AutoClose { get; set; }
        public bool SaveOnExit { get; set; } = true;
        public string CpuAffinity { get; set; } = "";
        public AppTheme Theme { get; set; } = AppTheme.ColourfulDark;
        public List<LaunchProfile> Profiles { get; set; } = new();
    }

    class Program
    {
        [DllImport("kernel32.dll")]
        private static extern bool FreeConsole();

        private const string AppTitle = "VRC Quick Launcher";
        private const string ProcessName = "VRChat";
        private const string ConfigFileName = "vrcql_config.json";
        private const string AppVersion = "1.6.2";
        
        private const string GithubRepoUrl = "https://github.com/hollyntt/VRChat-Quick-Launcher";
        private const string DocsUrl = "https://github.com/hollyntt/VRChat-Quick-Launcher/wiki";
        private const string LaunchOptionsWikiUrl = "https://wiki.vrchat.com/wiki/Launch_Options";

        private static string _appDir = "";
        private static string _configPath = "";
        private static string _status = "Ready.";
        private static bool _isError = false;
        private static bool _wasRunning = false;
        private static bool _shouldExit = false;
        private static float _animTime = 0f;

        private static Vector4 _accent = new Vector4(0.16f, 0.55f, 1.00f, 1f); // VRChat-ish blue
        private static Vector4 Danger = new Vector4(1.00f, 0.30f, 0.35f, 1f);
        private static Vector4 Good = new Vector4(0.45f, 0.95f, 0.55f, 1f);

        // Theme-derived colours, (re)computed in ApplyStyle and read every frame.
        private static Vector4 _windowBg = new Vector4(0.08f, 0.08f, 0.10f, 1f);
        private static Vector4 _titleBg = new Vector4(0.15f, 0.15f, 0.22f, 1f);
        private static Vector4 _titleBgActive = new Vector4(0.20f, 0.20f, 0.30f, 1f);
        private static bool _isDarkTheme = true;

        private static LauncherConfig _config = new();

        private static readonly string[] AccessLevels = { "Public", "Friends+", "Friends", "Invite+", "Invite" };
        private static readonly string[] Regions = { "US West", "US East", "Europe", "Japan" };

        static void Main()
        {
            _appDir = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule?.FileName ?? "") ?? Directory.GetCurrentDirectory();
            _configPath = Path.Combine(_appDir, ConfigFileName);

            HideConsoleInRelease();
            LoadConfig();
            RegisterVrManifest();

            Raylib.InitWindow(760, 660, AppTitle);
            Raylib.SetTargetFPS(60);
            rlImGui.Setup(true);

            try
            {
                string iconPath = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "icon.png"
                );
            
                if (File.Exists(iconPath))
                {
                    Image img = Raylib.LoadImage(iconPath);
                    Raylib.SetWindowIcon(img);
                    Raylib.UnloadImage(img);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to load icon: {ex.Message}");
            }
            
            ApplyStyle();

            while (!Raylib.WindowShouldClose() && !_shouldExit)
            {
                _animTime += Raylib.GetFrameTime();

                bool isRunning = Process.GetProcessesByName(ProcessName).Length > 0;
                if (isRunning != _wasRunning)
                {
                    if (isRunning) Raylib.MinimizeWindow();
                    else Raylib.RestoreWindow();
                    _wasRunning = isRunning;
                }

                Raylib.BeginDrawing();
                Raylib.ClearBackground(new Color(
                    (int)(_windowBg.X * 255), (int)(_windowBg.Y * 255), (int)(_windowBg.Z * 255), 255));
                rlImGui.Begin();
                DrawUI(isRunning);
                rlImGui.End();
                Raylib.EndDrawing();
            }

            if (_config.SaveOnExit) SaveConfig();
            rlImGui.Shutdown();
            Raylib.CloseWindow();
        }

        [Conditional("RELEASE")]
        private static void HideConsoleInRelease() => FreeConsole();

        // ---------------------------------------------------------------
        // Config persistence
        // ---------------------------------------------------------------

        private static void LoadConfig()
        {
            LoadConfigFrom(_configPath);

            if (string.IsNullOrWhiteSpace(_config.OscConfig))
                _config.OscConfig = "9000:localhost:9001";

            if (_config.Profiles.Count == 0)
                _config.Profiles.Add(new LaunchProfile { Number = 0, Description = "Main" });
        }

        // Loads a config from an arbitrary path (used by both startup and File > Open).
        // Falls back to the current in-memory config on any failure so a bad/missing
        // file never crashes the app or wipes unsaved work.
        // NOTE: does NOT touch ImGui here — this can run before the ImGui context
        // exists (startup), so style application is the caller's responsibility.
        private static bool LoadConfigFrom(string path)
        {
            try
            {
                if (!File.Exists(path)) return false;
                var loaded = JsonSerializer.Deserialize<LauncherConfig>(File.ReadAllText(path));
                if (loaded == null) return false;

                _config = loaded;
                return true;
            }
            catch
            {
                _status = "Failed to load config — file may be corrupt or invalid.";
                _isError = true;
                return false;
            }
        }

        private static void SaveConfig()
        {
            SaveConfigTo(_configPath);
        }

        private static void SaveConfigTo(string path)
        {
            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                File.WriteAllText(path, JsonSerializer.Serialize(_config, options));
                _configPath = path;
                _status = "Saved.";
                _isError = false;
            }
            catch (Exception ex)
            {
                _status = $"Failed to save config: {ex.Message}";
                _isError = true;
            }
        }

        // ---------------------------------------------------------------
        // VR manifest (lets SteamVR list/launch this tool as an overlay app)
        // ---------------------------------------------------------------

        private static void RegisterVrManifest()
        {
            const string appKey = "vrcql.launcher.opensource";
            string manifestPath = Path.Combine(_appDir, "vrcql.vrmanifest");
            string exePath = Process.GetCurrentProcess().MainModule?.FileName ?? "";

            try
            {
                var error = EVRInitError.None;
                OpenVR.Init(ref error, EVRApplicationType.VRApplication_Utility);
                if (error == EVRInitError.None)
                {
                    if (!OpenVR.Applications.IsApplicationInstalled(appKey))
                    {
                        string json = $@"{{ ""source"": ""builtin"", ""applications"": [ {{ ""app_key"": ""{appKey}"", ""launch_type"": ""binary"", ""binary_path_windows"": ""{exePath.Replace("\\", "\\\\")}"", ""working_directory"": ""{_appDir.Replace("\\", "\\\\")}"", ""strings"": {{ ""en_us"": {{ ""name"": ""{AppTitle}"" }} }} }} ] }}";
                        File.WriteAllText(manifestPath, json);
                        OpenVR.Applications.AddApplicationManifest(manifestPath, false);
                    }
                    OpenVR.Shutdown();
                }
            }
            catch { /* SteamVR not running/installed — not required for this tool to work */ }
        }

        // ---------------------------------------------------------------
        // Icon + style
        // ---------------------------------------------------------------

        private static Vector4 WithAlpha(Vector4 c, float a) => new Vector4(c.X, c.Y, c.Z, a);

        private static Vector4 Mix(Vector4 a, Vector4 b, float t) => new Vector4(
            a.X + (b.X - a.X) * t,
            a.Y + (b.Y - a.Y) * t,
            a.Z + (b.Z - a.Z) * t,
            1f);

        private static void ApplyStyle()
        {
            bool dark = _config.Theme == AppTheme.Dark || _config.Theme == AppTheme.ColourfulDark;
            bool colourful = _config.Theme == AppTheme.ColourfulLight || _config.Theme == AppTheme.ColourfulDark;
            _isDarkTheme = dark;

            // Start from ImGui's base palette, then override every accent-driven
            // colour below. (ImGui's defaults are blue, so anything we don't
            // override would look the same in every theme.)
            if (dark) ImGui.StyleColorsDark();
            else ImGui.StyleColorsLight();

            // Colourful variants get a vivid accent; plain variants get a neutral grey.
            _accent = colourful
                ? (dark ? new Vector4(0.90f, 0.20f, 0.30f, 1f) : new Vector4(0.16f, 0.55f, 1.00f, 1f))
                : (dark ? new Vector4(0.60f, 0.60f, 0.66f, 1f) : new Vector4(0.42f, 0.42f, 0.48f, 1f));

            Vector4 accentHi = Mix(_accent, new Vector4(1f, 1f, 1f, 1f), 0.25f);   // pressed / active
            Vector4 accentLo = Mix(_accent, new Vector4(0f, 0f, 0f, 1f), 0.35f);   // resting fills

            _windowBg = dark ? new Vector4(0.08f, 0.08f, 0.10f, 1f) : new Vector4(0.94f, 0.94f, 0.96f, 1f);
            Vector4 frameBg = dark ? new Vector4(0.14f, 0.14f, 0.18f, 1f) : new Vector4(0.86f, 0.86f, 0.90f, 1f);

            // Title bar (UpdateTitleBarStyle reads these when VRChat isn't running)
            if (colourful)
            {
                _titleBg = Mix(_windowBg, _accent, 0.35f);
                _titleBgActive = Mix(_windowBg, _accent, dark ? 0.60f : 0.55f);
            }
            else
            {
                _titleBg = dark ? new Vector4(0.15f, 0.15f, 0.18f, 1f) : new Vector4(0.82f, 0.82f, 0.85f, 1f);
                _titleBgActive = dark ? new Vector4(0.20f, 0.20f, 0.25f, 1f) : new Vector4(0.75f, 0.75f, 0.80f, 1f);
            }

            // Status text needs to stay readable against the theme background.
            Good = dark ? new Vector4(0.45f, 0.95f, 0.55f, 1f) : new Vector4(0.05f, 0.55f, 0.15f, 1f);
            Danger = dark ? new Vector4(1.00f, 0.30f, 0.35f, 1f) : new Vector4(0.80f, 0.10f, 0.15f, 1f);

            var style = ImGui.GetStyle();
            style.WindowRounding = 0f;
            style.ChildRounding = 0f;
            style.FrameRounding = 3f;

            var c = style.Colors;
            c[(int)ImGuiCol.WindowBg] = _windowBg;
            c[(int)ImGuiCol.PopupBg] = Mix(_windowBg, dark ? new Vector4(0f, 0f, 0f, 1f) : new Vector4(1f, 1f, 1f, 1f), 0.15f);
            c[(int)ImGuiCol.MenuBarBg] = frameBg;

            c[(int)ImGuiCol.FrameBg] = frameBg;
            c[(int)ImGuiCol.FrameBgHovered] = WithAlpha(Mix(frameBg, _accent, 0.35f), 1f);
            c[(int)ImGuiCol.FrameBgActive] = WithAlpha(Mix(frameBg, _accent, 0.55f), 1f);

            c[(int)ImGuiCol.Button] = accentLo;
            c[(int)ImGuiCol.ButtonHovered] = _accent;
            c[(int)ImGuiCol.ButtonActive] = accentHi;

            c[(int)ImGuiCol.Header] = WithAlpha(_accent, 0.40f);
            c[(int)ImGuiCol.HeaderHovered] = WithAlpha(_accent, 0.80f);
            c[(int)ImGuiCol.HeaderActive] = _accent;

            c[(int)ImGuiCol.CheckMark] = _accent;
            c[(int)ImGuiCol.SliderGrab] = _accent;
            c[(int)ImGuiCol.SliderGrabActive] = accentHi;

            c[(int)ImGuiCol.ScrollbarGrab] = accentLo;
            c[(int)ImGuiCol.ScrollbarGrabHovered] = _accent;
            c[(int)ImGuiCol.ScrollbarGrabActive] = accentHi;

            c[(int)ImGuiCol.Separator] = _accent;
            c[(int)ImGuiCol.SeparatorHovered] = accentHi;
            c[(int)ImGuiCol.SeparatorActive] = accentHi;

            c[(int)ImGuiCol.ResizeGrip] = WithAlpha(_accent, 0.25f);
            c[(int)ImGuiCol.ResizeGripHovered] = WithAlpha(_accent, 0.67f);
            c[(int)ImGuiCol.ResizeGripActive] = WithAlpha(_accent, 0.95f);

            c[(int)ImGuiCol.TextSelectedBg] = WithAlpha(_accent, 0.35f);
            c[(int)ImGuiCol.NavCursor] = _accent;
            c[(int)ImGuiCol.PlotHistogram] = _accent;
            c[(int)ImGuiCol.PlotHistogramHovered] = accentHi;

            c[(int)ImGuiCol.TitleBg] = _titleBg;
            c[(int)ImGuiCol.TitleBgActive] = _titleBgActive;
            c[(int)ImGuiCol.TitleBgCollapsed] = _titleBg;
        }

        private static void UpdateTitleBarStyle(bool isRunning)
        {
            var colors = ImGui.GetStyle().Colors;
            float pulse = (float)(0.5f + 0.5f * Math.Sin(_animTime * 3));
            if (isRunning)
            {
                // Green "VRChat is running" pulse; lighter greens on light themes so the title text stays readable.
                if (_isDarkTheme)
                {
                    colors[(int)ImGuiCol.TitleBg] = new Vector4(0.1f + pulse * 0.1f, 0.4f + pulse * 0.1f, 0.1f, 1f);
                    colors[(int)ImGuiCol.TitleBgActive] = new Vector4(0.2f + pulse * 0.2f, 0.6f + pulse * 0.2f, 0.2f, 1f);
                }
                else
                {
                    colors[(int)ImGuiCol.TitleBg] = new Vector4(0.60f + pulse * 0.05f, 0.85f, 0.60f, 1f);
                    colors[(int)ImGuiCol.TitleBgActive] = new Vector4(0.45f + pulse * 0.10f, 0.80f + pulse * 0.10f, 0.45f, 1f);
                }
            }
            else
            {
                // Idle: use the active theme's title colours instead of hardcoded ones.
                colors[(int)ImGuiCol.TitleBg] = _titleBg;
                colors[(int)ImGuiCol.TitleBgActive] = _titleBgActive;
            }
        }

        // ---------------------------------------------------------------
        // UI
        // ---------------------------------------------------------------

        private static void DrawUI(bool isRunning)
        {
            ImGui.SetNextWindowPos(Vector2.Zero);
            ImGui.SetNextWindowSize(new Vector2(Raylib.GetScreenWidth(), Raylib.GetScreenHeight()));

            UpdateTitleBarStyle(isRunning);

            ImGui.Begin(AppTitle, ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.MenuBar);

            DrawMenuBar();
            DrawPathRow();
            ImGui.Dummy(new Vector2(0, 8));
            DrawLaunchOptions();
            ImGui.Dummy(new Vector2(0, 8));
            DrawInstanceInfo();
            ImGui.Dummy(new Vector2(0, 8));
            DrawProfiles();
            ImGui.Dummy(new Vector2(0, 8));
            DrawFooter(isRunning);

            ImGui.End();
        }

        private static void DrawMenuBar()
        {
            if (!ImGui.BeginMenuBar()) return;

            if (ImGui.BeginMenu("File"))
            {
                if (ImGui.MenuItem("Open...")) OpenConfigDialog();
                if (ImGui.MenuItem("Save")) SaveConfig();
                if (ImGui.MenuItem("Save As...")) SaveConfigAsDialog();

                ImGui.Separator();

                bool saveOnExit = _config.SaveOnExit;
                if (ImGui.MenuItem("Save on Exit", "", saveOnExit)) _config.SaveOnExit = !saveOnExit;

                if (ImGui.MenuItem("Open Save Location")) OpenSaveLocation();

                ImGui.Separator();

                if (ImGui.MenuItem("Exit")) _shouldExit = true;
                ImGui.EndMenu();
            }

            if (ImGui.BeginMenu("Edit"))
            {
                ImGui.TextDisabled("CPU affinity");
                ImGui.SameLine();
                ImGui.SetNextItemWidth(120);
                string cpuAffinity = _config.CpuAffinity;
                if (ImGui.InputTextWithHint("##cpuaffinity", "e.g. 0,1,2,3", ref cpuAffinity, 64)) _config.CpuAffinity = cpuAffinity;

                bool autoLayout = _config.AutoLayout;
                if (ImGui.MenuItem("Auto-layout", "", autoLayout)) _config.AutoLayout = !autoLayout;

                bool autoClose = _config.AutoClose;
                if (ImGui.MenuItem("Auto-close", "", autoClose)) _config.AutoClose = !autoClose;

                ImGui.Separator();

                if (ImGui.MenuItem("Clear Installs Paths")) ClearInstallPaths();
                if (ImGui.MenuItem("Clear Profiles")) ClearProfiles();
                ImGui.EndMenu();
            }

            if (ImGui.BeginMenu("Themes"))
            {
                if (ImGui.MenuItem("Light", "", _config.Theme == AppTheme.Light)) SetTheme(AppTheme.Light);
                if (ImGui.MenuItem("Colourful Light", "", _config.Theme == AppTheme.ColourfulLight)) SetTheme(AppTheme.ColourfulLight);
                if (ImGui.MenuItem("Dark", "", _config.Theme == AppTheme.Dark)) SetTheme(AppTheme.Dark);
                if (ImGui.MenuItem("Colourful Dark", "", _config.Theme == AppTheme.ColourfulDark)) SetTheme(AppTheme.ColourfulDark);
                ImGui.EndMenu();
            }

            if (ImGui.BeginMenu("Help"))
            {
                if (ImGui.MenuItem("View Documentation")) OpenUrl(DocsUrl);
                ImGui.BeginDisabled();
                ImGui.MenuItem($"{AppTitle} v{AppVersion}");
                ImGui.EndDisabled();
                ImGui.EndMenu();
            }

            ImGui.EndMenuBar();
        }

        private static void SetTheme(AppTheme theme)
        {
            _config.Theme = theme;
            ApplyStyle();
        }

        private static void DrawPathRow()
        {
            ImGui.TextDisabled("VRChat");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - 90);
            string path = _config.VRChatPath;
            if (ImGui.InputText("##vrchatpath", ref path, 512)) _config.VRChatPath = path;
            ImGui.SameLine();
            if (ImGui.Button("Browse", new Vector2(80, 0))) BrowseForVRChatExe();
        }

        private static void DrawLaunchOptions()
        {
            ImGui.TextDisabled("Launch options");
            ImGui.SameLine(ImGui.GetContentRegionAvail().X - 30);
            if (ImGui.SmallButton("Clear")) ClearLaunchOptions();

            if (ImGui.CollapsingHeader("Debug", ImGuiTreeNodeFlags.DefaultOpen))
            {
                bool debugGui = _config.DebugGui;
                if (ImGui.Checkbox("Debug GUI", ref debugGui)) _config.DebugGui = debugGui;

                bool sdkLog = _config.SdkLog;
                if (ImGui.Checkbox("SDK log", ref sdkLog)) _config.SdkLog = sdkLog;

                bool udonLog = _config.UdonLog;
                if (ImGui.Checkbox("UDON log", ref udonLog)) _config.UdonLog = udonLog;

                bool watchWorlds = _config.WatchWorlds;
                if (ImGui.Checkbox("Watch worlds", ref watchWorlds)) _config.WatchWorlds = watchWorlds;

                bool watchAvatars = _config.WatchAvatars;
                if (ImGui.Checkbox("Watch avatars", ref watchAvatars)) _config.WatchAvatars = watchAvatars;

                ImGui.Dummy(new Vector2(0, 4));

                float fieldWidth = ImGui.GetContentRegionAvail().X * 0.6f;

                ImGui.TextDisabled("MIDI");
                ImGui.SameLine(90);
                ImGui.SetNextItemWidth(fieldWidth);
                string midi = _config.MidiDevice;
                if (ImGui.InputTextWithHint("##midi", "Device name", ref midi, 128)) _config.MidiDevice = midi;

                ImGui.TextDisabled("OSC");
                ImGui.SameLine(90);
                ImGui.SetNextItemWidth(fieldWidth);
                string osc = _config.OscConfig;
                if (ImGui.InputTextWithHint("##osc", "9000:localhost:9001", ref osc, 128)) _config.OscConfig = osc;

                ImGui.TextDisabled("Max FPS");
                ImGui.SameLine(90);
                ImGui.SetNextItemWidth(fieldWidth);
                string fps = _config.MaxFps;
                if (ImGui.InputTextWithHint("##maxfps", "Default", ref fps, 8)) _config.MaxFps = fps;

                ImGui.Dummy(new Vector2(0, 4));
                bool customEnabled = _config.CustomParametersEnabled;
                if (ImGui.Checkbox("Custom parameters", ref customEnabled)) _config.CustomParametersEnabled = customEnabled;
                ImGui.SameLine();
                ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
                string custom = _config.CustomParameters;
                ImGui.BeginDisabled(!_config.CustomParametersEnabled);
                if (ImGui.InputText("##customparams", ref custom, 512)) _config.CustomParameters = custom;
                ImGui.EndDisabled();
            }
        }

        private static void DrawInstanceInfo()
        {
            ImGui.TextDisabled("Instance info");
            ImGui.SameLine(ImGui.GetContentRegionAvail().X - 30);
            if (ImGui.SmallButton("Clear")) ClearInstanceInfo();

            int mode = (int)_config.InstanceMode;
            if (ImGui.RadioButton("Create", ref mode, (int)InstanceMode.Create)) _config.InstanceMode = InstanceMode.Create;
            ImGui.SameLine();
            if (ImGui.RadioButton("Join", ref mode, (int)InstanceMode.Join)) _config.InstanceMode = InstanceMode.Join;
            ImGui.SameLine();
            if (ImGui.RadioButton("None", ref mode, (int)InstanceMode.None)) _config.InstanceMode = InstanceMode.None;

            if (_config.InstanceMode == InstanceMode.Create)
            {
                ImGui.Indent();
                ImGui.TextDisabled("World ID");
                ImGui.SameLine(110);
                ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
                string worldId = _config.InstanceWorldId;
                if (ImGui.InputTextWithHint("##worldid", "Leave blank for your home world", ref worldId, 128)) _config.InstanceWorldId = worldId;

                ImGui.TextDisabled("Owner ID");
                ImGui.SameLine(110);
                ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
                string ownerId = _config.InstanceOwnerId;
                if (ImGui.InputTextWithHint("##ownerid", "usr_... (needed for Friends/Invite instances)", ref ownerId, 128)) _config.InstanceOwnerId = ownerId;

                int access = _config.InstanceAccessIndex;
                ImGui.TextDisabled("Type");
                ImGui.SameLine(110);
                ImGui.SetNextItemWidth(160);
                if (ImGui.Combo("##access", ref access, AccessLevels, AccessLevels.Length)) _config.InstanceAccessIndex = access;

                int region = _config.InstanceRegionIndex;
                ImGui.SameLine();
                ImGui.SetNextItemWidth(140);
                if (ImGui.Combo("##region", ref region, Regions, Regions.Length)) _config.InstanceRegionIndex = region;
                ImGui.Unindent();
            }
            else if (_config.InstanceMode == InstanceMode.Join)
            {
                ImGui.Indent();
                ImGui.TextDisabled("Instance link");
                ImGui.SameLine(110);
                ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
                string joinLink = _config.InstanceJoinLink;
                if (ImGui.InputTextWithHint("##joinlink", "vrchat://launch?ref=vrchat.com&id=wrld_...:instance", ref joinLink, 512)) _config.InstanceJoinLink = joinLink;
                ImGui.Unindent();
            }
        }

        private static void DrawProfiles()
        {
            ImGui.TextDisabled("Profiles");
            ImGui.SameLine(ImGui.GetContentRegionAvail().X - 190);
            if (ImGui.Button("Add profile")) AddProfile();
            ImGui.SameLine();
            if (ImGui.Button("Remove profile")) RemoveProfile();

            ImGui.Dummy(new Vector2(0, 4));
            ImGui.BeginChild("ProfilesList", new Vector2(0, 170), ImGuiChildFlags.Borders);

            for (int i = 0; i < _config.Profiles.Count; i++)
            {
                var profile = _config.Profiles[i];
                ImGui.PushID(i);

                bool selected = profile.Selected;
                if (ImGui.Checkbox("##selected", ref selected)) profile.Selected = selected;
                ImGui.SameLine();

                if (ImGui.Button("Launch", new Vector2(70, 0))) LaunchProfileProcess(profile);
                ImGui.SameLine();

                ImGui.TextDisabled("Profile");
                ImGui.SameLine();
                ImGui.SetNextItemWidth(50);
                int number = profile.Number;
                if (ImGui.InputInt("##number", ref number, 0, 0)) profile.Number = Math.Max(0, number);
                ImGui.SameLine();

                ImGui.TextDisabled("Desc");
                ImGui.SameLine();
                ImGui.SetNextItemWidth(180);
                string desc = profile.Description;
                if (ImGui.InputText("##desc", ref desc, 64)) profile.Description = desc;
                ImGui.SameLine();

                bool vr = profile.VR;
                if (ImGui.Checkbox("VR", ref vr)) profile.VR = vr;

                ImGui.PopID();
            }

            ImGui.EndChild();
        }

        private static void DrawFooter(bool isRunning)
        {
            bool autoLayout = _config.AutoLayout;
            if (ImGui.Checkbox("Auto-layout", ref autoLayout)) _config.AutoLayout = autoLayout;
            ImGui.SameLine();

            bool anySelected = _config.Profiles.Any(p => p.Selected);
            ImGui.BeginDisabled(!anySelected);
            if (ImGui.Button("Launch all selected", new Vector2(ImGui.GetContentRegionAvail().X, 30)))
            {
                LaunchAllSelected();
            }
            ImGui.EndDisabled();

            // Only surface errors (e.g. bad VRChat path); the "VRChat is running" /
            // "Ready." / "Saved." messages are intentionally not shown.
            if (_isError && !string.IsNullOrEmpty(_status))
            {
                ImGui.Dummy(new Vector2(0, 6));
                ImGui.TextColored(Danger, _status);
            }
        }

        // ---------------------------------------------------------------
        // Actions
        // ---------------------------------------------------------------

        private static void ClearLaunchOptions()
        {
            _config.DebugGui = false;
            _config.SdkLog = false;
            _config.UdonLog = false;
            _config.WatchWorlds = false;
            _config.WatchAvatars = false;
            _config.MidiDevice = "";
            _config.OscConfig = "9000:localhost:9001";
            _config.MaxFps = "";
            _config.CustomParametersEnabled = false;
            _config.CustomParameters = "";
        }

        private static void ClearInstanceInfo()
        {
            _config.InstanceMode = InstanceMode.None;
            _config.InstanceWorldId = "";
            _config.InstanceOwnerId = "";
            _config.InstanceJoinLink = "";
            _config.InstanceAccessIndex = 0;
            _config.InstanceRegionIndex = 0;
        }

        private static void AddProfile()
        {
            int nextNum = _config.Profiles.Count == 0 ? 0 : _config.Profiles.Max(p => p.Number) + 1;
            _config.Profiles.Add(new LaunchProfile { Number = nextNum });
        }

        private static void RemoveProfile()
        {
            var target = _config.Profiles.LastOrDefault(p => p.Selected) ?? _config.Profiles.LastOrDefault();
            if (target != null) _config.Profiles.Remove(target);
        }

        private static void KillAllVRChat()
        {
            foreach (var p in Process.GetProcessesByName(ProcessName))
            {
                try { p.Kill(); p.WaitForExit(); } catch { }
            }
            _status = "Closed all VRChat instances.";
            _isError = false;
        }

        private static void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch { /* ignore */ }
        }

        private static void BrowseForVRChatExe()
        {
#if WINDOWS_BUILD
            var buffer = new string('\0', 260);
            var ofn = new NativeMethods.OPENFILENAME
            {
                lStructSize = Marshal.SizeOf<NativeMethods.OPENFILENAME>(),
                lpstrFilter = "start_protected_game.exe\0start_protected_gamet.exe\0All files\0*.*\0\0",
                lpstrFile = buffer,
                nMaxFile = buffer.Length,
                lpstrTitle = "Locate start_protected_game.exe",
                Flags = 0x00001000 // OFN_FILEMUSTEXIST
            };

            if (NativeMethods.GetOpenFileNameW(ref ofn))
            {
                _config.VRChatPath = ofn.lpstrFile.TrimEnd('\0');
            }
#else
            _status = "Browse dialog is only available in the Windows build — type the path manually.";
#endif
        }

        [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetSaveFileNameW(ref NativeMethods.OPENFILENAME ofn);

        // ---------------------------------------------------------------
        // File menu — open/save a config from an arbitrary location
        // ---------------------------------------------------------------

        private static void OpenConfigDialog()
        {
#if WINDOWS_BUILD
            var buffer = new string('\0', 260);
            var ofn = new NativeMethods.OPENFILENAME
            {
                lStructSize = Marshal.SizeOf<NativeMethods.OPENFILENAME>(),
                lpstrFilter = "VRCQL config (*.json)\0*.json\0All files\0*.*\0\0",
                lpstrFile = buffer,
                nMaxFile = buffer.Length,
                lpstrTitle = "Open config",
                Flags = 0x00001000 // OFN_FILEMUSTEXIST
            };

            if (NativeMethods.GetOpenFileNameW(ref ofn))
            {
                string path = ofn.lpstrFile.TrimEnd('\0');
                if (LoadConfigFrom(path))
                {
                    _configPath = path;
                    ApplyStyle(); // the loaded config may carry a different theme
                    _status = "Loaded config.";
                    _isError = false;
                }
            }
#else
            _status = "Open dialog is only available in the Windows build.";
            _isError = true;
#endif
        }

        private static void SaveConfigAsDialog()
        {
#if WINDOWS_BUILD
            var buffer = new string('\0', 260);
            var ofn = new NativeMethods.OPENFILENAME
            {
                lStructSize = Marshal.SizeOf<NativeMethods.OPENFILENAME>(),
                lpstrFilter = "VRCQL config (*.json)\0*.json\0All files\0*.*\0\0",
                lpstrFile = buffer,
                nMaxFile = buffer.Length,
                lpstrTitle = "Save config as",
                Flags = 0x00000002 // OFN_OVERWRITEPROMPT
            };

            if (GetSaveFileNameW(ref ofn))
            {
                string path = ofn.lpstrFile.TrimEnd('\0');
                if (string.IsNullOrEmpty(Path.GetExtension(path))) path += ".json";
                SaveConfigTo(path);
            }
#else
            _status = "Save As dialog is only available in the Windows build.";
            _isError = true;
#endif
        }

        private static void OpenSaveLocation()
        {
            try
            {
                string folder = Path.GetDirectoryName(_configPath) ?? _appDir;
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
            }
            catch { /* non-fatal */ }
        }

        private static void ClearInstallPaths()
        {
            _config.VRChatPath = "";
            _status = "Cleared install path.";
            _isError = false;
        }

        private static void ClearProfiles()
        {
            _config.Profiles.Clear();
            _status = "Cleared profiles.";
            _isError = false;
        }

        // Best-effort reconstruction of VRChat's private-instance tag grammar.
        // VRChat's exact format has changed before and may change again —
        // verify against current behavior if instances stop resolving.
        private static string BuildInstanceArgument()
        {
            switch (_config.InstanceMode)
            {
                case InstanceMode.Join:
                    return _config.InstanceJoinLink.Trim();

                case InstanceMode.Create:
                    string worldId = _config.InstanceWorldId.Trim();
                    if (string.IsNullOrEmpty(worldId)) return "";

                    string access = AccessLevels[_config.InstanceAccessIndex];
                    string region = Regions[_config.InstanceRegionIndex];
                    string regionTag = region switch
                    {
                        "US West" => "us",
                        "US East" => "use",
                        "Europe" => "eu",
                        "Japan" => "jp",
                        _ => "us"
                    };
                    string accessTag = access switch
                    {
                        "Public" => "public",
                        "Friends+" => "hidden",
                        "Friends" => "friends",
                        "Invite+" => "private",
                        "Invite" => "private",
                        _ => "public"
                    };
                    string ownerId = _config.InstanceOwnerId.Trim();
                    if (accessTag != "public" && !string.IsNullOrEmpty(ownerId))
                        accessTag = $"{accessTag}({ownerId})";

                    string instanceId = $"{new Random().Next(10000, 99999)}~{accessTag}~region({regionTag})";
                    return $"vrchat://launch?ref=vrchat.com&id={worldId}:{instanceId}";

                default:
                    return "";
            }
        }

        private static string BuildLaunchArguments(LaunchProfile profile)
        {
            var args = new List<string>();

            if (!profile.VR) args.Add("--no-vr");
            args.Add($"--profile={profile.Number}");

            if (_config.DebugGui) args.Add("--enable-debug-gui");
            if (_config.SdkLog) args.Add("--enable-sdk-log-levels");
            if (_config.UdonLog) args.Add("--enable-udon-debug-logging");
            if (_config.WatchWorlds) args.Add("--watch-worlds");
            if (_config.WatchAvatars) args.Add("--watch-avatars");

            if (!string.IsNullOrWhiteSpace(_config.MidiDevice))
                args.Add($"--midi={_config.MidiDevice.Trim()}");

            if (!string.IsNullOrWhiteSpace(_config.OscConfig))
                args.Add($"--osc={_config.OscConfig.Trim()}");

            if (!string.IsNullOrWhiteSpace(_config.MaxFps) && int.TryParse(_config.MaxFps.Trim(), out int fps))
                args.Add($"--fps={fps}");

            string instanceArg = BuildInstanceArgument();
            if (!string.IsNullOrEmpty(instanceArg)) args.Add(instanceArg);

            if (_config.CustomParametersEnabled && !string.IsNullOrWhiteSpace(_config.CustomParameters))
                args.Add(_config.CustomParameters.Trim());

            return string.Join(" ", args);
        }

        private static void LaunchProfileProcess(LaunchProfile profile)
        {
            if (string.IsNullOrWhiteSpace(_config.VRChatPath) || !File.Exists(_config.VRChatPath))
            {
                _status = "Set a valid path to VRChat.exe first.";
                _isError = true;
                return;
            }

            try
            {
                string args = BuildLaunchArguments(profile);
                var process = Process.Start(new ProcessStartInfo(_config.VRChatPath)
                {
                    Arguments = args,
                    WorkingDirectory = Path.GetDirectoryName(_config.VRChatPath),
                    UseShellExecute = true
                });
                ApplyCpuAffinity(process);

                string label = string.IsNullOrEmpty(profile.Description) ? $"profile {profile.Number}" : $"{profile.Description} (profile {profile.Number})";
                _status = $"Launched {label}.";
                _isError = false;

                if (_config.AutoClose) _shouldExit = true;
            }
            catch (Exception ex)
            {
                _status = $"Failed to launch profile {profile.Number}: {ex.Message}";
                _isError = true;
            }
        }

        // CPU affinity is a comma-separated list of core indices, e.g. "0,1,2,3".
        // UseShellExecute launches mean we don't always get a real handle back
        // (esp. when VRChat's own launcher/shim reparents itself), so this is
        // best-effort and silently no-ops if it can't be applied.
        private static void ApplyCpuAffinity(Process? process)
        {
            if (process == null || string.IsNullOrWhiteSpace(_config.CpuAffinity)) return;

            try
            {
                long mask = 0;
                foreach (var part in _config.CpuAffinity.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (int.TryParse(part.Trim(), out int core) && core >= 0 && core < 64)
                        mask |= 1L << core;
                }
                if (mask != 0) process.ProcessorAffinity = (IntPtr)mask;
            }
            catch { /* process may have already exited, or affinity isn't supported here */ }
        }

        private static void LaunchAllSelected()
        {
            var selected = _config.Profiles.Where(p => p.Selected).ToList();
            if (selected.Count == 0)
            {
                _status = "No profiles selected.";
                _isError = true;
                return;
            }

            foreach (var profile in selected)
            {
                LaunchProfileProcess(profile);
            }

            if (_config.AutoLayout)
            {
                Task.Run(async () =>
                {
                    // Give the newly launched clients time to create their windows
                    // before we try to tile them.
                    await Task.Delay(8000);
                    AutoLayoutWindows();
                });
            }
        }

        private static void AutoLayoutWindows()
        {
#if WINDOWS_BUILD
            var vrchatPids = new HashSet<int>(Process.GetProcessesByName(ProcessName).Select(p => p.Id));
            if (vrchatPids.Count == 0) return;

            var handles = new List<IntPtr>();
            NativeMethods.EnumWindows((hWnd, lParam) =>
            {
                NativeMethods.GetWindowThreadProcessId(hWnd, out uint pid);
                if (vrchatPids.Contains((int)pid) && NativeMethods.IsWindowVisible(hWnd))
                {
                    handles.Add(hWnd);
                }
                return true;
            }, IntPtr.Zero);

            if (handles.Count == 0) return;

            int screenW = Raylib.GetMonitorWidth(Raylib.GetCurrentMonitor());
            int screenH = Raylib.GetMonitorHeight(Raylib.GetCurrentMonitor());
            int cols = (int)Math.Ceiling(Math.Sqrt(handles.Count));
            int rows = (int)Math.Ceiling((double)handles.Count / cols);
            int cellW = screenW / cols;
            int cellH = screenH / rows;

            for (int i = 0; i < handles.Count; i++)
            {
                int col = i % cols;
                int row = i / cols;
                NativeMethods.MoveWindow(handles[i], col * cellW, row * cellH, cellW, cellH, true);
            }
#endif
        }
    }
}