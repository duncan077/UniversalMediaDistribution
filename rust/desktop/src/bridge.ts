import {invoke as nativeInvoke, type InvokeArgs, type InvokeOptions} from "@tauri-apps/api/core";
import {localizeDiagnostic} from "./locale";

let libraryReady: Promise<void> | undefined;

export async function invoke<T>(command: string, args?: InvokeArgs, options?: InvokeOptions): Promise<T> {
  try {
    await (libraryReady ??= nativeInvoke<void>("initialize_library"));
    return await nativeInvoke<T>(command, args, options);
  }
  catch (error) { throw localizeDiagnostic(String(error)); }
}
