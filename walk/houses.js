// ===========================================================================
// House design core: the client's house-designer pieces (walls.txt, roof.txt, floors.txt, stairs.txt) and plans
// that compose into statics. A plan is what gets saved and edited, never the statics themselves.
//
// Wall rules (as in UO's own houses): an east-west piece sits on its tile's south edge, a north-south piece on its
// east edge; a tile with both gets the corner piece; a post fills a free tile at the north-west end of a run.
// Each storey is 20 high. Pitched roofs rise 3 per tile and overhang one tile south and east.
// ===========================================================================
const Kit = {
  walls: [], roofs: [], floors: [], stairs: [], ready: false,
  async load() {
    const read = async n => { const f = findAnywhere(n); return f ? new TextDecoder().decode(await readBytes(f, 0, f.size)) : null; };
    const rows = text => {
      const out = []; let head = null;
      for (const line of (text || "").split(/\r?\n/)) {
        const c = line.split("\t");
        if (c[0] === "Category") { head = c; continue; }
        if (!head || !/^\d+$/.test(c[0])) continue;
        const r = {}; head.forEach((h, i) => { r[h] = h === "Comment" ? (c[i] || "").trim() : +c[i] || 0; }); out.push(r);
      }
      return out;
    };
    const ids = (r, ...cols) => cols.map(k => r[k]).filter(v => v > 0);
    this.walls = rows(await read("walls.txt")).map(r => ({ name: r.Comment, cat: r.Category, style: r.Style,
      ew: ids(r, "South1", "South2", "South3"), ns: ids(r, "East1", "East2", "East3"), corner: r.Corner, post: r.Post,
      winEW: ids(r, "WindowS", "AltWindowS", "SecondAltWindowS"), winNS: ids(r, "WindowE", "AltWindowE", "SecondAltWindowE") }))
      .filter(w => w.ew.length && w.ns.length && w.corner);
    this.roofs = rows(await read("roof.txt")).map(r => ({ name: r.Comment, n: r.North, e: r.East, s: r.South, w: r.West,
      ridgeNS: r.NSCrosspiece, ridgeEW: r.EWCrosspiece, dentN: r.NDent, dentE: r.EDent, dentS: r.SDent, dentW: r.WDent,
      tN: r.NTPiece, tE: r.ETPiece, tS: r.STPiece, tW: r.WTPiece, x: r.XPiece })).filter(r => r.n && r.s);
    this.floors = rows(await read("floors.txt")).map(r => ({ name: r.Comment, tiles: Array.from({ length: 16 }, (_, i) => r["F" + (i + 1)]).filter(v => v > 0) }))
      .filter(f => f.tiles.length);
    this.stairs = rows(await read("stairs.txt")).map(r => ({ name: r.Comment, block: r.Block, n: r.North, e: r.East, s: r.South, w: r.West }))
      .filter(s => s.block && s.n);
    this.ready = this.walls.length > 0 && this.roofs.length > 0;
    return this.ready;
  },
  find(list, name) { const n = (name || "").toLowerCase(); return list.find(x => x.name.toLowerCase() === n) || list.find(x => x.name.toLowerCase().includes(n)) || list[0]; },
  wall(name) { return this.find(this.walls, name); },
  roof(name) { return this.find(this.roofs, name); },
  floor(name) { return this.find(this.floors, name); },
  stair(name) { return this.find(this.stairs, name); },
  /** Low walls of the same material (gables, railings, battlements), or null when the material has none:
   *  a full-height stand-in would poke through roofs. */
  low(w, kind = "Quarter") {
    const order = { Quarter: ["Quarter", "Short", "Half"], Half: ["Half", "Short", "Quarter"], Edging: ["Roof Edging", "Half", "Short", "Quarter"] }[kind] || [kind];
    for (const k of order) { const hit = this.walls.find(x => x.cat === w.cat && x !== w && x.name.includes(k)); if (hit) return hit; }
    return null;
  },
};

/**
 * A plan, all positions in plan tiles (0,0 = north-west), relative z:
 *  storeys: [[x,y]...] per storey (0 = ground), each the cells of that floor;
 *  walls/floor/roof: material names; per-storey overrides in storeyWalls[s] / storeyFloor[s];
 *  doors: [x,y,side,storey] gaps in the outline (side N/E/S/W of cell x,y);
 *  inner: [x,y,side,storey,door?] interior walls on a cell's south (S) or east (E) edge;
 *  roofs: [{x,y,w,h,storey,kind:"gable"|"hip"|"flat"|"pyramid", ridge?:"ns"|"ew", roof?, battlements?}];
 *  stairs: [{x,y,storey,dir}] four steps rising one storey toward dir (N/E/S/W) from (x,y);
 *  balconies: [{x,y,w,h,storey}] open floors with a low railing;
 *  chimneys: [[x,y,height]]; porch: {x,y,w,h} ground floor with corner posts; items: [[id,x,y,z]];
 *  windows: "auto" (default) or "none"; battlements on flat roofs by default.
 */
/** ServUO door classes and the art of their closed WestCW door; facing f adds 2f. */
const DOOR_TYPES = { DarkWoodDoor: 0x06A5, MediumWoodDoor: 0x06B5, LightWoodDoor: 0x06D5, StrongWoodDoor: 0x06E5, RattanDoor: 0x0695,
  MetalDoor: 0x0675, MetalDoor2: 0x06C5, BarredMetalDoor: 0x0685, IronGate: 0x0824, IronGateShort: 0x084C, LightWoodGate: 0x0839, DarkWoodGate: 0x0866 };
