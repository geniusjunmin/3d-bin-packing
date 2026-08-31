import * as THREE from 'three';
import { OrbitControls } from 'three/addons/controls/OrbitControls.js';

const state = {
  boxes: [],
  items: [],
  quantities: new Map(),
  result: null,
  activeBox: 0,
  shellVisible: true
};

const palette = ['#d8ef51', '#ff8c42', '#57c7d4', '#c084fc', '#fb7185', '#60a5fa', '#facc15', '#5ee0a0'];
const $ = selector => document.querySelector(selector);
const byId = id => document.getElementById(id);

let scene;
let camera;
let renderer;
let controls;
let contentGroup;
let shellGroup;
let itemMeshes = [];
let raycaster;
let pointer;
let currentBoxSize = 500;
const animation = { playing: false, elapsed: 0, lastTime: 0, duration: 650 };

document.addEventListener('DOMContentLoaded', initialize);

async function initialize() {
  bindEvents();
  initViewer();
  await refreshCatalogs();
}

function bindEvents() {
  byId('new-box').addEventListener('click', () => openBoxForm());
  byId('new-item').addEventListener('click', () => openItemForm());
  byId('box-form').addEventListener('submit', saveBox);
  byId('item-form').addEventListener('submit', saveItem);
  document.querySelectorAll('[data-cancel]').forEach(button => button.addEventListener('click', event => {
    byId(`${event.currentTarget.dataset.cancel}-form`).classList.add('hidden');
  }));
  byId('box-list').addEventListener('click', handleCatalogAction);
  byId('item-list').addEventListener('click', handleCatalogAction);
  byId('order-lines').addEventListener('click', handleQuantityClick);
  byId('order-lines').addEventListener('input', handleQuantityInput);
  byId('order-lines').addEventListener('change', handleQuantityInput);
  byId('auto-pack').addEventListener('click', autoPack);
  byId('random-test').addEventListener('click', randomTest);
  byId('toggle-shell').addEventListener('click', toggleShell);
  byId('reset-camera').addEventListener('click', resetCamera);
  byId('replay').addEventListener('click', replayAnimation);
  byId('play-pause').addEventListener('click', toggleAnimation);
  byId('show-final').addEventListener('click', showFinalState);
  byId('export-json').addEventListener('click', exportResult);
  window.addEventListener('resize', resizeViewer);
}

async function refreshCatalogs() {
  try {
    const [boxes, items] = await Promise.all([api('/api/boxes'), api('/api/items')]);
    state.boxes = boxes;
    state.items = items;
    items.forEach(item => {
      if (!state.quantities.has(item.id)) state.quantities.set(item.id, item.quantity ?? 0);
    });
    for (const key of [...state.quantities.keys()]) {
      if (!items.some(item => item.id === key)) state.quantities.delete(key);
    }
    renderBoxes();
    renderItems();
    renderOrderLines();
  } catch (error) {
    toast(error.message, true);
  }
}

async function api(url, options = {}) {
  const response = await fetch(url, {
    ...options,
    headers: { 'Content-Type': 'application/json', ...(options.headers || {}) }
  });
  const body = response.status === 204 ? null : await response.json().catch(() => null);
  if (!response.ok) {
    const message = body?.error || body?.title || firstValidationError(body?.errors) || `请求失败 (${response.status})`;
    throw new Error(message);
  }
  return body;
}

function firstValidationError(errors) {
  if (!errors) return null;
  const value = Object.values(errors)[0];
  return Array.isArray(value) ? value[0] : value;
}

function renderBoxes() {
  byId('box-list').innerHTML = state.boxes.length ? state.boxes.map(box => `
    <div class="catalog-card">
      <span class="catalog-icon">□</span>
      <div><strong>${escapeHtml(box.name)}</strong><small>${box.length} × ${box.width} × ${box.height} mm<br>${box.maxWeightKg ? `承重 ${box.maxWeightKg} kg` : '不限承重'} · ${box.cost != null ? `¥${box.cost.toFixed(2)}` : '未设成本'}</small></div>
      <div class="card-actions"><button data-type="box" data-action="edit" data-id="${box.id}" title="编辑">✎</button><button class="danger" data-type="box" data-action="delete" data-id="${box.id}" title="删除">×</button></div>
    </div>`).join('') : '<div class="empty-list">暂无箱型，请先新建。</div>';
}

