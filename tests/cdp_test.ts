import { assertEquals, assertRejects } from "@std/assert";
import { Cdp, CdpConnectionError, type CdpSocket } from "../src/cdp.ts";

class FakeSocket implements CdpSocket {
  readyState = 0;
  onopen: (() => void) | null = null;
  onerror: (() => void) | null = null;
  onclose: (() => void) | null = null;
  onmessage: ((event: { data: string }) => void) | null = null;
  sent: string[] = [];
  send(data: string) {
    this.sent.push(data);
  }
  close() {
    this.readyState = 3;
  }
}

Deno.test("rejects pending and future CDP calls when the socket closes", async () => {
  const socket = new FakeSocket();
  const cdp = new Cdp("ws://test", () => socket);
  const opening = cdp.open();
  socket.readyState = 1;
  socket.onopen?.();
  await opening;

  const pending = cdp.call("Page.enable");
  assertEquals(socket.sent.length, 1);
  socket.onclose?.();
  await assertRejects(() => pending, CdpConnectionError, "connection closed");
  await assertRejects(() => cdp.call("Page.enable"), CdpConnectionError, "not open");
});

Deno.test("resolves CDP replies and rejects protocol errors", async () => {
  const socket = new FakeSocket();
  const cdp = new Cdp("ws://test", () => socket);
  const opening = cdp.open();
  socket.onopen?.();
  await opening;
  const success = cdp.call<{ ok: boolean }>("Runtime.enable");
  socket.onmessage?.({ data: JSON.stringify({ id: 1, result: { ok: true } }) });
  assertEquals(await success, { ok: true });
  const failure = cdp.call("Runtime.evaluate");
  socket.onmessage?.({ data: JSON.stringify({ id: 2, error: { message: "rejected" } }) });
  await assertRejects(() => failure, Error, "rejected");
  cdp.close();
});

Deno.test("rejects and detaches listeners when closed while connecting", async () => {
  const socket = new FakeSocket();
  const cdp = new Cdp("ws://test", () => socket);
  const opening = cdp.open();
  cdp.close();
  await assertRejects(() => opening, CdpConnectionError, "closed by client");
  assertEquals(socket.onopen, null);
  assertEquals(socket.onmessage, null);
});
