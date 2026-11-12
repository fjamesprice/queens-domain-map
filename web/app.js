import * as THREE from './vendor/three.module.js';
import { OrbitControls } from './vendor/OrbitControls.js';
import { GLTFLoader } from './vendor/GLTFLoader.js';
import { MeshoptDecoder } from './vendor/meshopt_decoder.module.js';

// Build version, taken from this script's own URL (index.html loads app.js?v=...). Appended to every data request so
// Cloudflare and browsers fetch fresh copies after an update instead of serving stale cached ones.
const VER = new URL(import.meta.url).searchParams.get('v') || '';
const vq = (url) => (VER ? url + (url.includes('?') ? '&' : '?') + 'v=' + VER : url);
const $ = (s) => document.querySelector(s);
const el = (tag, cls, text) => { const e = document.createElement(tag); if (cls) e.className = cls; if (text != null) e.textContent = text; return e; };
const fmt = (n) => (n == null ? '?' : Number.isInteger(n) ? String(n) : n.toFixed(1));

// ------------------------------------------------------------------ state
const state = {
  levels: [], cats: [], catByKey: {}, markers: [], items: {}, enemies: {},
  levelOn: [], levelObj: [], levelState: [], // 'idle' | 'loading' | 'loaded' | 'error'
  catOn: {}, search: '', onlyHidden: false, selected: -1, hover: -1,
  visible: new Uint8Array(0), searchText: [], itemWhere: {},
  sliceY: Infinity, worldMinY: -5000, worldMaxY: 20000,
};

// ------------------------------------------------------------------ renderer / scene
const canvas = $('#c');
const renderer = new THREE.WebGLRenderer({ canvas, antialias: true, logarithmicDepthBuffer: true, powerPreference: 'high-performance' });
renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));
renderer.localClippingEnabled = true;
renderer.outputColorSpace = THREE.SRGBColorSpace;

const scene = new THREE.Scene();
const BG = new THREE.Color('#141922');
scene.background = BG;
scene.fog = new THREE.Fog(BG, 60000, 450000);

const camera = new THREE.PerspectiveCamera(55, 1, 20, 2000000);
camera.position.set(0, 60000, 60000);

const controls = new OrbitControls(camera, canvas);
controls.enableDamping = true;
controls.dampingFactor = 0.12;
controls.screenSpacePanning = true;
controls.zoomToCursor = true;
controls.minDistance = 100;
controls.maxDistance = 600000;
controls.keys = {}; // we handle keys ourselves
controls.mouseButtons = { LEFT: THREE.MOUSE.ROTATE, MIDDLE: THREE.MOUSE.PAN, RIGHT: -1 };

scene.add(new THREE.HemisphereLight(0xe6eeff, 0x3a3026, 1.9));
const sun = new THREE.DirectionalLight(0xfff4e0, 1.7);
sun.position.set(0.45, 1, 0.3);
scene.add(sun);

const slicePlane = new THREE.Plane(new THREE.Vector3(0, -1, 0), 1e9);
const worldRoot = new THREE.Group();
scene.add(worldRoot);

// ------------------------------------------------------------------ materials
const matCache = new Map();
const texCache = new Map();
const texLoader = new THREE.TextureLoader();
let useTextures = true;
try { useTextures = localStorage.getItem('qdmap.textures') !== '0'; } catch {}

function boosted(hex) {
  const c = new THREE.Color('#' + hex);
  const hsl = {}; c.getHSL(hsl, THREE.SRGBColorSpace);
  c.setHSL(hsl.h, Math.min(1, hsl.s * 1.35), Math.min(0.85, hsl.l * 1.08), THREE.SRGBColorSpace);
  return c;
}

function getTexture(file, tile) {
  const key = file + '@' + tile;
  let t = texCache.get(key);
  if (t) return t;
  t = texLoader.load(vq('data/tex/' + file));
  t.flipY = false;
  t.colorSpace = THREE.SRGBColorSpace;
  t.wrapS = t.wrapT = THREE.RepeatWrapping;
  t.anisotropy = Math.min(4, renderer.capabilities.getMaxAnisotropy());
  t.repeat.set(tile, tile);
  texCache.set(key, t);
  return t;
}

// Material names are JSON written by the extractor: c = flat color, t = texture, k = tint, m = alpha masked, s = tiling,
// u = unlit (drawn without lighting in its own color, like the dark floor 13 m under the sea that hides the depths).
function mapMaterial(src) {
  let info = {};
  try { info = JSON.parse(src.name || '{}'); } catch {}
  if (!info.c) info.c = src.color ? src.color.getHexString(THREE.SRGBColorSpace) : '999999';
  const opacity = src.opacity ?? 1;
  const transparent = src.transparent || opacity < 1;
  const key = (src.name || info.c) + (transparent ? 't' + opacity.toFixed(2) : '');
  let m = matCache.get(key);
  if (!m) {
    const opts = {
      side: THREE.DoubleSide, clippingPlanes: [slicePlane],
      transparent, opacity: transparent ? Math.min(opacity, 0.3) : 1, depthWrite: !transparent,
    };
    m = info.u ? new THREE.MeshBasicMaterial(opts) : new THREE.MeshLambertMaterial({ ...opts, flatShading: true });
    const flat = info.u ? new THREE.Color('#' + info.c) : boosted(info.c);
    m.userData = { flat, tint: new THREE.Color('#' + (info.k || 'ffffff')), tex: info.t, tile: info.s || 1, masked: !!info.m };
    applyTextureMode(m);
    matCache.set(key, m);
  }
  return m;
}

function applyTextureMode(m) {
  const u = m.userData;
  m.flatShading = !(useTextures && u.tex);
  if (useTextures && u.tex) {
    m.map = getTexture(u.tex, u.tile);
    m.color.copy(u.tint);
    m.alphaTest = u.masked ? 0.5 : 0;
  } else {
    m.map = null;
    m.color.copy(u.flat);
    m.alphaTest = 0;
  }
  m.needsUpdate = true;
}

function setTextures(on) {
  useTextures = on;
  try { localStorage.setItem('qdmap.textures', on ? '1' : '0'); } catch {}
  for (const m of matCache.values()) applyTextureMode(m);
}

// ------------------------------------------------------------------ level loading
const loader = new GLTFLoader();
loader.setMeshoptDecoder(MeshoptDecoder);
let loadingCount = 0;
const loadQueue = [];
let triCount = 0;
const smallMeshes = [];
let smallShown = true, smallCheck = 0;
// Small props show when either the orbit point or the camera itself is near the playable space. After flying with WASD
// or looking around, the orbit point can be far ahead of the camera, so the orbit distance alone is not enough.
function cameraNearMarkers(r) {
  const c = camera.position, r2 = r * r;
  for (let i = 0; i < state.markers.length; i++) {
    const m = state.markers[i];
    if (!state.levelOn[m.l]) continue;
    const p = m.p;
    if ((p[0] - c.x) ** 2 + (p[1] - c.y) ** 2 + (p[2] - c.z) ** 2 < r2) return true;
  }
  return false;
}
function updateSmall(now) {
  if (now - smallCheck < 250) return;
  smallCheck = now;
  const want = controls.getDistance() < 25000 || cameraNearMarkers(8000);
  if (want === smallShown) return;
  smallShown = want;
  for (const o of smallMeshes) o.visible = want;
}

function setLevel(i, on) {
  state.levelOn[i] = on;
  if (on && state.levelState[i] === 'idle') { state.levelState[i] = 'queued'; loadQueue.push(i); pumpQueue(); }
  if (state.levelObj[i]) state.levelObj[i].visible = on;
  if (on) markSeen(i);
  updateAreaRow(i);
  refreshMarkers();
}
// An area switched on or off by the user (Areas list): streaming then leaves it as the user set it.
function setLevelByUser(i, on) {
  state.levelWant[i] = on;
  setLevel(i, on);
}

