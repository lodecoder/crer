import type { TemplateScreenshotPolicy, WindowBounds } from "./types.ts";

/** Parse a run-only override; zero is a valid, fully transparent value. */
export function parsePlanWindowOpacityOverride(value: string | undefined): number | undefined {
  if (value === undefined) return undefined;
  const opacity = Number(value);
  if (!value.trim() || !Number.isFinite(opacity) || opacity < 0 || opacity > 1) {
    throw new Error("--plan-window-opacity-override must be a finite number from 0 to 1");
  }
  return opacity;
}

export function parseTemplateScreenshotPolicy(
  value: string | undefined,
): TemplateScreenshotPolicy | undefined {
  if (value === undefined) return undefined;
  if (value !== "all" && value !== "failure-only") {
    throw new Error("--template-screenshots must be all or failure-only");
  }
  return value;
}

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