function renderItems() {
  byId('item-list').innerHTML = state.items.length ? state.items.map((item, index) => `
    <div class="catalog-card">
      <span class="catalog-icon" style="color:${palette[index % palette.length]}">■</span>
      <div><strong>${escapeHtml(item.name)}</strong><small>${item.length} × ${item.width} × ${item.height} mm<br>${item.allowRotation ? '允许旋转' : '固定方向'} · ${item.weightKg ? `${item.weightKg} kg` : '未设重量'}</small></div>
      <div class="card-actions"><button data-type="item" data-action="edit" data-id="${item.id}" title="编辑">✎</button><button class="danger" data-type="item" data-action="delete" data-id="${item.id}" title="删除">×</button></div>
    </div>`).join('') : '<div class="empty-list">暂无商品，请先新建。</div>';
}

function renderOrderLines() {
  byId('order-lines').innerHTML = state.items.length ? state.items.map(item => `
    <div class="order-line">
      <div><strong>${escapeHtml(item.name)}</strong><small>${item.length} × ${item.width} × ${item.height} mm</small></div>
      <div class="quantity-control">
        <button data-quantity="minus" data-id="${item.id}" aria-label="减少">−</button>
        <input data-quantity-input data-id="${item.id}" type="number" min="0" max="10000" value="${state.quantities.get(item.id) ?? 0}" aria-label="${escapeHtml(item.name)} 数量">
        <button data-quantity="plus" data-id="${item.id}" aria-label="增加">＋</button>
      </div>
    </div>`).join('') : '<div class="empty-list">新增商品后即可创建订单。</div>';
  updateOrderCount();
}

function openBoxForm(box = null) {
  byId('box-form').classList.remove('hidden');
  byId('box-id').value = box?.id || '';
  byId('box-name').value = box?.name || '';
  byId('box-length').value = box?.length || '';
  byId('box-width').value = box?.width || '';
  byId('box-height').value = box?.height || '';
  byId('box-weight').value = box?.maxWeightKg ?? '';
  byId('box-cost').value = box?.cost ?? '';
  byId('box-name').focus();
}

function openItemForm(item = null) {
  byId('item-form').classList.remove('hidden');
  byId('item-id').value = item?.id || '';
  byId('item-name').value = item?.name || '';
  byId('item-length').value = item?.length || '';
  byId('item-width').value = item?.width || '';
  byId('item-height').value = item?.height || '';
  byId('item-weight').value = item?.weightKg ?? '';
  byId('item-quantity').value = item?.quantity ?? 1;
  byId('item-rotation').checked = item?.allowRotation ?? true;
  byId('item-name').focus();
}

async function saveBox(event) {
  event.preventDefault();
  const id = byId('box-id').value;
  const payload = {
    name: byId('box-name').value.trim(),
    length: Number(byId('box-length').value),
    width: Number(byId('box-width').value),
    height: Number(byId('box-height').value),
    maxWeightKg: nullableNumber(byId('box-weight').value),
    cost: nullableNumber(byId('box-cost').value)
  };
  await saveCatalog('box', id, payload);
}

async function saveItem(event) {
  event.preventDefault();
  const id = byId('item-id').value;
  const payload = {
    name: byId('item-name').value.trim(),
    length: Number(byId('item-length').value),
    width: Number(byId('item-width').value),
    height: Number(byId('item-height').value),
    weightKg: nullableNumber(byId('item-weight').value),
    quantity: Number(byId('item-quantity').value),
    allowRotation: byId('item-rotation').checked
  };
  await saveCatalog('item', id, payload);
}