const DOOR_FACINGS = ["WestCW", "EastCCW", "WestCCW", "EastCW", "SouthCW", "NorthCCW", "SouthCCW", "NorthCW"];
const doorArt = (type, facing) => (DOOR_TYPES[type] || DOOR_TYPES.DarkWoodDoor) + 2 * Math.max(0, DOOR_FACINGS.indexOf(facing));
/** A door that suits the walls: light wood with light wood, strong doors in stone, gates in palisades. */
function doorFor(walls) {
  const n = (walls || "").toLowerCase();
  return n.includes("light wood") || n.includes("plaster") ? "LightWoodDoor" : n.includes("palisade") || n.includes("log") ? "DarkWoodGate"
    : /stone|brick|marble|granite|gothic|limestone/.test(n) ? "StrongWoodDoor" : n.includes("sandstone") || n.includes("clay") ? "MediumWoodDoor" : "DarkWoodDoor";
}

function composePlan(plan) {
  const out = [], put = (id, x, y, z) => { if (id > 0) out.push({ id, x, y, z }); };
  out.items = [];
  const S = plan.storeys || [];
  const sets = S.map(cells => new Set(cells.map(([x, y]) => x + "," + y)));
  const has = (s, x, y) => !!sets[s] && sets[s].has(x + "," + y);
  const wallsOf = s => Kit.wall(plan.storeyWalls?.[s] || plan.walls);
  const floorOf = s => Kit.floor(plan.storeyFloor?.[s] || plan.floor);
  const doorSet = new Set((plan.doors || []).map(([x, y, side, s]) => `${x},${y},${side},${s || 0}`));
  const stairwell = new Set();
  for (const st of plan.stairs || []) {
    const [dx, dy] = { N: [0, -1], S: [0, 1], E: [1, 0], W: [-1, 0] }[st.dir] || [0, -1];
    for (let k = 0; k < 4; k++) stairwell.add(`${st.x + dx * k},${st.y + dy * k},${st.storey + 1}`);
  }
  let seed = plan.seed || 1; const rnd = () => ((seed = (seed * 1103515245 + 12345) & 0x7fffffff) / 0x7fffffff);

  for (let s = 0; s < S.length; s++) {
    const z = s * 20, W = wallsOf(s), F = floorOf(s);
    // Floors (the ground floor too: houses stand on their own floor).
    for (const [x, y] of S[s]) if (!stairwell.has(`${x},${y},${s}`)) put(F.tiles[(x * 7 + y * 13 + s) % Math.min(4, F.tiles.length)], x, y, z);
    // Outline: east-west pieces on south edges, north-south pieces on east edges.
    const ew = new Map(), ns = new Map(); // "x,y" -> true
    for (const [x, y] of S[s]) {
      if (!has(s, x, y - 1) && !doorSet.has(`${x},${y},N,${s}`)) ew.set(`${x},${y - 1}`, 1);
      if (!has(s, x, y + 1) && !doorSet.has(`${x},${y},S,${s}`)) ew.set(`${x},${y}`, 1);
      if (!has(s, x - 1, y) && !doorSet.has(`${x},${y},W,${s}`)) ns.set(`${x - 1},${y}`, 1);
      if (!has(s, x + 1, y) && !doorSet.has(`${x},${y},E,${s}`)) ns.set(`${x},${y}`, 1);
    }
    for (const [x, y, side, st, door] of plan.inner || []) if ((st || 0) === s && !door) (side === "S" ? ew : ns).set(`${x},${y}`, 1);
    traceWalls(out, W, ew, ns, z, plan.windows !== "none", rnd);
  }
  // Stairs: blocks under each step, the step piece on top.
  for (const st of plan.stairs || []) {
    const set = Kit.stair(st.set || plan.stairSet || "Dark Wood"), [dx, dy] = { N: [0, -1], S: [0, 1], E: [1, 0], W: [-1, 0] }[st.dir] || [0, -1];
    const piece = { N: set.n, S: set.s, E: set.e, W: set.w }[st.dir] || set.n, base = st.storey * 20;
    for (let k = 0; k < 4; k++) {
      for (let b = 0; b < k; b++) put(set.block, st.x + dx * k, st.y + dy * k, base + b * 5);
      put(piece, st.x + dx * k, st.y + dy * k, base + k * 5);
    }
  }
  // Balconies: an open floor with a low railing where it isn't against the house.
  for (const b of plan.balconies || []) {
    const z = b.storey * 20, F = floorOf(b.storey), rail = Kit.low(wallsOf(b.storey)) || Kit.low(Kit.wall("Dark Wood Standard Walls"));
    const cells = new Set(); for (let y = b.y; y < b.y + b.h; y++) for (let x = b.x; x < b.x + b.w; x++) { cells.add(x + "," + y); put(F.tiles[0], x, y, z); }
    const ew = new Map(), ns = new Map(), inHouse = (x, y) => has(b.storey, x, y);
    for (const k of cells) {
      const [x, y] = k.split(",").map(Number);
      if (!cells.has(x + "," + (y - 1)) && !inHouse(x, y - 1)) ew.set(`${x},${y - 1}`, 1);
      if (!cells.has(x + "," + (y + 1)) && !inHouse(x, y + 1)) ew.set(`${x},${y}`, 1);
      if (!cells.has((x - 1) + "," + y) && !inHouse(x - 1, y)) ns.set(`${x - 1},${y}`, 1);
      if (!cells.has((x + 1) + "," + y) && !inHouse(x + 1, y)) ns.set(`${x},${y}`, 1);
    }
    traceWalls(out, rail, ew, ns, z, false, rnd);
  }
  // Roofs: larger ones first; later roofs don't overlap earlier ones (cross gables meet the main roof).
  // Upper storeys first, larger roofs first; a roof never covers cells with a storey above them, and later
  // roofs don't overlap earlier ones (cross gables meet the main roof, skirts wrap upper floors).
  const covered = new Set();
  for (const r of (plan.roofs || []).slice().sort((a, b) => b.storey - a.storey || b.w * b.h - a.w * a.h)) {
    const above = new Set();
    for (let k = r.storey + 1; k < S.length; k++) for (const [x, y] of S[k]) { above.add(x + "," + y); above.add((x + 1) + "," + y); above.add(x + "," + (y + 1)); above.add((x + 1) + "," + (y + 1)); }
    composeRoof(out, plan, r, covered, wallsOf, floorOf, above);
  }
  // Chimneys: a stack of posts through the roof.
  for (const [x, y, h] of plan.chimneys || []) {
    const W = Kit.wall(plan.chimneyWalls || "Fieldstone Standard Walls");
    for (let z = 0; z <= (h || 40); z += 20) put(W.post, x, y, z);
  }
  // A porch: floor and corner posts.
  if (plan.porch) {
    const p = plan.porch, F = floorOf(0), W = wallsOf(0);
    for (let y = p.y; y < p.y + p.h; y++) for (let x = p.x; x < p.x + p.w; x++) put(F.tiles[(x + y) % Math.min(4, F.tiles.length)], x, y, 0);
    for (const [x, y] of [[p.x - 1, p.y - 1], [p.x + p.w - 1, p.y - 1], [p.x - 1, p.y + p.h - 1], [p.x + p.w - 1, p.y + p.h - 1]]) if (!has(0, x, y) && !has(0, x + 1, y + 1)) put(W.post, x, y, 0);
  }
  for (const [id, x, y, z] of plan.items || []) put(id, x, y, z);
  // Doors in the doorways: server items (they open), on the wall's tile, facing along the wall.
  if (plan.doorType !== "none")
    for (const [x, y, side, s] of plan.doors || []) {
      const type = plan.doorType || doorFor(wallsOf(s || 0).name), facing = side === "N" || side === "S" ? "WestCW" : "SouthCW";
      const [tx, ty] = side === "N" ? [x, y - 1] : side === "W" ? [x - 1, y] : [x, y];
      out.items.push({ kind: "door", type, facing, id: doorArt(type, facing), x: tx, y: ty, z: (s || 0) * 20 });
    }
  return out;
}

