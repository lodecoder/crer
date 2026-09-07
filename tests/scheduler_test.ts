import { assertEquals } from "@std/assert";
import { KeyedLock, mapWithCancellation, mapWithConcurrency, Semaphore } from "../src/scheduler.ts";

Deno.test("limits concurrent work while preserving result order", async () => {
  let active = 0;
  let peak = 0;
  const result = await mapWithConcurrency([1, 2, 3, 4], 2, async (value) => {
    active++;
    peak = Math.max(peak, active);
    await new Promise((resolve) => setTimeout(resolve, 5));
    active--;
    return value * 10;
  });
  assertEquals(peak, 2);
  assertEquals(result, [10, 20, 30, 40]);
});

Deno.test("shares one concurrency limit across nested work", async () => {
  const gate = new Semaphore(2);
  let active = 0;
  let peak = 0;
  const leaf = () =>
    gate.run(async () => {
      active++;
      peak = Math.max(peak, active);
      await new Promise((resolve) => setTimeout(resolve, 5));
      active--;
    });
  await Promise.all([
    Promise.all([leaf(), leaf()]),
    Promise.all([leaf(), leaf()]),
  ]);
  assertEquals(peak, 2);
});

Deno.test("does not start queued work after cancellation", async () => {
  const gate = new Semaphore(1);
  const controller = new AbortController();
  let secondStarted = false;
  const first = gate.run(async () => {
    await new Promise((resolve) => setTimeout(resolve, 5));
  });
  const second = gate.run(() => {
    secondStarted = true;
    return Promise.resolve();
  }, controller.signal);
  controller.abort();
  await Promise.all([first, second]);
  assertEquals(secondStarted, false);
});

Deno.test("stops scheduling after a fail-fast result", async () => {
  const started: number[] = [];
  const result = await mapWithConcurrency([1, 2, 3], 1, (value) => {
    started.push(value);
    return Promise.resolve(value);
  }, (value) => value === 1);
  assertEquals(started, [1]);
  assertEquals(result, [1]);
});

Deno.test("fail-fast aborts running work and does not start queued work", async () => {
  const started: number[] = [];
  let runningAborted = false;
  await mapWithCancellation(
    [1, 2, 3],
    2,
    async (value, signal) => {
      started.push(value);
      if (value === 1) return await Promise.resolve("failed");
      await new Promise<void>((resolve) => {
        signal.addEventListener("abort", () => {
          runningAborted = true;
          resolve();
        }, { once: true });
      });
      return "cancelled";
    },
    (result) => result === "failed",
  );
  assertEquals(started, [1, 2]);
  assertEquals(runningAborted, true);
});

Deno.test("serializes identical persistent profile keys only", async () => {
  const locks = new KeyedLock();
  let sameKeyActive = 0;
  let sameKeyPeak = 0;
  const run = (key: string) =>
    locks.run(key, async () => {
      if (key === "same") sameKeyPeak = Math.max(sameKeyPeak, ++sameKeyActive);
      await new Promise((resolve) => setTimeout(resolve, 5));
      if (key === "same") sameKeyActive--;
    });
  await Promise.all([run("same"), run("same"), run("other")]);
  assertEquals(sameKeyPeak, 1);
});
