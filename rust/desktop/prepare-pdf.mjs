import { cpSync, mkdirSync, rmSync } from "node:fs";
import { basename } from "node:path";
import { fileURLToPath } from "node:url";

for (const name of ["cmaps", "standard_fonts", "wasm", "iccs"]) {
  const destination = new URL(`./public/pdfjs/${name}/`, import.meta.url);
  mkdirSync(destination, { recursive: true });
  cpSync(fileURLToPath(new URL(`./node_modules/pdfjs-dist/${name}/`, import.meta.url)), fileURLToPath(destination), {
    recursive: true, filter: source => basename(source) !== "quickjs-eval.wasm",
  });
}
rmSync(new URL("./public/pdfjs/wasm/quickjs-eval.wasm", import.meta.url), {force: true});