/** Wall pieces for the given edge sets: corners, run ends and middles, windows on middles, posts at run starts. */
function traceWalls(out, W, ew, ns, z, windows, rnd) {
  const tiles = new Set([...ew.keys(), ...ns.keys()]);
  for (const k of tiles) {
    const [x, y] = k.split(",").map(Number), e = ew.has(k), n = ns.has(k);
    let id;
    if (e && n) id = W.corner;
    else if (e) {
      const west = ew.has((x - 1) + "," + y), east = ew.has((x + 1) + "," + y);
      id = W.ew.length < 3 ? W.ew[0] : !west ? W.ew[0] : !east ? W.ew[2] : W.ew[1];
      if (windows && west && east && W.winEW.length && (x % 3 === 1) && rnd() < 0.8) id = W.winEW[0];
    } else {
      const north = ns.has(x + "," + (y - 1)), south = ns.has(x + "," + (y + 1));
      id = W.ns.length < 3 ? W.ns[0] : !north ? W.ns[2] : !south ? W.ns[0] : W.ns[1];
      if (windows && north && south && W.winNS.length && (y % 3 === 1) && rnd() < 0.8) id = W.winNS[0];
    }
    out.push({ id, x, y, z });
  }
  // Posts: the free tile north-west of a run's start closes the corner.
  const posts = new Set();
  for (const k of ew.keys()) { const [x, y] = k.split(",").map(Number), t = (x - 1) + "," + y; if (!tiles.has(t)) posts.add(t); }
  for (const k of ns.keys()) { const [x, y] = k.split(",").map(Number), t = x + "," + (y - 1); if (!tiles.has(t)) posts.add(t); }
  for (const k of posts) { const [x, y] = k.split(",").map(Number); if (W.post) out.push({ id: W.post, x, y, z }); }
}

