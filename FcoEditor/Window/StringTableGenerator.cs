using Hexa.NET.ImGui;
using System.Numerics;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Amicitia.IO.Binary;
using DirectXTexNet;
using libfco;
using Converse.Rendering;

namespace Converse
{
    public static class StringTableGenerator
    {
        // ── State ─────────────────────────────────────────────────────────
        static bool isProcessing = false;
        static int mode = 0; // 0 = single, 1 = batch
        static string ftePath = "";
        static string atlasPath = "";
        static string outputPath = "";
        static string batchDir = "";
        static string dbPath = "";
        static float threshold = 0.20f;
        static int idOffset = 246;
        static string logText = "";
        static string dbStatus = "";
        static string pendingChar = "";
        static SixLabors.ImageSharp.Image<Rgba32> pendingGlyphImg = null;
        static GlTexture pendingGlyphTex = null;
        static int pendingCid = -1;
        static float pendingDist = 0;
        static bool waitingForUser = false;
        static bool focusTextInput = false;
        static List<TableEntry> generatedTable = null;
        static Dictionary<string, List<bool[]>> hashDb = new();
        static int processedCount = 0;
        static int totalCount = 0;
        static Queue<PendingGlyph> glyphQueue = new();

        struct TableEntry
        {
            public string Letter;
            public int ConverseID;
        }

        struct PendingGlyph
        {
            public SixLabors.ImageSharp.Image<Rgba32> Img;
            public int Cid;
            public float Dist;
        }

        struct GlyphResult
        {
            public string Letter;
            public int ConverseID;
        }

        // ── DCT Perceptual Hash ───────────────────────────────────────────
        const int HASH_SIZE = 32;
        const int DCT_KEEP = 32;

        static float[] Dct1D(float[] x)
        {
            int N = x.Length;
            float[] output = new float[N];
            for (int k = 0; k < N; k++)
            {
                float sum = 0;
                for (int n = 0; n < N; n++)
                {
                    sum += x[n] * MathF.Cos(MathF.PI * k * (2 * n + 1) / (2 * N));
                }
                output[k] = sum;
            }
            return output;
        }

        static bool[] Phash(SixLabors.ImageSharp.Image<Rgba32> img)
        {
            var resized = img.Clone();
            resized.Mutate(x => x.Resize(HASH_SIZE, HASH_SIZE, KnownResamplers.Lanczos3));

            float[,] gray = new float[HASH_SIZE, HASH_SIZE];
            for (int y = 0; y < HASH_SIZE; y++)
                for (int x = 0; x < HASH_SIZE; x++)
                {
                    var p = resized[x, y];
                    gray[y, x] = p.A > 0 ? (0.299f * p.R + 0.587f * p.G + 0.114f * p.B) : 0;
                }
            resized.Dispose();

            float[,] dct2d = new float[HASH_SIZE, HASH_SIZE];
            float[] row = new float[HASH_SIZE];
            for (int y = 0; y < HASH_SIZE; y++)
            {
                for (int x = 0; x < HASH_SIZE; x++)
                    row[x] = gray[y, x];
                var dctRow = Dct1D(row);
                for (int x = 0; x < HASH_SIZE; x++)
                    dct2d[y, x] = dctRow[x];
            }

            float[] col = new float[HASH_SIZE];
            for (int x = 0; x < HASH_SIZE; x++)
            {
                for (int y = 0; y < HASH_SIZE; y++)
                    col[y] = dct2d[y, x];
                var dctCol = Dct1D(col);
                for (int y = 0; y < HASH_SIZE; y++)
                    dct2d[y, x] = dctCol[y];
            }

            float[] block = new float[DCT_KEEP * DCT_KEEP];
            int idx = 0;
            for (int y = 0; y < DCT_KEEP; y++)
                for (int x = 0; x < DCT_KEEP; x++)
                    block[idx++] = dct2d[y, x];

            float median = Median(block);
            bool[] hash = new bool[block.Length];
            for (int i = 0; i < block.Length; i++)
                hash[i] = block[i] > median;
            return hash;
        }