async function saveCatalog(type, id, payload) {
  const plural = type === 'box' ? 'boxes' : 'items';
  try {
    await api(id ? `/api/${plural}/${id}` : `/api/${plural}`, {
      method: id ? 'PUT' : 'POST',
      body: JSON.stringify(payload)
    });
    byId(`${type}-form`).classList.add('hidden');
    await refreshCatalogs();
    toast(id ? '修改已保存。' : '新记录已添加。');
  } catch (error) {
    toast(error.message, true);
  }
}

async function handleCatalogAction(event) {
  const button = event.target.closest('[data-action]');
  if (!button) return;
  const { type, action, id } = button.dataset;
  const collection = type === 'box' ? state.boxes : state.items;
  const record = collection.find(item => item.id === id);
  if (!record) return;

  if (action === 'edit') {
    type === 'box' ? openBoxForm(record) : openItemForm(record);
    return;
  }
  if (!confirm(`确定删除“${record.name}”吗？`)) return;
  try {
    await api(`/api/${type === 'box' ? 'boxes' : 'items'}/${id}`, { method: 'DELETE' });
    await refreshCatalogs();
    toast('记录已删除。');
  } catch (error) {
    toast(error.message, true);
  }
}

function handleQuantityClick(event) {
  const button = event.target.closest('[data-quantity]');
  if (!button) return;
  const value = state.quantities.get(button.dataset.id) || 0;
  state.quantities.set(button.dataset.id, Math.max(0, value + (button.dataset.quantity === 'plus' ? 1 : -1)));
  renderOrderLines();
}

function handleQuantityInput(event) {
  if (!event.target.matches('[data-quantity-input]')) return;
  state.quantities.set(event.target.dataset.id, Math.max(0, Number(event.target.value) || 0));
  updateOrderCount();
}

function updateOrderCount() {
  const count = [...state.quantities.values()].reduce((sum, value) => sum + value, 0);
  byId('order-count').textContent = `${count} 件`;
}

async function autoPack() {
  const items = [...state.quantities]
    .filter(([, quantity]) => quantity > 0)
    .map(([itemId, quantity]) => ({ itemId, quantity }));
  if (!items.length) {
    toast('请至少选择 1 件商品。', true);
    return;
  }
  await runPacking('/api/packing/pack', { items });
}

async function randomTest() {
  setBusy(true);
  try {
    const response = await api('/api/packing/random', { method: 'POST' });
    state.items.forEach(item => state.quantities.set(item.id, 0));
    response.orderLines.forEach(line => state.quantities.set(line.itemId, line.quantity));
    renderOrderLines();
    showResult(response.result);
    toast(`随机订单已生成：${response.result.summary.totalItemCount} 件商品。`);
  } catch (error) {
    toast(error.message, true);
  } finally {
    setBusy(false);
  }
}

async function runPacking(url, payload) {
  setBusy(true);
  try {
    const result = await api(url, { method: 'POST', body: JSON.stringify(payload) });
    showResult(result);
    toast(`装箱完成，共使用 ${result.summary.totalBoxCount} 个箱子。`);
  } catch (error) {
    toast(error.message, true);
  } finally {
    setBusy(false);
  }
}

function setBusy(busy) {
  [byId('auto-pack'), byId('random-test')].forEach(button => button.disabled = busy);
  byId('auto-pack').innerHTML = busy ? 'CALCULATING…' : 'AUTO PACK <span>→</span>';
}

function showResult(result) {
  state.result = result;
  state.activeBox = 0;
  byId('empty-state').classList.add('hidden');
  byId('result-section').classList.remove('hidden');
  renderSummary();
  renderBoxTabs();
  renderDetails();
  resizeViewer();
  loadBoxScene(0);
  requestAnimationFrame(resizeViewer);
  byId('result-section').scrollIntoView({ behavior: 'smooth', block: 'start' });
}

