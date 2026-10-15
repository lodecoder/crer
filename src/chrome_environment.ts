/** Keep the CfT manifest's DPI awareness even when launched from a compatibility-mode shell. */
export function chromeEnvironment(
  compatibilityLayer = Deno.env.get("__COMPAT_LAYER"),
): Record<string, string> {
  if (compatibilityLayer === undefined) return {};
  // DPIUNAWARE makes Windows bitmap-scale the entire window while CDP still reports DPR 1.
  // HIGHDPIAWARE also replaces Chrome's per-monitor awareness with system DPI awareness.
  const layers = compatibilityLayer.split(/\s+/).filter((layer) =>
    layer && !/^(DPIUNAWARE|GDIDPISCALING|HIGHDPIAWARE)$/i.test(layer)
  );
  const value = layers.some((layer) => layer !== "~") ? layers.join(" ") : "";
  // An empty value masks the inherited variable without changing the parent shell's environment.
  return { __COMPAT_LAYER: value };
}