        static float Median(float[] arr)
        {
            var sorted = (float[])arr.Clone();
            Array.Sort(sorted);
            int mid = sorted.Length / 2;
            return sorted.Length % 2 == 0 ? (sorted[mid - 1] + sorted[mid]) / 2f : sorted[mid];
        }

        static float HashDist(bool[] a, bool[] b)
        {
            int diff = 0;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) diff++;
            return (float)diff / a.Length;
        }

        // ── Database ──────────────────────────────────────────────────────
        static void LoadDb(string path)
        {
            hashDb.Clear();
            if (!File.Exists(path)) return;
            try
            {
                string json = File.ReadAllText(path);
                var doc = System.Text.Json.JsonDocument.Parse(json);
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    var hashes = new List<bool[]>();
                    foreach (var h in prop.Value.EnumerateArray())
                    {
                        var arr = new bool[h.GetArrayLength()];
                        int i = 0;
                        foreach (var v in h.EnumerateArray())
                            arr[i++] = v.GetBoolean();
                        hashes.Add(arr);
                    }
                    hashDb[prop.Name] = hashes;
                }
                dbStatus = $"DB loaded: {hashDb.Count} chars";
            }
            catch
            {
                dbStatus = "DB error, starting fresh";
            }
        }

        static void SaveDb(string path)
        {
            try
            {
                var list = new List<object>();
                foreach (var kv in hashDb)
                {
                    var hashes = kv.Value.Select(h => h.ToList()).ToList();
                    list.Add(new { key = kv.Key, hashes });
                }
                var dict = hashDb.ToDictionary(kv => kv.Key, kv => kv.Value.Select(h => h.ToList()).ToList());
                string json = System.Text.Json.JsonSerializer.Serialize(dict, new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                });
                File.WriteAllText(path, json);
            }
            catch { }
        }

        static void DbAdd(string character, SixLabors.ImageSharp.Image<Rgba32> img)
        {
            var h = Phash(img);
            if (!hashDb.ContainsKey(character))
            {
                hashDb[character] = new List<bool[]> { h };
            }
            else
            {
                if (hashDb[character].All(e => HashDist(h, e) > 0.05f))
                    hashDb[character].Add(h);
            }
        }

        static (string ch, float dist) FindBest(SixLabors.ImageSharp.Image<Rgba32> img)
        {
            if (hashDb.Count == 0) return (null, 1f);
            var h = Phash(img);
            string bestChar = null;
            float bestDist = 1f;
            foreach (var kv in hashDb)
            {
                float d = kv.Value.Min(s => HashDist(h, s));
                if (d < bestDist)
                {
                    bestDist = d;
                    bestChar = kv.Key;
                }
            }
            return bestDist <= threshold ? (bestChar, bestDist) : (null, bestDist);
        }

        // ── Load Atlas Image (supports DDS and PNG) ────────────────────────
        static SixLabors.ImageSharp.Image<Rgba32> LoadAtlasImage(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".dds")
            {
                var scratch = TexHelper.Instance.LoadFromDDSFile(path, DDS_FLAGS.NONE);
                var image = scratch.GetImage(0);
                int width = image.Width;
                int height = image.Height;

                var decompressed = scratch.Decompress(0, DXGI_FORMAT.R8G8B8A8_UNORM);
                var decompressedImage = decompressed.GetImage(0);
                var pixels = decompressedImage.Pixels;

                var img = new SixLabors.ImageSharp.Image<Rgba32>(width, height);
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        int offset = y * (int)decompressedImage.RowPitch + x * 4;
                        byte r = Marshal.ReadByte(pixels, offset);
                        byte g = Marshal.ReadByte(pixels, offset + 1);
                        byte b = Marshal.ReadByte(pixels, offset + 2);
                        byte a = Marshal.ReadByte(pixels, offset + 3);
                        img[x, y] = new Rgba32(r, g, b, a);
                    }
                }

                scratch.Dispose();
                decompressed.Dispose();
                return img;
            }
            else
            {
                return SixLabors.ImageSharp.Image.Load<Rgba32>(path);
            }
        }

        // ── FTE Processing ────────────────────────────────────────────────
        static (List<TableEntry> entries, int autoN, int manualN, int skipN) ProcessFte(
            string ftePath, string atlasPath, string dbPath)
        {
            var fte = new FontTexture();
            using (var reader = new BinaryObjectReader(ftePath, Endianness.Big, System.Text.Encoding.UTF8))
            {
                fte = reader.ReadObject<FontTexture>();
            }

            if (fte.Textures.Count < 1)
            {
                Log("No textures in FTE");
                return (new(), 0, 0, 0);
            }

            SixLabors.ImageSharp.Image<Rgba32> atlas;
            try
            {
                atlas = LoadAtlasImage(atlasPath);
            }
            catch (Exception e)
            {
                Log($"Cannot open atlas: {e.Message}");
                return (new(), 0, 0, 0);
            }

            var charTex = fte.Textures[^1];
            float W = charTex.Size.X;
            float H = charTex.Size.Y;

            var entries = new List<TableEntry>();
            int autoN = 0, manualN = 0, skipN = 0;

            for (int i = 0; i < fte.Characters.Count; i++)
            {
                var c = fte.Characters[i];
                if (c.TextureIndex < fte.Textures.Count - 1) continue;

                int cid = c.CharacterID;
                int x0 = Math.Max(0, (int)MathF.Round(c.TopLeft.X * W));
                int y0 = Math.Max(0, (int)MathF.Round(c.TopLeft.Y * H));
                int x1 = Math.Min(atlas.Width, (int)MathF.Round(c.BottomRight.X * W));
                int y1 = Math.Min(atlas.Height, (int)MathF.Round(c.BottomRight.Y * H));

                if (x1 <= x0) continue; // Invalid: no width

                // Space glyph: has width but no visible pixels
                if (x1 > x0 && y1 == y0)
                {
                    entries.Add(new TableEntry { Letter = " ", ConverseID = cid });
                    autoN++;
                    Log($"  OK  {cid,4}  ->  ' '   (space)");
                    continue;
                }

                if (y1 <= y0) continue; // Invalid: no height (but wasn't a space)

                SixLabors.ImageSharp.Image<Rgba32> glyph;
                try
                {
                    glyph = atlas.Clone(ctx => ctx.Crop(new Rectangle(x0, y0, x1 - x0, y1 - y0)));
                }
                catch { skipN++; continue; }

                var (ch, dist) = FindBest(glyph);
                if (ch != null)
                {
                    entries.Add(new TableEntry { Letter = ch, ConverseID = cid });
                    autoN++;
                    Log($"  OK  {cid,4}  ->  '{ch}'  ({dist:F3})");
                    if (dist > 0)
                    {
                        DbAdd(ch, glyph);
                        SaveDb(dbPath);
                    }
                    glyph.Dispose();
                }
                else
                {
                    glyphQueue.Enqueue(new PendingGlyph { Img = glyph, Cid = cid, Dist = dist });
                    manualN++;
                }
            }

            atlas.Dispose();
            return (entries, autoN, manualN, skipN);
        }

        static void Log(string msg)
        {
            logText += msg + "\n";
        }

        // ── Fixed Control Entries ─────────────────────────────────────────
        static readonly TableEntry[] ControlEntries = new[]
        {
            new TableEntry { Letter = "\\n", ConverseID = 0 },
            new TableEntry { Letter = "{A}", ConverseID = 100 },
            new TableEntry { Letter = "{B}", ConverseID = 101 },
            new TableEntry { Letter = "{X}", ConverseID = 102 },
            new TableEntry { Letter = "{Y}", ConverseID = 103 },
            new TableEntry { Letter = "{LB}", ConverseID = 104 },
            new TableEntry { Letter = "{RB}", ConverseID = 105 },
            new TableEntry { Letter = "{LT}", ConverseID = 106 },
            new TableEntry { Letter = "{RT}", ConverseID = 107 },
            new TableEntry { Letter = "{LSUP}", ConverseID = 108 },
            new TableEntry { Letter = "{LSRIGHT}", ConverseID = 109 },
            new TableEntry { Letter = "{LSDOWN}", ConverseID = 110 },
            new TableEntry { Letter = "{LSLEFT}", ConverseID = 111 },
            new TableEntry { Letter = "{RSUP}", ConverseID = 112 },
            new TableEntry { Letter = "{RSRIGHT}", ConverseID = 113 },
            new TableEntry { Letter = "{RSDOWN}", ConverseID = 114 },
            new TableEntry { Letter = "{RSLEFT}", ConverseID = 115 },
            new TableEntry { Letter = "{DPADUP}", ConverseID = 116 },
            new TableEntry { Letter = "{DPADRIGHT}", ConverseID = 117 },
            new TableEntry { Letter = "{DPADDOWN}", ConverseID = 118 },
            new TableEntry { Letter = "{DPADLEFT}", ConverseID = 119 },
            new TableEntry { Letter = "{START}", ConverseID = 120 },
            new TableEntry { Letter = "{SELECT}", ConverseID = 121 },
            new TableEntry { Letter = "{LOADING}", ConverseID = 122 },
        };

        // ── Save Table as Converse JSON ───────────────────────────────────
        static void SaveTable(string path, List<TableEntry> entries)
        {
            var allEntries = ControlEntries.Concat(entries).ToList();
            var tableData = allEntries.Select(e => new { Letter = e.Letter, ConverseID = e.ConverseID }).ToList();
            string json = System.Text.Json.JsonSerializer.Serialize(tableData, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
            File.WriteAllText(path, json);
        }

        // ── Render ────────────────────────────────────────────────────────
        public static void Render(ConverseProject renderer)
        {
            if (string.IsNullOrEmpty(dbPath))
                dbPath = Path.Combine(Program.Path, "Resources", "glyph_db.json");

            ImGui.Text("FTE String Table Generator");
            ImGui.Separator();

            // Mode selection
            ImGui.RadioButton("Single File", ref mode, 0);
            ImGui.SameLine();
            ImGui.RadioButton("Batch Mode", ref mode, 1);
            ImGui.Spacing();

            if (mode == 0)
            {
                // Single file mode
                ImGui.InputText("FTE Path", ref ftePath, 512);
                ImGui.SameLine();
                if (ImGui.Button("...##fte"))
                {
                    var d = NativeFileDialogSharp.Dialog.FileOpen("fte");
                    if (d.IsOk) ftePath = d.Path;
                }

                ImGui.InputText("Atlas Path", ref atlasPath, 512);
                ImGui.SameLine();
                if (ImGui.Button("...##atlas"))
                {
                    var d = NativeFileDialogSharp.Dialog.FileOpen("dds,png");
                    if (d.IsOk) atlasPath = d.Path;
                }

                ImGui.InputText("Output Path", ref outputPath, 512);
                ImGui.SameLine();
                if (ImGui.Button("...##output"))
                {
                    var d = NativeFileDialogSharp.Dialog.FileSave("json");
                    if (d.IsOk) outputPath = d.Path;
                }
            }
            else
            {
                // Batch mode
                ImGui.InputText("Directory", ref batchDir, 512);
                ImGui.SameLine();
                if (ImGui.Button("...##dir"))
                {
                    var d = NativeFileDialogSharp.Dialog.FolderPicker();
                    if (d.IsOk) batchDir = d.Path;
                }
                ImGui.TextWrapped("Tables will be saved next to each FTE file with the same name.");
            }

            ImGui.Spacing();
            ImGui.InputText("DB Path", ref dbPath, 512);
            ImGui.SameLine();
            if (ImGui.Button("...##db"))
            {
                var d = NativeFileDialogSharp.Dialog.FileOpen("json");
                if (d.IsOk) dbPath = d.Path;
            }

            ImGui.SliderFloat("Threshold", ref threshold, 0.01f, 0.50f);
            ImGui.InputInt("ID Offset", ref idOffset);

            if (dbPath.Length > 0 && hashDb.Count == 0)
                LoadDb(dbPath);
            ImGui.TextColored(new Vector4(0.5f, 0.8f, 1, 1), dbStatus);

            ImGui.Spacing();
            ImGui.Separator();

            // Start button
            bool canStart = mode == 0
                ? (!string.IsNullOrEmpty(ftePath) && !string.IsNullOrEmpty(atlasPath) && !string.IsNullOrEmpty(outputPath))
                : !string.IsNullOrEmpty(batchDir);

            if (!canStart) ImGui.BeginDisabled();
            if (ImGui.Button("Generate Table", new Vector2(200, 30)))
            {
                isProcessing = true;
                logText = "";
                generatedTable = new();
                glyphQueue.Clear();
                waitingForUser = false;
                processedCount = 0;
                totalCount = 0;

                if (mode == 0)
                {
                    LoadDb(dbPath);
                    var (entries, autoN, manualN, skipN) = ProcessFte(ftePath, atlasPath, dbPath);
                    generatedTable.AddRange(entries);
                    Log($"Auto: {autoN}  Manual pending: {manualN}  Skipped: {skipN}");

                    if (glyphQueue.Count > 0)
                    {
                        ProcessNextGlyph();
                    }
                    else
                    {
                        FinalizeTable();
                    }
                }
                else
                {
                    // Batch mode
                    LoadDb(dbPath);
                    var fteFiles = Directory.GetFiles(batchDir, "*.fte", SearchOption.AllDirectories);
                    totalCount = fteFiles.Length;
                    Log($"Found {totalCount} FTE files");

                    foreach (var fteFile in fteFiles)
                    {
                        string atlasFile = FindAtlas(fteFile);
                        if (atlasFile == null)
                        {
                            Log($"[!] No atlas for {Path.GetFileName(fteFile)}");
                            processedCount++;
                            continue;
                        }

                        string outFile = Path.Combine(
                            Path.GetDirectoryName(fteFile),
                            Path.GetFileNameWithoutExtension(fteFile) + ".json");

                        Log($"\n[{processedCount + 1}/{totalCount}] {Path.GetFileName(fteFile)}");
                        var (entries, autoN, manualN, skipN) = ProcessFte(fteFile, atlasFile, dbPath);

                        // For batch, skip manual entries and save
                        var allEntries = ControlEntries.Concat(entries).ToList();
                        SaveTable(outFile, allEntries);
                        Log($"  Saved: {outFile}  (auto:{autoN} skip:{skipN})");
                        processedCount++;
                    }
                    Log($"\nBatch complete: {processedCount} files");
                    isProcessing = false;
                }
            }
            if (!canStart) ImGui.EndDisabled();

            ImGui.SameLine();
            if (isProcessing && waitingForUser)
            {
                ImGui.TextColored(new Vector4(1, 0.8f, 0, 1), "Waiting for input...");
            }

            // User input for unknown glyph (inline, not popup)
            if (waitingForUser && pendingGlyphImg != null)
            {
                ImGui.Spacing();
                ImGui.Separator();
                ImGui.TextColored(new Vector4(1, 0.5f, 0, 1), $"Unknown Glyph");
                ImGui.Text($"ID={pendingCid}   Size: {pendingGlyphImg.Width}x{pendingGlyphImg.Height}   Dist={pendingDist:F3}");

                // Show glyph image
                if (pendingGlyphTex != null)
                {
                    float scale = MathF.Max(1, 128f / MathF.Max(pendingGlyphImg.Width, pendingGlyphImg.Height));
                    Vector2 imgSize = new Vector2(pendingGlyphImg.Width * scale, pendingGlyphImg.Height * scale);
                    ImGui.Image(new ImTextureID(pendingGlyphTex.Id), imgSize);
                }

                // Auto-focus text input
                if (focusTextInput)
                {
                    ImGui.SetKeyboardFocusHere();
                    focusTextInput = false;
                }
                ImGui.InputText("##glyphInput", ref pendingChar, 10);
                ImGui.SameLine();
                if (ImGui.Button("Confirm##glyph") || ImGui.IsKeyPressed(ImGuiKey.Enter))
                {
                    if (!string.IsNullOrEmpty(pendingChar))
                    {
                        generatedTable.Add(new TableEntry { Letter = pendingChar, ConverseID = pendingCid });
                        DbAdd(pendingChar, pendingGlyphImg);
                        SaveDb(dbPath);
                        Log($"  Manual: {pendingCid,4}  ->  '{pendingChar}'");
                    }
                    else
                    {
                        Log($"  Skipped: {pendingCid}");
                    }
                    CleanupPendingGlyph();
                    ProcessNextGlyph();
                }
                ImGui.SameLine();
                if (ImGui.Button("Skip##glyph") || ImGui.IsKeyPressed(ImGuiKey.Escape))
                {
                    Log($"  Skipped: {pendingCid}");
                    CleanupPendingGlyph();
                    ProcessNextGlyph();
                }
            }

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Text("Log:");
            if (ImGui.BeginChild("##log", new Vector2(-1, 200), ImGuiChildFlags.FrameStyle))
            {
                ImGui.TextWrapped(logText);
                if (ImGui.GetScrollY() >= ImGui.GetScrollMaxY())
                    ImGui.SetScrollHereY(1f);
                ImGui.EndChild();
            }
            ImGui.EndTabItem();
        }

        static void ProcessNextGlyph()
        {
            if (glyphQueue.Count > 0)
            {
                var g = glyphQueue.Dequeue();
                pendingGlyphImg = g.Img;
                pendingCid = g.Cid;
                pendingDist = g.Dist;
                pendingChar = "";
                waitingForUser = true;
                focusTextInput = true;

                // Create texture for display
                pendingGlyphTex?.Dispose();
                pendingGlyphTex = CreateTextureFromImage(g.Img);
            }
            else
            {
                waitingForUser = false;
                if (mode == 0)
                    FinalizeTable();
            }
        }

        static void CleanupPendingGlyph()
        {
            pendingGlyphImg?.Dispose();
            pendingGlyphImg = null;
            pendingGlyphTex?.Dispose();
            pendingGlyphTex = null;
        }

        static unsafe GlTexture CreateTextureFromImage(SixLabors.ImageSharp.Image<Rgba32> img)
        {
            int width = img.Width;
            int height = img.Height;
            byte[] pixels = new byte[width * height * 4];

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    var p = img[x, y];
                    int offset = (y * width + x) * 4;
                    pixels[offset] = p.R;
                    pixels[offset + 1] = p.G;
                    pixels[offset + 2] = p.B;
                    pixels[offset + 3] = p.A;
                }
            }

            fixed (byte* pBytes = pixels)
            {
                return new GlTexture((nint)pBytes, width, height);
            }
        }

        static void FinalizeTable()
        {
            if (generatedTable.Count > 0)
            {
                SaveTable(outputPath, generatedTable);
                Log($"\nTable saved: {outputPath}");
                Log($"Total entries: {generatedTable.Count + ControlEntries.Length}");
            }
            isProcessing = false;
        }

        static string FindAtlas(string ftePath)
        {
            string dir = Path.GetDirectoryName(ftePath);
            string stem = Path.GetFileNameWithoutExtension(ftePath);
            string[] exts = { ".dds", ".DDS", ".png", ".PNG" };

            foreach (var ext in exts)
            {
                string p1 = Path.Combine(dir, stem + ext);
                if (File.Exists(p1)) return p1;
                string p2 = Path.Combine(dir, stem + "_000" + ext);
                if (File.Exists(p2)) return p2;
            }
            return null;
        }

        public static void Reset()
        {
            logText = "";
            isProcessing = false;
            waitingForUser = false;
            generatedTable = null;
            glyphQueue.Clear();
            pendingGlyphImg?.Dispose();
            pendingGlyphImg = null;
            pendingGlyphTex?.Dispose();
            pendingGlyphTex = null;
        }
    }
}
