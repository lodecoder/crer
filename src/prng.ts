import type { Jitter, Point } from "./types.ts";
const MASK = 0xffff_ffff_ffff_ffffn;
const rotl = (x: bigint, k: bigint) => ((x << k) | (x >> (64n - k))) & MASK;
function splitmix64(x: bigint): [bigint, bigint] {
  x = (x + 0x9e3779b97f4a7c15n) & MASK;
  let z = x;
  z = ((z ^ (z >> 30n)) * 0xbf58476d1ce4e5b9n) & MASK;
  z = ((z ^ (z >> 27n)) * 0x94d049bb133111ebn) & MASK;
  return [x, (z ^ (z >> 31n)) & MASK];
}
export class Random {
  #s: bigint[];
  constructor(seed: bigint) {
    this.#s = [];
    for (let i = 0; i < 4; i++) {
      let x: bigint;
      [seed, x] = splitmix64(seed);
      this.#s.push(x);
    }
  }
  next() {
    const r = rotl((this.#s[1] * 5n) & MASK, 7n) * 9n & MASK;
    const t = (this.#s[1] << 17n) & MASK;
    this.#s[2] ^= this.#s[0];
    this.#s[3] ^= this.#s[1];
    this.#s[1] ^= this.#s[2];
    this.#s[0] ^= this.#s[3];
    this.#s[2] ^= t;
    this.#s[3] = rotl(this.#s[3], 45n);
    return Number(r >> 11n) / 9007199254740992;
  }
}
export function randomSeed() {
  const a = new Uint32Array(2);
  crypto.getRandomValues(a);
  return (BigInt(a[0]) << 32n | BigInt(a[1])).toString();
}
export function jitter(
  point: Point,
  config: Jitter | undefined,
  random: Random,
  size: Point,
): Point | undefined {
  if (!config?.enabled || config.distribution === "none") return point;
  for (let i = 0; i < 16; i++) {
    const theta = 2 * Math.PI * random.next();
    const r = config.distribution === "uniform"
      ? Math.sqrt(random.next()) * config.radius_px
      : Math.min(
        config.radius_px,
        Math.sqrt(-2 * Math.log(Math.max(random.next(), Number.MIN_VALUE))) * Math.cos(theta)
          * config.radius_px / 3,
      );
    const p = { x: point.x + Math.cos(theta) * r, y: point.y + Math.sin(theta) * r };
    if (
      p.x >= config.min_distance_from_edge_px && p.y >= config.min_distance_from_edge_px
      && p.x <= size.x - config.min_distance_from_edge_px
      && p.y <= size.y - config.min_distance_from_edge_px
    ) return p;
  }
  return config.out_of_bounds === "disable-for-step" ? point : undefined;
}
