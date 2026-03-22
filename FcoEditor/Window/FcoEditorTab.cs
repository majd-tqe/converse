using Hexa.NET.ImGui;
using System.Numerics;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Amicitia.IO.Binary;
using libfco;

namespace Converse
{
    public static class FcoEditorTab
    {
        static int mode = 0; // 0 = Extract, 1 = Import
        static int subMode = 0; // 0 = Single, 1 = Batch
        static string fcoPath = "";
        static string csvPath = "";
        static string tablePath = "";
        static string outputPath = "";
        static string batchDir = "";
        static string csvDir = "";
        static string logText = "";

        static readonly string[] AlignmentNames = { "Left", "Center", "Right", "Justified" };

        // ── Extract to CSV ─────────────────────────────────────────────
        static void ExtractToCsv(string fcoFile, string tableFile, string outputFile)
        {
            try
            {
                var fco = new FontConverse();
                using (var reader = new BinaryObjectReader(fcoFile, Endianness.Big, Encoding.UTF8))
                {
                    fco = reader.ReadObject<FontConverse>();
                }

                var table = LoadTable(tableFile);
                var sb = new StringBuilder();
                sb.AppendLine("File,Group,GroupIndex,CellIndex,CellName,Alignment,Text");

                int count = 0;
                for (int gi = 0; gi < fco.Groups.Count; gi++)
                {
                    var group = fco.Groups[gi];
                    for (int ci = 0; ci < group.Cells.Count; ci++)
                    {
                        var cell = group.Cells[ci];
                        string text = MessageToText(cell.Message, table, cell.Highlights);
                        string alignment = (int)cell.Alignment < AlignmentNames.Length ? AlignmentNames[(int)cell.Alignment] : "Unknown";
                        sb.AppendLine($"{EscapeCsv(Path.GetFileName(fcoFile))},{EscapeCsv(group.Name)},{gi},{ci},{EscapeCsv(cell.Name)},{alignment},{EscapeCsv(text)}");
                        count++;
                    }
                }

                File.WriteAllText(outputFile, sb.ToString(), Encoding.UTF8);
                Log($"Extracted {count} entries to {outputFile}");
            }
            catch (Exception ex)
            {
                Log($"Error: {ex.Message}");
            }
        }

        // ── Import from CSV ─────────────────────────────────────────────
        static void ImportFromCsv(string csvFile, string fcoFile, string tableFile, string outputFile)
        {
            try
            {
                var fco = new FontConverse();
                using (var reader = new BinaryObjectReader(fcoFile, Endianness.Big, Encoding.UTF8))
                {
                    fco = reader.ReadObject<FontConverse>();
                }

                var reverseTable = LoadReverseTable(tableFile);
                var lines = File.ReadAllLines(csvFile, Encoding.UTF8);
                if (lines.Length < 2) { Log("CSV is empty"); return; }

                var rows = new Dictionary<(int, int), CsvRow>();
                for (int i = 1; i < lines.Length; i++)
                {
                    var row = ParseCsvRow(lines[i]);
                    if (row != null)
                    {
                        int gi = int.Parse(row.GroupIndex);
                        int ci = int.Parse(row.CellIndex);
                        rows[(gi, ci)] = row;
                    }
                }

                int count = 0;
                for (int gi = 0; gi < fco.Groups.Count; gi++)
                {
                    for (int ci = 0; ci < fco.Groups[gi].Cells.Count; ci++)
                    {
                        if (rows.TryGetValue((gi, ci), out var row))
                        {
                            var cell = fco.Groups[gi].Cells[ci];
                            cell.Message = TextToMessage(row.Text, reverseTable);
                            cell.Highlights = ParseHighlights(row.Text);
                            int alignIdx = Array.IndexOf(AlignmentNames, row.Alignment);
                            if (alignIdx >= 0)
                                cell.Alignment = (Cell.TextAlign)alignIdx;
                            fco.Groups[gi].Cells[ci] = cell;
                            count++;
                        }
                    }
                }

                using (var writer = new BinaryObjectWriter(outputFile, Endianness.Big, Encoding.UTF8))
                {
                    writer.WriteObject(fco);
                }

                Log($"Imported {count} entries to {outputFile}");
            }
            catch (Exception ex)
            {
                Log($"Error: {ex.Message}");
            }
        }