// ------------------------------------------------------------------ area streaming
// To avoid spoilers, areas are not all loaded at startup. An area (of those on by default) loads once the camera comes
// within STREAM_RANGE of its nearest marker, nearest first, and then stays. Height differences count STREAM_HEIGHT
// times, because many areas are stacked under each other and are only reached by going down. Areas seen before are
// remembered in this browser. The sea and the island's outer terrain are what a new player sees anyway.
const STREAM_RANGE = 10000; // 100 m
const STREAM_HEIGHT = 3;
const ALWAYS_LOADED = new Set(['050_Ocean', '070_UpperShell']);
const SEEN_KEY = 'qdmap.seenAreas';
let seenAreas = new Set();
try { seenAreas = new Set(JSON.parse(localStorage.getItem(SEEN_KEY) || '[]')); } catch {}
function markSeen(i) {
  const id = state.levels[i].id;
  if (seenAreas.has(id)) return;
  seenAreas.add(id);
  try { localStorage.setItem(SEEN_KEY, JSON.stringify([...seenAreas])); } catch {}
}
// Distance from p to each area's nearest marker (height weighted); areas without markers use their bounds.
function areaDistances(p) {
  const d2 = new Array(state.levels.length).fill(Infinity);
  for (const m of state.markers) {
    const dx = m.p[0] - p.x, dy = (m.p[1] - p.y) * STREAM_HEIGHT, dz = m.p[2] - p.z;
    const v = dx * dx + dy * dy + dz * dz;
    if (v < d2[m.l]) d2[m.l] = v;
  }
  return d2.map((v, i) => {
    if (v < Infinity) return Math.sqrt(v);
    const b = state.levels[i].bounds;
    if (!b) return 0;
    const dx = Math.max(b[0][0] - p.x, 0, p.x - b[1][0]);
    const dy = Math.max(b[0][1] - p.y, 0, p.y - b[1][1]) * STREAM_HEIGHT;
    const dz = Math.max(b[0][2] - p.z, 0, p.z - b[1][2]);
    return Math.hypot(dx, dy, dz);
  });
}
let streamCheck = -Infinity;
function updateStreaming(now) {
  if (now - streamCheck < 400) return;
  streamCheck = now;
  const dist = areaDistances(camera.position);
  const near = [];
  state.levels.forEach((lv, i) => {
    if (state.levelOn[i] || !state.levelWant[i]) return;
    if (dist[i] < STREAM_RANGE || ALWAYS_LOADED.has(lv.id)) near.push([dist[i], i]);
  });
  near.sort((a, b) => a[0] - b[0]).forEach(([, i]) => setLevel(i, true));
}
function showAllAreas() {
  state.levels.forEach((lv, i) => { if (state.levelWant[i] && !state.levelOn[i]) setLevel(i, true); });
}

function prepMesh(o) {
  if (!o.isMesh) return;
  o.material = mapMaterial(o.material);
  o.matrixAutoUpdate = false;
  o.updateMatrix();
  const g = o.geometry;
  if (!g.attributes.normal) g.computeVertexNormals();
  const n = (g.index ? g.index.count : g.attributes.position.count) / 3;
  triCount += n * (o.isInstancedMesh ? o.count : 1);
  if (o.isInstancedMesh) o.computeBoundingSphere();
  // Small props (rubble, grass, pots) are hidden when zoomed far out.
  if (!g.boundingSphere) g.computeBoundingSphere();
  let sc = 1;
  if (o.isInstancedMesh && o.count) { const mm = new THREE.Matrix4(); o.getMatrixAt(0, mm); sc = new THREE.Vector3().setFromMatrixScale(mm).length() / Math.sqrt(3); }
  else sc = new THREE.Vector3().setFromMatrixScale(o.matrixWorld).length() / Math.sqrt(3);
  if (g.boundingSphere.radius * sc < 150) { smallMeshes.push(o); o.visible = smallShown; }
}

// Posed models for a level: skeleton props (the game's still poses), wildlife (frame 0 of each critter's idle) and NPCs
// (their idle, with helmets, tools and other socket attachments). Node names start "<actor>|", and all nodes of one actor
// are culled as one model. They hang off the level's root, so area toggles and the cut-away apply to them; each model is
// distance- and occlusion-culled. kind ('skel', 'wild' or 'npc') is recorded in userData for test scripts.
function loadPosedModels(file, kind, lv, root, li) {
  loader.load(vq('data/' + file), (gltf) => {
    const sc = gltf.scene;
    root.add(sc);
    sc.updateMatrixWorld(true);
    const groups = new Map();
    sc.children.forEach((node, k) => {
      const actor = (node.name || '').split('|')[0] || '#' + k;
      if (!groups.has(actor)) groups.set(actor, []);
      groups.get(actor).push(node);
    });
    const sphere = new THREE.Sphere();
    for (const nodes of groups.values()) {
      const box = new THREE.Box3();
      for (const node of nodes) {
        node.traverse((o) => {
          o.matrixAutoUpdate = false;
          if (!o.isMesh) return;
          o.userData[kind] = true;
          o.material = mapMaterial(o.material);
          if (!o.geometry.attributes.normal) o.geometry.computeVertexNormals();
          triCount += (o.geometry.index ? o.geometry.index.count : o.geometry.attributes.position.count) / 3;
        });
        box.expandByObject(node);
        node.visible = false;
      }
      box.getBoundingSphere(sphere);
      const it = addCullItem(sphere.center, sphere.radius, () => state.levelOn[li], (it) => { for (const node of nodes) node.visible = it.show; });
      it.kind = kind;
    }
    updateStatus();
  }, undefined, (err) => console.warn(kind + ' models failed', lv.id, err));
}

function pumpQueue() {
  // nearest to the camera first
  if (loadQueue.length > 1) { const dist = areaDistances(camera.position); loadQueue.sort((a, b) => dist[a] - dist[b]); }
  while (loadingCount < 3 && loadQueue.length) {
    const i = loadQueue.shift();
    if (!state.levelOn[i]) { state.levelState[i] = 'idle'; updateAreaRow(i); continue; }
    loadingCount++;
    state.levelState[i] = 'loading';
    updateAreaRow(i); updateLoading();
    const lv = state.levels[i];
    loader.load(vq('data/' + lv.glb), (gltf) => {
      const root = gltf.scene;
      root.traverse(prepMesh);
      root.updateMatrixWorld(true);
      root.visible = state.levelOn[i];
      worldRoot.add(root);
      state.levelObj[i] = root;
      state.levelState[i] = 'loaded';
      if (lv.skel) loadPosedModels(lv.skel, 'skel', lv, root, i);
      if (lv.wild) loadPosedModels(lv.wild, 'wild', lv, root, i);
      if (lv.npc) loadPosedModels(lv.npc, 'npc', lv, root, i);
      done();
    }, undefined, (err) => {
      console.warn('level load failed', lv.id, err);
      lv.tries = (lv.tries || 0) + 1;
      if (lv.tries < 3) { state.levelState[i] = 'queued'; setTimeout(() => { loadQueue.push(i); pumpQueue(); }, 1500 * lv.tries); loadingCount--; updateAreaRow(i); pumpQueue(); return; }
      state.levelState[i] = 'error';
      done();
    });
    function done() { loadingCount--; updateAreaRow(i); updateLoading(); updateStatus(); pumpQueue(); }
  }
}

function updateLoading() {
  const total = state.levels.filter((_, i) => state.levelOn[i]).length;
  const loaded = state.levels.filter((_, i) => state.levelOn[i] && (state.levelState[i] === 'loaded' || state.levelState[i] === 'error')).length;
  const box = $('#loading');
  if (loaded >= total) { box.classList.add('done'); return; }
  box.classList.remove('done');
  $('#loadBar').style.width = (100 * loaded / Math.max(1, total)).toFixed(0) + '%';
  $('#loadText').textContent = `Loading areas ${loaded}/${total}...`;
}

// ------------------------------------------------------------------ markers (points)
const GLYPH_COLS = 8, CELL = 64;
function makeAtlas(glyphs) {
  const cv = document.createElement('canvas');
  cv.width = cv.height = GLYPH_COLS * CELL;
  const g = cv.getContext('2d');
  g.textAlign = 'center'; g.textBaseline = 'middle';
  g.font = `bold ${CELL * 0.55}px system-ui, sans-serif`;
  g.fillStyle = '#fff';
  glyphs.forEach((ch, i) => {
    const x = (i % GLYPH_COLS) * CELL + CELL / 2, y = Math.floor(i / GLYPH_COLS) * CELL + CELL / 2 + 2;
    g.fillText(ch, x, y);
  });
  const tex = new THREE.CanvasTexture(cv);
  tex.flipY = false;
  tex.colorSpace = THREE.NoColorSpace;
  return tex;
}