function renderSummary() {
  const summary = state.result.summary;
  const boxNames = Object.entries(summary.boxesByType).map(([name, count]) => `${escapeHtml(name)} × ${count}`).join('<br>');
  byId('summary-cards').innerHTML = `
    <div class="summary-card"><small>ORDER ITEMS</small><strong>${summary.totalItemCount} <em>件</em></strong></div>
    <div class="summary-card"><small>BOXES USED</small><strong>${summary.totalBoxCount} <em>箱</em></strong></div>
    <div class="summary-card accent"><small>SPACE UTILIZATION</small><strong>${summary.utilizationPercent.toFixed(1)} <em>%</em></strong></div>
    <div class="summary-card"><small>ITEM VOLUME</small><strong>${formatVolume(summary.totalItemVolumeMm3)} <em>cm³</em></strong></div>
    <div class="summary-card"><small>BOX MIX</small><strong style="font-size:14px;line-height:1.45">${boxNames}</strong></div>`;
}

function renderBoxTabs() {
  byId('box-tabs').innerHTML = state.result.boxes.map((box, index) => `
    <button class="box-tab ${index === state.activeBox ? 'active' : ''}" data-box-index="${index}">BOX ${String(box.number).padStart(2, '0')} · ${escapeHtml(box.box.name)}</button>`).join('');
  byId('box-tabs').querySelectorAll('[data-box-index]').forEach(button => button.addEventListener('click', () => {
    state.activeBox = Number(button.dataset.boxIndex);
    renderBoxTabs();
    loadBoxScene(state.activeBox);
  }));
}

function renderDetails() {
  byId('box-details').innerHTML = state.result.boxes.map(box => `
    <article class="box-detail">
      <div class="box-detail-head">
        <div><h3>Box #${box.number} · ${escapeHtml(box.box.name)}</h3><p>${box.box.length} × ${box.box.width} × ${box.box.height} mm</p></div>
        <div class="metric"><small>ITEM COUNT</small><strong>${box.items.length} 件</strong></div>
        <div class="metric"><small>WEIGHT</small><strong>${box.totalWeightKg.toFixed(2)} kg</strong></div>
        <div class="metric"><small>UTILIZATION</small><strong class="utilization">${box.utilizationPercent.toFixed(2)}%</strong></div>
      </div>
      <div class="item-table-wrap"><table class="item-table">
        <thead><tr><th>商品</th><th>坐标 X / Y / Z</th><th>最终尺寸 L × W × H</th><th>原始尺寸</th><th>旋转</th><th>重量</th></tr></thead>
        <tbody>${box.items.map(item => `<tr>
          <td><span class="item-name-cell"><i class="legend-swatch" style="background:${colorFor(item.itemTypeId)}"></i>${escapeHtml(item.name)} #${item.sequence}</span></td>
          <td>${item.x} / ${item.y} / ${item.z}</td>
          <td>${item.length} × ${item.width} × ${item.height}</td>
          <td>${item.originalLength} × ${item.originalWidth} × ${item.originalHeight}</td>
          <td>${item.rotation}</td><td>${item.weightKg.toFixed(2)} kg</td>
        </tr>`).join('')}</tbody>
      </table></div>
    </article>`).join('');
}

function initViewer() {
  const container = byId('viewer');
  scene = new THREE.Scene();
  camera = new THREE.PerspectiveCamera(42, 1, 0.1, 100000);
  renderer = new THREE.WebGLRenderer({ antialias: true, alpha: true });
  renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));
  renderer.outputColorSpace = THREE.SRGBColorSpace;
  renderer.shadowMap.enabled = true;
  renderer.shadowMap.type = THREE.PCFSoftShadowMap;
  container.insertBefore(renderer.domElement, container.firstChild);

  controls = new OrbitControls(camera, renderer.domElement);
  controls.enableDamping = true;
  controls.dampingFactor = .07;
  controls.screenSpacePanning = true;

  scene.add(new THREE.HemisphereLight(0xeaf7ed, 0x25342a, 2.1));
  const key = new THREE.DirectionalLight(0xffffff, 2.4);
  key.position.set(900, 1200, 700);
  key.castShadow = true;
  scene.add(key);
  const rim = new THREE.DirectionalLight(0xcbed45, 1.2);
  rim.position.set(-700, 300, -800);
  scene.add(rim);

  contentGroup = new THREE.Group();
  scene.add(contentGroup);
  raycaster = new THREE.Raycaster();
  pointer = new THREE.Vector2();
  renderer.domElement.addEventListener('pointerdown', inspectFromPointer);
  resizeViewer();
  requestAnimationFrame(renderFrame);
}

