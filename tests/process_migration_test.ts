import assert from "node:assert/strict";
import { createServer, type Socket } from "node:net";
import { test } from "node:test";
import { launchProcess, reserveLocalPort, runProcess } from "../src/process.ts";

test("process launch rejects missing executables without leaving a pending status", async () => {
  await assert.rejects(launchProcess(`crer-missing-${crypto.randomUUID()}.exe`), {
    code: "ENOENT",
  });
});

test("process execution captures both large outputs and preserves exit status", async () => {
  const size = 1024 * 1024;
  const result = await runProcess(process.execPath, {
    args: [
      "-e",
      `process.stdout.write('a'.repeat(${size})); process.stderr.write('b'.repeat(${size})); process.exitCode = 7;`,
    ],
  });
  assert.equal(result.code, 7);
  assert.equal(result.signal, null);
  assert.deepEqual(result.stdout, new Uint8Array(size).fill(97));
  assert.deepEqual(result.stderr, new Uint8Array(size).fill(98));
});

test("process launch exposes web streams and merges the parent environment", async () => {
  const child = await launchProcess(process.execPath, {
    args: [
      "-e",
      "process.stdout.write(JSON.stringify({ value: process.env.CRER_PROCESS_TEST, inherited: process.env.PATH }));",
    ],
    env: { CRER_PROCESS_TEST: "値" },
    stderr: "null",
  });
  assert.ok(child.pid > 0);
  assert.ok(child.stdout instanceof ReadableStream);
  const output = JSON.parse(await new Response(child.stdout).text());
  assert.equal(output.value, "値");
  assert.equal(output.inherited, process.env.PATH);
  assert.equal(await new Response(child.stderr).text(), "");
  assert.equal((await child.status).code, 0);
});

test("process kill settles the process status", async () => {
  const child = await launchProcess(process.execPath, {
    args: ["-e", "setInterval(() => {}, 1000)"],
    stdout: "null",
    stderr: "null",
  });
  child.kill("SIGKILL");
  const status = await child.status;
  assert.notEqual(status.code, 0);
  assert.equal(status.signal, "SIGKILL");
});

test("process status settles before a descendant releases inherited stderr", async () => {
  let descendant: Socket | undefined;
  let ready: (() => void) | undefined;
  const descendantReady = new Promise<void>((resolve) => ready = resolve);
  const server = createServer((socket) => {
    descendant = socket;
    socket.on("error", () => {});
    socket.once("data", () => ready?.());
  });
  await new Promise<void>((resolve, reject) => {
    server.once("error", reject);
    server.listen({ host: "127.0.0.1", port: 0, exclusive: true }, resolve);
  });
  const address = server.address();
  assert.ok(address && typeof address !== "string");
  const descendantCode = `
    const socket = require('node:net').connect(${address.port}, '127.0.0.1');
    socket.once('connect', () => {
      socket.write('ready');
      process.send('ready');
    });
    socket.once('data', () => {
      socket.end();
      process.stderr.end('released', () => process.exit(0));
    });
    setTimeout(() => process.exit(2), 10000).unref();
  `;
  const parentCode = `
    const child = require('node:child_process').spawn(process.execPath, ['-e', ${
    JSON.stringify(descendantCode)
  }], {
      stdio: ['ignore', 'ignore', 2, 'ipc'], windowsHide: true, detached: true,
    });
    child.once('message', () => process.exit(0));
  `;
  const child = await launchProcess(process.execPath, {
    args: ["-e", parentCode],
    stdout: "null",
  });
  let stderrClosed = false;
  const stderr = new Response(child.stderr).text().then((output) => {
    stderrClosed = true;
    return output;
  });
  let deadline: ReturnType<typeof setTimeout> | undefined;
  try {
    const status = await Promise.race([
      Promise.all([descendantReady, child.status]).then(([, result]) => result),
      new Promise<never>((_, reject) => {
        deadline = setTimeout(
          () =>
            reject(
              new Error(`process status waited for stderr (descendant connected: ${!!descendant})`),
            ),
          5000,
        );
      }),
    ]);
    assert.equal(status.code, 0);
    assert.equal(stderrClosed, false);
    descendant!.write("release");
    assert.equal(await stderr, "released");
  } finally {
    clearTimeout(deadline);
    if (descendant && !descendant.destroyed && !descendant.writableEnded) {
      descendant.end("release");
    }
    await new Promise<void>((resolve, reject) => {
      server.close((error) => error ? reject(error) : resolve());
    });
  }
});

test("reserved local port is closed before it is returned", async () => {
  const port = await reserveLocalPort();
  assert.ok(Number.isInteger(port) && port > 0 && port <= 65535);
  const server = createServer();
  try {
    await new Promise<void>((resolve, reject) => {
      server.once("error", reject);
      server.listen({ host: "127.0.0.1", port, exclusive: true }, resolve);
    });
  } finally {
    await new Promise<void>((resolve, reject) => {
      server.close((error) => error ? reject(error) : resolve());
    });
  }
});