const markerVS = /* glsl */`
  attribute vec3 aColor; attribute float aGlyph; attribute float aVis; attribute float aSel;
  uniform float uSize; uniform float uPixelRatio; uniform float uSliceY;
  varying vec3 vColor; varying float vGlyph; varying float vSel; varying float vSmall;
  #include <common>
  #include <logdepthbuf_pars_vertex>
  void main() {
    vColor = aColor; vGlyph = aGlyph; vSel = aSel;
    vec4 mv = modelViewMatrix * vec4(position + vec3(0.0, 60.0, 0.0), 1.0);
    mv.xyz += normalize(-mv.xyz) * 120.0;
    gl_Position = projectionMatrix * mv;
    float vis = aVis * step(position.y, uSliceY);
    float dist = length(mv.xyz);
    float k = clamp(9000.0 / dist, 0.45, 1.0);
    vSmall = 1.0 - smoothstep(0.5, 0.75, k);
    gl_PointSize = uSize * uPixelRatio * vis * max(k, aSel) * (1.0 + aSel * 0.6);
    if (vis < 0.5) gl_Position = vec4(2.0, 2.0, 2.0, 1.0);
    #include <logdepthbuf_vertex>
  }`;
const markerFS = /* glsl */`
  uniform sampler2D uAtlas; uniform float uAlpha;
  varying vec3 vColor; varying float vGlyph; varying float vSel; varying float vSmall;
  #include <common>
  #include <logdepthbuf_pars_fragment>
  void main() {
    #include <logdepthbuf_fragment>
    vec2 pc = gl_PointCoord - 0.5;
    float d = length(pc);
    if (d > 0.5) discard;
    float cols = ${GLYPH_COLS}.0;
    vec2 cell = vec2(mod(vGlyph, cols), floor(vGlyph / cols));
    float glyph = texture2D(uAtlas, (cell + gl_PointCoord) / cols).r;
    vec3 ring = mix(vec3(0.05), vec3(1.0), vSel);
    vec3 col = d > 0.40 ? ring : mix(vColor, vec3(0.07), glyph * 0.92 * (1.0 - vSmall));
    float edge = smoothstep(0.5, 0.46, d);
    gl_FragColor = vec4(col, uAlpha * edge);
  }`;

let markerGeom = null, markerFront = null, markerBack = null;
const markerUniforms = {
  uSize: { value: 22 }, uPixelRatio: { value: renderer.getPixelRatio() }, uSliceY: { value: 1e9 }, uAtlas: { value: null },
};

function buildMarkers() {
  const n = state.markers.length;
  const pos = new Float32Array(n * 3), col = new Float32Array(n * 3), glyph = new Float32Array(n), vis = new Float32Array(n), sel = new Float32Array(n);
  const glyphs = [...new Set(state.cats.map((c) => c.glyph))];
  const tmp = new THREE.Color();
  state.markers.forEach((m, i) => {
    pos.set(m.p, i * 3);
    const cat = state.catByKey[m.c];
    tmp.set(cat ? cat.color : '#888');
    col.set([tmp.r, tmp.g, tmp.b], i * 3);
    glyph[i] = glyphs.indexOf(cat ? cat.glyph : '?');
  });
  markerGeom = new THREE.BufferGeometry();
  markerGeom.setAttribute('position', new THREE.BufferAttribute(pos, 3));
  markerGeom.setAttribute('aColor', new THREE.BufferAttribute(col, 3));
  markerGeom.setAttribute('aGlyph', new THREE.BufferAttribute(glyph, 1));
  markerGeom.setAttribute('aVis', new THREE.BufferAttribute(vis, 1));
  markerGeom.setAttribute('aSel', new THREE.BufferAttribute(sel, 1));
  markerGeom.computeBoundingSphere();
  markerUniforms.uAtlas.value = makeAtlas(glyphs);
  const mk = (alpha, depthTest, order) => {
    const mat = new THREE.ShaderMaterial({
      uniforms: { ...markerUniforms, uAlpha: { value: alpha } }, vertexShader: markerVS, fragmentShader: markerFS,
      transparent: true, depthTest, depthWrite: false,
    });
    const p = new THREE.Points(markerGeom, mat);
    p.frustumCulled = false; p.renderOrder = order;
    scene.add(p);
    return p;
  };
  markerBack = mk(0.38, false, 10);
  markerFront = mk(1.0, true, 11);
  state.visible = new Uint8Array(n);
}

function markerVisible(i) {
  const m = state.markers[i];
  if (i === state.selected) return true;
  // Searching also finds markers in areas the camera has not reached yet (but not in areas switched off by hand).
  if (state.search) return (state.levelOn[m.l] || state.levelWant[m.l]) && state.searchText[i].includes(state.search);
  if (!state.levelOn[m.l]) return false;
  if (!state.catOn[m.c]) return false;
  if (state.onlyHidden && m.it && !(m.diff === 'Hidden' || m.diff === 'Out of view')) return false;
  return true;
}

let refreshPending = false;
function refreshMarkers() {
  if (refreshPending || !markerGeom) return;
  refreshPending = true;
  requestAnimationFrame(() => {
    refreshPending = false;
    const vis = markerGeom.attributes.aVis.array;
    let shown = 0;
    for (let i = 0; i < state.markers.length; i++) {
      const v = markerVisible(i) ? 1 : 0;
      vis[i] = v; state.visible[i] = v; shown += v;
    }
    markerGeom.attributes.aVis.needsUpdate = true;
    updateEnemyModels();
    $('#listCount').textContent = shown;
    renderList();
    updateStatus();
  });
}

function setSelected(i) {
  const sel = markerGeom.attributes.aSel.array;
  if (state.selected >= 0) sel[state.selected] = 0;
  state.selected = i;
  if (i >= 0) sel[i] = 1;
  markerGeom.attributes.aSel.needsUpdate = true;
  refreshMarkers();
}

// ------------------------------------------------------------------ distance + occlusion culling
// Skeleton props and enemy models are drawn only when they are within CULL_RANGE of the camera and not hidden behind
// level geometry. Each one has an invisible box drawn after the opaque scene inside a WebGL2 occlusion query; when no
// pixel of the box passes the depth test, the model is skipped until a later query sees the box again.
const CULL_RANGE = 15000; // 150 m
const gl = renderer.getContext();
const cullItems = [];
const cullPending = new Set();
const cullRoot = new THREE.Group();
scene.add(cullRoot);
const proxyGeom = new THREE.BoxGeometry(2, 2, 2);
const proxyMat = new THREE.MeshBasicMaterial({ colorWrite: false, depthWrite: false });
let cullCheck = 0;

// center/radius: world-space bounding sphere. enabled(): whether the model may show at all (area loaded, filter on).
// onChange(item) is called when item.show changes.
function addCullItem(center, radius, enabled, onChange) {
  const it = { center: center.clone(), radius, enabled, onChange, show: false, inRange: false, near: false, occluded: false, query: null, pending: false, active: false };
  const px = new THREE.Mesh(proxyGeom, proxyMat);
  px.matrixAutoUpdate = false;
  px.visible = false;
  px.renderOrder = 1e9; // after all level geometry
  px.onBeforeRender = () => {
    if (it.pending) return;
    if (!it.query) it.query = gl.createQuery();
    gl.beginQuery(gl.ANY_SAMPLES_PASSED, it.query);
    it.active = true;
  };
  px.onAfterRender = () => {
    if (!it.active) return;
    gl.endQuery(gl.ANY_SAMPLES_PASSED);
    it.active = false; it.pending = true; cullPending.add(it);
  };
  it.proxy = px;
  setCullBounds(it, center, radius);
  cullRoot.add(px);
  cullItems.push(it);
  return it;
}
function setCullBounds(it, center, radius) {
  it.center.copy(center); it.radius = radius;
  it.proxy.matrix.compose(it.center, tmpQ0, tmpS0.setScalar(radius));
  it.proxy.matrixWorldNeedsUpdate = true;
}
const tmpQ0 = new THREE.Quaternion(), tmpS0 = new THREE.Vector3();
function setCullShow(it) {
  // Inside or right next to the box the query cannot see it, so close items always show.
  const show = it.inRange && (it.near || !it.occluded);
  if (show !== it.show) { it.show = show; it.onChange(it); }
}
// Distance and enabled state: a few times a second.
function updateCullRange(now) {
  if (now - cullCheck < 200) return;
  cullCheck = now;
  const c = camera.position;
  for (const it of cullItems) {
    const d2 = it.center.distanceToSquared(c);
    const inRange = it.enabled() && d2 < (CULL_RANGE + it.radius) ** 2;
    it.near = d2 < (it.radius * 1.8) ** 2;
    it.proxy.visible = inRange && !it.near;
    if (inRange !== it.inRange) { it.inRange = inRange; if (inRange) it.occluded = false; }
    setCullShow(it);
  }
}
// Query results arrive a frame or two after the draw.
function pollCullQueries() {
  for (const it of cullPending) {
    if (!gl.getQueryParameter(it.query, gl.QUERY_RESULT_AVAILABLE)) continue;
    it.occluded = !gl.getQueryParameter(it.query, gl.QUERY_RESULT);
    it.pending = false;
    cullPending.delete(it);
    setCullShow(it);
  }
}

