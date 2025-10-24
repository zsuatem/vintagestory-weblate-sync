#!/usr/bin/env dotnet

#:package Newtonsoft.Json@13.0.4
#:package Semver@3.0.0

using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Security.Cryptography;
using NJson = Newtonsoft.Json;
using System.Diagnostics.CodeAnalysis;
using Semver;

void Log(string msg, ConsoleColor color = ConsoleColor.Gray)
{
    Console.ForegroundColor = color;
    Console.WriteLine(msg);
    Console.ResetColor();
}

string Command(params string[] args)
{
    if (args.Length == 0)
        return "help";
    return args[0].ToLowerInvariant();
}

NJson.Linq.JToken LoadAnyJson(string path)
{
    try
    {
        var jsonText = File.ReadAllText(path);
        using var reader = new NormalizingJsonTextReader(new StringReader(jsonText));
        return NJson.Linq.JToken.ReadFrom(reader)!;
    }
    catch (Exception ex)
    {
        Log($"❌ Failed to parse {path}: {ex.Message}", ConsoleColor.Red);
        throw;
    }
}

async Task<HashSet<string>> FetchRecentlyUpdatedModIds()
{
    try
    {
        using var http = new HttpClient();
        var response = await http.GetAsync("https://mods.vintagestory.at/api/mods");
        if (!response.IsSuccessStatusCode)
        {
            Log("❌ Failed to fetch mods list from API", ConsoleColor.Red);
            return new HashSet<string>();
        }

        var json = await response.Content.ReadAsStringAsync();
        var data = NJson.Linq.JObject.Parse(json);
        var mods = data["mods"] as NJson.Linq.JArray;
        var recentIds = new HashSet<string>();

        if (mods == null) return recentIds;

        var now = DateTime.UtcNow;
        var utcToday = new DateTime(now.Year, now.Month, now.Day, 0, 0, 0, DateTimeKind.Utc);
        var utcYesterday = utcToday.AddDays(-1);

        foreach (var mod in mods.OfType<NJson.Linq.JObject>())
        {
            var lastReleasedStr = mod["lastreleased"]?.ToString();
            if (string.IsNullOrWhiteSpace(lastReleasedStr)) continue;

            if (DateTime.TryParse(lastReleasedStr, out var lastReleased))
            {
                if (lastReleased >= utcYesterday)
                {
                    var modidStrs = mod["modidstrs"] as NJson.Linq.JArray;
                    if (modidStrs != null)
                    {
                        foreach (var id in modidStrs)
                        {
                            var idStr = id.ToString();
                            if (!string.IsNullOrWhiteSpace(idStr))
                                recentIds.Add(idStr);
                        }
                    }
                }
            }
        }

        return recentIds;
    }
    catch (Exception ex)
    {
        Log($"❌ Error fetching mods list: {ex.Message}", ConsoleColor.Red);
        return new HashSet<string>();
    }
}

int CompareVersions(string version1, string version2)
{
    version1 = version1.TrimStart('v', 'V').Trim();
    version2 = version2.TrimStart('v', 'V').Trim();

    if (string.Equals(version1, version2, StringComparison.OrdinalIgnoreCase))
        return 0;

    try
    {
        var semver1 = SemVersion.Parse(version1, SemVersionStyles.Any);
        var semver2 = SemVersion.Parse(version2, SemVersionStyles.Any);
        return semver1.ComparePrecedenceTo(semver2);
    }
    catch
    {
        return string.Compare(version1, version2, StringComparison.OrdinalIgnoreCase);
    }
}

void FixJson()
{
    Log("🔧 Converting *.json files (./mods and ./game, LF-only)", ConsoleColor.Cyan);

    var baseDir = Directory.GetCurrentDirectory();
    var searchDirs = new[]
    {
        Path.Combine(baseDir, Paths.ModsFolder),
        Path.Combine(baseDir, Paths.GameFolder)
    };

    foreach (var dir in searchDirs)
    {
        if (!Directory.Exists(dir))
        {
            Log($"⚠️  Skipping missing folder: {dir}", ConsoleColor.DarkYellow);
            continue;
        }

        foreach (var file in Directory.EnumerateFiles(dir, "*.json", SearchOption.AllDirectories))
        {
            try
            {
                var token = LoadAnyJson(file);
                var jsonText = token.ToString(NJson.Formatting.Indented);
                jsonText = jsonText.Replace("\r\n", "\n") + "\n";
                File.WriteAllText(file, jsonText, new UTF8Encoding(false));

                Log($"✅ {Path.GetRelativePath(baseDir, file)}", ConsoleColor.Green);
            }
            catch (Exception ex)
            {
                Log($"❌ {Path.GetRelativePath(baseDir, file)}: {ex.Message}", ConsoleColor.Red);
            }
        }
    }

    Log("🏁 Conversion completed (LF-only).", ConsoleColor.Cyan);
}

