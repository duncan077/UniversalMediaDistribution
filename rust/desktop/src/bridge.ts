import {invoke as nativeInvoke, type InvokeArgs, type InvokeOptions} from "@tauri-apps/api/core";
import {localizeDiagnostic} from "./locale";

export async function invoke<T>(command: string, args?: InvokeArgs, options?: InvokeOptions): Promise<T> {
  try { return await nativeInvoke<T>(command, args, options); }
  catch (error) { throw localizeDiagnostic(String(error)); }
}
