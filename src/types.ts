export type FailureKind =
  | "navigation"
  | "timeout"
  | "action"
  | "assertion"
  | "jitter_bounds"
  | "template";
export type FailurePolicy = "abort" | "continue";
export type Point = { x: number; y: number };
export type Jitter = {
  enabled: boolean;
  distribution: "none" | "uniform" | "normal";
  radius_px: number;
  min_distance_from_edge_px: number;
  out_of_bounds: "fail" | "disable-for-step";
};
export type TemplateDefaults = {
  min_similarity?: number;
  random_inset_px?: number;
  on_missing?: "fail" | "skip";
};
export type Step = {
  do: string;
  at?: Point;
  delta?: Point;
  value?: string;
  key?: string;
  url?: string;
  state?: string;
  /** Wait after this successful operation before executing the next step. */
  delay_ms?: number;
  jitter?: Jitter;
  locator_hint?: { role?: string; name?: string; text?: string };
  template?: { path: string } & TemplateDefaults;
  then?: Step[];
  [key: string]: unknown;
};
export type Scenario = {
  version: 1;
  name: string;
  browser: {
    chrome: string;
    profile?: string;
    initial_url: string;
    window?: {
      bounds?: { left?: number; top?: number; width?: number; height?: number };
      /** Requested CfT content size; scrollbars can make the CSS viewport smaller. */
      content?: { width: number; height: number };
      /** CSS viewport used for coordinates; defaults to content for older scenarios. */
      viewport?: { width: number; height: number };
    };
    display?: {
      expected_dpr?: number;
      browser_zoom?: number;
      zoom_check?: "strict" | "advisory" | "off";
    };
  };
  playback?: {
    seed?: string;
    speed?: number;
    step_delay_ms?: number;
    /** Defaults for click steps that use image template matching. */
    template?: TemplateDefaults;
    jitter?: Jitter;
    timeouts?: { navigation_ms?: number; action_ms?: number };
    on_failure?: Partial<Record<FailureKind | "default", FailurePolicy>>;
  };
  steps: Step[];
};
export type PlanNode = { scenario: string } | { serial: PlanNode[] } | {
  parallel: { fail_fast?: boolean; jobs: PlanNode[] };
};
export type Plan = {
  version: 1;
  name: string;
  max_parallel?: number;
  timeouts?: { worker_ms?: number };
  on_failure?: Partial<
    Record<"default" | "scenario_failure" | "timeout" | "environment", FailurePolicy>
  >;
  run: PlanNode;
};
export type RunResult = { code: 0 | 2 | 3 | 4 | 5; failures: string[]; runDir?: string };