function composeRoof(out, plan, r, covered, wallsOf, floorOf, above = new Set()) {
  const top = (r.storey + 1) * 20, R = Kit.roof(r.roof || plan.roof), kind = r.kind || "gable";
  const put = (id, x, y, z) => { if (id > 0) out.push({ id, x, y, z }); };
  if (kind === "flat") {
    const F = floorOf(r.storey), cells = new Set();
    const own = new Set((plan.storeys[r.storey] || []).map(([a, b]) => a + "," + b)), upper = new Set((plan.storeys[r.storey + 1] || []).map(([a, b]) => a + "," + b));
    for (let y = r.y; y < r.y + r.h; y++) for (let x = r.x; x < r.x + r.w; x++) { const k = x + "," + y; if (covered.has(k) || upper.has(k) || !own.has(k)) continue; cells.add(k); put(F.tiles[(x + y) % Math.min(4, F.tiles.length)], x, y, top); }
    if (r.battlements === false) return;
    // Battlements: the material's roof edging all round, else its half walls with every other piece left out.
    const W = Kit.low(wallsOf(r.storey), "Edging"), ew = new Map(), ns = new Map(), up = (x, y) => upper.has(x + "," + y);
    if (!W) { for (const k of cells) covered.add(k); return; }
    const every = W.name.includes("Edging") ? 1 : 2;
    for (const k of cells) {
      const [x, y] = k.split(",").map(Number);
      if (up(x, y)) continue;
      if (!cells.has(x + "," + (y - 1)) && !up(x, y - 1) && x % every === 0) ew.set(`${x},${y - 1}`, 1);
      if (!cells.has(x + "," + (y + 1)) && !up(x, y + 1) && x % every === 0) ew.set(`${x},${y}`, 1);
      if (!cells.has((x - 1) + "," + y) && !up(x - 1, y) && y % every === 0) ns.set(`${x - 1},${y}`, 1);
      if (!cells.has((x + 1) + "," + y) && !up(x + 1, y) && y % every === 0) ns.set(`${x},${y}`, 1);
    }
    for (const [k] of ew) { const [x, y] = k.split(",").map(Number); out.push({ id: ns.has(k) ? W.corner : W.ew[Math.min(1, W.ew.length - 1)], x, y, z: top }); }
    for (const [k] of ns) if (!ew.has(k)) { const [x, y] = k.split(",").map(Number); out.push({ id: W.ns[Math.min(1, W.ns.length - 1)], x, y, z: top }); }
    for (const k of cells) covered.add(k);
    return;
  }
  // Pitched roofs cover the interior plus one tile south and east.
  const x0 = r.x, y0 = r.y, x1 = r.x + r.w, y1 = r.y + r.h; // inclusive overhang edge
  const nx = x1 - x0 + 1, ny = y1 - y0 + 1, cells = [];
  if (kind === "gable") {
    const ridgeNS = r.ridge ? r.ridge === "ns" : nx <= ny, n = ridgeNS ? nx : ny;
    for (let i = 0; i < n; i++) {
      const d = Math.min(i, n - 1 - i), ridge = d === Math.floor((n - 1) / 2) && (n % 2 === 1 || i === n / 2 - 1 || i === n / 2);
      const id = ridgeNS ? (ridge ? R.ridgeNS : i < n / 2 ? R.w : R.e) : (ridge ? R.ridgeEW : i < n / 2 ? R.n : R.s);
      if (ridgeNS) for (let y = y0; y <= y1; y++) cells.push([id, x0 + i, y, top + 3 * d]);
      else for (let x = x0; x <= x1; x++) cells.push([id, x, y0 + i, top + 3 * d]);
    }
    // Gables close the triangle under each end with low walls.
    const L = Kit.low(wallsOf(r.storey));
    if (L) for (let k = 0; k < Math.floor((n - 1) / 2); k++)
      for (let i = k + 1; i <= n - 2 - k; i++) {
        const z = top + 3 * k;
        if (ridgeNS) { cells.push([L.ew[Math.min(1, L.ew.length - 1)], x0 + i - 1, y1 - 1, z, true]); cells.push([L.ew[Math.min(1, L.ew.length - 1)], x0 + i - 1, y0 - 1, z, true]); }
        else { cells.push([L.ns[Math.min(1, L.ns.length - 1)], x1 - 1, y0 + i - 1, z, true]); cells.push([L.ns[Math.min(1, L.ns.length - 1)], x0 - 1, y0 + i - 1, z, true]); }
      }
  } else {
    // Hipped (and pyramid): every tile slopes toward its nearest edge; the top is a ridge, or a peak on squares.
    const dmax = Math.floor((Math.min(nx, ny) - 1) / 2);
    for (let j = 0; j < ny; j++) for (let i = 0; i < nx; i++) {
      const dN = j, dS = ny - 1 - j, dW = i, dE = nx - 1 - i, d = Math.min(dN, dS, dW, dE);
      let id;
      if (d === dmax && nx === ny && (nx % 2 === 1)) id = R.x || R.ridgeNS;            // the peak
      else if (d === dmax && (nx > ny ? (dN === d || dS === d) && dN + dS <= 1 || (ny % 2 === 1 && dN === dS) : (dW === d || dE === d) && dW + dE <= 1 || (nx % 2 === 1 && dW === dE)))
        id = nx > ny ? (dW === d || dE === d ? (dW === d ? R.tW || R.w : R.tE || R.e) : R.ridgeEW) : (dN === d || dS === d ? (dN === d ? R.tN || R.n : R.tS || R.s) : R.ridgeNS);
      else if (dN === d && dW === d) id = R.dentN || R.n;
      else if (dN === d && dE === d) id = R.dentE || R.e;
      else if (dS === d && dE === d) id = R.dentS || R.s;
      else if (dS === d && dW === d) id = R.dentW || R.w;
      else id = dN === d ? R.n : dS === d ? R.s : dW === d ? R.w : R.e;
      cells.push([id, x0 + i, y0 + j, top + 3 * d]);
    }
  }
  const mine = new Set();
  for (const [id, x, y, z, gable] of cells) {
    if (covered.has(x + "," + y) || above.has(x + "," + y)) continue;
    put(id, x, y, z);
    if (!gable) mine.add(x + "," + y);
  }
  for (const k of mine) covered.add(k);
}

/** Cells of the union of rectangles [x,y,w,h], minus holes. */
function rectCells(rects, holes = []) {
  const set = new Set();
  for (const [x, y, w, h] of rects) for (let j = y; j < y + h; j++) for (let i = x; i < x + w; i++) set.add(i + "," + j);
  for (const [x, y, w, h] of holes) for (let j = y; j < y + h; j++) for (let i = x; i < x + w; i++) set.delete(i + "," + j);
  return [...set].map(k => k.split(",").map(Number));
}

