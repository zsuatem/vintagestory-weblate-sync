import * as fs from 'fs';
import * as path from 'path';
import { parse } from 'json5';

const modsPath = './mods';

function fixFile(filePath: string) {
    const content = fs.readFileSync(filePath, 'utf8');
    try {
        const fixedContent = content.replace(/(?<!\\)\r?\n/g, "\\n");
        const parsed = parse(fixedContent);
        fs.writeFileSync(filePath, JSON.stringify(parsed, null, 2));
        console.log(`✔ Fixed: ${filePath}`);
    } catch (e) {
        console.warn(`✖ Failed to parse: ${filePath}`, (e as Error).message);
    }
}

function walk(dir: string) {
    for (const file of fs.readdirSync(dir)) {
        const fullPath = path.join(dir, file);
        const stat = fs.statSync(fullPath);
        if (stat.isDirectory()) {
            walk(fullPath);
        } else if (file.toLowerCase() === 'en.json') {
            fixFile(fullPath);
        }
    }
}

walk(modsPath);
