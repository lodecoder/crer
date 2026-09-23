import { assertEquals, assertRejects, assertThrows } from "@std/assert";
import type { CdpEvent } from "../src/cdp.ts";
import { EnvironmentError } from "../src/errors.ts";
import { UrlBlocker } from "../src/url_blocker.ts";

class FakeCdp {
  calls: { method: string; params: Record<string, unknown>; sessionId?: string }[] = [];
  listener?: (event: CdpEvent) => void;
  closed = false;
  rejectMethod?: string;
  call<T>(method: string, params: Record<string, unknown> = {}, sessionId?: string): Promise<T> {
    this.calls.push({ method, params, sessionId });
    return method === this.rejectMethod
      ? Promise.reject(new Error("interception rejected"))
      : Promise.resolve({} as T);
  }
  on(_method: string, listener: (event: CdpEvent) => void) {
    this.listener = listener;
    return () => {
      this.listener = undefined;
    };
  }
  close() {
    this.closed = true;
  }
  request(url: string, sessionId = "page") {
    this.listener?.({ sessionId, params: { requestId: "request", request: { url } } });
  }
}

Deno.test("blocks matching URLs using regex alternation, anchors, query strings and repeated matches", async () => {
  const cdp = new FakeCdp();
  const blocker = new UrlBlocker(cdp, "page");
  await blocker.configure([
    String.raw`^https://cdn\.example/images/(ad|banner)\d+\.(png|jpg)(\?.*)?$`,
    "/track\\?",
  ]);
  for (
    const url of [
      "https://cdn.example/images/ad1.png",
      "https://cdn.example/images/banner2.jpg?v=1",
      "https://site.example/track?id=1",
      "https://cdn.example/images/ad1.png",
    ]
  ) {
    cdp.request(url);
    assertEquals(cdp.calls.at(-1), {
      method: "Fetch.failRequest",
      params: { requestId: "request", errorReason: "BlockedByClient" },
      sessionId: "page",
    });
  }
  for (
    const url of [
      "https://cdn.example/images/logo.png",
      "https://other.example/images/ad1.png",
      "https://cdn.example/images/ad1.png/extra",
    ]
  ) {
    cdp.request(url);
    assertEquals(cdp.calls.at(-1)?.method, "Fetch.continueRequest");
  }
  const count = cdp.calls.length;
  cdp.request("https://site.example/track?id=1", "other-page");
  assertEquals(cdp.calls.length, count);
});

Deno.test("reused sessions replace patterns and restore cache/service workers when disabled", async () => {
  const cdp = new FakeCdp();
  const blocker = new UrlBlocker(cdp, "page");
  await blocker.configure();
  assertEquals(cdp.calls, []);
  await blocker.configure(["old"]);
  assertEquals(cdp.calls, [
    { method: "Network.setBypassServiceWorker", params: { bypass: true }, sessionId: "page" },
    { method: "Network.setCacheDisabled", params: { cacheDisabled: true }, sessionId: "page" },
    {
      method: "Fetch.enable",
      params: { patterns: [{ urlPattern: "*", requestStage: "Request" }] },
      sessionId: "page",
    },
  ]);
  await blocker.configure(["new"]);
  cdp.request("https://example/old");
  assertEquals(cdp.calls.at(-1)?.method, "Fetch.continueRequest");
  cdp.request("https://example/new");
  assertEquals(cdp.calls.at(-1)?.method, "Fetch.failRequest");
  await blocker.configure([]);
  assertEquals(cdp.calls.slice(-3), [
    { method: "Network.setBypassServiceWorker", params: { bypass: false }, sessionId: "page" },
    { method: "Network.setCacheDisabled", params: { cacheDisabled: false }, sessionId: "page" },
    { method: "Fetch.disable", params: {}, sessionId: "page" },
  ]);
  // A queued event after disable must still be released.
  cdp.request("https://example/new");
  assertEquals(cdp.calls.at(-1)?.method, "Fetch.continueRequest");
  await blocker.configure(["again"]);
  await blocker.configure();
  assertEquals(cdp.calls.at(-1)?.method, "Fetch.disable");
});

Deno.test("interception errors close the connection and surface as environment failures", async () => {
  for (const method of ["Fetch.failRequest", "Fetch.continueRequest"]) {
    const cdp = new FakeCdp();
    const blocker = new UrlBlocker(cdp, "page");
    await blocker.configure(["blocked"]);
    cdp.rejectMethod = method;
    cdp.request(
      method === "Fetch.failRequest" ? "https://example/blocked" : "https://example/allowed",
    );
    await Promise.resolve();
    assertEquals(cdp.closed, true);
    assertThrows(() => blocker.assertHealthy(), EnvironmentError, "URL blocking failed");
    await assertRejects(() => blocker.configure([]), EnvironmentError);
  }
});

Deno.test("interception setup errors propagate", async () => {
  const cdp = new FakeCdp();
  cdp.rejectMethod = "Fetch.enable";
  await assertRejects(
    () => new UrlBlocker(cdp, "page").configure(["blocked"]),
    Error,
    "interception rejected",
  );
});