// ------------------------------------------------------------------ enemy models
// One rest-pose model per enemy type, instanced at every visible enemy marker within range that is not occluded,
// facing the spawner's direction.
const ENEMY_CATS = new Set(['enemy', 'boss', 'mimic']);
const enemyCull = new Map(); // marker index -> cull item
let enemyDirty = false;
const enemyRoot = new THREE.Group();
scene.add(enemyRoot);
const enemyModels = new Map(); // enemy key -> { ready, parts: [{ geom, mat }], inst: [InstancedMesh], cap }
const enemyCapacity = new Map();
const tmpM = new THREE.Matrix4(), tmpQ = new THREE.Quaternion(), tmpE = new THREE.Euler(), tmpP = new THREE.Vector3(), ONE = new THREE.Vector3(1, 1, 1);

function ensureEnemyModel(key) {
  let em = enemyModels.get(key);
  if (em) return em;
  em = { ready: false, parts: [], inst: [] };
  enemyModels.set(key, em);
  const file = state.enemies[key]?.model;
  if (!file) return em;
  loader.load(vq('data/enemies/' + file), (gltf) => {
    gltf.scene.updateMatrixWorld(true);
    gltf.scene.traverse((o) => {
      if (!o.isMesh) return;
      const geom = o.geometry.clone();
      geom.applyMatrix4(o.matrixWorld);
      if (!geom.attributes.normal) geom.computeVertexNormals();
      em.parts.push({ geom, mat: mapMaterial(o.material) });
    });
    const cap = enemyCapacity.get(key) || 1;
    em.inst = em.parts.map(({ geom, mat }) => {
      const im = new THREE.InstancedMesh(geom, mat, cap);
      im.count = 0; im.frustumCulled = false;
      enemyRoot.add(im);
      return im;
    });
    em.ready = true;
    // Fit the occlusion boxes of this enemy type to the model (it stands on the marker and turns about it).
    const box = new THREE.Box3();
    em.parts.forEach(({ geom }) => { geom.computeBoundingBox(); box.union(geom.boundingBox); });
    const s = box.getBoundingSphere(new THREE.Sphere());
    const r = s.radius + Math.hypot(s.center.x, s.center.z);
    for (const [i, it] of enemyCull) {
      const m = state.markers[i];
      if (m.en === key) setCullBounds(it, tmpP.set(m.p[0], m.p[1] + s.center.y, m.p[2]), r);
    }
    updateEnemyModels();
  }, undefined, (err) => console.warn('enemy model failed', key, err));
  return em;
}

function updateEnemyModels() {
  const groups = new Map();
  for (let i = 0; i < state.markers.length; i++) {
    const m = state.markers[i];
    if (!state.visible[i] || !m.en || !ENEMY_CATS.has(m.c)) continue;
    if (!enemyCull.get(i)?.show) continue;
    let g = groups.get(m.en); if (!g) groups.set(m.en, g = []); g.push(i);
  }
  for (const [key, em] of enemyModels) if (!groups.has(key)) em.inst.forEach((im) => (im.count = 0));
  for (const [key, idxs] of groups) {
    if (!state.enemies[key]?.model) continue;
    const em = ensureEnemyModel(key);
    if (!em.ready) continue;
    for (const im of em.inst) {
      const n = Math.min(idxs.length, im.instanceMatrix.count);
      for (let j = 0; j < n; j++) {
        const m = state.markers[idxs[j]];
        tmpP.set(m.p[0], m.p[1], m.p[2]);
        tmpQ.setFromEuler(tmpE.set(0, -THREE.MathUtils.degToRad(m.y || 0), 0));
        im.setMatrixAt(j, tmpM.compose(tmpP, tmpQ, ONE));
      }
      im.count = n;
      im.instanceMatrix.needsUpdate = true;
    }
  }
}

// ------------------------------------------------------------------ picking
const v3 = new THREE.Vector3();
// Markers under the cursor, best first (closest to the cursor, then nearest the camera).
function pickCandidates(clientX, clientY) {
  const rect = canvas.getBoundingClientRect();
  const x = clientX - rect.left, y = clientY - rect.top;
  const r = Math.max(10, markerUniforms.uSize.value * 0.6);
  const out = [];
  const pos = markerGeom.attributes.position.array;
  for (let i = 0; i < state.markers.length; i++) {
    if (!state.visible[i]) continue;
    if (pos[i * 3 + 1] > state.sliceY) continue;
    v3.set(pos[i * 3], pos[i * 3 + 1] + 60, pos[i * 3 + 2]).project(camera);
    if (v3.z > 1 || v3.z < -1) continue;
    const sx = (v3.x + 1) / 2 * rect.width, sy = (1 - v3.y) / 2 * rect.height;
    const d = (sx - x) ** 2 + (sy - y) ** 2;
    if (d < r * r) out.push({ i, d, z: v3.z });
  }
  out.sort((a, b) => (Math.abs(a.d - b.d) <= 8 ? a.z - b.z : a.d - b.d));
  return out.map((c) => c.i);
}

// With "Markers through walls" off, markers hidden behind level geometry must not take hover or clicks. The
// candidates are drawn again (same shader, size and depth test as the visible marker pass, but writing nothing)
// inside occlusion queries; the first one with any visible pixel wins. Results arrive a frame or two later.
const PROBES = 8;
const probeMat = new THREE.ShaderMaterial({
  uniforms: { ...markerUniforms, uAlpha: { value: 1 } }, vertexShader: markerVS, fragmentShader: markerFS,
  transparent: true, depthTest: true, depthWrite: false, colorWrite: false,
});
const probes = [];
let probeReq = null; // { cands: [marker index], kind: 'hover' | 'click', cb(markerIndex or -1) }
for (let k = 0; k < PROBES; k++) {
  const g = new THREE.BufferGeometry();
  g.setAttribute('position', new THREE.BufferAttribute(new Float32Array(3), 3));
  g.setAttribute('aColor', new THREE.BufferAttribute(new Float32Array(3), 3));
  g.setAttribute('aGlyph', new THREE.BufferAttribute(new Float32Array(1), 1));
  g.setAttribute('aVis', new THREE.BufferAttribute(new Float32Array([1]), 1));
  g.setAttribute('aSel', new THREE.BufferAttribute(new Float32Array(1), 1));
  const obj = new THREE.Points(g, probeMat);
  obj.frustumCulled = false; obj.renderOrder = 12; obj.visible = false;
  const pr = { obj, query: null, state: 'idle', result: false };
  obj.onBeforeRender = () => {
    if (pr.state !== 'armed') return;
    gl.beginQuery(gl.ANY_SAMPLES_PASSED, pr.query);
    pr.state = 'active';
  };
  obj.onAfterRender = () => {
    if (pr.state !== 'active') return;
    gl.endQuery(gl.ANY_SAMPLES_PASSED);
    pr.state = 'pending';
  };
  scene.add(obj);
  probes.push(pr);
}
function probeMarkers(cands, kind, cb) {
  probeReq = { cands: cands.slice(0, PROBES), kind, cb };
  probes.forEach((pr, k) => {
    const i = probeReq.cands[k];
    if (pr.query) gl.deleteQuery(pr.query);
    pr.query = null;
    if (i === undefined) { pr.obj.visible = false; pr.state = 'idle'; return; }
    const g = pr.obj.geometry;
    g.attributes.position.array.set(markerGeom.attributes.position.array.subarray(i * 3, i * 3 + 3));
    g.attributes.position.needsUpdate = true;
    g.attributes.aSel.array[0] = markerGeom.attributes.aSel.array[i];
    g.attributes.aSel.needsUpdate = true;
    pr.query = gl.createQuery();
    pr.obj.visible = true; pr.state = 'armed';
  });
}
function pollProbes() {
  if (!probeReq) return;
  for (let k = 0; k < probeReq.cands.length; k++) {
    const pr = probes[k];
    if (pr.state === 'pending') {
      if (!gl.getQueryParameter(pr.query, gl.QUERY_RESULT_AVAILABLE)) return;
      pr.result = !!gl.getQueryParameter(pr.query, gl.QUERY_RESULT);
      pr.state = 'done';
    }
    if (pr.state !== 'done') return;
  }
  const req = probeReq;
  probeReq = null;
  probes.forEach((pr) => (pr.obj.visible = false));
  const k = req.cands.findIndex((_, k) => probes[k].result);
  req.cb(k < 0 ? -1 : req.cands[k]);
}
// Resolve the marker under the cursor, respecting "Markers through walls"; cb gets a marker index or -1.
function pickVisibleMarker(clientX, clientY, kind, cb) {
  const cands = pickCandidates(clientX, clientY);
  if (!cands.length) { cb(-1); return; }
  if ($('#xray').checked) { cb(cands[0]); return; }
  probeMarkers(cands, kind, cb);
}

