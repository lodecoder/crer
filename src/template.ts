import type { Point } from "./types.ts";

export type TemplateOptions = {
  path: string;
  min_similarity?: number;
  random_inset_px?: number;
  on_missing?: "fail" | "skip";
};
export type TemplateMatch = {
  x: number;
  y: number;
  width: number;
  height: number;
  similarity: number;
};

export class TemplateMatchError extends Error {
  constructor(message: string, public readonly screenshot: string) {
    super(message);
    this.name = "TemplateMatchError";
  }
}

type Evaluation<T> = {
  result: { value?: T; description?: string };
  exceptionDetails?: { text?: string; exception?: { description?: string } };
};

function evaluationFailure<T>(evaluation: Evaluation<T>): string | undefined {
  return evaluation.exceptionDetails?.exception?.description
    ?? evaluation.exceptionDetails?.text;
}

function validMatch(value: unknown): value is TemplateMatch {
  if (!value || typeof value !== "object") return false;
  const match = value as Record<string, unknown>;
  return [match.x, match.y, match.width, match.height, match.similarity].every((part) =>
    typeof part === "number" && Number.isFinite(part)
  ) && (match.width as number) > 0 && (match.height as number) > 0;
}

const toBase64 = (bytes: Uint8Array) => {
  let text = "";
  for (let offset = 0; offset < bytes.length; offset += 0x8000) {
    text += String.fromCharCode(...bytes.subarray(offset, offset + 0x8000));
  }
  return btoa(text);
};

export function templatePath(base: string | undefined, path: string) {
  if (/^(?:[A-Za-z]:[\\/]|\\\\)/.test(path)) return path;
  return `${base ?? Deno.cwd()}${base ? "/" : ""}${path}`;
}

export function randomPointInMatch(
  match: TemplateMatch,
  inset: number,
  random: () => number,
): Point {
  const safeInset = Math.max(
    0,
    Math.min(
      Math.floor(inset),
      Math.floor((match.width - 1) / 2),
      Math.floor((match.height - 1) / 2),
    ),
  );
  const width = match.width - safeInset * 2;
  const height = match.height - safeInset * 2;
  return {
    x: match.x + safeInset + Math.floor(random() * Math.max(1, width)),
    y: match.y + safeInset + Math.floor(random() * Math.max(1, height)),
  };
}

export async function matchTemplate(
  cdpCall: <T>(method: string, params: Record<string, unknown>) => Promise<T>,
  options: TemplateOptions,
  base: string | undefined,
): Promise<{ match: TemplateMatch; screenshot: string }> {
  const [screenshot, template] = await Promise.all([
    cdpCall<{ data: string }>("Page.captureScreenshot", { format: "png" }),
    Deno.readFile(templatePath(base, options.path)),
  ]);
  const templateUrl = `data:image/png;base64,${toBase64(template)}`;
  const result = await cdpCall<Evaluation<TemplateMatch>>("Runtime.evaluate", {
    expression: `(async () => {
      const load = (url) => new Promise((resolve, reject) => {
        const image = new Image(); image.onload = () => resolve(image); image.onerror = () => reject(new Error("image decode failed")); image.src = url;
      });
      const [screen, source] = await Promise.all([load(${
      JSON.stringify(`data:image/png;base64,${screenshot.data}`)
    }), load(${JSON.stringify(templateUrl)})]);
      if (source.width > screen.width || source.height > screen.height) throw new Error("template is larger than screenshot");
      const canvas = document.createElement("canvas"); canvas.width = screen.width; canvas.height = screen.height;
      const context = canvas.getContext("2d", { willReadFrequently: true });
      context.drawImage(screen, 0, 0);
      const pixels = context.getImageData(0, 0, screen.width, screen.height).data;
      canvas.width = source.width; canvas.height = source.height;
      context.drawImage(source, 0, 0);
      const pattern = context.getImageData(0, 0, source.width, source.height).data;
      const samples = [];
      for (let gy = 0; gy < Math.min(8, source.height); gy++) for (let gx = 0; gx < Math.min(8, source.width); gx++) samples.push([Math.floor((gx + 0.5) * source.width / Math.min(8, source.width)), Math.floor((gy + 0.5) * source.height / Math.min(8, source.height))]);
      let best = { x: 0, y: 0, width: source.width, height: source.height, similarity: -1 };
      let bestDifference = Number.POSITIVE_INFINITY;
      for (let y = 0; y <= screen.height - source.height; y++) for (let x = 0; x <= screen.width - source.width; x++) {
        let difference = 0;
        for (const [sx, sy] of samples) {
          const patternOffset = (sy * source.width + sx) * 4;
          const screenOffset = ((y + sy) * screen.width + x + sx) * 4;
          difference += Math.abs(pattern[patternOffset] - pixels[screenOffset]) + Math.abs(pattern[patternOffset + 1] - pixels[screenOffset + 1]) + Math.abs(pattern[patternOffset + 2] - pixels[screenOffset + 2]);
          if (difference >= bestDifference) break;
        }
        if (difference < bestDifference) { bestDifference = difference; best = { x, y, width: source.width, height: source.height, similarity: 1 - difference / (samples.length * 255 * 3) }; }
      }
      return best;
    })()`,
    awaitPromise: true,
    returnByValue: true,
  });
  const failure = evaluationFailure(result);
  if (failure) {
    throw new TemplateMatchError(`template match evaluation failed: ${failure}`, screenshot.data);
  }
  const match = result.result.value;
  if (!validMatch(match)) {
    throw new TemplateMatchError("template match returned an invalid result", screenshot.data);
  }
  return { match, screenshot: screenshot.data };
}