async Task UpdateMods()
{
    Log("📦 Updating mod translations...", ConsoleColor.Yellow);

    var baseDir = Directory.GetCurrentDirectory();
    var modsDir = Path.Combine(baseDir, Paths.ModsFolder);
    var updateLogPath = Path.Combine(baseDir, Paths.UpdateLog);

    var modsDb = new ModsDatabase(Paths.ModsJson);
    try
    {
        modsDb.Load();
    }
    catch (Exception ex)
    {
        Log($"⚠️  Failed to read {Paths.ModsJson}: {ex.Message}", ConsoleColor.DarkYellow);
        return;
    }

    if (modsDb.Count == 0)
    {
        Log($"⚠️  No mods in {Paths.ModsJson}", ConsoleColor.DarkYellow);
        return;
    }

    var recentModIds = await FetchRecentlyUpdatedModIds();
    if (recentModIds.Count == 0)
    {
        Log("ℹ️  No recently updated mods.", ConsoleColor.Gray);
    }

    using var http = new HttpClient();
    var updatedMods = new List<string>();
    bool anyUpdated = false;

    foreach (var mod in modsDb.GetAll())
    {
        var name = mod["name"]?.ToString() ?? "";
        var modid = mod["modid"]?.ToString() ?? "";
        var currentVersion = mod["version"]?.ToString();
        var modFolder = Path.Combine(modsDir, name);

        if (string.IsNullOrWhiteSpace(modid))
        {
            Log($"⚠️  Skipping mod without modid: {name}", ConsoleColor.DarkYellow);
            continue;
        }

        if (!recentModIds.Contains(modid) && !string.IsNullOrWhiteSpace(currentVersion))
        {
            Log($"⏩ Skipping {name}, no recent updates", ConsoleColor.DarkGray);
            continue;
        }

        Log($"🔍 Checking mod: {name} ({modid})", ConsoleColor.Cyan);

        try
        {
            var apiUrl = $"https://mods.vintagestory.at/api/mod/{modid}";
            var resp = await http.GetAsync(apiUrl);
            if (!resp.IsSuccessStatusCode)
            {
                Log($"⚠️  Cannot fetch API for {modid}: {resp.StatusCode}", ConsoleColor.DarkYellow);
                continue;
            }

            var json = await resp.Content.ReadAsStringAsync();
            var jo = NJson.Linq.JObject.Parse(json);

            var latest = jo["mod"]?["releases"]?.First;
            var latestVersion = latest?["modversion"]?.ToString();
            var fileUrl = latest?["mainfile"]?.ToString();

            if (string.IsNullOrWhiteSpace(latestVersion) || string.IsNullOrWhiteSpace(fileUrl))
            {
                Log($"⚠️  No file data for {modid}", ConsoleColor.DarkYellow);
                continue;
            }

            var isFirstTime = string.IsNullOrWhiteSpace(currentVersion);
            var shouldUpdate = false;
            
            if (isFirstTime)
            {
                shouldUpdate = true;
            }
            else
            {
                var cmp = CompareVersions(currentVersion!, latestVersion);
                shouldUpdate = cmp < 0;
            }

            if (!shouldUpdate)
            {
                Log($"✅ Latest version ({latestVersion}) already downloaded.", ConsoleColor.Green);
                continue;
            }

            Log($"⬇️  Downloading version {latestVersion} from {fileUrl}", ConsoleColor.Blue);
            Directory.CreateDirectory(modFolder);
            var tmp = Path.GetTempFileName();
            
            try
            {
                var zipBytes = await http.GetByteArrayAsync(fileUrl);
                await File.WriteAllBytesAsync(tmp, zipBytes);

                using (var archive = System.IO.Compression.ZipFile.OpenRead(tmp))
                {
                    var langEntries = archive.Entries
                        .Where(e => e.FullName.EndsWith("/lang/en.json", StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    if (langEntries.Count == 0)
                    {
                        Log($"❌ {name}: No lang/en.json files", ConsoleColor.Red);
                        continue;
                    }

                    foreach (var entry in langEntries)
                    {
                        using var stream = entry.Open();
                        using var reader2 = new StreamReader(stream);
                        var enText = await reader2.ReadToEndAsync();

                        var fullName = entry.FullName.Replace("\\", "/");
                        string relativePath;

                        if (fullName.Contains("assets/"))
                        {
                            relativePath = fullName.Substring(fullName.IndexOf("assets/", StringComparison.OrdinalIgnoreCase));
                        }
                        else
                        {
                            relativePath = fullName;
                        }

                        var outPath = Path.Combine(modFolder, relativePath.Replace("/", Path.DirectorySeparatorChar.ToString()));
                        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);

                        try
                        {
                            var token = NJson.Linq.JToken.Parse(enText);
                            var jsonOut = token.ToString(NJson.Formatting.Indented)
                                               .Replace("\r\n", "\n") + "\n";
                            File.WriteAllText(outPath, jsonOut, new UTF8Encoding(false));
                            Log($"💾 Saved: {outPath}", ConsoleColor.Green);
                        }
                        catch
                        {
                            File.WriteAllText(outPath, enText.Replace("\r\n", "\n") + "\n", new UTF8Encoding(false));
                            Log($"⚠️  {name}: en.json invalid JSON — saved raw", ConsoleColor.DarkYellow);
                        }
                    }
                }

                var fromVer = currentVersion ?? "none";
                mod["version"] = latestVersion;
                mod["lastUpdated"] = DateTime.UtcNow.ToString("yyyy-MM-dd");
                anyUpdated = true;

                updatedMods.Add($"- {name}: {fromVer} → {latestVersion}");
                Log($"✅ Updated: {name} to version {latestVersion}", ConsoleColor.Green);
            }
            finally
            {
                if (File.Exists(tmp))
                {
                    try { File.Delete(tmp); } catch { }
                }
            }
        }
        catch (Exception ex)
        {
            Log($"❌ {name} ({modid}): {ex.Message}", ConsoleColor.Red);
        }
    }

    if (anyUpdated)
    {
        modsDb.Save();

        var logContent = new StringBuilder();
        logContent.AppendLine("Updated mods:");
        logContent.AppendLine();
        foreach (var update in updatedMods)
        {
            logContent.AppendLine(update);
        }
        File.WriteAllText(updateLogPath, logContent.ToString(), new UTF8Encoding(false));

        Log($"\n💾 Changes saved to {Paths.ModsJson} and {Paths.UpdateLog}", ConsoleColor.Cyan);
    }
    else
    {
        Log("\nℹ️  No changes - everything up to date.", ConsoleColor.Gray);
    }
}

(int total, int translated) CountTranslated(NJson.Linq.JObject en, NJson.Linq.JObject pl)
{
    int total = 0;
    int translated = 0;

    foreach (var prop in en.Properties())
    {
        var enVal = prop.Value;
        var plVal = pl[prop.Name];

        if (enVal is NJson.Linq.JObject enObj)
        {
            var plObj = plVal as NJson.Linq.JObject ?? new NJson.Linq.JObject();
            var nested = CountTranslated(enObj, plObj);
            total += nested.total;
            translated += nested.translated;
        }
        else
        {
            total++;
            if (plVal != null && !string.IsNullOrWhiteSpace(plVal.ToString()))
            {
                translated++;
            }
        }
    }

    return (total, translated);
}

string ComputeJsonHash(NJson.Linq.JObject json)
{
    NJson.Linq.JObject NormalizeJson(NJson.Linq.JObject obj)
    {
        var normalized = new NJson.Linq.JObject();
        
        foreach (var prop in obj.Properties().OrderBy(p => p.Name))
        {
            if (prop.Value is NJson.Linq.JObject nestedObj)
            {
                normalized[prop.Name] = NormalizeJson(nestedObj);
            }
            else
            {
                normalized[prop.Name] = prop.Value;
            }
        }
        
        return normalized;
    }
    
    var normalizedJson = NormalizeJson(json);
    var jsonText = normalizedJson.ToString(NJson.Formatting.None);
    var bytes = Encoding.UTF8.GetBytes(jsonText);
    
    using var sha256 = SHA256.Create();
    var hashBytes = sha256.ComputeHash(bytes);
    return Convert.ToHexString(hashBytes).ToLowerInvariant();
}

void BuildModPack(string? versionArg)
{
    if (string.IsNullOrWhiteSpace(versionArg))
    {
        Log("❌ Usage: dotnet mods.cs build <version>", ConsoleColor.Red);
        return;
    }

    var version = versionArg.Trim();
    var baseDir = Directory.GetCurrentDirectory();
    var modsDir = Path.Combine(baseDir, Paths.ModsFolder);
    var distDir = Path.Combine(baseDir, Paths.DistFolder);

    Directory.CreateDirectory(distDir);

    var packName = $"PolishTranslationsPack_v{version}";
    var buildDir = Path.Combine(distDir, packName);
    
    if (Directory.Exists(buildDir))
        Directory.Delete(buildDir, true);
    Directory.CreateDirectory(buildDir);

    var authors = File.Exists(Paths.Translators)
        ? File.ReadAllLines(Paths.Translators).Where(l => !string.IsNullOrWhiteSpace(l)).ToArray()
        : new[] { "Community Translators" };

    var modInfo = new NJson.Linq.JObject
    {
        ["type"] = "content",
        ["name"] = "Polish Translations Pack",
        ["modid"] = "polishtranslationspack",
        ["description"] = "Polish Translations Pack - a collection of Polish localizations for various Vintage Story mods.",
        ["website"] = "https://mods.vintagestory.at/polishtranslationspack",
        ["version"] = version,
        ["authors"] = new NJson.Linq.JArray(authors),
        ["dependencies"] = new NJson.Linq.JObject { ["game"] = "" }
    };
    var modInfoJson = modInfo.ToString(NJson.Formatting.Indented).Replace("\r\n", "\n");
    File.WriteAllText(Path.Combine(buildDir, "modinfo.json"), modInfoJson, new UTF8Encoding(false));

    Log($"🏗️  Building pack {packName}...", ConsoleColor.Blue);

    var modsDb = new ModsDatabase(Paths.ModsJson);
    try { modsDb.Load(); } catch { }

    var modStats = new List<(string name, string version, string hash, int translated, int total, bool complete)>();
    var mergedFiles = new Dictionary<string, NJson.Linq.JObject>();

    foreach (var modFolder in Directory.GetDirectories(modsDir))
    {
        var modName = Path.GetFileName(modFolder);

        var enFiles = Directory.GetFiles(modFolder, "en.json", SearchOption.AllDirectories);
        var plFiles = Directory.GetFiles(modFolder, "pl.json", SearchOption.AllDirectories);

        if (enFiles.Length == 0)
        {
            Log($"⚠️  {modName}: no en.json – skipping.", ConsoleColor.DarkYellow);
            continue;
        }
        if (plFiles.Length == 0)
        {
            Log($"⚠️  {modName}: no pl.json – skipping.", ConsoleColor.DarkYellow);
            continue;
        }

        NJson.Linq.JObject MergeJsons(string[] files)
        {
            var merged = new NJson.Linq.JObject();
            foreach (var file in files)
            {
                try
                {
                    var j = NJson.Linq.JObject.Parse(File.ReadAllText(file));
                    merged.Merge(j, new NJson.Linq.JsonMergeSettings
                    {
                        MergeArrayHandling = NJson.Linq.MergeArrayHandling.Union,
                        MergeNullValueHandling = NJson.Linq.MergeNullValueHandling.Ignore
                    });
                }
                catch (Exception ex)
                {
                    Log($"⚠️  {modName}: error in {Path.GetFileName(file)} – {ex.Message}", ConsoleColor.DarkYellow);
                }
            }
            return merged;
        }

        var enJson = MergeJsons(enFiles);
        var plJson = MergeJsons(plFiles);

        var (total, translated) = CountTranslated(enJson, plJson);
        var complete = total > 0 && translated == total;

        var translationHash = ComputeJsonHash(plJson);
        var modVersion = modsDb.GetVersion(modName) ?? "unknown";

        if (!complete)
        {
            var missing = total - translated;
            Log($"⚠️  {modName}: incomplete ({translated}/{total}, missing {missing})", ConsoleColor.DarkYellow);
        }
        else
        {
            Log($"✅ {modName}: complete ({translated}/{total})", ConsoleColor.Green);
        }

        modStats.Add((modName, modVersion, translationHash, translated, total, complete));

        if (!complete) continue;

        for (int i = 0; i < plFiles.Length; i++)
        {
            var plFile = plFiles[i];
            var plData = NJson.Linq.JObject.Parse(File.ReadAllText(plFile));
            
            var relativePath = plFile.Replace(modFolder, "").Replace("\\", "/").TrimStart('/');
            if (relativePath.Contains("assets/"))
            {
                relativePath = relativePath.Substring(relativePath.IndexOf("assets/"));
            }

            if (!mergedFiles.ContainsKey(relativePath))
            {
                mergedFiles[relativePath] = new NJson.Linq.JObject();
            }

            mergedFiles[relativePath].Merge(plData, new NJson.Linq.JsonMergeSettings
            {
                MergeArrayHandling = NJson.Linq.MergeArrayHandling.Union,
                MergeNullValueHandling = NJson.Linq.MergeNullValueHandling.Ignore
            });
        }
    }

    Log($"📝 Saving merged translations...", ConsoleColor.Cyan);
    foreach (var (relativePath, mergedData) in mergedFiles)
    {
        var targetPath = Path.Combine(buildDir, relativePath.Replace("/", Path.DirectorySeparatorChar.ToString()));
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        
        var jsonText = mergedData.ToString(NJson.Formatting.Indented).Replace("\r\n", "\n") + "\n";
        File.WriteAllText(targetPath, jsonText, new UTF8Encoding(false));
    }

    var completeMods = modStats.Where(s => s.complete).OrderBy(s => s.name).ToList();
    var incompleteMods = modStats.Where(s => !s.complete).OrderBy(s => s.name).ToList();
    
    var (changelogForZip, changelogForDist) = BuildChangelog(version, completeMods, incompleteMods, distDir);
    
    var changelogFilePath = Path.Combine(distDir, $"changelog_{version}.txt");
    File.WriteAllText(changelogFilePath, changelogForDist, new UTF8Encoding(false));
    File.WriteAllText(Path.Combine(buildDir, "changelog.txt"), changelogForZip, new UTF8Encoding(false));

    var zipPath = Path.Combine(distDir, $"{packName}.zip");
    if (File.Exists(zipPath)) File.Delete(zipPath);

    Log($"📦 Creating ZIP...", ConsoleColor.Cyan);

    using (var archive = new ZipArchive(File.Create(zipPath), ZipArchiveMode.Create))
    {
        foreach (var file in Directory.EnumerateFiles(buildDir, "*", SearchOption.AllDirectories))
        {
            var entryName = Path.GetRelativePath(buildDir, file).Replace("\\", "/");
            var entry = archive.CreateEntry(entryName, CompressionLevel.SmallestSize);
            
            using var entryStream = entry.Open();
            using var fileStream = File.OpenRead(file);
            fileStream.CopyTo(entryStream);
        }

        var modiconPath = Path.Combine(baseDir, Paths.ModIcon);
        if (File.Exists(modiconPath))
        {
            var entry = archive.CreateEntry(Paths.ModIcon, CompressionLevel.SmallestSize);
            using var entryStream = entry.Open();
            using var fileStream = File.OpenRead(modiconPath);
            fileStream.CopyTo(entryStream);
            Log($"🖼️  Added {Paths.ModIcon}", ConsoleColor.Gray);
        }
    }

    try
    {
        Directory.Delete(buildDir, true);
        Log($"🗑️  Removed temporary folder", ConsoleColor.Gray);
    }
    catch (Exception ex)
    {
        Log($"⚠️  Failed to remove build folder: {ex.Message}", ConsoleColor.DarkYellow);
    }

    SaveBuildHistory(version, completeMods, distDir);

    Log($"\n✅ Built pack: {zipPath}", ConsoleColor.Green);
    Log($"📝 Changelog: {changelogFilePath}", ConsoleColor.Gray);
    
    if (completeMods.Any())
    {
        Log($"\n✅ Complete mods ({completeMods.Count}):", ConsoleColor.Green);
        foreach (var stat in completeMods)
        {
            Log($"   - {stat.name}: {stat.translated}/{stat.total} translated", ConsoleColor.Gray);
        }
    }
    
    if (incompleteMods.Any())
    {
        Log($"\n⚠️  Incomplete mods ({incompleteMods.Count}):", ConsoleColor.DarkYellow);
        foreach (var stat in incompleteMods)
        {
            Log($"   - {stat.name}: {stat.translated}/{stat.total} translated", ConsoleColor.Gray);
        }
    }
}

(string changelogForZip, string changelogForDist) BuildChangelog(
    string version,
    List<(string name, string version, string hash, int translated, int total, bool complete)> completeMods,
    List<(string name, string version, string hash, int translated, int total, bool complete)> incompleteMods,
    string distDir)
{
    var historyPath = Path.Combine(distDir, Paths.BuildHistory);
    
    Dictionary<string, (string version, string hash)>? previousMods = null;
    if (File.Exists(historyPath))
    {
        try
        {
            var historyText = File.ReadAllText(historyPath);
            var history = NJson.Linq.JArray.Parse(historyText);
            
            if (history.Count > 0)
            {
                var lastBuild = history[0] as NJson.Linq.JObject;
                var mods = lastBuild?["mods"] as NJson.Linq.JArray;
                
                if (mods != null)
                {
                    previousMods = new Dictionary<string, (string version, string hash)>();
                    foreach (var mod in mods.OfType<NJson.Linq.JObject>())
                    {
                        var name = mod["name"]?.ToString();
                        var ver = mod["version"]?.ToString();
                        var hash = mod["hash"]?.ToString();
                        if (name != null && ver != null && hash != null)
                        {
                            previousMods[name] = (ver, hash);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log($"⚠️  Failed to load previous build history: {ex.Message}", ConsoleColor.DarkYellow);
        }
    }

    var addedMods = new List<(string name, string version)>();
    var updatedMods = new List<(string name, string oldVersion, string newVersion)>();
    var fixedMods = new List<(string name, string version)>();

    foreach (var mod in completeMods)
    {
        if (previousMods == null || !previousMods.ContainsKey(mod.name))
        {
            addedMods.Add((mod.name, mod.version));
        }
        else
        {
            var (previousVersion, previousHash) = previousMods[mod.name];
            
            if (previousVersion != mod.version)
            {
                updatedMods.Add((mod.name, previousVersion, mod.version));
            }
            else if (previousHash != mod.hash)
            {
                fixedMods.Add((mod.name, mod.version));
            }
        }
    }

    var zipReport = new StringBuilder();
    zipReport.AppendLine($"📦 Polish Translations Pack v{version}");
    zipReport.AppendLine();

    if (addedMods.Any())
    {
        zipReport.AppendLine("✅ Added translations:");
        foreach (var (name, ver) in addedMods.OrderBy(m => m.name))
        {
            zipReport.AppendLine($"- {name} v{ver}");
        }
        zipReport.AppendLine();
    }

    if (updatedMods.Any())
    {
        zipReport.AppendLine("🔄 Updated translations:");
        foreach (var (name, oldVer, newVer) in updatedMods.OrderBy(m => m.name))
        {
            zipReport.AppendLine($"- {name}: v{oldVer} → v{newVer}");
        }
        zipReport.AppendLine();
    }

    if (fixedMods.Any())
    {
        zipReport.AppendLine("🔧 Translation fixes:");
        foreach (var (name, ver) in fixedMods.OrderBy(m => m.name))
        {
            zipReport.AppendLine($"- {name} v{ver}");
        }
        zipReport.AppendLine();
    }

    if (!addedMods.Any() && !updatedMods.Any() && !fixedMods.Any())
    {
        zipReport.AppendLine("✅ Included translations:");
        foreach (var mod in completeMods)
        {
            zipReport.AppendLine($"- {mod.name} v{mod.version}");
        }
    }
    else
    {
        zipReport.AppendLine();
        zipReport.AppendLine("📋 Full list of included mods:");
        foreach (var mod in completeMods)
        {
            zipReport.AppendLine($"- {mod.name} v{mod.version}");
        }
    }

    var distReport = new StringBuilder(zipReport.ToString());

    if (incompleteMods.Any())
    {
        distReport.AppendLine();
        distReport.AppendLine();
        distReport.AppendLine();
        distReport.AppendLine("⚠️ Incomplete translations (not included):");
        foreach (var mod in incompleteMods)
        {
            distReport.AppendLine($"- {mod.name}: {mod.translated}/{mod.total} translated");
        }
    }

    return (zipReport.ToString().TrimEnd(), distReport.ToString().TrimEnd());
}

void SaveBuildHistory(string version, List<(string name, string version, string hash, int translated, int total, bool complete)> completeMods, string distDir)
{
    try
    {
        var historyPath = Path.Combine(distDir, Paths.BuildHistory);
        
        NJson.Linq.JArray history;
        if (File.Exists(historyPath))
        {
            var existingText = File.ReadAllText(historyPath);
            history = NJson.Linq.JArray.Parse(existingText);
        }
        else
        {
            history = new NJson.Linq.JArray();
        }

        var buildEntry = new NJson.Linq.JObject
        {
            ["version"] = version,
            ["date"] = DateTime.UtcNow.ToString("yyyy-MM-dd"),
            ["timestamp"] = DateTime.UtcNow.ToString("o"),
            ["modsCount"] = completeMods.Count,
            ["mods"] = new NJson.Linq.JArray(
                completeMods.Select(m => new NJson.Linq.JObject
                {
                    ["name"] = m.name,
                    ["version"] = m.version,
                    ["hash"] = m.hash
                }).ToArray()
            )
        };

        history.Insert(0, buildEntry);

        var historyText = history.ToString(NJson.Formatting.Indented).Replace("\r\n", "\n") + "\n";
        File.WriteAllText(historyPath, historyText, new UTF8Encoding(false));

        Log($"📊 Build history saved to {Paths.BuildHistory}", ConsoleColor.Gray);
    }
    catch (Exception ex)
    {
        Log($"⚠️  Failed to save build history: {ex.Message}", ConsoleColor.DarkYellow);
    }
}

var cmd = Command(args);

Console.WriteLine(cmd);
switch (cmd)
{
    case "fix":
        FixJson();
        break;
    case "update":
        await UpdateMods();
        break;
    case "build":
        BuildModPack(args.Length > 1 ? args[1] : null);
        break;
    default:
        Log("Available commands:", ConsoleColor.White);
        Log("  dotnet mods.cs fix", ConsoleColor.Gray);
        Log("  dotnet mods.cs update", ConsoleColor.Gray);
        Log("  dotnet mods.cs build <version>", ConsoleColor.Gray);
        break;
}

class NormalizingJsonTextReader : NJson.JsonTextReader
{
    public NormalizingJsonTextReader(TextReader reader) : base(reader) { }

    public override object? Value
    {
        get
        {
            if (base.TokenType == NJson.JsonToken.String && base.Value is string s)
            {
                return s.Contains('\r') ? s.Replace("\r\n", "\n").Replace("\r", "\n") : s;
            }
            return base.Value;
        }
    }
}

static class Paths
{
    public const string ModsJson = "mods.json";
    public const string BuildHistory = "build-history.json";
    public const string UpdateLog = "update-log.txt";
    public const string ModIcon = "modicon.png";
    public const string Translators = "translators.txt";
    public const string ModsFolder = "mods";
    public const string GameFolder = "game";
    public const string DistFolder = "dist";
}

class ModsDatabase
{
    private List<NJson.Linq.JObject> _mods = new();
    private readonly string _filePath;

    public ModsDatabase(string filePath = "mods.json")
    {
        _filePath = filePath;
    }

    public void Load()
    {
        if (!File.Exists(_filePath))
        {
            _mods = new List<NJson.Linq.JObject>();
            return;
        }

        try
        {
            var text = File.ReadAllText(_filePath);
            var jArray = NJson.Linq.JArray.Parse(text);
            _mods = jArray.OfType<NJson.Linq.JObject>().ToList();
        }
        catch (Exception ex)
        {
            throw new Exception($"Failed to load {_filePath}: {ex.Message}", ex);
        }
    }

    public void Save()
    {
        var jArray = new NJson.Linq.JArray(_mods.ToArray());
        var jsonText = jArray.ToString(NJson.Formatting.Indented).Replace("\r\n", "\n") + "\n";
        File.WriteAllText(_filePath, jsonText, new UTF8Encoding(false));
    }

    public List<NJson.Linq.JObject> GetAll() => _mods;
    public int Count => _mods.Count;

    public NJson.Linq.JObject? FindByName(string name)
    {
        return _mods.FirstOrDefault(m => 
            string.Equals(m["name"]?.ToString(), name, StringComparison.OrdinalIgnoreCase));
    }

    public string? GetVersion(string name) => FindByName(name)?["version"]?.ToString();
    public string? GetModId(string name) => FindByName(name)?["modid"]?.ToString();
}