const raycaster = new THREE.Raycaster();
function pickWorld(clientX, clientY) {
  const rect = canvas.getBoundingClientRect();
  const ndc = new THREE.Vector2(((clientX - rect.left) / rect.width) * 2 - 1, -((clientY - rect.top) / rect.height) * 2 + 1);
  raycaster.setFromCamera(ndc, camera);
  const hits = raycaster.intersectObjects(worldRoot.children.filter((o) => o.visible), true);
  for (const h of hits) if (h.point.y <= state.sliceY) return h.point;
  return null;
}

// ------------------------------------------------------------------ camera helpers
let flight = null;
function flyTo(target, dist) {
  const dir = camera.position.clone().sub(controls.target);
  if (dir.lengthSq() < 1) dir.set(0, 1, 1);
  dir.normalize();
  if (dir.y < 0.35) { dir.y = 0.35; dir.normalize(); }
  const d = dist ?? Math.min(Math.max(controls.getDistance(), 1200), 4000);
  flight = {
    t0: performance.now(), dur: 750,
    fromT: controls.target.clone(), toT: target.clone(),
    fromP: camera.position.clone(), toP: target.clone().add(dir.multiplyScalar(d)),
  };
}
function flyToBounds(b) {
  if (!b) return;
  const lo = new THREE.Vector3(...b[0]), hi = new THREE.Vector3(...b[1]);
  const c = lo.clone().add(hi).multiplyScalar(0.5);
  const size = hi.clone().sub(lo).length();
  flyTo(c, Math.max(2500, size * 0.9));
}
// Opening view: standing on the beach where a new game starts (PlayerStart_1 in the persistent level, game x -1586
// y -131, ground at z -113), at eye height, looking inland the way the player spawns (+X).
function startView() {
  camera.position.set(-1586, 57, -131);
  controls.target.set(-1586 + 2000, -143, -131);
  controls.update();
  flight = null;
}
// "Reset view": the whole island from above.
function resetView() {
  const lo = new THREE.Vector3(Infinity, Infinity, Infinity), hi = new THREE.Vector3(-Infinity, -Infinity, -Infinity);
  state.levels.forEach((lv, i) => {
    if (!state.levelOn[i] || !lv.bounds || lv.region !== 'Isle of Neasied' || lv.id === '050_Ocean') return;
    lo.min(new THREE.Vector3(...lv.bounds[0])); hi.max(new THREE.Vector3(...lv.bounds[1]));
  });
  if (!isFinite(lo.x)) return;
  const c = lo.clone().add(hi).multiplyScalar(0.5);
  const size = hi.clone().sub(lo).length();
  flight = {
    t0: performance.now(), dur: 750, fromT: controls.target.clone(), toT: c,
    fromP: camera.position.clone(), toP: c.clone().add(new THREE.Vector3(0.15, 0.75, 0.65).normalize().multiplyScalar(size * 0.8)),
  };
}

// keyboard movement
const keys = new Set();
window.addEventListener('keydown', (e) => {
  if (e.target.matches('input[type=search], input[type=text], textarea')) return;
  if (e.target.matches('input[type=range]') && e.key.length === 1) e.target.blur();
  const k = e.key.toLowerCase();
  if ('wasdqe'.includes(k) && k.length === 1) { keys.add(k); e.preventDefault(); }
  // Shift or Space is the 4x boost. Space is there because Firefox always opens its own menu on Shift + right-click.
  if (e.key === 'Shift') keys.add('shift');
  if (e.code === 'Space') { keys.add('boost'); e.preventDefault(); }
  if (e.key === 'Escape') closeInfo();
});
window.addEventListener('keyup', (e) => {
  keys.delete(e.key.toLowerCase());
  if (e.key === 'Shift') keys.delete('shift');
  if (e.code === 'Space') {
    keys.delete('boost');
    // Stop the keyup from clicking a focused button or checkbox.
    if (!e.target.matches('input[type=search], input[type=text], textarea')) e.preventDefault();
  }
});
window.addEventListener('blur', () => keys.clear());

const MOVE_KEYS = ['w', 'a', 's', 'd', 'q', 'e'];
// Fly speed multiplier from the toolbar slider: 0..100 maps to 0.125x..2x, default 50 = 0.5x.
// Fly speed slider: 0..100 maps logarithmically to 2..400 m/s. Default 38 = about 15 m/s.
const SPEED_DEFAULT = 38;
const speedFromSlider = (v) => 2 * Math.pow(200, v / 100);
let flySpeed = speedFromSlider(SPEED_DEFAULT);
const moving = () => MOVE_KEYS.some((k) => keys.has(k));

// W/S fly along the view direction (including up/down), A/D strafe, Q/E straight down/up.
function applyKeys(dt) {
  if (!moving()) return;
  const fwd = new THREE.Vector3(); camera.getWorldDirection(fwd);
  const right = new THREE.Vector3().crossVectors(fwd, camera.up);
  if (right.lengthSq() < 1e-6) right.set(1, 0, 0);
  right.normalize();
  const mv = new THREE.Vector3();
  if (keys.has('w')) mv.add(fwd); if (keys.has('s')) mv.sub(fwd);
  if (keys.has('d')) mv.add(right); if (keys.has('a')) mv.sub(right);
  if (keys.has('e')) mv.y += 1; if (keys.has('q')) mv.y -= 1;
  if (mv.lengthSq() === 0) return;
  const speed = flySpeed * 100 * (keys.has('boost') || keys.has('shift') ? 4 : 1); // cm per second
  mv.normalize().multiplyScalar(speed * dt);
  camera.position.add(mv); controls.target.add(mv);
  flight = null;
}

// Mouse look: turn the view in place, pivoting at the camera. Right-drag always; left-drag while a movement key is held.
// The orbit distance is kept, so wheel zoom and left-drag orbit still behave normally afterwards.
let look = null;
const lookSph = new THREE.Spherical();
function lookRotate(dx, dy) {
  const off = controls.target.clone().sub(camera.position);
  lookSph.setFromVector3(off);
  lookSph.theta -= dx * 0.0035;
  lookSph.phi = THREE.MathUtils.clamp(lookSph.phi + dy * 0.0035, 0.02, Math.PI - 0.02);
  off.setFromSpherical(lookSph);
  controls.target.copy(camera.position).add(off);
}
window.addEventListener('pointerdown', (e) => {
  if (e.target !== canvas || e.pointerType !== 'mouse') return;
  if (e.button === 2 || (e.button === 0 && moving())) {
    look = { id: e.pointerId, x: e.clientX, y: e.clientY };
    controls.enabled = false;
    flight = null;
    canvas.setPointerCapture(e.pointerId);
    canvas.style.cursor = 'grabbing';
  }
}, true);
window.addEventListener('pointermove', (e) => {
  if (!look || e.pointerId !== look.id) return;
  lookRotate(e.clientX - look.x, e.clientY - look.y);
  look.x = e.clientX; look.y = e.clientY;
}, true);
let lookEndedAt = 0;
function endLook(e) {
  if (!look || e.pointerId !== look.id) return;
  look = null;
  lookEndedAt = performance.now();
  controls.enabled = true;
  canvas.style.cursor = '';
}
window.addEventListener('pointerup', endLook, true);
window.addEventListener('pointercancel', endLook, true);
window.addEventListener('contextmenu', (e) => {
  if (e.target === canvas || look || performance.now() - lookEndedAt < 400) e.preventDefault();
}, true);

// ------------------------------------------------------------------ render loop
let last = performance.now();
function frame(now) {
  const dt = Math.min(0.1, (now - last) / 1000); last = now;
  if (flight) {
    const t = Math.min(1, (now - flight.t0) / flight.dur);
    const e = t < 0.5 ? 2 * t * t : 1 - Math.pow(-2 * t + 2, 2) / 2;
    controls.target.lerpVectors(flight.fromT, flight.toT, e);
    camera.position.lerpVectors(flight.fromP, flight.toP, e);
    if (t >= 1) flight = null;
  }
  applyKeys(dt);
  controls.update();
  updateSmall(now);
  updateStreaming(now);
  updateCullRange(now);
  if (enemyDirty) { enemyDirty = false; updateEnemyModels(); }
  renderer.render(scene, camera);
  pollCullQueries();
  pollProbes();
  requestAnimationFrame(frame);
}