function loadBoxScene(index) {
  resizeViewer();
  clearGroup(contentGroup);
  itemMeshes = [];
  animation.playing = false;
  animation.elapsed = 0;
  byId('play-pause').textContent = '▶';
  byId('timeline-progress').style.width = '0%';
  byId('animation-label').textContent = '准备播放';
  byId('item-inspector').className = 'inspector-empty';
  byId('item-inspector').innerHTML = '<div class="cursor-icon">⌖</div><p>选择箱内商品以查看坐标、最终尺寸与旋转方式。</p>';

  const packedBox = state.result.boxes[index];
  const box = packedBox.box;
  currentBoxSize = Math.max(box.length, box.width, box.height);

  shellGroup = new THREE.Group();
  const shellGeometry = new THREE.BoxGeometry(box.length, box.height, box.width);
  const shellMaterial = new THREE.MeshPhysicalMaterial({ color: 0x9ccba8, transparent: true, opacity: .075, roughness: .15, metalness: 0, side: THREE.DoubleSide, depthWrite: false });
  const shell = new THREE.Mesh(shellGeometry, shellMaterial);
  shell.position.set(0, box.height / 2, 0);
  shellGroup.add(shell);
  const edges = new THREE.LineSegments(new THREE.EdgesGeometry(shellGeometry), new THREE.LineBasicMaterial({ color: 0xb9d7c0, transparent: true, opacity: .9 }));
  edges.position.copy(shell.position);
  shellGroup.add(edges);
  shellGroup.visible = state.shellVisible;
  contentGroup.add(shellGroup);

  const floor = new THREE.Mesh(
    new THREE.PlaneGeometry(box.length * 1.3, box.width * 1.3),
    new THREE.MeshStandardMaterial({ color: 0x1f2b23, roughness: 1, transparent: true, opacity: .65 })
  );
  floor.rotation.x = -Math.PI / 2;
  floor.position.y = -1;
  floor.receiveShadow = true;
  contentGroup.add(floor);

  packedBox.items.forEach(item => {
    const geometry = new THREE.BoxGeometry(item.length * .985, item.height * .985, item.width * .985);
    const color = colorFor(item.itemTypeId);
    const material = new THREE.MeshStandardMaterial({ color, roughness: .55, metalness: .02, transparent: true, opacity: .93 });
    const mesh = new THREE.Mesh(geometry, material);
    mesh.castShadow = true;
    mesh.receiveShadow = true;
    mesh.userData.item = item;
    mesh.userData.finalPosition = new THREE.Vector3(
      item.x + item.length / 2 - box.length / 2,
      item.z + item.height / 2,
      item.y + item.width / 2 - box.width / 2
    );
    mesh.position.copy(mesh.userData.finalPosition);

    const itemEdges = new THREE.LineSegments(new THREE.EdgesGeometry(geometry), new THREE.LineBasicMaterial({ color: 0x17211b, transparent: true, opacity: .5 }));
    mesh.add(itemEdges);
    mesh.add(makeLabel(`${item.name} #${item.sequence}`, Math.max(item.length, item.width)));
    contentGroup.add(mesh);
    itemMeshes.push(mesh);
  });

  renderLegend(packedBox);
  resetCamera();
  showFinalState();
}