// ===========================================================================
// ShardForge designs: templates (cottage to castle) x themes (materials) x sizes. Each entry is a recipe;
// its plan is generated on demand, so the door can face whichever way a plot faces.
// ===========================================================================
const HOUSE_THEMES = {
  thatch:     { name: "Thatch & plaster", walls: "Wood and Plaster Plain Walls", floor: "Dark Wood 1", roof: "Thatch Roof", stairs: "Dark Wood", chimney: "Fieldstone Standard Walls" },
  timber:     { name: "Timber",           walls: "Dark Wood Standard Walls",    floor: "Dark Wood 1", roof: "Wooden Shingles", stairs: "Dark Wood", chimney: "Fieldstone Standard Walls" },
  lightwood:  { name: "Light wood",       walls: "Light Wood Standard Walls",   floor: "Light Wood 1", roof: "Wooden Shingles", stairs: "Light Wood", chimney: "Grey Brick Standard Walls" },
  log:        { name: "Log",              walls: "Log Standard Walls",          floor: "Dark Wood 2", roof: "Log Roof", stairs: "Dark Wood", chimney: "Fieldstone Standard Walls" },
  fieldstone: { name: "Fieldstone",       walls: "Fieldstone Standard Walls",   floor: "Grey Pavers and Flagstones", roof: "Slate Roof 1", stairs: "Grey Stone", chimney: "Fieldstone Standard Walls" },
  brick:      { name: "Brick",            walls: "Light Brick Standard Walls",  floor: "Red Brick", roof: "Tile Roof", stairs: "Light Stone", chimney: "Light Brick Standard Walls" },
  greybrick:  { name: "Grey brick",       walls: "Grey Brick Standard Walls",   floor: "Grey Pavers and Flagstones", roof: "Slate Roof 1", stairs: "Grey Stone", chimney: "Grey Brick Standard Walls" },
  stone:      { name: "Castle stone",     walls: "Weathered Stone Standard Walls", floor: "Grey Pavers and Flagstones", roof: "Stone Roof", stairs: "Weathered Stone", chimney: "Weathered Stone Standard Walls" },
  marble:     { name: "Marble",           walls: "White Marble Fancy Walls",    floor: "Misc Tile", roof: "Slate Roof 1", stairs: "Tan Marble", chimney: "White Marble Fancy Walls" },
  sandstone:  { name: "Sandstone",        walls: "Sandstone Brick Fancy Walls", floor: "Sandstone Bricks", roof: "Palm Fronds", stairs: "Sandstone", chimney: "Sandstone Brick Fancy Walls" },
  adobe:      { name: "Adobe",            walls: "Clay Standard Walls",         floor: "Red Tile", roof: "Tile Roof", stairs: "Sandstone Brick", chimney: "Clay Standard Walls" },
  plaster:    { name: "Town plaster",     walls: "Plaster Standard Walls - Light", floor: "Wood Floor 3", roof: "Tile Roof", stairs: "Light Wood", chimney: "Light Brick Standard Walls" },
  gothic:     { name: "Gothic",           walls: "Gothic Walls 1",              floor: "Grey Pavers and Flagstones", roof: "Slate Roof 1", stairs: "Gothic", chimney: "Weathered Stone Standard Walls" },
  batten:     { name: "Board & batten",   walls: "Board and Batten Walls 1",    floor: "Wood Floor 1", roof: "Wooden Shingles", stairs: "Board and Batten", chimney: "Fieldstone Standard Walls" },
  palisade:   { name: "Frontier",         walls: "Palisades Standard Walls",    floor: "Dirt and Cobblestones", roof: "Log Roof", stairs: "Dark Wood", chimney: "Fieldstone Standard Walls" },
};
// Furniture (checked against the client's art): pieces for a north wall run along X, for a west wall along Y.
const FURN = { shelfN: [0x0A97, 0x0A98, 0x0A9B, 0x0A9D], shelfW: [0x0A99, 0x0A9A, 0x0A9C, 0x0A9E], chestN: [0x0E43, 0x0A2C], chestW: [0x0E42],
  table: 0x0B73, chairX: 0x0B52, chairY: 0x0B53, counter: 0x0B3F, keg: 0x1AD7, barrel: 0x0FAE, forge: 0x0FB1, anvil: 0x0FB0, candelabra: 0x0B26,
  benchX: 0x0B2D, benchY: 0x0B2C, stool: 0x0A2A, crates: [0x0E3C, 0x0E3D], hay: [0x1036, 0x0F34], bedN: [0x0A60, 0x0A61], sack: 0x1045 };