/** Finds distinct template rectangles at or above a threshold in one screenshot. */
export async function matchTemplates(
  cdpCall: <T>(method: string, params: Record<string, unknown>) => Promise<T>,
  options: TemplateOptions,
  base: string | undefined,
  minSimilarity: number,
  maxMatches: number,
): Promise<{ matches: TemplateMatch[]; screenshot: string }> {
  const [screenshot, template] = await Promise.all([
    cdpCall<{ data: string }>("Page.captureScreenshot", { format: "png" }),
    Deno.readFile(templatePath(base, options.path)),
  ]);
  const templateUrl = `data:image/png;base64,${toBase64(template)}`;
  const result = await cdpCall<Evaluation<TemplateMatch[]>>("Runtime.evaluate", {
    expression: `(async () => {
      const load = (url) => new Promise((resolve, reject) => {
        const image = new Image(); image.onload = () => resolve(image); image.onerror = () => reject(new Error("image decode failed")); image.src = url;
      });
      const [screen, source] = await Promise.all([load(${
      JSON.stringify(`data:image/png;base64,${screenshot.data}`)
    }), load(${JSON.stringify(templateUrl)})]);
      if (source.width > screen.width || source.height > screen.height) throw new Error("template is larger than screenshot");
      const canvas = document.createElement("canvas"); canvas.width = screen.width; canvas.height = screen.height;
      const context = canvas.getContext("2d", { willReadFrequently: true });
      context.drawImage(screen, 0, 0);
      const pixels = context.getImageData(0, 0, screen.width, screen.height).data;
      canvas.width = source.width; canvas.height = source.height;
      context.drawImage(source, 0, 0);
      const pattern = context.getImageData(0, 0, source.width, source.height).data;
      const samples = [];
      for (let gy = 0; gy < Math.min(8, source.height); gy++) for (let gx = 0; gx < Math.min(8, source.width); gx++) samples.push([Math.floor((gx + 0.5) * source.width / Math.min(8, source.width)), Math.floor((gy + 0.5) * source.height / Math.min(8, source.height))]);
      const candidates = [];
      const overlap = (a, b) => {
        const left = Math.max(a.x, b.x), top = Math.max(a.y, b.y);
        const right = Math.min(a.x + a.width, b.x + b.width), bottom = Math.min(a.y + a.height, b.y + b.height);
        const intersection = Math.max(0, right - left) * Math.max(0, bottom - top);
        const union = a.width * a.height + b.width * b.height - intersection;
        return union ? intersection / union : 0;
      };
      const candidateLimit = Math.max(${maxMatches} * 20, ${maxMatches});
      for (let y = 0; y <= screen.height - source.height; y++) for (let x = 0; x <= screen.width - source.width; x++) {
        let difference = 0;
        for (const [sx, sy] of samples) {
          const patternOffset = (sy * source.width + sx) * 4;
          const screenOffset = ((y + sy) * screen.width + x + sx) * 4;
          difference += Math.abs(pattern[patternOffset] - pixels[screenOffset]) + Math.abs(pattern[patternOffset + 1] - pixels[screenOffset + 1]) + Math.abs(pattern[patternOffset + 2] - pixels[screenOffset + 2]);
          if (difference > (1 - ${minSimilarity}) * samples.length * 255 * 3) break;
        }
        const similarity = 1 - difference / (samples.length * 255 * 3);
        if (similarity < ${minSimilarity}) continue;
        const candidate = { x, y, width: source.width, height: source.height, similarity };
        const duplicate = candidates.findIndex((existing) => overlap(existing, candidate) >= 0.5);
        if (duplicate >= 0) {
          if (candidate.similarity > candidates[duplicate].similarity) candidates[duplicate] = candidate;
        } else if (candidates.length < candidateLimit) {
          candidates.push(candidate);
        } else {
          let worst = 0;
          for (let index = 1; index < candidates.length; index++) if (candidates[index].similarity < candidates[worst].similarity) worst = index;
          if (candidate.similarity > candidates[worst].similarity) candidates[worst] = candidate;
        }
      }
      return candidates.sort((a, b) => b.similarity - a.similarity).slice(0, ${maxMatches});
    })()`,
    awaitPromise: true,
    returnByValue: true,
  });
  const failure = evaluationFailure(result);
  if (failure) {
    throw new TemplateMatchError(`template match evaluation failed: ${failure}`, screenshot.data);
  }
  if (!Array.isArray(result.result.value) || !result.result.value.every(validMatch)) {
    throw new TemplateMatchError("template matching returned an invalid result", screenshot.data);
  }
  return {
    matches: result.result.value,
    screenshot: screenshot.data,
  };
}
