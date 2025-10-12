import * as fs from 'fs';
import * as path from 'path';
import * as JSZip from 'jszip';

/**
 * Helper: Recursively read all JSON files in a directory.
 */
function findFiles(dir: string, filename: string): string[] {
    const result: string[] = [];
    for (const file of fs.readdirSync(dir)) {
        const full = path.join(dir, file);
        const stat = fs.statSync(full);
        if (stat.isDirectory()) {
            result.push(...findFiles(full, filename));
        } else if (file.toLowerCase() === filename.toLowerCase()) {
            result.push(full);
        }
    }
    return result;
}

/**
 * Deep merge of two JSON objects (used for merging same-path translations)
 */
function deepMerge(target: any, source: any): any {
    for (const key of Object.keys(source)) {
        if (typeof source[key] === "object" && !Array.isArray(source[key]) && target[key]) {
            deepMerge(target[key], source[key]);
        } else {
            target[key] = source[key];
        }
    }
    return target;
}

/**
 * Compare en.json vs pl.json and count translated entries
 */
function countTranslated(en: any, pl: any): { total: number; translated: number } {
    let total = 0;
    let translated = 0;
    for (const key of Object.keys(en)) {
        const enVal = en[key];
        const plVal = pl[key];
        if (typeof enVal === "object" && enVal !== null) {
            const nested = countTranslated(enVal, plVal || {});
            total += nested.total;
            translated += nested.translated;
        } else {
            total++;
            if (plVal !== undefined && plVal !== "") translated++;
        }
    }
    return { total, translated };
}

/**
 * Read translators.txt -> string[]
 */
function readTranslators(file: string): string[] {
    if (!fs.existsSync(file)) return [];
    return fs.readFileSync(file, "utf-8").split(/\r?\n/).map(l => l.trim()).filter(Boolean);
}

/**
 * Read version from CLI argument
 */
const version = process.argv[2];
if (!version) {
    console.error("❌ Usage: tsx scripts/build-mod.ts <version>");
    process.exit(1);
}

const baseDir = process.cwd();
const modsDir = path.join(baseDir, "mods");
const translatorsFile = path.join(baseDir, "translators.txt");
const authors = readTranslators(translatorsFile);

const outputDir = path.join(baseDir, "dist");
fs.mkdirSync(outputDir, { recursive: true });

const zip = new JSZip();
const mergedFiles: Record<string, any> = {};
const logC: string[] = [];
const logI: string[] = [];

console.log(`🧩 Building Polish Translations Pack v${version}`);
console.log("===============================================");

for (const modName of fs.readdirSync(modsDir)) {
    const modPath = path.join(modsDir, modName);
    if (!fs.statSync(modPath).isDirectory()) continue;

    const enFiles = findFiles(modPath, "en.json");
    const plFiles = findFiles(modPath, "pl.json");
    if (enFiles.length === 0 || plFiles.length === 0) continue;

    for (const enFile of enFiles) {
        const relative = enFile.split("assets")[1];
        if (!relative) continue;
        const plFile = plFiles.find(f => f.endsWith(relative.replace("en.json", "pl.json")));
        if (!plFile) continue;

        const enData = JSON.parse(fs.readFileSync(enFile, "utf-8"));
        const plData = JSON.parse(fs.readFileSync(plFile, "utf-8"));
        const { total, translated } = countTranslated(enData, plData);
        const complete = total > 0 && translated === total;

        const shortRel = relative.replace(/^[/\\]/, "").replace(/\\/g, "/");
        if (complete) {
            console.log(`✅ ${modName}: ${translated}/${total} translated`);
            logC.push(`${modName}: ${translated}/${total} translated`);
            if (!mergedFiles[shortRel]) mergedFiles[shortRel] = {};
            deepMerge(mergedFiles[shortRel], plData);
        } else {
            console.log(`⚠️ ${modName}: incomplete (${translated}/${total})`);
            logI.push(`${modName}: ${translated}/${total} translated`);
        }
    }
}

console.log("\n📦 Packaging ZIP...");

// Add merged translations to zip
for (const rel of Object.keys(mergedFiles)) {
    const outputPath = "assets/" + rel.replace(/en\.json$/i, "pl.json");
    const data = JSON.stringify(mergedFiles[rel], null, 2);
    zip.file(outputPath, data);
}

// Create modinfo.json
const modinfo = {
    type: "content",
    name: "Polish Translations Pack",
    modid: "polishtranslationspack",
    description: "Polish Translations Pack - a collection of Polish localizations for various Vintage Story mods.",
    website: "https://mods.vintagestory.at/polishtranslationspack",
    version: version,
    authors: authors,
    contributors: ["test"],
    dependencies: { "game": "" },
};
zip.file("modinfo.json", JSON.stringify(modinfo, null, 2));

// Add modicon.png if exists
const logoPath = path.resolve("./modicon.png");

if (fs.existsSync(logoPath)) {
    const logoData = fs.readFileSync(logoPath);
    zip.file("modicon.png", logoData);
    console.log("🖼️ Added modicon.png to ZIP");
} else {
    console.warn("⚠️ modicon.png not found, skipping icon.");
}

// Create changelog
const MODS_JSON_PATH = path.join(__dirname, "..", "mods.json");
const mods = JSON.parse(fs.readFileSync(MODS_JSON_PATH, "utf-8"));

let changelog = `📦 Polish Translations Pack v${version}\n\n`;
changelog += "✅ Included translations:\n";

for (const line of logC) {
    const mod = mods.find((m: any) => line.includes(m.name));
    changelog += `- ${mod.name} v${mod?.version || "unknown"}\n`;
}

// if (logI.length > 0) {
//     changelog += `\n⚠️ Incomplete translations (not included):\n`;
//     for (const line of logI) {
//         const mod = mods.find((m: any) => line.includes(m.name));
//         changelog += `- ${mod.name} v${mod?.version || "unknown"}\n`;
//     }
// }

const changelogPath = path.join(outputDir, "changelog.txt");
fs.writeFileSync(changelogPath, changelog);
zip.file("changelog.txt", changelog);

// Write ZIP
const zipFileName = `PolishTranslationsPack_v${version}.zip`;
const zipPath = path.join(outputDir, zipFileName);
zip
    .generateNodeStream({
        type: "nodebuffer",
        streamFiles: true,
        compression: "DEFLATE",
        compressionOptions: { level: 9 }
    })
    .pipe(fs.createWriteStream(zipPath))
    .on("finish", () => {
        console.log(`\n✅ Done! Created ${zipFileName}`);
        if (logC.length) {
            console.log("\n✅ Complete mods:");
            for (const line of logC) console.log("  " + line);
        }
        if (logI.length) {
            console.log("\n⚠️ Incomplete mods:");
            for (const line of logI) console.log("  " + line);
        }
    });


