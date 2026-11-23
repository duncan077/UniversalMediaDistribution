import { cpSync, mkdirSync } from "node:fs";
import { fileURLToPath } from "node:url";

for (const name of ["cmaps", "standard_fonts", "wasm", "iccs"]) {
  const destination = new URL(`./public/pdfjs/${name}/`, import.meta.url);
  mkdirSync(destination, { recursive: true });
  cpSync(fileURLToPath(new URL(`./node_modules/pdfjs-dist/${name}/`, import.meta.url)), fileURLToPath(destination), { recursive: true });
}
