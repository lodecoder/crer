export type FailureKind =
  | "navigation"
  | "timeout"
  | "action"
  | "assertion"
  | "jitter_bounds"
  | "template";
export type FailurePolicy = "abort" | "continue";
export type Point = { x: number; y: number };
export type WindowBounds = { left: number; top: number; width?: number; height?: number };
export type Jitter = {
  enabled: false;
  distribution?: "none" | "uniform" | "normal";
  radius_px?: number;
  min_distance_from_edge_px?: number;
  out_of_bounds?: "fail" | "disable-for-step";
} | {
  enabled: true;
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
export type FunctionDefinition = {
  params?: string[];
  steps: Step[];
};
export type Step = {
  do: string;
  at?: Point;
  delta?: Point;
  value?: string;
  message?: string;
  function?: string;
  args?: Record<string, string | number>;
  key?: string;
  url?: string;
  state?: string;
  /** Wait after this successful operation before executing the next step. */
  delay_ms?: number;
  /** Keep the primary button pressed for this duration on a click step. */
  hold_ms?: number;
  jitter?: Jitter;
  locator_hint?: { role?: string; name?: string; text?: string };
  template?: { path: string } & TemplateDefaults;
  equals?: { left: string | number; right: string | number };
  /** Weekdays accepted by a conditional step, using mon through sun. */
  weekdays?: string[];
  /** Optional IANA time zone for a weekday conditional; defaults to the local system zone. */
  time_zone?: string;
  then?: Step[];
  else?: Step[];
  count?: number;
  max_matches?: number;
  max_attempts?: number;
  on_limit?: "fail" | "continue";
  steps?: Step[];
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
      bounds?: WindowBounds;
      /** Bring CfT to the foreground before every replay step (Windows only). */
      foreground?: boolean;
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
  /** Named reusable step sequences, invoked by { do: call, function: <name> }. */
  functions?: Record<string, Step[] | FunctionDefinition>;
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