function resize() {
  const w = canvas.clientWidth, h = canvas.clientHeight;
  renderer.setSize(w, h, false);
  camera.aspect = w / Math.max(1, h);
  camera.updateProjectionMatrix();
  markerUniforms.uPixelRatio.value = renderer.getPixelRatio();
}
window.addEventListener('resize', resize);

// ------------------------------------------------------------------ pointer events
let downAt = null;
canvas.addEventListener('pointerdown', (e) => { downAt = { x: e.clientX, y: e.clientY, t: performance.now() }; });
canvas.addEventListener('pointerup', (e) => {
  if (!downAt) return;
  const moved = Math.hypot(e.clientX - downAt.x, e.clientY - downAt.y);
  downAt = null;
  if (moved > 5 || e.button !== 0) return;
  pickVisibleMarker(e.clientX, e.clientY, 'click', (i) => { if (i >= 0) select(i, false); });
});
canvas.addEventListener('dblclick', (e) => {
  const p = pickWorld(e.clientX, e.clientY);
  if (p) flyTo(p, Math.min(controls.getDistance(), 3000));
});
let hoverRaf = 0;
canvas.addEventListener('pointermove', (e) => {
  if (e.buttons || look) { $('#tooltip').hidden = true; return; }
  cancelAnimationFrame(hoverRaf);
  hoverRaf = requestAnimationFrame(() => {
    if (!markerGeom || probeReq?.kind === 'click') return; // a click is being resolved; don't replace it
    pickVisibleMarker(e.clientX, e.clientY, 'hover', (i) => {
      const tip = $('#tooltip');
      canvas.style.cursor = i >= 0 ? 'pointer' : '';
      if (i < 0) { tip.hidden = true; return; }
      const m = state.markers[i];
      const rect = canvas.getBoundingClientRect();
      tip.replaceChildren(el('div', null, m.n), el('div', 'tsub', `${catLabel(m.c)} · ${state.levels[m.l].name}`));
      tip.style.left = (e.clientX - rect.left) + 'px';
      tip.style.top = (e.clientY - rect.top) + 'px';
      tip.hidden = false;
    });
  });
});
canvas.addEventListener('pointerleave', () => { $('#tooltip').hidden = true; });

// ------------------------------------------------------------------ UI: filters
const catLabel = (k) => state.catByKey[k]?.label ?? k;

function swatch(cat) {
  const s = el('span', 'swatch', cat.glyph);
  s.style.setProperty('--sw', cat.color);
  return s;
}

function buildFilters() {
  const box = $('#filters');
  box.replaceChildren();
  const groups = [];
  for (const c of state.cats) { let g = groups.find((x) => x.name === c.group); if (!g) groups.push(g = { name: c.group, cats: [] }); g.cats.push(c); }
  for (const g of groups) {
    const wrap = el('div', 'fgroup');
    const head = el('div', 'fgroup-head');
    head.append(el('span', null, g.name));
    const tog = el('button', null, 'toggle');
    tog.addEventListener('click', () => {
      const anyOff = g.cats.some((c) => !state.catOn[c.key]);
      g.cats.forEach((c) => (state.catOn[c.key] = anyOff));
      syncFilterRows(); refreshMarkers(); saveFilters();
    });
    head.append(tog);
    wrap.append(head);
    for (const c of g.cats) {
      if (!c.count) continue;
      const row = el('div', 'frow');
      row.dataset.cat = c.key;
      row.append(swatch(c), el('span', null, c.label), el('span', 'count', String(c.count)));
      row.addEventListener('click', () => { state.catOn[c.key] = !state.catOn[c.key]; syncFilterRows(); refreshMarkers(); saveFilters(); });
      wrap.append(row);
    }
    box.append(wrap);
  }
  syncFilterRows();
}
function syncFilterRows() {
  document.querySelectorAll('.frow').forEach((r) => r.classList.toggle('off', !state.catOn[r.dataset.cat]));
}
function saveFilters() { try { localStorage.setItem('qdmap.cats', JSON.stringify(state.catOn)); } catch {} }
function loadFilters() {
  state.cats.forEach((c) => (state.catOn[c.key] = c.on));
  try { const s = JSON.parse(localStorage.getItem('qdmap.cats') || 'null'); if (s) Object.assign(state.catOn, s); } catch {}
}

$('#allOn').addEventListener('click', () => { state.cats.forEach((c) => (state.catOn[c.key] = true)); syncFilterRows(); refreshMarkers(); saveFilters(); });
$('#allOff').addEventListener('click', () => { state.cats.forEach((c) => (state.catOn[c.key] = false)); syncFilterRows(); refreshMarkers(); saveFilters(); });
$('#defaults').addEventListener('click', () => { state.cats.forEach((c) => (state.catOn[c.key] = c.on)); syncFilterRows(); refreshMarkers(); saveFilters(); });
$('#onlyHidden').addEventListener('change', (e) => { state.onlyHidden = e.target.checked; refreshMarkers(); });

// ------------------------------------------------------------------ UI: areas
const areaRows = [];
function buildAreas() {
  const box = $('#areas');
  box.replaceChildren();
  const regions = [];
  state.levels.forEach((lv, i) => { let r = regions.find((x) => x.name === lv.region); if (!r) regions.push(r = { name: lv.region, idx: [] }); r.idx.push(i); });
  const counts = new Array(state.levels.length).fill(0);
  state.markers.forEach((m) => counts[m.l]++);
  for (const r of regions) {
    const wrap = el('div', 'region');
    const head = el('div', 'region-head');
    const cb = el('input'); cb.type = 'checkbox';
    cb.checked = r.idx.some((i) => state.levelOn[i]);
    cb.addEventListener('change', () => r.idx.forEach((i) => setLevelByUser(i, cb.checked)));
    head.append(cb, el('span', null, r.name));
    wrap.append(head);
    for (const i of r.idx) {
      const lv = state.levels[i];
      const row = el('div', 'lrow');
      const c = el('input'); c.type = 'checkbox'; c.checked = state.levelOn[i];
      c.addEventListener('change', () => setLevelByUser(i, c.checked));
      const name = el('span', 'lname', lv.name);
      name.title = lv.id + (lv.areaName ? ` (${lv.areaName})` : '');
      name.addEventListener('click', () => {
        if (!state.levelOn[i]) { setLevelByUser(i, true); c.checked = true; }
        flyToBounds(lv.bounds);
        if (lv.bounds) setSliceY(lv.bounds[1][1] + 1500);
        $('#sidebar').classList.remove('open');
      });
      const st = el('span', 'lstate', counts[i] ? `${counts[i]}` : '');
      row.append(c, name, st);
      wrap.append(row);
      areaRows[i] = { row, c, st, count: counts[i] };
    }
    box.append(wrap);
  }
}
function updateAreaRow(i) {
  const r = areaRows[i];
  if (!r) return;
  r.c.checked = !!state.levelOn[i];
  const s = state.levelState[i];
  r.st.textContent = s === 'loading' || s === 'queued' ? 'loading' : s === 'error' ? 'failed' : (r.count ? String(r.count) : '');
}

// ------------------------------------------------------------------ UI: list + search
function renderList() {
  const ul = $('#list');
  const rows = [];
  const t = controls.target;
  for (let i = 0; i < state.markers.length; i++) {
    if (!state.visible[i]) continue;
    const p = state.markers[i].p;
    rows.push([i, (p[0] - t.x) ** 2 + (p[1] - t.y) ** 2 + (p[2] - t.z) ** 2]);
  }
  rows.sort((a, b) => a[1] - b[1]);
  const frag = document.createDocumentFragment();
  for (const [i] of rows.slice(0, 400)) {
    const m = state.markers[i];
    const li = el('li');
    const icon = markerIcon(m);
    if (icon) { const img = el('img'); img.src = icon; img.alt = ''; img.loading = 'lazy'; li.append(img); }
    else li.append(swatch(state.catByKey[m.c]));
    const txt = el('div', 'txt');
    txt.append(el('div', null, m.n), el('div', 'meta', `${catLabel(m.c)} · ${state.levels[m.l].name}`));
    li.append(txt);
    li.addEventListener('click', () => select(i, true));
    frag.append(li);
  }
  ul.replaceChildren(frag);
  $('#listHint').textContent = rows.length > 400 ? `Showing the 400 nearest of ${rows.length} markers. Search or filter to narrow it down.` : 'Markers matching the current filters and search, nearest to the view first.';
}
let listTimer = 0;
controls.addEventListener('end', () => { clearTimeout(listTimer); listTimer = setTimeout(() => { if ($('#tab-list').classList.contains('active')) renderList(); }, 300); });

