import * as fs from "fs";
import * as path from "path";
import fetch from "node-fetch";
import AdmZip = require("adm-zip");
import * as semver from "semver";

interface ModEntry {
    name: string;
    modid: string;
    version?: string;
}

interface ModListItem {
    modidstrs: string[];
    lastreleased: string;
}

const MODS_JSON_PATH = path.join(__dirname, "..", "mods.json");
const MODS_DIR = path.join(__dirname, "..", "mods");
const UPDATE_LOG_PATH = path.join(__dirname, "..", "update-log.txt");

function isRecentlyReleased(lastReleased: string): boolean {
    const date = new Date(lastReleased);
    const now = new Date();
    const utcToday = new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), now.getUTCDate()));
    const utcYesterday = new Date(utcToday);
    utcYesterday.setUTCDate(utcYesterday.getUTCDate() - 1);

    return date >= utcYesterday;
}

async function fetchRecentlyUpdatedModIds(): Promise<Set<string>> {
    try {
        const res = await fetch("https://mods.vintagestory.at/api/mods");
        const data = await res.json();
        const recentIds = new Set<string>();

        for (const mod of data.mods as ModListItem[]) {
            if (isRecentlyReleased(mod.lastreleased)) {
                for (const id of mod.modidstrs) {
                    recentIds.add(id);
                }
            }
        }

        return recentIds;
    } catch (err) {
        console.error("❌ Failed to fetch mod list from API:", err);
        return new Set();
    }
}

async function main() {
    const mods: ModEntry[] = JSON.parse(fs.readFileSync(MODS_JSON_PATH, "utf-8"));
    const updatedMods: string[] = [];
    let updated = false;

    const recentIds = await fetchRecentlyUpdatedModIds();
    if (recentIds.size === 0) {
        console.log("ℹ️ No recently updated mods found.");
        return;
    }

    for (const mod of mods) {
        const isFirstTime = !mod.version || mod.version.trim() === "";
        if (!recentIds.has(mod.modid) && !isFirstTime) {
            console.log(`⏩ Skipping ${mod.name}, not updated recently`);
            continue;
        }

        console.log(`🔍 Checking mod: ${mod.name} (${mod.modid})`);
        const apiUrl = `https://mods.vintagestory.at/api/mod/${mod.modid}`;
        let response;

        try {
            response = await fetch(apiUrl);
            if (!response.ok) throw new Error(`API error: ${response.statusText}`);
        } catch (err) {
            console.error(`❌ Failed to fetch API data: ${err}`);
            continue;
        }

        const data = await response.json();
        const latest = data.mod?.releases?.[0];
        if (!latest?.modversion) {
            console.warn("⚠️ No version found in API.");
            continue;
        }

        const latestVersion = latest.modversion;
        const currentVersion = mod.version;
        const isNew = !currentVersion || semver.gt(latestVersion, currentVersion);

        if (!isNew) {
            console.log(`✅ Latest version (${latestVersion}) already downloaded.`);
            continue;
        }

        const fileUrl = latest.mainfile;
        if (!fileUrl) {
            console.warn("⚠️ No mainfile found in release data.");
            continue;
        }

        console.log(`⬇️ Downloading version ${latestVersion} from ${fileUrl}`);
        let zipBuffer;
        try {
            const zipRes = await fetch(fileUrl);
            zipBuffer = await zipRes.arrayBuffer();
        } catch (err) {
            console.error(`❌ Failed to download ZIP: ${err}`);
            continue;
        }

        const zip = new AdmZip(Buffer.from(zipBuffer));
        const entries = zip.getEntries();
        const matches = entries.filter((e: typeof entries[0]) => e.entryName.toLowerCase().endsWith("/lang/en.json"));

        if (matches.length === 0) {
            console.error(`❌ ${mod.name}: No lang/en.json files found`);
            continue;
        }

        const modFolder = path.join(MODS_DIR, mod.name);

        for (const entry of matches) {
            const fileData = entry.getData().toString("utf-8");
            const relativePath = entry.entryName;
            const outputPath = path.join(modFolder, relativePath);
            fs.mkdirSync(path.dirname(outputPath), { recursive: true });
            fs.writeFileSync(outputPath, fileData);
            console.log(`💾 Saved: ${outputPath}`);
        }

        mod.version = latestVersion;
        updated = true;

        const fromVer = currentVersion ?? "none";
        updatedMods.push(`- ${mod.name}: ${fromVer} → ${latestVersion}`);
        console.log(`✅ Updated: ${mod.name} to version ${latestVersion}`);
    }

    if (updated) {
        fs.writeFileSync(MODS_JSON_PATH, JSON.stringify(mods, null, 2));
        fs.writeFileSync(UPDATE_LOG_PATH, `Updated mods:\n\n${updatedMods.join("\n")}\n`);
        console.log("\n💾 Changes saved to mods.json and update-log.txt");
    } else {
        console.log("\nℹ️ No changes - everything is up to date.");
    }
}

main().catch(err => {
    console.error("🔥 Critical error occurred:", err);
    process.exit(1);
});