        // ── Batch Extract ───────────────────────────────────────────────
        static void BatchExtract(string dir, string outDir)
        {
            Directory.CreateDirectory(outDir);
            var files = Directory.GetFiles(dir, "*.fco", SearchOption.AllDirectories);
            Log($"Found {files.Length} FCO files");

            foreach (var fcoFile in files)
            {
                string tableFile = FindTable(fcoFile);
                if (string.IsNullOrEmpty(tableFile))
                {
                    Log($"  Skip {Path.GetFileName(fcoFile)} - no table");
                    continue;
                }

                string relPath = Path.GetRelativePath(dir, fcoFile);
                string subDir = Path.Combine(outDir, Path.GetDirectoryName(relPath) ?? "");
                Directory.CreateDirectory(subDir);
                string outFile = Path.Combine(subDir, Path.GetFileNameWithoutExtension(fcoFile) + ".csv");

                ExtractToCsv(fcoFile, tableFile, outFile);
                Log($"  {relPath}");
            }
        }

        // ── Batch Import ────────────────────────────────────────────────
        static void BatchImport(string fcoDir, string csvDir)
        {
            var files = Directory.GetFiles(fcoDir, "*.fco", SearchOption.AllDirectories);
            Log($"Found {files.Length} FCO files");

            foreach (var fcoFile in files)
            {
                string relPath = Path.GetRelativePath(fcoDir, fcoFile);
                string csvFile = Path.Combine(csvDir, Path.GetDirectoryName(relPath) ?? "",
                    Path.GetFileNameWithoutExtension(fcoFile) + ".csv");

                if (!File.Exists(csvFile))
                {
                    Log($"  Skip {relPath} - no CSV");
                    continue;
                }

                string tableFile = FindTable(fcoFile);
                if (string.IsNullOrEmpty(tableFile))
                {
                    Log($"  Skip {relPath} - no table");
                    continue;
                }

                string outFile = fcoFile;
                ImportFromCsv(csvFile, fcoFile, tableFile, outFile);
                Log($"  {relPath}");
            }
        }

        // ── Helpers ─────────────────────────────────────────────────────
        static Dictionary<int, string> LoadTable(string path)
        {
            var table = new Dictionary<int, string>();
            try
            {
                var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
                foreach (var e in json.RootElement.EnumerateArray())
                {
                    int cid = e.GetProperty("ConverseID").GetInt32();
                    string letter = e.GetProperty("Letter").GetString();
                    if (!string.IsNullOrEmpty(letter)) table[cid] = letter;
                }
            }
            catch { }
            return table;
        }

        static Dictionary<string, int> LoadReverseTable(string path)
        {
            var table = new Dictionary<string, int>();
            try
            {
                var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
                foreach (var e in json.RootElement.EnumerateArray())
                {
                    int cid = e.GetProperty("ConverseID").GetInt32();
                    string letter = e.GetProperty("Letter").GetString();
                    if (!string.IsNullOrEmpty(letter) && !table.ContainsKey(letter))
                        table[letter] = cid;
                }
            }
            catch { }
            return table;
        }

        static string MessageToText(int[] message, Dictionary<int, string> table, List<CellColor> highlights)
        {
            var chars = new List<string>();
            foreach (int cid in message)
            {
                if (cid == 0) chars.Add("\\n");
                else if (table.ContainsKey(cid)) chars.Add(table[cid]);
                else chars.Add($"{{{cid}}}");
            }

            if (highlights == null || highlights.Count == 0)
                return string.Join("", chars);

            var sorted = highlights.OrderBy(h => h.Start).ToList();
            var result = new StringBuilder();

            for (int i = 0; i < chars.Count; i++)
            {
                foreach (var hl in sorted.Where(h => h.Start == i))
                {
                    var c = hl.ArgbColor;
                    result.Append($"<color #{(int)(c.X * 255):X2}{(int)(c.Y * 255):X2}{(int)(c.Z * 255):X2}>");
                }
                result.Append(chars[i]);
                foreach (var hl in sorted.Where(h => h.End == i))
                {
                    result.Append("</color>");
                }
            }

            return result.ToString();
        }

        static int[] TextToMessage(string text, Dictionary<string, int> reverseTable)
        {
            var message = new List<int>();
            int i = 0;
            while (i < text.Length)
            {
                if (text.Substring(i).StartsWith("<color "))
                {
                    int end = text.IndexOf('>', i);
                    if (end != -1) { i = end + 1; continue; }
                }
                if (text.Substring(i).StartsWith("</color>"))
                {
                    i += 8; continue;
                }
                if (text.Substring(i).StartsWith("\\n"))
                {
                    message.Add(0); i += 2; continue;
                }
                if (text[i] == '{')
                {
                    int end = text.IndexOf('}', i);
                    if (end != -1 && int.TryParse(text.Substring(i + 1, end - i - 1), out int cid))
                    {
                        message.Add(cid); i = end + 1; continue;
                    }
                }
                string ch = text[i].ToString();
                message.Add(reverseTable.ContainsKey(ch) ? reverseTable[ch] : (int)text[i]);
                i++;
            }
            return message.ToArray();
        }

