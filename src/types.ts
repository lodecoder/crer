export type FailureKind = "navigation" | "timeout" | "action" | "assertion" | "jitter_bounds";
export type FailurePolicy = "abort" | "continue";
export type Point = { x: number; y: number };
export type Jitter = {
  enabled: boolean;
  distribution: "none" | "uniform" | "normal";
  radius_px: number;
  min_distance_from_edge_px: number;
  out_of_bounds: "fail" | "disable-for-step";
};
export type Step = {
  do: string;
  at?: Point;
  delta?: Point;
  value?: string;
  key?: string;
  url?: string;
  state?: string;
  jitter?: Jitter;
  locator_hint?: { role?: string; name?: string };
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
      content?: { width: number; height: number };
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