let searchTimer = 0;
$('#search').addEventListener('input', (e) => {
  clearTimeout(searchTimer);
  searchTimer = setTimeout(() => {
    state.search = e.target.value.trim().toLowerCase();
    refreshMarkers();
    if (state.search) switchTab('list');
  }, 150);
});

function switchTab(name) {
  document.querySelectorAll('.tabs button').forEach((b) => b.classList.toggle('active', b.dataset.tab === name));
  document.querySelectorAll('.tab-body').forEach((b) => b.classList.toggle('active', b.id === 'tab-' + name));
  if (name === 'list') renderList();
}
document.querySelectorAll('.tabs button').forEach((b) => b.addEventListener('click', () => switchTab(b.dataset.tab)));

// ------------------------------------------------------------------ UI: info panel
function markerIcon(m) {
  if (m.it && m.it.length) { const it = state.items[m.it[0][0]]; if (it?.icon) return 'data/icons/' + it.icon; }
  return null;
}
function itemRow(key, right, onClick) {
  const it = state.items[key];
  const li = el('li', onClick ? 'link' : '');
  if (it?.icon) { const img = el('img'); img.src = 'data/icons/' + it.icon; img.alt = ''; li.append(img); }
  const t = el('div');
  t.append(el('div', null, it ? it.name : key));
  li.append(t);
  if (right) li.append(el('span', 'right', right));
  if (onClick) li.addEventListener('click', onClick);
  return li;
}
function markerRow(i, right) {
  const m = state.markers[i];
  const li = el('li', 'link');
  const icon = markerIcon(m);
  if (icon) { const img = el('img'); img.src = icon; img.alt = ''; li.append(img); } else li.append(swatch(state.catByKey[m.c]));
  const t = el('div');
  t.append(el('div', null, m.n), el('div', 'sub', `${catLabel(m.c)} · ${state.levels[m.l].name}`));
  li.append(t);
  if (right) li.append(el('span', 'right', right));
  li.addEventListener('click', () => select(i, true));
  return li;
}
function chip(text, cls) { return el('span', 'chip' + (cls ? ' ' + cls : ''), text); }
function section(title, ...nodes) { const f = document.createDocumentFragment(); f.append(el('h3', null, title), ...nodes); return f; }
function whereToFind(key, exclude) {
  const ul = el('ul', 'ilist');
  const spots = (state.itemWhere[key] || []).filter((i) => i !== exclude);
  if (!spots.length) { ul.append(el('li', 'sub', 'Not placed in the world. It may be a reward, a drop or a shop item.')); return ul; }
  spots.slice(0, 8).forEach((i) => ul.append(markerRow(i)));
  if (spots.length > 8) ul.append(el('li', 'sub', `and ${spots.length - 8} more (search for it)`));
  return ul;
}

function select(i, fly) {
  setSelected(i);
  const m = state.markers[i];
  if (!state.levelOn[m.l]) setLevelByUser(m.l, true);
  if (fly) flyTo(new THREE.Vector3(...m.p));
  showInfo(i);
  history.replaceState(null, '', '#m=' + i);
}
function closeInfo() {
  $('#info').hidden = true;
  if (markerGeom) setSelected(-1);
  history.replaceState(null, '', location.pathname + location.search);
}
$('#closeInfo').addEventListener('click', closeInfo);

function showInfo(i) {
  const m = state.markers[i];
  const cat = state.catByKey[m.c];
  const body = $('#infoBody');
  body.replaceChildren();
  const head = el('div', 'ihead');
  const icon = markerIcon(m);
  if (icon) { const img = el('img', 'icon'); img.src = icon; img.alt = ''; head.append(img); } else head.append(swatch(cat));
  const ht = el('div');
  ht.append(el('h2', null, m.n));
  const c = chip(cat.label); const dot = el('span', 'dot'); dot.style.setProperty('--sw', cat.color); c.prepend(dot);
  ht.append(c, chip(state.levels[m.l].name));
  head.append(ht);
  body.append(head);

  const flags = el('div');
  if (m.diff) flags.append(chip(m.diff, m.diff === 'Hidden' ? 'good' : 'warn'));
  if (m.act) flags.append(chip('Appears after an event', 'warn'));
  if (m.inside != null) flags.append(chip('Inside a container', 'warn'));
  if (m.trap) flags.append(chip('Disguised trap', 'bad'));
  if (m.ambush) flags.append(chip(m.c === 'boss' ? 'Appears when triggered' : 'Ambush: appears when triggered', 'bad'));
  if (m.once) flags.append(chip('Does not respawn', ''));
  if (flags.childNodes.length) body.append(flags);

  // items
  if (m.it) {
    for (const [key, n] of m.it) {
      const it = state.items[key];
      if (!it) continue;
      if (m.it.length > 1) body.append(el('h3', null, it.name + (n > 1 ? ` ×${n}` : '')));
      if (it.desc) body.append(el('p', 'desc', it.desc));
      if (it.sell) body.append(el('p', null, `Sells for ${it.sell} coins.`));
    }
    if (m.inside != null) { const ul = el('ul', 'ilist'); ul.append(markerRow(m.inside)); body.append(section('Found inside', ul)); }
    const k = m.it[0][0];
    const others = (state.itemWhere[k] || []).length - 1;
    if (others > 0 && others < 60) body.append(section(`Other locations (${others})`, whereToFind(k, i)));
  }
  // containers
  if (m.in) {
    const ul = el('ul', 'ilist');
    if (!m.in.length) ul.append(el('li', 'sub', 'Empty or random loot'));
    m.in.forEach((t) => ul.append(markerRow(t)));
    body.append(section('Contents', ul));
  }
  if (m.amb && m.amb.length) {
    const ul = el('ul', 'ilist');
    const counts = {};
    m.amb.forEach((t) => (counts[state.markers[t].n] = (counts[state.markers[t].n] || 0) + 1));
    Object.entries(counts).forEach(([n, k]) => ul.append(el('li', null, `${k}× ${n}`)));
    body.append(section('Opening this triggers an ambush', ul));
  }
  // enemies
  if (m.en) {
    const e = state.enemies[m.en];
    if (e) {
      const st = el('div', 'stats');
      for (const [v, l] of [[e.hp, 'HP'], [e.dmg, 'Damage'], [e.xp, 'XP']]) { const s = el('div', 'stat'); s.append(el('b', null, fmt(v)), el('span', null, l)); st.append(s); }
      body.append(st);
      if (e.desc) body.append(el('p', 'desc', e.desc));
      const res = Object.entries(e.res || {});
      if (res.length) {
        const t = el('table', 'kv');
        res.forEach(([k, v]) => { const tr = el('tr'); tr.append(el('td', null, k), el('td', null, v)); t.append(tr); });
        body.append(section('Damage taken', t));
      }
      if (e.status) body.append(el('p', null, `Inflicts: ${e.status}`));
      if (e.drops?.length) {
        const ul = el('ul', 'ilist');
        e.drops.forEach((d) => ul.append(itemRow(d.item, `${Math.round(d.chance * 100)}%`)));
        body.append(section('Drops', ul));
      }
    }
  }
  // npcs / shops
  if (m.stock?.length) {
    const ul = el('ul', 'ilist');
    m.stock.forEach((s) => ul.append(itemRow(s.item, `${s.price} coins${s.inf ? '' : s.stock > 1 ? ` · ${s.stock} in stock` : ''}`)));
    body.append(section('Stock', ul));
  }
  if (m.rewards?.length) {
    const ul = el('ul', 'ilist');
    m.rewards.forEach((r) => {
      if (state.items[r.item]) ul.append(itemRow(r.item, r.need ? `at ${r.need}` : ''));
      else ul.append(el('li', null, `${r.item}${r.need ? ` at ${r.need}` : ''}`));
    });
    body.append(section('Rewards', ul));
  }
  // places
  if (m.region) body.append(el('p', null, m.region));
  if (m.desc) body.append(el('p', 'desc', m.desc));
  if (m.img) { const img = el('img', 'shot'); img.src = 'data/icons/' + m.img; img.alt = m.n; body.append(img); }
  if (m.text) body.append(el('p', 'desc', m.text));
  // doors / keys
  if (m.keys?.length) {
    const wrap = document.createDocumentFragment();
    for (const k of m.keys) {
      const ul = el('ul', 'ilist');
      ul.append(itemRow(k));
      wrap.append(ul);
      const where = whereToFind(k, -1);
      wrap.append(el('div', 'sub', 'Where to find it:'), where);
    }
    body.append(section(m.c === 'receptacle' ? 'Accepts' : 'Requires', wrap));
  }
  if (m.needs > 1) body.append(el('p', null, `Needs ${m.needs} switches or events to open.`));
  if (m.rune) body.append(section('Rune', el('p', null, `Revealed and activated with ${m.rune === 'Any' ? 'any rune charm' : 'the ' + m.rune + ' rune charm'} equipped (Rune Vision).`)));
  if (m.c === 'secret') body.append(el('p', 'desc', 'A wall section that slides away when you interact with it. Look for faint markings or a draft.'));
  if (m.trig?.length) {
    const ul = el('ul', 'ilist');
    m.trig.slice(0, 12).forEach((t) => ul.append(markerRow(t)));
    body.append(section('Activates', ul));
  }
  const p = m.p;
  body.append(el('div', 'coords', `x ${p[0].toFixed(0)}  y ${p[2].toFixed(0)}  z ${p[1].toFixed(0)}  (game units)`));
  const btns = el('div', 'btnrow');
  const go = el('button', 'small-btn', 'Fly here'); go.addEventListener('click', () => flyTo(new THREE.Vector3(...m.p)));
  const cut = el('button', 'small-btn', 'Cut away above this'); cut.addEventListener('click', () => setSliceY(m.p[1] + 400));
  const link = el('button', 'small-btn', 'Copy link');
  link.addEventListener('click', async () => { try { await navigator.clipboard.writeText(location.href); link.textContent = 'Copied'; } catch { link.textContent = location.hash; } });
  btns.append(go, cut, link);
  body.append(btns);
  $('#info').hidden = false;
  $('#info').scrollTop = 0;
}

