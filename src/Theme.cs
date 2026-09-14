using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace WSLKeepAliveTray
{
    public sealed class Theme
    {
        public string Id, Name;
        public Color Background, Surface, Header, Ink, Muted, Border, Accent;
        public Color ChartBackground, ChartInk, Grid, Primary, Secondary;
        public bool Light;
        public override string ToString() { return Name; }
        internal Theme(string id, string name, bool light, params string[] colors)
        {
            Id = id; Name = name; Light = light;
            Background = C(colors[0]); Surface = C(colors[1]); Header = C(colors[2]);
            Ink = C(colors[3]); Muted = C(colors[4]); Border = C(colors[5]); Accent = C(colors[6]);
            ChartBackground = C(colors[7]); ChartInk = C(colors[8]); Grid = C(colors[9]);
            Primary = C(colors[10]); Secondary = C(colors[11]);
        }
        private static Color C(string value) { return ColorTranslator.FromHtml("#" + value); }
    }

    public static class ThemeManager
    {
        public static readonly Theme[] All = {
            new Theme("zijin", "紫金山上", false, "111723", "1b2333", "292639", "f4eee7", "b9bdcd", "414b60", "efbd83", "172032", "f4eee7", "35415a", "efbd83", "bca5df"),
            new Theme("blackboard", "深林印象 · 黑板", true, "f5f4ef", "fffefb", "e8eee2", "183b34", "667a72", "cbd6c5", "24674f", "163f35", "f4f7e8", "355b4a", "c5f28b", "f0c98b"),
            new Theme("mission", "夜航指挥舱", false, "0b1818", "112421", "0e211c", "e9f0e7", "9cb5a9", "294039", "bded89", "0e211c", "eff6ec", "294039", "96d293", "64cbd1"),
            new Theme("paper", "科研白板", true, "f6f8fa", "ffffff", "edf3f9", "1c3552", "607489", "dce4ee", "2768a3", "ffffff", "244968", "e1e8ef", "1974ac", "996429"),
            new Theme("classic", "经典深色", false, "0d151f", "18222f", "121c28", "e9eff4", "97a9b8", "313f4f", "4aa3ff", "18222f", "b9c6d1", "263746", "2fd381", "ffb84d")
        };
        private static readonly string SettingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WSLKeepAliveTray", "theme.txt");
        public static Theme Current = Read(SettingsPath);
        public static event EventHandler Changed;
        public static Theme Find(string id)
        {
            foreach (Theme theme in All) if (theme.Id == id) return theme;
            return All[0];
        }
        internal static Theme Read(string path)
        {
            try { return Find(File.ReadAllText(path).Trim()); } catch { return All[0]; }
        }
        internal static void Save(string path, string id)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, Find(id).Id);
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
        public static void Select(string id, bool persist)
        {
            Current = Find(id);
            if (persist)
            {
                try { Save(SettingsPath, Current.Id); }
                catch (Exception ex) { AppLog.Write("保存主题失败: " + ex.Message); }
            }
            if (Changed != null) Changed(null, EventArgs.Empty);
        }
    }

    internal static class BrandAssets
    {
        private static readonly Image logo = LoadLogo();
        public static Image Logo { get { return logo; } }
        private static Image LoadLogo()
        {
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("DeepForest.Icon"))
            using (Image original = Image.FromStream(stream)) return new Bitmap(original);
        }
    }

    internal sealed class ThemeColorTable : ProfessionalColorTable
    {
        private Theme theme;
        public ThemeColorTable(Theme value) { theme = value; UseSystemColors = false; }
        public override Color ToolStripDropDownBackground { get { return theme.Surface; } }
        public override Color ImageMarginGradientBegin { get { return theme.Header; } }
        public override Color ImageMarginGradientMiddle { get { return theme.Header; } }
        public override Color ImageMarginGradientEnd { get { return theme.Header; } }
        public override Color MenuItemSelected { get { return theme.Header; } }
        public override Color MenuItemBorder { get { return theme.Accent; } }
        public override Color MenuBorder { get { return theme.Border; } }
        public override Color SeparatorDark { get { return theme.Border; } }
        public override Color SeparatorLight { get { return theme.Surface; } }
        public override Color CheckBackground { get { return theme.Header; } }
        public override Color CheckSelectedBackground { get { return theme.Header; } }
        public override Color MenuItemSelectedGradientBegin { get { return theme.Header; } }
        public override Color MenuItemSelectedGradientEnd { get { return theme.Header; } }
    }
}
