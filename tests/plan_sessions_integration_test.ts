import { assert, assertEquals } from "@std/assert";
import type { RunResult } from "../src/types.ts";

const chrome = Deno.env.get("CRER_TEST_CHROME");

Deno.test({
  name: "plan reuses separate profile windows in parallel and isolates worker timeouts",
  ignore: Deno.build.os !== "windows" || !chrome,
  async fn() {
    const id = crypto.randomUUID();
    const directory = await Deno.makeTempDir({ dir: ".crer", prefix: "plan-sessions-" });
    const profiles = ["a", "b"].map((name) => `.crer/profiles/plan-${id}-${name}`);
    const seen = new Map<string, string>();
    const arrived = new Set<string>();
    let release!: () => void;
    const barrier = new Promise<void>((resolve) => release = resolve);
    let overlapped = false;
    let barrierExpired = false;
    const timer = setTimeout(() => {
      barrierExpired = true;
      release();
    }, 10_000);
    const server = Deno.serve(
      { hostname: "127.0.0.1", port: 0, onListen() {} },
      async (request) => {
        const url = new URL(request.url);
        if (url.pathname === "/seen") {
          seen.set(url.searchParams.get("name")!, url.searchParams.get("token")!);
          return new Response("ok");
        }
        const name = url.pathname.slice(1);
        if (name === "a1" || name === "b1") {
          arrived.add(name);
          if (arrived.size === 2) {
            overlapped = !barrierExpired;
            clearTimeout(timer);
            release();
          }
          await barrier;
        }
        return new Response(
          `<!doctype html><title>CRER parallel reuse test</title><script>
        const token = sessionStorage.token ||= crypto.randomUUID();
        fetch('/seen?name=${name}&token=' + token);
      </script><p>${name}</p>`,
          { headers: { "content-type": "text/html", "cache-control": "no-store" } },
        );
      },
    );
    const scenario = async (name: string, profile: string, ms = 100) => {
      await Deno.writeTextFile(
        `${directory}/${name}.yaml`,
        JSON.stringify({
          version: 1,
          name,
          browser: {
            profile: `persistent:${profile}`,
            initial_url: `http://127.0.0.1:${server.addr.port}/${name}`,
            window: {
              opacity: 0.7,
              foreground: false,
              bounds: { left: 20, top: 20, width: 400, height: 300 },
            },
          },
          playback: { log_color: profile === profiles[0] ? "cyan" : "green" },
          steps: [{ do: "log", message: "started" }, { do: "wait_for", state: "network_idle" }, {
            do: "sleep",
            ms,
          }],
        }),
      );
    };
    const run = async (
      jobs: string[][],
      workerMs: number,
      expectedCode: number,
      opacityOverride?: number,
    ) => {
      const planPath = `${directory}/plan.yaml`;
      await Deno.writeTextFile(
        planPath,
        JSON.stringify({
          version: 1,
          name: "parallel reuse",
          max_parallel: 2,
          browser_session: { reuse: "same-profile", focus: "once" },
          timeouts: { worker_ms: workerMs },
          on_failure: { timeout: "continue" },
          run: {
            parallel: {
              jobs: jobs.map((names) => ({
                serial: names.map((name) => ({ scenario: `${name}.yaml` })),
              })),
            },
          },
        }),
      );
      const output = await new Deno.Command(Deno.execPath(), {
        args: [
          "run",
          "-A",
          "src/main.ts",
          "run",
          planPath,
          "--chrome",
          chrome!,
          ...(opacityOverride === undefined
            ? []
            : ["--plan-window-opacity-override", String(opacityOverride)]),
        ],
        stdout: "piped",
        stderr: "piped",
      }).output();
      assertEquals(output.code, expectedCode, new TextDecoder().decode(output.stderr));
      const stdout = new TextDecoder().decode(output.stdout);
      for (const name of jobs.flat()) assert(stdout.includes(`[${name}] started\n`), stdout);
      assertEquals(stdout.includes("[crer]"), false);
      assertEquals(
        stdout.includes("\x1b["),
        false,
        "redirected logs must not contain color escapes",
      );
      const results = JSON.parse(stdout.slice(stdout.indexOf("[\n"))) as RunResult[];
      assertEquals(results.length, jobs.flat().length);
      return await Promise.all(results.map(async (result) => ({
        ...result,
        metadata: JSON.parse(await Deno.readTextFile(`${result.runDir}/run.json`)),
      })));
    };
    try {
      for (const name of ["a1", "a2", "b1", "b2"]) {
        await scenario(name, profiles[name[0] === "a" ? 0 : 1]);
      }
      const results = await run([["a1", "a2"], ["b1", "b2"]], 0, 0, 0);
      for (const result of results) assertEquals(result.metadata.windowOpacityOverride, 0);
      assert(overlapped, "both first scenarios must reach the server before either is released");
      for (const prefix of ["a", "b"]) {
        const first = results.find((result) => result.metadata.scenario === `${prefix}1`)!;
        const last = results.find((result) => result.metadata.scenario === `${prefix}2`)!;
        assertEquals(first.metadata.browserSession.reused, false);
        assertEquals(last.metadata.browserSession.reused, true);
        assert(seen.get(`${prefix}1`));
        assertEquals(seen.get(`${prefix}1`), seen.get(`${prefix}2`));
        assertEquals(
          await Deno.stat(`${first.runDir}/shutdown.json`).then(() => true, () => false),
          false,
        );
        const shutdown = JSON.parse(await Deno.readTextFile(`${last.runDir}/shutdown.json`));
        assertEquals(shutdown.exited, true);
      }
      assert(seen.get("a1") !== seen.get("b1"), "profiles must use separate browser pages");

      // A timeout discards only A's session; B's retained window must still be reusable.
      await scenario("t1", profiles[0], 15_000);
      await scenario("t2", profiles[0]);
      await scenario("u1", profiles[1], 2000);
      await scenario("u2", profiles[1], 3000);
      const timed = await run([["t1", "t2"], ["u1", "u2"]], 6000, 4);
      const byName = new Map(timed.map((result) => [result.metadata.scenario, result]));
      for (const result of timed) assertEquals(result.metadata.windowOpacityOverride, undefined);
      assert(
        byName.get("t1")!.failures.some((failure) => failure.includes("plan:timeout:worker_ms")),
      );
      assertEquals(byName.get("t2")!.metadata.browserSession.reused, false);
      assertEquals(byName.get("u2")!.metadata.browserSession.reused, true);
      for (const name of ["t2", "u1", "u2"]) assertEquals(byName.get(name)!.code, 0);
      for (const name of ["t1", "t2", "u2"]) {
        const shutdown = JSON.parse(
          await Deno.readTextFile(`${byName.get(name)!.runDir}/shutdown.json`),
        );
        assertEquals(shutdown.exited, true);
      }
    } finally {
      clearTimeout(timer);
      release();
      await server.shutdown();
      await Deno.remove(directory, { recursive: true });
      for (const profile of profiles) {
        await Deno.remove(profile, { recursive: true }).catch(() => {});
      }
    }
  },
});