function makeLabel(text, itemScale) {
  const canvas = document.createElement('canvas');
  canvas.width = 512;
  canvas.height = 96;
  const context = canvas.getContext('2d');
  context.fillStyle = 'rgba(19,29,23,.82)';
  context.fillRect(6, 6, 500, 84);
  context.strokeStyle = 'rgba(203,237,69,.9)';
  context.lineWidth = 3;
  context.strokeRect(6, 6, 500, 84);
  context.fillStyle = '#f7fff8';
  context.font = '600 28px sans-serif';
  context.textAlign = 'center';
  context.textBaseline = 'middle';
  context.fillText(text.slice(0, 28), 256, 50);
  const texture = new THREE.CanvasTexture(canvas);
  texture.colorSpace = THREE.SRGBColorSpace;
  const sprite = new THREE.Sprite(new THREE.SpriteMaterial({ map: texture, transparent: true, depthTest: false }));
  const width = Math.min(Math.max(itemScale * .65, 85), 180);
  sprite.scale.set(width, width * .1875, 1);
  sprite.position.set(0, 0, 0);
  sprite.renderOrder = 4;
  return sprite;
}

function renderLegend(packedBox) {
  const groups = [...new Map(packedBox.items.map(item => [item.itemTypeId, item])).values()];
  byId('item-legend').innerHTML = groups.map(item => {
    const count = packedBox.items.filter(candidate => candidate.itemTypeId === item.itemTypeId).length;
    return `<div class="legend-row"><i class="legend-swatch" style="background:${colorFor(item.itemTypeId)}"></i><span>${escapeHtml(item.name)}</span><small>× ${count}</small></div>`;
  }).join('');
}

function inspectFromPointer(event) {
  const bounds = renderer.domElement.getBoundingClientRect();
  pointer.x = ((event.clientX - bounds.left) / bounds.width) * 2 - 1;
  pointer.y = -((event.clientY - bounds.top) / bounds.height) * 2 + 1;
  raycaster.setFromCamera(pointer, camera);
  const intersection = raycaster.intersectObjects(itemMeshes, false)[0];
  if (!intersection) return;
  const item = intersection.object.userData.item;
  itemMeshes.forEach(mesh => mesh.material.emissive.setHex(0x000000));
  intersection.object.material.emissive.setHex(0x243b1e);
  byId('item-inspector').className = 'inspector-data';
  byId('item-inspector').innerHTML = `
    <h3>${escapeHtml(item.name)} #${item.sequence}</h3><small>${escapeHtml(item.instanceId)}</small>
    <div class="data-grid">
      <div class="data-cell"><small>POSITION X</small><strong>${item.x} mm</strong></div>
      <div class="data-cell"><small>POSITION Y</small><strong>${item.y} mm</strong></div>
      <div class="data-cell"><small>POSITION Z</small><strong>${item.z} mm</strong></div>
      <div class="data-cell"><small>ROTATION</small><strong>${item.rotation}</strong></div>
      <div class="data-cell"><small>FINAL SIZE</small><strong>${item.length}×${item.width}×${item.height}</strong></div>
      <div class="data-cell"><small>WEIGHT</small><strong>${item.weightKg.toFixed(2)} kg</strong></div>
    </div>`;
}

function toggleShell() {
  state.shellVisible = !state.shellVisible;
  if (shellGroup) shellGroup.visible = state.shellVisible;
  byId('toggle-shell').classList.toggle('active', state.shellVisible);
}

function resetCamera() {
  const box = state.result?.boxes[state.activeBox]?.box;
  if (!box) return;
  const distance = Math.max(box.length, box.width, box.height) * 1.65;
  camera.position.set(distance * .85, distance * .72, distance * .9);
  camera.near = Math.max(.1, currentBoxSize / 1000);
  camera.far = currentBoxSize * 20;
  camera.updateProjectionMatrix();
  controls.target.set(0, box.height * .34, 0);
  controls.update();
}

function replayAnimation() {
  animation.elapsed = 0;
  animation.lastTime = performance.now();
  animation.playing = true;
  byId('play-pause').textContent = 'Ⅱ';
  updateAnimatedItems();
}

function toggleAnimation() {
  if (!itemMeshes.length) return;
  if (animation.elapsed >= itemMeshes.length * animation.duration) animation.elapsed = 0;
  animation.playing = !animation.playing;
  animation.lastTime = performance.now();
  byId('play-pause').textContent = animation.playing ? 'Ⅱ' : '▶';
}

