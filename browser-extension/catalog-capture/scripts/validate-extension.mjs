import { readFile, stat } from "node:fs/promises";
import path from "node:path";
import process from "node:process";
import { fileURLToPath } from "node:url";
import vm from "node:vm";

const extensionRoot = path.resolve(
  path.dirname(fileURLToPath(import.meta.url)),
  ".."
);
const manifestPath = path.join(extensionRoot, "manifest.json");
const manifest = JSON.parse(await readFile(manifestPath, "utf8"));

const errors = [];

if (manifest.manifest_version !== 3) {
  errors.push("manifest_version must be 3.");
}

if (!manifest.side_panel?.default_path) {
  errors.push("The manifest must define side_panel.default_path.");
}

for (const permission of ["downloads", "scripting", "sidePanel", "storage"]) {
  if (!manifest.permissions?.includes(permission)) {
    errors.push(`The manifest must include the ${permission} permission.`);
  }
}

if (!Array.isArray(manifest.content_scripts) || manifest.content_scripts.length === 0) {
  errors.push("The manifest must register the persistent page content scripts.");
}

const referencedFiles = [
  manifest.side_panel?.default_path,
  manifest.background?.service_worker,
  ...(manifest.content_scripts ?? []).flatMap((entry) => entry.js ?? [])
].filter(Boolean);

for (const relativePath of referencedFiles) {
  try {
    const details = await stat(path.join(extensionRoot, relativePath));
    if (!details.isFile()) {
      errors.push(`${relativePath} is not a file.`);
    }
  } catch {
    errors.push(`${relativePath} does not exist.`);
  }
}

const sidePanelHtml = await readFile(
  path.join(extensionRoot, manifest.side_panel.default_path),
  "utf8"
);
for (const match of sidePanelHtml.matchAll(
  /<(?:script[^>]+src|link[^>]+href)="([^"]+)"/gu
)) {
  const relativePath = path.join(
    path.dirname(manifest.side_panel.default_path),
    match[1]
  );
  try {
    await stat(path.join(extensionRoot, relativePath));
  } catch {
    errors.push(`${relativePath} referenced by the side panel does not exist.`);
  }
}

const scriptFiles = [
  "src/shared.js",
  "src/content.js",
  "src/background.js",
  "src/sidepanel.js"
];

for (const relativePath of scriptFiles) {
  try {
    const source = await readFile(path.join(extensionRoot, relativePath), "utf8");
    new vm.Script(source, { filename: relativePath });
  } catch (error) {
    errors.push(
      `${relativePath} has invalid JavaScript: ${
        error instanceof Error ? error.message : String(error)
      }`
    );
  }
}

if (errors.length > 0) {
  for (const error of errors) {
    console.error(`- ${error}`);
  }
  process.exitCode = 1;
} else {
  console.log("Extension manifest and referenced files are valid.");
}