/** A design: a plan builder with its own little random generator, so the same recipe always gives the same house. */
function planner(theme, seed) {
  const T = HOUSE_THEMES[theme] || HOUSE_THEMES.timber;
  let s = seed >>> 0 || 1;
  const rnd = () => ((s = (s * 1664525 + 1013904223) >>> 0) / 4294967296), pick = a => a[Math.floor(rnd() * a.length)];
  const P = { walls: T.walls, floor: T.floor, roof: T.roof, stairSet: T.stairs, chimneyWalls: T.chimney, storeys: [], doors: [], inner: [], roofs: [], stairs: [], balconies: [], chimneys: [], items: [], seed };
  const api = {
    P, rnd, pick, T,
    /** Adds rectangles to storeys from..to-1. */
    block(x, y, w, h, from, to) { for (let k = from; k < to; k++) { P.storeys[k] = P.storeys[k] || []; P.storeys[k].push(...rectCells([[x, y, w, h]])); } return api; },
    hole(x, y, w, h, from, to) { for (let k = from; k < to; k++) if (P.storeys[k]) P.storeys[k] = P.storeys[k].filter(([a, b]) => !(a >= x && a < x + w && b >= y && b < y + h)); return api; },
    roof(x, y, w, h, storey, kind = "gable", extra = {}) { P.roofs.push({ x, y, w, h, storey, kind, ...extra }); return api; },
    door(x, y, side, storey = 0) { P.doors.push([x, y, side, storey]); return api; },
    stairs(x, y, storey, dir) { P.stairs.push({ x, y, storey, dir }); return api; },
    item(id, x, y, z = 0) { P.items.push([id, x, y, z]); return api; },
    done(name) { for (let k = 0; k < P.storeys.length; k++) { const seen = new Set(); P.storeys[k] = (P.storeys[k] || []).filter(([x, y]) => !seen.has(x + "," + y) && seen.add(x + "," + y)); } P.name = name; return P; },
  };
  return api;
}
/** Where the front door goes on a rectangle x,y,w,h for a door side. */
function frontDoor(d, x, y, w, h, side, storey = 0) {
  const mx = x + Math.floor((w - 1) / 2), my = y + Math.floor((h - 1) / 2);
  if (side === "N") d.door(mx, y, "N", storey); else if (side === "E") d.door(x + w - 1, my, "E", storey);
  else if (side === "W") d.door(x, my, "W", storey); else d.door(mx, y + h - 1, "S", storey);
}
/** Simple furniture against the north and west walls of a room, by use. */
function furnish(d, x, y, w, h, use, z = 0) {
  const F = FURN, pick = d.pick, used = new Set(), put = (id, a, b) => { const k = a + "," + b; if (used.has(k) || a < x || b < y || a >= x + w || b >= y + h) return; used.add(k); d.item(id, a, b, z); };
  // keep the middle of each wall clear for doors
  used.add((x + Math.floor((w - 1) / 2)) + "," + (y + h - 1)); used.add((x + w - 1) + "," + (y + Math.floor((h - 1) / 2)));
  const cx = x + Math.floor(w / 2), cy = y + Math.floor(h / 2);
  if (use === "tavern") { for (let a = x; a < x + w; a++) if (d.rnd() < 0.6) put(d.rnd() < 0.5 ? F.keg : F.barrel, a, y); for (let b = y + 2; b < y + h - 1; b += 3) for (let a = x + 1; a < x + w - 1; a += 3) { put(F.table, a, b); put(F.chairY, a - 1, b); put(F.chairX, a, b - 1); } }
  else if (use === "shop") { for (let a = x + 1; a < x + w - 1; a++) put(F.counter, a, cy); for (let a = x; a < x + w; a++) if (d.rnd() < 0.8) put(pick(F.shelfN), a, y); }
  else if (use === "smithy") { put(F.forge, x, y); put(F.forge, x + 1, y); put(F.anvil, x + 1, y + 2); put(F.barrel, x, y + h - 2); }
  else if (use === "chapel") { put(F.candelabra, x, y); put(F.candelabra, x + w - 1, y); for (let b = y + 2; b < y + h - 1; b += 2) for (let a = x + 1; a < x + w - 1; a++) if (a !== cx) put(F.benchX, a, b); }
  else if (use === "barn") { for (let b = y; b < y + h; b++) for (let a = x; a < x + w; a++) if (d.rnd() < 0.25) put(d.rnd() < 0.7 ? pick(F.hay) : F.sack, a, b); }
  else if (use === "store") { for (let b = y; b < y + h; b++) for (let a = x; a < x + w; a++) if (d.rnd() < 0.35) put(d.rnd() < 0.6 ? pick(F.crates) : F.barrel, a, b); }
  else if (use === "hall") { for (let a = x + 1; a < x + w - 1; a++) { put(F.table, a, cy); put(F.benchX, a, cy - 1); put(F.benchX, a, cy + 1); } put(F.candelabra, x, y); put(F.candelabra, x + w - 1, y); }
  else { put(pick(F.shelfN), x, y); if (w > 2) put(pick(F.chestN), x + w - 1, y); if (h > 2) put(pick(F.shelfW), x, y + 1); put(F.table, cx, cy); put(F.chairY, cx - 1, cy); put(F.chairX, cx, cy - 1); }
}

