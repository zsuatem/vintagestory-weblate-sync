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

const MODS_JSON_PATH = path.join(__dirname, "..", "mods.json");
const MODS_DIR = path.join(__dirname, "..", "mods");
const UPDATE_LOG_PATH = path.join(__dirname, "..", "update-log.txt");

async function main() {
    const mods: ModEntry[] = JSON.parse(fs.readFileSync(MODS_JSON_PATH, "utf-8"));
    let updated = false;
    const updatedMods: string[] = [];

    for (const mod of mods) {
        console.log(`\n🔍 Checking mod: ${mod.name} (${mod.modid})`);
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

        const langFiles = entries.filter(e => e.entryName.toLowerCase().endsWith("/lang/en.json"));
        if (langFiles.length === 0) {
            console.warn(`⚠️ No lang/en.json found in ${mod.name}`);
            continue;
        }

        // Clear mod folder before reimport
        const modFolder = path.join(MODS_DIR, mod.name);
        fs.rmSync(modFolder, { recursive: true, force: true });

        for (const entry of langFiles) {
            const relativePath = entry.entryName;
            const fullPath = path.join(modFolder, relativePath);
            fs.mkdirSync(path.dirname(fullPath), { recursive: true });
            fs.writeFileSync(fullPath, entry.getData());
            console.log(`✔ Extracted: ${relativePath}`);
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
        console.log("\nℹ️ No changes — everything is up to date.");
    }
}

main().catch(err => {
    console.error("🔥 Critical error occurred:", err);
    process.exit(1);
});