        static List<CellColor> ParseHighlights(string text)
        {
            var highlights = new List<CellColor>();
            string currentColor = null;
            int charIndex = 0, i = 0;

            while (i < text.Length)
            {
                if (text.Substring(i).StartsWith("<color "))
                {
                    int end = text.IndexOf('>', i);
                    if (end != -1)
                    {
                        if (currentColor != null && highlights.Count > 0)
                            highlights[^1].End = charIndex - 1;

                        string hex = text.Substring(i + 7, end - i - 7);
                        if (hex.StartsWith("#") && hex.Length == 7)
                        {
                            var color = new CellColor(2);
                            color.Start = charIndex;
                            color.End = -1;
                            color.ArgbColor = new Vector4(
                                Convert.ToInt32(hex.Substring(1, 2), 16) / 255f,
                                Convert.ToInt32(hex.Substring(3, 2), 16) / 255f,
                                Convert.ToInt32(hex.Substring(5, 2), 16) / 255f, 1f);
                            highlights.Add(color);
                        }
                        currentColor = hex;
                        i = end + 1;
                        continue;
                    }
                }
                if (text.Substring(i).StartsWith("</color>"))
                {
                    if (currentColor != null && highlights.Count > 0)
                        highlights[^1].End = charIndex - 1;
                    currentColor = null;
                    i += 8;
                    continue;
                }
                if (text.Substring(i).StartsWith("\\n"))
                {
                    charIndex++; i += 2; continue;
                }
                if (text[i] == '{')
                {
                    int end = text.IndexOf('}', i);
                    if (end != -1) { charIndex++; i = end + 1; continue; }
                }
                charIndex++;
                i++;
            }

            if (currentColor != null && highlights.Count > 0)
                highlights[^1].End = charIndex - 1;

            return highlights;
        }

        static string FindTable(string fcoFile)
        {
            string dir = Path.GetDirectoryName(fcoFile);
            string stem = Path.GetFileNameWithoutExtension(fcoFile);
            string sameName = Path.Combine(dir, stem + ".json");
            if (File.Exists(sameName)) return sameName;
            string defaultTable = Path.Combine(dir, "fte_ConverseMain.json");
            if (File.Exists(defaultTable)) return defaultTable;
            return "";
        }

        static string EscapeCsv(string s)
        {
            if (s.Contains(',') || s.Contains('"') || s.Contains('\n'))
                return "\"" + s.Replace("\"", "\"\"") + "\"";
            return s;
        }

        class CsvRow
        {
            public string GroupIndex, CellIndex, Alignment, Text;
        }

        static CsvRow ParseCsvRow(string line)
        {
            var fields = new List<string>();
            bool inQuotes = false;
            var field = new StringBuilder();
            for (int i = 0; i < line.Length; i++)
            {
                if (line[i] == '"')
                {
                    if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                    {
                        field.Append('"'); i++;
                    }
                    else inQuotes = !inQuotes;
                }
                else if (line[i] == ',' && !inQuotes)
                {
                    fields.Add(field.ToString()); field.Clear();
                }
                else field.Append(line[i]);
            }
            fields.Add(field.ToString());
            if (fields.Count >= 7)
                return new CsvRow { GroupIndex = fields[2], CellIndex = fields[3], Alignment = fields[5], Text = fields[6] };
            return null;
        }

        static void Log(string msg) { logText += msg + "\n"; }