// ------------------------------------------------------------------ slice
const sliceInput = $('#slice');
function sliderToY(v) { return v >= 1000 ? Infinity : state.worldMinY + (state.worldMaxY - state.worldMinY) * (v / 1000); }
function setSliceY(y) {
  state.sliceY = y;
  slicePlane.constant = isFinite(y) ? y : 1e9;
  markerUniforms.uSliceY.value = isFinite(y) ? y : 1e9;
  $('#sliceVal').textContent = isFinite(y) ? `${(y / 100).toFixed(0)} m` : 'off';
  const v = isFinite(y) ? Math.round(1000 * (y - state.worldMinY) / (state.worldMaxY - state.worldMinY)) : 1000;
  if (Number(sliceInput.value) !== v) sliceInput.value = String(Math.max(0, Math.min(999, v)));
}
sliceInput.addEventListener('input', () => setSliceY(sliderToY(Number(sliceInput.value))));
$('#msize').addEventListener('input', (e) => (markerUniforms.uSize.value = Number(e.target.value)));
$('#xray').addEventListener('change', (e) => { if (markerBack) markerBack.visible = e.target.checked; });
$('#resetView').addEventListener('click', () => { setSliceY(Infinity); resetView(); });
$('#showAllAreas').addEventListener('click', showAllAreas);
{
  const input = $('#speed');
  let v = SPEED_DEFAULT;
  try { const s = localStorage.getItem('qdmap.flyspeed'); if (s !== null && !isNaN(Number(s))) v = Number(s); } catch {}
  const show = () => { $('#speedVal').textContent = `${flySpeed < 10 ? flySpeed.toFixed(1) : Math.round(flySpeed)} m/s`; };
  input.value = String(v);
  flySpeed = speedFromSlider(v);
  show();
  input.addEventListener('input', () => {
    flySpeed = speedFromSlider(Number(input.value));
    show();
    try { localStorage.setItem('qdmap.flyspeed', input.value); } catch {}
  });
  // Give keyboard focus back to the map so WASD works straight after using a slider or checkbox.
  document.querySelectorAll('#toolbar input').forEach((el) => el.addEventListener('change', () => el.blur()));
}
$('#textures').checked = useTextures;
$('#textures').addEventListener('change', (e) => setTextures(e.target.checked));
$('#helpBtn').addEventListener('click', () => ($('#help').hidden = false));
$('#closeHelp').addEventListener('click', () => ($('#help').hidden = true));
$('#help').addEventListener('click', (e) => { if (e.target.id === 'help') $('#help').hidden = true; });
$('#openSide').addEventListener('click', () => $('#sidebar').classList.add('open'));
$('#closeSide').addEventListener('click', () => $('#sidebar').classList.remove('open'));

function updateStatus() {
  const shown = state.visible.reduce((a, b) => a + b, 0);
  const loaded = state.levelState.filter((s) => s === 'loaded').length;
  $('#status').textContent = `${shown} markers shown · ${loaded} areas loaded · ${(triCount / 1e6).toFixed(1)}M triangles`;
}

// ------------------------------------------------------------------ boot
async function boot() {
  const [lv, mk, ct] = await Promise.all(['levels', 'markers', 'catalog'].map((n) => fetch(vq(`data/${n}.json`)).then((r) => r.json())));
  state.levels = lv.levels;
  state.cats = lv.categories;
  state.cats.forEach((c) => (state.catByKey[c.key] = c));
  state.markers = mk.markers;
  state.items = ct.items;
  state.enemies = ct.enemies;
  state.levelWant = state.levels.map((l) => l.on); // on by default; loaded by streaming (see updateStreaming)
  state.levelOn = state.levels.map(() => false);
  state.levelState = state.levels.map(() => 'idle');
  state.levelObj = state.levels.map(() => null);

  // search text and item index
  state.searchText = state.markers.map((m) => {
    const parts = [m.n, catLabel(m.c), state.levels[m.l].name, state.levels[m.l].region, m.region || ''];
    (m.it || []).forEach(([k]) => parts.push(state.items[k]?.name || ''));
    (m.in || []).forEach((t) => parts.push(state.markers[t]?.n || ''));
    (m.keys || []).forEach((k) => parts.push(state.items[k]?.name || ''));
    (m.stock || []).forEach((s) => parts.push(state.items[s.item]?.name || ''));
    if (m.en) parts.push(state.enemies[m.en]?.name || '');
    return parts.join(' | ').toLowerCase();
  });
  state.markers.forEach((m, i) => (m.it || []).forEach(([k]) => (state.itemWhere[k] ||= []).push(i)));
  state.markers.forEach((m) => { if (m.en) enemyCapacity.set(m.en, (enemyCapacity.get(m.en) || 0) + 1); });
  // Occlusion boxes for enemy models; sized for a person until the type's model has loaded.
  state.markers.forEach((m, i) => {
    if (!m.en || !ENEMY_CATS.has(m.c) || !state.enemies[m.en]?.model) return;
    const it = addCullItem(new THREE.Vector3(m.p[0], m.p[1] + 100, m.p[2]), 200,
      () => !!state.visible[i] && state.levelOn[m.l], () => { enemyDirty = true; });
    enemyCull.set(i, it);
  });

  let minY = Infinity, maxY = -Infinity;
  state.levels.forEach((l) => { if (l.bounds && l.on) { minY = Math.min(minY, l.bounds[0][1]); maxY = Math.max(maxY, l.bounds[1][1]); } });
  state.worldMinY = minY - 500; state.worldMaxY = maxY + 500;

  loadFilters();
  buildMarkers();
  buildFilters();
  buildAreas();
  resize();
  startView();
  // Areas this browser has already been near load straight away (nearest first); the rest stream in as the camera moves.
  state.levels.forEach((l, i) => { if (state.levelWant[i] && seenAreas.has(l.id)) setLevel(i, true); });
  updateStreaming(performance.now());
  refreshMarkers();
  updateLoading();

  const h = location.hash.match(/m=(\d+)/);
  if (h && state.markers[+h[1]]) setTimeout(() => select(+h[1], true), 50);
  requestAnimationFrame(frame);
}
boot().catch((e) => { console.error(e); $('#loadText').textContent = 'Failed to load map data: ' + e.message; });
window.__qd = { state, setLevel, showAllAreas, flyTo, THREE, camera, controls, scene, setSliceY, renderer, smallMeshes, enemyRoot, cullItems, enemyCull };
