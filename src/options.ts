import type { WindowBounds } from "./types.ts";

/** Parse the run-only, non-merging browser window bounds override. */
export function parsePlanWindowBoundsOverride(value: string | undefined): WindowBounds | undefined {
  if (value === undefined) return undefined;
  const values = value.split(",").map((part) => Number(part.trim()));
  if (values.length !== 2 && values.length !== 4) {
    throw new Error("--plan-window-bounds-override must be left,top or left,top,width,height");
  }
  if (!values.every(Number.isInteger)) {
    throw new Error("--plan-window-bounds-override values must be integers");
  }
  const [left, top, width, height] = values;
  if ((width !== undefined && width <= 0) || (height !== undefined && height <= 0)) {
    throw new Error("--plan-window-bounds-override width and height must be positive");
  }
  return { left, top, ...(width === undefined ? {} : { width, height }) };
}