        // ── Render ──────────────────────────────────────────────────────
        public static void Render(ConverseProject renderer)
        {
            ImGui.Text("FCO CSV Editor");
            ImGui.Separator();

            // Mode selection
            ImGui.RadioButton("Extract##mode", ref mode, 0);
            ImGui.SameLine();
            ImGui.RadioButton("Import##mode", ref mode, 1);
            ImGui.Spacing();

            // Sub mode selection
            ImGui.RadioButton("Single##sub", ref subMode, 0);
            ImGui.SameLine();
            ImGui.RadioButton("Batch##sub", ref subMode, 1);
            ImGui.Spacing();
            ImGui.Separator();

            if (mode == 0) // Extract
            {
                if (subMode == 0) // Single
                {
                    ImGui.InputText("FCO##ext", ref fcoPath, 512);
                    ImGui.SameLine();
                    if (ImGui.Button("...##fcoext"))
                    {
                        var d = NativeFileDialogSharp.Dialog.FileOpen("fco");
                        if (d.IsOk) fcoPath = d.Path;
                    }

                    ImGui.InputText("Table##ext", ref tablePath, 512);
                    ImGui.SameLine();
                    if (ImGui.Button("...##tableext"))
                    {
                        var d = NativeFileDialogSharp.Dialog.FileOpen("json");
                        if (d.IsOk) tablePath = d.Path;
                    }

                    ImGui.InputText("Output CSV##ext", ref outputPath, 512);
                    ImGui.SameLine();
                    if (ImGui.Button("...##outext"))
                    {
                        var d = NativeFileDialogSharp.Dialog.FileSave("csv");
                        if (d.IsOk) outputPath = d.Path;
                    }

                    ImGui.Spacing();
                    if (ImGui.Button("Extract##btn", new Vector2(200, 30)))
                    {
                        string t = string.IsNullOrEmpty(tablePath) ? FindTable(fcoPath) : tablePath;
                        string o = string.IsNullOrEmpty(outputPath) ? Path.ChangeExtension(fcoPath, ".csv") : outputPath;
                        ExtractToCsv(fcoPath, t, o);
                    }
                }
                else // Batch
                {
                    ImGui.InputText("Folder##extb", ref batchDir, 512);
                    ImGui.SameLine();
                    if (ImGui.Button("...##dirext"))
                    {
                        var d = NativeFileDialogSharp.Dialog.FolderPicker();
                        if (d.IsOk) batchDir = d.Path;
                    }

                    ImGui.Spacing();
                    if (ImGui.Button("Batch Extract##btn", new Vector2(200, 30)) && !string.IsNullOrEmpty(batchDir))
                    {
                        string outDir = Path.Combine(batchDir, "csv_output");
                        BatchExtract(batchDir, outDir);
                    }
                }
            }
            else // Import
            {
                if (subMode == 0) // Single
                {
                    ImGui.InputText("CSV##imp", ref csvPath, 512);
                    ImGui.SameLine();
                    if (ImGui.Button("...##csvimp"))
                    {
                        var d = NativeFileDialogSharp.Dialog.FileOpen("csv");
                        if (d.IsOk) csvPath = d.Path;
                    }

                    ImGui.InputText("Original FCO##imp", ref fcoPath, 512);
                    ImGui.SameLine();
                    if (ImGui.Button("...##fcoimp"))
                    {
                        var d = NativeFileDialogSharp.Dialog.FileOpen("fco");
                        if (d.IsOk) fcoPath = d.Path;
                    }

                    ImGui.InputText("Table##imp", ref tablePath, 512);
                    ImGui.SameLine();
                    if (ImGui.Button("...##tableimp"))
                    {
                        var d = NativeFileDialogSharp.Dialog.FileOpen("json");
                        if (d.IsOk) tablePath = d.Path;
                    }

                    ImGui.InputText("Output FCO##imp", ref outputPath, 512);
                    ImGui.SameLine();
                    if (ImGui.Button("...##outimp"))
                    {
                        var d = NativeFileDialogSharp.Dialog.FileSave("fco");
                        if (d.IsOk) outputPath = d.Path;
                    }

                    ImGui.Spacing();
                    if (ImGui.Button("Import##btn", new Vector2(200, 30)))
                    {
                        string t = string.IsNullOrEmpty(tablePath) ? FindTable(fcoPath) : tablePath;
                        string o = string.IsNullOrEmpty(outputPath) ? fcoPath : outputPath;
                        ImportFromCsv(csvPath, fcoPath, t, o);
                    }
                }
                else // Batch
                {
                    ImGui.InputText("FCO Folder##impb", ref batchDir, 512);
                    ImGui.SameLine();
                    if (ImGui.Button("...##dirimp"))
                    {
                        var d = NativeFileDialogSharp.Dialog.FolderPicker();
                        if (d.IsOk) batchDir = d.Path;
                    }

                    ImGui.InputText("CSV Folder##impb", ref csvDir, 512);
                    ImGui.SameLine();
                    if (ImGui.Button("...##csvdir"))
                    {
                        var d = NativeFileDialogSharp.Dialog.FolderPicker();
                        if (d.IsOk) csvDir = d.Path;
                    }

                    ImGui.Spacing();
                    if (ImGui.Button("Batch Import##btn", new Vector2(200, 30)) && !string.IsNullOrEmpty(batchDir) && !string.IsNullOrEmpty(csvDir))
                    {
                        BatchImport(batchDir, csvDir);
                    }
                }
            }

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Text("Log:");
            if (ImGui.BeginChild("##fcolog", new Vector2(-1, 200), ImGuiChildFlags.FrameStyle))
            {
                ImGui.TextWrapped(logText);
                if (ImGui.GetScrollY() >= ImGui.GetScrollMaxY())
                    ImGui.SetScrollHereY(1f);
                ImGui.EndChild();
            }

            ImGui.EndTabItem();
        }

        public static void Reset()
        {
            logText = "";
            fcoPath = "";
            csvPath = "";
            tablePath = "";
            outputPath = "";
            batchDir = "";
            csvDir = "";
        }
    }
}
