import * as fs from 'fs';
import * as path from 'path';
import { parse } from 'json5';

const modsPath = './mods';

function fixFile(filePath: string) {
    let content = fs.readFileSync(filePath, 'utf8');

    try {
        const parsed = parse(content);
        fs.writeFileSync(filePath, JSON.stringify(parsed, null, 2));
        console.log(`✔ Fixed (clean): ${filePath}`);
    } catch (e1) {
        const flattened = content
            .replace(/\r\n/g, '\n')
            .replace(/\r/g, '\n')
            .replace(/\n[ \t]+/g, '');

        try {
            const parsed = parse(flattened);
            fs.writeFileSync(filePath, JSON.stringify(parsed, null, 2));
            console.log(`✔ Fixed (flattened): ${filePath}`);
        } catch (e2) {
            console.warn(`✖ Failed to parse: ${filePath}`, (e2 as Error).message);
        }
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
