import { dirname, isAbsolute, resolve } from "node:path";

/** Returns an absolute directory for a scenario or plan file path. */
export function fileDirectory(file: string): string {
  return dirname(resolve(file));
}

/** Resolves a path against a known directory while preserving absolute inputs. */
export function resolveFromDirectory(directory: string | undefined, path: string): string {
  return isAbsolute(path) ? resolve(path) : resolve(directory ?? Deno.cwd(), path);
}
