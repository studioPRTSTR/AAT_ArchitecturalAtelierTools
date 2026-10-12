// #! csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

using Eto.Drawing;
using Eto.Forms;

using Rhino;


AATPreferences.Run(RhinoDoc.ActiveDoc);


class LayerSetting
{
    public string Tool;
    public string ConfigFilename;
    public string Key;
    public string Fallback;

    public LayerSetting(string tool, string configFilename, string key, string fallback)
    {
        Tool = tool;
        ConfigFilename = configFilename;
        Key = key;
        Fallback = fallback;
    }
}


static class AATPreferences
{
    const string PluginName = "ArchitecturalAtelierTools";

    // One row per tool that draws on its own layer. Add new tools here.
    static readonly LayerSetting[] LayerSettings =
    {
        new LayerSetting("SoftInsulation", "SoftInsulation.json", "layer_name", "A_Isolation_01"),
    };


    static string UserConfigDir =>
        Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData), PluginName);


    // Same lookup order as the tools: user folder, next to the plugin, next to the script.
    static IEnumerable<string> ConfigCandidates(string configFilename, string scriptPath)
    {
        yield return Path.Combine(UserConfigDir, configFilename);

        string assemblyDir = null;
        try { assemblyDir = Path.GetDirectoryName(typeof(AATPreferences).Assembly.Location); } catch (Exception) { }
        if (!string.IsNullOrEmpty(assemblyDir))
            yield return Path.Combine(assemblyDir, configFilename);

        string scriptDir = Path.GetDirectoryName(scriptPath);
        if (!string.IsNullOrEmpty(scriptDir))
            yield return Path.Combine(scriptDir, configFilename);
    }


    static string ReadConfig(string configFilename, [CallerFilePath] string scriptPath = "")
    {
        try
        {
            string configPath = ConfigCandidates(configFilename, scriptPath).FirstOrDefault(File.Exists);
            if (configPath != null)
                return File.ReadAllText(configPath);
        }
        catch (Exception)
        {
        }
        return "{\n}\n";
    }


    static string ReadString(string json, string key, string fallback)
    {
        var m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
        return m.Success ? Regex.Unescape(m.Groups[1].Value) : fallback;
    }


    static string WriteString(string json, string key, string value)
    {
        string quoted = "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

        var existing = new Regex("(\"" + Regex.Escape(key) + "\"\\s*:\\s*)\"(?:[^\"\\\\]|\\\\.)*\"");
        if (existing.IsMatch(json))
            return existing.Replace(json, m => m.Groups[1].Value + quoted, 1);

        int brace = json.IndexOf('{');
        if (brace < 0)
            return "{\n    \"" + key + "\": " + quoted + "\n}\n";
        bool isEmpty = json.Substring(brace + 1).TrimStart().StartsWith("}");
        return json.Insert(brace + 1, "\n    \"" + key + "\": " + quoted + (isEmpty ? "\n" : ","));
    }


    static bool ShowDialog(RhinoDoc doc, string[] current, out string[] chosen)
    {
        List<string> layerNames = doc.Layers
            .Where(layer => !layer.IsDeleted)
            .OrderBy(layer => layer.SortIndex)
            .Select(layer => layer.FullPath)
            .ToList();

        var boxes = new List<ComboBox>();
        var rows = new TableLayout { Spacing = new Size(10, 6) };
        for (int i = 0; i < LayerSettings.Length; i++)
        {
            var box = new ComboBox { DataStore = layerNames, Text = current[i], AutoComplete = true, Width = 280 };
            boxes.Add(box);
            rows.Rows.Add(new TableRow(
                new Label { Text = LayerSettings[i].Tool, VerticalAlignment = VerticalAlignment.Center },
                box));
        }

        var dialog = new Dialog<bool> { Title = "ArchitecturalAtelierTools Preferences", Padding = 12, Resizable = false };
        var save = new Button { Text = "Save" };
        var cancel = new Button { Text = "Cancel" };
        save.Click += (sender, e) => dialog.Close(true);
        cancel.Click += (sender, e) => dialog.Close(false);
        dialog.DefaultButton = save;
        dialog.AbortButton = cancel;

        dialog.Content = new StackLayout
        {
            Spacing = 10,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Items =
            {
                new Label { Text = "Layer each tool draws on (pick a layer from this drawing or type a name):" },
                rows,
                new Label { Text = "Saved to " + UserConfigDir, TextColor = Colors.Gray },
                new TableLayout(new TableRow(new TableCell(null, true), save, cancel)) { Spacing = new Size(6, 0) },
            },
        };

        bool ok = dialog.ShowModal(Rhino.UI.RhinoEtoApp.MainWindow);
        chosen = boxes.Select(box => (box.Text ?? "").Trim()).ToArray();
        return ok;
    }


    public static void Run(RhinoDoc doc)
    {
        string[] configs = LayerSettings.Select(s => ReadConfig(s.ConfigFilename)).ToArray();
        string[] current = LayerSettings.Select((s, i) => ReadString(configs[i], s.Key, s.Fallback)).ToArray();

        if (!ShowDialog(doc, current, out string[] chosen))
        {
            RhinoApp.WriteLine("Cancelled.");
            return;
        }

        try
        {
            Directory.CreateDirectory(UserConfigDir);
            for (int i = 0; i < LayerSettings.Length; i++)
            {
                LayerSetting s = LayerSettings[i];
                string layerName = chosen[i].Length > 0 ? chosen[i] : s.Fallback;
                File.WriteAllText(Path.Combine(UserConfigDir, s.ConfigFilename), WriteString(configs[i], s.Key, layerName));
                RhinoApp.WriteLine("AATPreferences: {0} draws on layer '{1}'.", s.Tool, layerName);
            }
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine("AATPreferences: could not save settings - {0}", ex.Message);
        }
    }
}