function showFinalState() {
  animation.playing = false;
  animation.elapsed = itemMeshes.length * animation.duration;
  byId('play-pause').textContent = '▶';
  updateAnimatedItems();
}

function updateAnimatedItems() {
  const packedBox = state.result?.boxes[state.activeBox];
  if (!packedBox) return;
  const total = itemMeshes.length * animation.duration;
  itemMeshes.forEach((mesh, index) => {
    const local = animation.elapsed - index * animation.duration;
    if (local < 0) {
      mesh.visible = false;
      return;
    }
    mesh.visible = true;
    const progress = Math.min(1, local / animation.duration);
    const eased = 1 - Math.pow(1 - progress, 3);
    const final = mesh.userData.finalPosition;
    const start = new THREE.Vector3(final.x + currentBoxSize * .42, packedBox.box.height + currentBoxSize * .55, final.z - currentBoxSize * .35);
    mesh.position.lerpVectors(start, final, eased);
    mesh.rotation.y = (1 - eased) * Math.PI * .55;
  });
  const progress = total ? Math.min(100, animation.elapsed / total * 100) : 100;
  byId('timeline-progress').style.width = `${progress}%`;
  const placed = Math.min(itemMeshes.length, Math.floor(animation.elapsed / animation.duration) + (animation.elapsed >= total ? 0 : 1));
  byId('animation-label').textContent = animation.elapsed >= total ? '装箱完成' : `ITEM ${Math.max(1, placed)} / ${itemMeshes.length}`;
}

function renderFrame(time) {
  if (animation.playing) {
    animation.elapsed += Math.min(50, time - animation.lastTime);
    animation.lastTime = time;
    const total = itemMeshes.length * animation.duration;
    if (animation.elapsed >= total) {
      animation.elapsed = total;
      animation.playing = false;
      byId('play-pause').textContent = '▶';
    }
    updateAnimatedItems();
  }
  controls.update();
  renderer.render(scene, camera);
  requestAnimationFrame(renderFrame);
}

function resizeViewer() {
  if (!renderer) return;
  const container = byId('viewer');
  const width = Math.max(1, container.clientWidth);
  const height = Math.max(1, container.clientHeight);
  renderer.setSize(width, height, false);
  camera.aspect = width / height;
  camera.updateProjectionMatrix();
}

function clearGroup(group) {
  while (group.children.length) {
    const object = group.children.pop();
    object.traverse(child => {
      child.geometry?.dispose();
      if (Array.isArray(child.material)) child.material.forEach(material => disposeMaterial(material));
      else if (child.material) disposeMaterial(child.material);
    });
  }
}

function disposeMaterial(material) {
  material.map?.dispose();
  material.dispose();
}

function exportResult() {
  if (!state.result) return;
  const blob = new Blob([JSON.stringify(state.result, null, 2)], { type: 'application/json' });
  const link = document.createElement('a');
  link.href = URL.createObjectURL(blob);
  link.download = `packing-result-${new Date().toISOString().replace(/[:.]/g, '-')}.json`;
  link.click();
  URL.revokeObjectURL(link.href);
}

function colorFor(id) {
  let hash = 0;
  for (const char of id) hash = ((hash << 5) - hash + char.charCodeAt(0)) | 0;
  return palette[Math.abs(hash) % palette.length];
}

function formatVolume(mm3) {
  return new Intl.NumberFormat('zh-CN', { maximumFractionDigits: 0 }).format(mm3 / 1000);
}

function nullableNumber(value) {
  return value === '' ? null : Number(value);
}

function escapeHtml(value) {
  return String(value).replace(/[&<>'"]/g, char => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', "'": '&#39;', '"': '&quot;' })[char]);
}

let toastTimer;
function toast(message, error = false) {
  const element = byId('toast');
  element.textContent = message;
  element.classList.toggle('error', error);
  element.classList.add('show');
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => element.classList.remove('show'), 3400);
}
