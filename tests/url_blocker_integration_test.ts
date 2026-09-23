import { assert, assertEquals } from "@std/assert";
import {
  closeSharedBrowserSession,
  createSharedBrowserSession,
  playScenario,
} from "../src/runtime.ts";
import type { Scenario } from "../src/types.ts";

// Opt in explicitly: this test launches the supplied Chrome for Testing executable.
const chrome = Deno.env.get("CRER_TEST_CHROME");
Deno.test({
  name: "URL blocking applies before initial navigation and changes across reused Chrome sessions",
  ignore: !chrome,
  async fn() {
    const requests: string[] = [];
    const server = Deno.serve({ hostname: "127.0.0.1", port: 0, onListen() {} }, (request) => {
      const path = new URL(request.url).pathname;
      requests.push(path);
      if (path === "/redirect") {
        return Response.redirect(new URL("/ad.svg?redirected=1", request.url), 302);
      }
      if (path.endsWith(".svg")) {
        return new Response(
          "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"10\" height=\"10\"><rect width=\"10\" height=\"10\" fill=\"red\"/></svg>",
          {
            headers: { "content-type": "image/svg+xml", "cache-control": "max-age=3600" },
          },
        );
      }
      return new Response(
        `<!doctype html><body><p id="status" role="status">loading</p><script>
        Promise.all(["ad.svg", "logo.svg", "redirect"].map(path => new Promise(resolve => {
          const img = new Image();
          img.onload = () => resolve(path + ":loaded");
          img.onerror = () => resolve(path + ":blocked");
          img.src = "/" + path;
          document.body.append(img);
        }))).then(results => document.getElementById("status").textContent = results.join(" "));
      </script>`,
        { headers: { "content-type": "text/html", "cache-control": "no-store" } },
      );
    });
    const sharedSession = createSharedBrowserSession();
    const profileDir = `${Deno.cwd()}/.crer/profiles/url-blocking-${crypto.randomUUID()}`;
    const base = `http://127.0.0.1:${server.addr.port}`;
    try {
      const cases = [
        {
          patterns: [String.raw`/ad\.svg(\?.*)?$`],
          expected: "ad.svg:blocked logo.svg:loaded redirect:blocked",
          blocked: "/ad.svg",
          allowed: "/logo.svg",
        },
        {
          patterns: [String.raw`/logo\.svg$`],
          expected: "ad.svg:loaded logo.svg:blocked redirect:loaded",
          blocked: "/logo.svg",
          allowed: "/ad.svg",
        },
        { patterns: undefined, expected: "ad.svg:loaded logo.svg:loaded redirect:loaded" },
      ];
      for (const [index, item] of cases.entries()) {
        requests.length = 0;
        const scenario: Scenario = {
          version: 1,
          name: "url-blocking-integration",
          browser: {
            chrome: "chrome-for-testing@pinned",
            initial_url: `${base}/?case=${index}`,
            block_urls: item.patterns,
          },
          steps: [
            { do: "wait_for", locator_hint: { text: item.expected }, state: "visible" },
            { do: "wait_for", state: "network_idle" },
            { do: "navigate", url: `${base}/?case=${index}&again=1` },
            { do: "wait_for", locator_hint: { text: item.expected }, state: "visible" },
            { do: "wait_for", state: "network_idle" },
          ],
        };
        const result = await playScenario(scenario, {
          chromePath: chrome!,
          profileDir,
          sharedSession,
        });
        assertEquals(result.code, 0, JSON.stringify(result));
        const launch = JSON.parse(await Deno.readTextFile(`${result.runDir}/launch.json`));
        assertEquals(launch.reused === true, index > 0);
        if (item.blocked) {
          assert(!requests.includes(item.blocked), `unexpected request to ${item.blocked}`);
        }
        if (item.allowed) {
          assert(requests.includes(item.allowed), `missing request to ${item.allowed}`);
        }
      }
    } finally {
      await closeSharedBrowserSession(sharedSession);
      await server.shutdown();
      await Deno.remove(profileDir, { recursive: true }).catch(() => {});
    }
  },
});