const HOUSE_TEMPLATES = {
  hut: { name: "Hut", size: "small", themes: ["log", "palisade", "thatch"], make(d, o) {
    const w = 4 + (d.rnd() < 0.5 ? 1 : 0), h = 4;
    d.block(0, 0, w, h, 0, 1).roof(0, 0, w, h, 0, "gable"); frontDoor(d, 0, 0, w, h, o.door); furnish(d, 0, 0, w, h, "home"); } },
  cottage: { name: "Cottage", size: "small", themes: ["thatch", "timber", "fieldstone", "log", "brick", "batten"], make(d, o) {
    const w = 6 + Math.floor(d.rnd() * 3), h = 5;
    d.block(0, 0, w, h, 0, 1).roof(0, 0, w, h, 0, "gable", { ridge: "ew" }); frontDoor(d, 0, 0, w, h, o.door);
    d.P.chimneys.push([w - 2, 1, 40]);
    if (o.door === "S") d.P.porch = { x: Math.floor((w - 1) / 2) - 1, y: h, w: 3, h: 2 };
    furnish(d, 0, 0, w, h, "home"); } },
  farmhouse: { name: "Farmhouse", size: "medium", themes: ["timber", "thatch", "fieldstone", "batten", "log"], make(d, o) {
    const w = 9 + Math.floor(d.rnd() * 3), h = 5, wing = 4, wl = 4;
    d.block(0, 0, w, h, 0, 2).block(w - wing, h, wing, wl, 0, 1);
    d.roof(0, 0, w, h, 1, "gable", { ridge: "ew" }).roof(w - wing, h, wing, wl, 0, "gable", { ridge: "ns" });
    frontDoor(d, 0, 0, w - wing, h, o.door === "S" ? "S" : o.door); d.door(w - wing, h + 1, "W");
    d.stairs(1, h - 1, 0, "N"); d.P.chimneys.push([1, 0, 60]);
    furnish(d, 2, 0, w - wing - 2, h, "home"); furnish(d, w - wing, h, wing, wl, "store"); } },
  townhouse: { name: "Townhouse", size: "small", themes: ["plaster", "brick", "greybrick", "timber", "lightwood"], make(d, o) {
    const w = 5 + Math.floor(d.rnd() * 2), h = 7 + Math.floor(d.rnd() * 2), st = 2 + (d.rnd() < 0.5 ? 1 : 0);
    d.block(0, 0, w, h, 0, st).roof(0, 0, w, h, st - 1, "gable", { ridge: "ns" }); frontDoor(d, 0, 0, w, h, o.door);
    for (let k = 0; k < st - 1; k++) d.stairs(w - 1, h - 2 - (k % 2 ? 0 : 0), k, "N");
    if (o.door === "S") d.P.balconies.push({ x: 0, y: h, w, h: 1, storey: 1 });
    d.P.chimneys.push([0, 1, 20 * st + 10]); furnish(d, 0, 0, w - 1, h, "home"); } },
  shop: { name: "Shop", size: "small", themes: ["plaster", "timber", "brick", "fieldstone", "adobe"], make(d, o) {
    const w = 7 + Math.floor(d.rnd() * 2), h = 6;
    d.block(0, 0, w, h, 0, 2).roof(0, 0, w, h, 1, "gable", { ridge: "ew" }); frontDoor(d, 0, 0, w, h, o.door);
    d.stairs(w - 1, h - 2, 0, "N"); if (o.door === "S") d.P.porch = { x: 0, y: h, w, h: 2 };
    furnish(d, 0, 0, w - 1, h, "shop"); } },
  barn: { name: "Barn", size: "medium", themes: ["timber", "batten", "log", "lightwood"], make(d, o) {
    const w = 8, h = 10 + Math.floor(d.rnd() * 3);
    d.block(0, 0, w, h, 0, 1).roof(0, 0, w, h, 0, "gable", { ridge: "ns" });
    d.door(3, h - 1, "S").door(4, h - 1, "S"); if (o.door !== "S") frontDoor(d, 0, 0, w, h, o.door); furnish(d, 0, 0, w, h, "barn"); } },
  tavern: { name: "Tavern", size: "medium", themes: ["timber", "plaster", "fieldstone", "brick", "thatch"], make(d, o) {
    const w = 11 + Math.floor(d.rnd() * 2), h = 8;
    d.block(0, 0, w, h, 0, 2).block(1, 1, w - 2, h - 2, 2, 3).roof(0, 0, w, h, 1, "hip").roof(1, 1, w - 2, h - 2, 2, "gable", { ridge: "ew" });
    frontDoor(d, 0, 0, w, h, o.door); d.stairs(w - 1, h - 2, 0, "N").stairs(w - 2, h - 3, 1, "N");
    if (o.door === "S") d.P.balconies.push({ x: 1, y: h, w: w - 2, h: 1, storey: 1 });
    d.P.chimneys.push([0, 1, 70]); furnish(d, 0, 0, w - 1, h, "tavern"); } },
  chapel: { name: "Chapel", size: "medium", themes: ["fieldstone", "marble", "greybrick", "gothic", "sandstone"], make(d, o) {
    const w = 7, h = 12, t = 4;
    d.block(0, 0, w, h, 0, 2).roof(0, 0, w, h, 1, "gable", { ridge: "ns" });
    d.block(Math.floor((w - t) / 2), h, t, t, 0, 4).roof(Math.floor((w - t) / 2), h, t, t, 3, "pyramid");
    d.door(Math.floor((w - t) / 2) + 1, h + t - 1, "S").door(Math.floor((w - t) / 2) + 1, h, "N");
    furnish(d, 0, 0, w, h, "chapel"); } },
  manor: { name: "Manor", size: "large", themes: ["brick", "fieldstone", "marble", "greybrick", "plaster"], make(d, o) {
    const w = 14 + Math.floor(d.rnd() * 3), h = 7, wing = 5, wl = 5;
    d.block(0, 0, w, h, 0, 2).block(0, h, wing, wl, 0, 2).block(w - wing, h, wing, wl, 0, 2);
    d.roof(0, 0, w, h, 1, "hip").roof(0, h, wing, wl, 1, "gable", { ridge: "ns" }).roof(w - wing, h, wing, wl, 1, "gable", { ridge: "ns" });
    const mx = Math.floor((w - 1) / 2); d.door(mx, h - 1, "S"); d.door(wing - 1, h + 2, "E"); d.door(w - wing, h + 2, "W");
    d.P.porch = { x: mx - 1, y: h, w: 3, h: 2 };
    d.stairs(1, h - 1, 0, "N"); d.P.balconies.push({ x: mx - 1, y: h, w: 3, h: 1, storey: 1 });
    d.P.chimneys.push([2, 0, 60], [w - 3, 0, 60]);
    furnish(d, 3, 0, w - 6, h, "hall"); furnish(d, 0, h, wing, wl, "home"); furnish(d, w - wing, h, wing, wl, "home"); } },
  villa: { name: "Courtyard villa", size: "large", themes: ["sandstone", "adobe", "marble"], make(d, o) {
    const w = 14, h = 12, b = 4;
    d.block(0, 0, w, h, 0, 1).hole(b, b, w - 2 * b, h - b, 0, 1).block(0, 0, w, b, 1, 2);
    d.roof(0, 0, w, b, 1, "flat").roof(0, b, b, h - b, 0, "flat").roof(w - b, b, b, h - b, 0, "flat");
    d.door(b, b + 1, "W").door(w - b - 1, b + 1, "E").door(Math.floor(w / 2), b - 1, "S"); d.stairs(1, b - 1, 0, "N");
    for (let x = b; x < w - b; x++) d.item(x % 2 ? 0x0D2F : 0x0C84, x, h - 1, 0);
    furnish(d, 1, 0, w - 2, b, "hall"); } },
  tower: { name: "Wizard tower", size: "medium", themes: ["stone", "marble", "gothic", "fieldstone", "greybrick"], make(d, o) {
    const n = 6 + (d.rnd() < 0.5 ? 1 : 0), st = 4;
    d.block(0, 0, n, n, 0, st).roof(0, 0, n, n, st - 1, d.rnd() < 0.5 ? "pyramid" : "flat"); frontDoor(d, 0, 0, n, n, o.door);
    for (let k = 0; k < st - 1; k++) k % 2 ? d.stairs(n - 1, 1, k, "S") : d.stairs(0, n - 2, k, "N");
    d.item(FURN.candelabra, 1, 1, 0).item(FURN.table, 2, 2, 20).item(FURN.chairX, 2, 1, 20).item(FURN.shelfN[0], 1, 0, 40); } },
  watchtower: { name: "Watchtower", size: "small", themes: ["stone", "fieldstone", "log", "palisade"], make(d, o) {
    const n = 5;
    d.block(0, 0, n, n, 0, 3).roof(0, 0, n, n, 2, "flat"); frontDoor(d, 0, 0, n, n, o.door);
    d.stairs(0, n - 2, 0, "N").stairs(n - 1, 1, 1, "S"); d.item(FURN.barrel, 1, 1, 0).item(FURN.crates[0], 2, 1, 0); } },
  keep: { name: "Keep", size: "large", themes: ["stone", "fieldstone", "greybrick", "gothic"], make(d, o) {
    const n = 11, t = 4, st = 3;
    d.block(1, 1, n - 2, n - 2, 0, st).roof(1, 1, n - 2, n - 2, st - 1, "flat");
    for (const [x, y] of [[0, 0], [n - t, 0], [0, n - t], [n - t, n - t]]) d.block(x, y, t, t, 0, st + 1).roof(x, y, t, t, st, "flat");
    frontDoor(d, 1, 1, n - 2, n - 2, o.door); d.stairs(t, n - 3, 0, "N").stairs(n - t - 1, t, 1, "S");
    furnish(d, t, t, n - 2 * t, n - 2 * t, "hall"); } },
  fort: { name: "Frontier fort", size: "large", themes: ["palisade", "log"], make(d, o) {
    const n = 18, t = 4;
    d.block(0, 0, n, n, 0, 1).hole(1, 1, n - 2, n - 2, 0, 1);
    for (const [x, y] of [[0, 0], [n - t, 0], [0, n - t], [n - t, n - t]]) d.block(x, y, t, t, 0, 2).roof(x, y, t, t, 1, "pyramid");
    d.block(5, 3, 8, 6, 0, 2).roof(5, 3, 8, 6, 1, "gable", { ridge: "ew" }); d.door(8, 8, "S");
    const g = Math.floor(n / 2); d.door(g, n - 1, "S").door(g, n - 1, "N").door(g - 1, n - 1, "S").door(g - 1, n - 1, "N");
    d.stairs(6, 7, 0, "N"); furnish(d, 5, 3, 8, 6, "store"); } },
  castle: { name: "Castle", size: "huge", themes: ["stone", "fieldstone", "greybrick", "gothic", "sandstone"], make(d, o) {
    const n = 26 + Math.floor(d.rnd() * 3) * 2, t = 6, wall = 2, st = 2;
    // Curtain wall two tiles thick, two storeys, walkway with battlements on top.
    d.block(0, 0, n, n, 0, st).hole(wall, wall, n - 2 * wall, n - 2 * wall, 0, st).roof(0, 0, n, n, st - 1, "flat");
    for (const [x, y] of [[0, 0], [n - t, 0], [0, n - t], [n - t, n - t]]) d.block(x, y, t, t, 0, st + 2).roof(x, y, t, t, st + 1, "flat");
    // Gatehouse: two towers flanking a passage through the south wall.
    const g = Math.floor(n / 2) - 1;
    d.block(g - 4, n - 5, 4, 5, 0, st + 1).roof(g - 4, n - 5, 4, 5, st, "flat").block(g + 2, n - 5, 4, 5, 0, st + 1).roof(g + 2, n - 5, 4, 5, st, "flat");
    for (const x of [g, g + 1]) { d.door(x, n - 1, "S").door(x, n - wall, "N"); }
    // The keep in the courtyard, with a hall.
    const kx = Math.floor(n / 2) - 6, ky = wall + 3, kw = 12, kh = 9;
    d.block(kx, ky, kw, kh, 0, 3).roof(kx, ky, kw, kh, 2, "flat").block(kx + kw - 4, ky - 1, 4, 4, 0, 4).roof(kx + kw - 4, ky - 1, 4, 4, 3, "pyramid");
    frontDoor(d, kx, ky, kw, kh, "S"); d.stairs(kx, ky + kh - 2, 0, "N").stairs(kx + 1, ky + 1, 1, "S");
    furnish(d, kx + 2, ky, kw - 6, kh, "hall"); } },
};
/** Every catalog recipe: template x theme, with a size note. */
function houseCatalog() {
  const out = [];
  for (const [key, t] of Object.entries(HOUSE_TEMPLATES))
    for (const theme of t.themes) out.push({ key: `sf:${key}:${theme}`, template: key, theme, name: `${HOUSE_THEMES[theme].name} ${t.name.toLowerCase()}`, size: t.size, seed: [...(key + theme)].reduce((h, c) => (h * 31 + c.charCodeAt(0)) >>> 0, 7) });
  return out;
}
/** The plan for a recipe with its front door on a side. */
function recipePlan(r, door = "S") {
  const t = HOUSE_TEMPLATES[r.template], d = planner(r.theme, r.seed);
  t.make(d, { door });
  return d.done(r.name);
}
