/* ============================================================
   最终练化 · app.js
   M0：Tab 切换 + Store 封装
   M2：训练计划页 —— 计划数据层 / 动作库面板 / 动作配置面板
   M3：计划管理 —— 新建 / 切换 / 重命名 / 删除
   M4：主页引导式训练流程 —— 状态机 / 倒计时 / 提示音 / 刷新恢复
   M5：训练历史页 —— 列表 + 训练详情
   M6：数据管理 —— 导出 / 导入 / 清空
   ============================================================ */
'use strict';

/* ---------- 全局存储封装 ----------
   所有 key 自动加 "lianhua." 前缀，值一律经 JSON 序列化。 */
const Store = {
  PREFIX: 'lianhua.',

  get(key, fallback) {
    try {
      const raw = localStorage.getItem(Store.PREFIX + key);
      return raw === null ? fallback : JSON.parse(raw);
    } catch (err) {
      // 值损坏或存储不可用时，退回默认值
      return fallback;
    }
  },

  set(key, value) {
    try {
      localStorage.setItem(Store.PREFIX + key, JSON.stringify(value));
    } catch (err) {
      // iOS 隐私模式等场景写入会失败，静默降级
    }
  },
};

// 挂到 window：控制台调试与后续里程碑直接使用
window.Store = Store;

/* ---------- Tab 切换（事件委托，M0） ---------- */
const navBar = document.querySelector('.topbar-nav');
const allTabs = document.querySelectorAll('.tab');
const allPages = document.querySelectorAll('.page');

// 切换到指定页：更新标签高亮，目标页重新播放淡入动画
function activatePage(pageName) {
  allTabs.forEach((tab) => {
    tab.classList.toggle('active', tab.dataset.page === pageName);
  });

  allPages.forEach((page) => {
    page.classList.toggle('active', page.id === 'page-' + pageName);
  });
}

navBar.addEventListener('click', (event) => {
  const tab = event.target.closest('.tab');
  if (!tab) return;
  activatePage(tab.dataset.page);
});

/* ============================================================
   M2 训练计划
   ============================================================ */

/* ---------- 标签映射 ---------- */
const PHASE_LABEL = { warmup: '热身', main: '正式' };
const TYPE_LABEL = { weight: '负重', bodyweight: '自重' };
const MODE_LABEL = { reps: '次数', time: '时间' };

/* ---------- 中英双语显示辅助（纯显示层，不影响数据与逻辑） ----------
   enSpan(en)：生成小一号的英文 <span class="en">(en)</span>，追加在中文后；
   exEn(id)：查 translations.js 的动作英文名，查不到返回空串。 */
const PHASE_EN = { warmup: 'Warm-up', main: 'Main' };
const TYPE_EN = { weight: 'Weight', bodyweight: 'Bodyweight' };
const MODE_EN = { reps: 'Reps', time: 'Time' };
const PART_EN = {
  '全部': 'All', '胸': 'Chest', '背': 'Back', '腿': 'Legs', '肩': 'Shoulders',
  '臂': 'Arms', '核心': 'Core', '全身': 'Full Body', '奥举': 'Olympic', '有氧': 'Cardio',
};
const EQUIP_EN = {
  '杠铃': 'Barbell', '哑铃': 'Dumbbell', '壶铃': 'Kettlebell', '绳索': 'Cable',
  '器械': 'Machine', '弹力带': 'Band', '徒手': 'None', '其他': 'Other',
};

function enSpan(en) {
  const s = document.createElement('span');
  s.className = 'en';
  s.textContent = '(' + en + ')';
  return s;
}

function exEn(id) {
  return (window.EX_EN && window.EX_EN[id]) || '';
}

// 中英字段摘要行（部位 · 阶段 · 类型 · 模式 · 器械），source 为库动作或计划条目
function metaEn(source) {
  return [
    PART_EN[source.part] || source.part,
    PHASE_EN[source.phase] || source.phase,
    TYPE_EN[source.type] || source.type,
    MODE_EN[source.mode] || source.mode,
    EQUIP_EN[source.equip] || source.equip,
  ].join(' · ');
}

/* ---------- DOM 引用 ---------- */
const planTitle = document.getElementById('plan-title');
const planList = document.getElementById('plan-list');
const btnAddExercise = document.getElementById('btn-add-exercise');
const libOverlay = document.getElementById('lib-overlay');
const libResults = document.getElementById('lib-results');
const libSearch = document.getElementById('lib-search');
const cfgOverlay = document.getElementById('cfg-overlay');
const cfgCard = cfgOverlay.querySelector('.cfg-card');
const cfgName = document.getElementById('cfg-name');
const cfgTags = document.getElementById('cfg-tags');
const cfgSaveBtn = document.getElementById('cfg-save');
const cfgOrderBox = document.getElementById('cfg-order');
const cfgUpBtn = document.getElementById('cfg-up');
const cfgDownBtn = document.getElementById('cfg-down');
const cfgDeleteBtn = document.getElementById('cfg-delete');

/* ---------- 计划数据层 ----------
   plans = [{ id, name, items: [PlanItem] }]；activePlanId 指向当前计划。
   PlanItem 从动作库快照字段，不引用库对象：
   { uid, exId, name, part, phase, type, mode, sets, reps, sec, weight, restSec } */
let plans = [];
let activePlanId = '';

function makeUid() {
  return 'it-' + Date.now() + '-' + Math.random().toString(36).slice(2, 8);
}

function initPlanData() {
  plans = Store.get('plans', null);
  if (!Array.isArray(plans) || plans.length === 0) {
    plans = [{ id: 'plan-1', name: '默认计划', items: [] }];
    Store.set('plans', plans);
  }
  activePlanId = Store.get('activePlanId', null);
  if (!plans.some(p => p.id === activePlanId)) {
    activePlanId = plans[0].id;
    Store.set('activePlanId', activePlanId);
  }
}

function getActivePlan() {
  return plans.find(p => p.id === activePlanId) || plans[0];
}

// 每次增删改后立即持久化
function persistPlans() {
  Store.set('plans', plans);
}

/* ---------- 计划页渲染 ---------- */
// 重量步进 2.5 会产生小数，统一格式化防浮点误差
function fmtNum(v) {
  return String(Math.round(v * 10) / 10);
}

// 参数摘要：如「4组 × 10次 · 40kg · 间歇60秒」「3组 × 30秒 · 徒手」
function itemSummary(item) {
  let text = item.mode === 'reps'
    ? item.sets + '组 × ' + item.reps + '次'
    : item.sets + '组 × ' + item.sec + '秒';
  text += item.type === 'weight' ? ' · ' + fmtNum(item.weight) + 'kg' : ' · 徒手';
  return text + ' · 间歇' + item.restSec + '秒';
}

// 参数摘要英文版（与 itemSummary 一一对应，仅用于显示）
function itemSummaryEn(item) {
  let text = item.mode === 'reps'
    ? item.sets + ' sets × ' + item.reps + ' reps'
    : item.sets + ' sets × ' + item.sec + 's';
  text += item.type === 'weight' ? ' · ' + fmtNum(item.weight) + 'kg' : ' · bodyweight';
  return text + ' · rest ' + item.restSec + 's';
}

function buildItemRow(item, index) {
  const row = document.createElement('button');
  row.type = 'button';
  row.className = 'plan-item';
  row.dataset.uid = item.uid;

  const num = document.createElement('span');
  num.className = 'item-index';
  num.textContent = String(index + 1).padStart(2, '0');

  const main = document.createElement('span');
  main.className = 'item-main';

  const name = document.createElement('span');
  name.className = 'item-name';
  name.textContent = item.name;
  const nameEn = exEn(item.exId);
  if (nameEn) name.appendChild(enSpan(nameEn));

  const tags = document.createElement('span');
  tags.className = 'item-tags';
  const tagPairs = [
    [PHASE_LABEL[item.phase], PHASE_EN[item.phase]],
    [TYPE_LABEL[item.type], TYPE_EN[item.type]],
    [MODE_LABEL[item.mode], MODE_EN[item.mode]],
  ];
  tagPairs.forEach(pair => {
    const tag = document.createElement('span');
    tag.className = 'tag';
    tag.textContent = pair[0];
    if (pair[1]) tag.appendChild(enSpan(pair[1]));
    tags.appendChild(tag);
  });

  const summary = document.createElement('span');
  summary.className = 'item-summary';
  summary.textContent = itemSummary(item);
  summary.appendChild(enSpan(itemSummaryEn(item)));

  main.appendChild(name);
  main.appendChild(tags);
  main.appendChild(summary);
  row.appendChild(num);
  row.appendChild(main);
  return row;
}

// 空状态/错误提示节点（en 为可选英文对照）
function noteBox(text, isError, en) {
  const box = document.createElement('div');
  box.className = 'panel empty-state' + (isError ? ' empty-error' : '');
  const p = document.createElement('p');
  p.textContent = text;
  if (en) p.appendChild(enSpan(en));
  box.appendChild(p);
  return box;
}

function renderPlanPage() {
  const plan = getActivePlan();
  planTitle.textContent = plan.name;
  planList.innerHTML = '';

  // 边界：动作库未加载或为空
  if (!Array.isArray(window.EXERCISES) || window.EXERCISES.length === 0) {
    btnAddExercise.hidden = true;
    planList.appendChild(noteBox('动作库未加载，请检查 exercises.js', true, 'Exercise library failed to load — check exercises.js'));
    return;
  }
  btnAddExercise.hidden = false;

  if (plan.items.length === 0) {
    planList.appendChild(noteBox('还没有动作，点下方「＋ 添加动作」开始编排', false, 'No exercises yet — tap "+ Add Exercise" below'));
    return;
  }

  const frag = document.createDocumentFragment();
  plan.items.forEach((item, index) => frag.appendChild(buildItemRow(item, index)));
  planList.appendChild(frag);
}

/* ---------- 覆盖层通用 ---------- */
function openOverlay(el) {
  el.hidden = false;
  syncBodyLock();
}

function closeOverlay(el) {
  el.hidden = true;
  syncBodyLock();
}

// 任一覆盖层打开时锁住背景滚动
function syncBodyLock() {
  document.body.classList.toggle('locked',
    !libOverlay.hidden || !cfgOverlay.hidden || !mgrOverlay.hidden ||
    !detailOverlay.hidden || !dataOverlay.hidden);
}

/* ---------- 动作库面板 ---------- */
const libState = { search: '', part: '全部', phase: '全部', type: '全部' };

// 搜索与三个筛选维度同时生效（AND）
function filteredExercises() {
  const q = libState.search.trim();
  return window.EXERCISES.filter(ex =>
    (q === '' || ex.name.indexOf(q) !== -1) &&
    (libState.part === '全部' || ex.part === libState.part) &&
    (libState.phase === '全部' || ex.phase === libState.phase) &&
    (libState.type === '全部' || ex.type === libState.type)
  );
}

function buildLibRow(ex) {
  const row = document.createElement('button');
  row.type = 'button';
  row.className = 'lib-row';
  row.dataset.exId = ex.id;

  const name = document.createElement('span');
  name.className = 'lib-name';
  name.textContent = ex.name;
  const nameEn = exEn(ex.id);
  if (nameEn) name.appendChild(enSpan(nameEn));

  const meta = document.createElement('span');
  meta.className = 'lib-meta';
  meta.textContent = [
    ex.part, PHASE_LABEL[ex.phase], TYPE_LABEL[ex.type], MODE_LABEL[ex.mode], ex.equip,
  ].join(' · ');
  meta.appendChild(enSpan(metaEn(ex)));

  row.appendChild(name);
  row.appendChild(meta);
  return row;
}

function renderLibResults() {
  const rows = filteredExercises();
  libResults.innerHTML = '';
  const frag = document.createDocumentFragment();
  if (rows.length === 0) {
    const note = document.createElement('p');
    note.className = 'lib-note';
    note.textContent = '没有匹配的动作，换个关键词或筛选试试';
    note.appendChild(enSpan('No matches — try another keyword or filter'));
    frag.appendChild(note);
  } else {
    rows.forEach(ex => frag.appendChild(buildLibRow(ex)));
  }
  libResults.appendChild(frag);   // DocumentFragment 一次性插入
}

function openLibrary() {
  renderLibResults();
  openOverlay(libOverlay);
}

/* ---------- 配置面板（新建/编辑共用） ---------- */
const cfg = {
  mode: 'new',          // "new" | "edit"
  index: -1,            // 编辑时条目在 plan.items 中的下标
  ex: null,             // 新建时的库动作引用
  itemType: 'weight',   // 当前动作 type（决定重量行显隐）
  itemMode: 'reps',     // 当前动作 mode（决定次数/秒数行显隐）
  values: null,
};

// 渲染配置面板内容；source 为库动作（新建）或计划条目（编辑）
function showConfig(source) {
  cfgName.textContent = source.name;
  const cfgNameEn = exEn(source.exId || source.id);
  if (cfgNameEn) cfgName.appendChild(enSpan(cfgNameEn));
  cfgTags.textContent = [
    source.part, PHASE_LABEL[source.phase], TYPE_LABEL[source.type], MODE_LABEL[source.mode], source.equip,
  ].join(' · ');
  cfgTags.appendChild(enSpan(metaEn(source)));
  cfg.itemType = source.type;
  cfg.itemMode = source.mode;

  // 按动作的 mode/type 显隐数值行：reps→次数，time→秒数，自重不显示重量
  document.getElementById('field-reps').hidden = cfg.itemMode !== 'reps';
  document.getElementById('field-sec').hidden = cfg.itemMode !== 'time';
  document.getElementById('field-weight').hidden = cfg.itemType === 'bodyweight';

  cfgCard.querySelectorAll('#cfg-phase-seg .seg-btn').forEach(btn => {
    btn.classList.toggle('active', btn.dataset.phase === cfg.values.phase);
  });

  const isEdit = cfg.mode === 'edit';
  cfgOrderBox.hidden = !isEdit;
  cfgDeleteBtn.hidden = !isEdit;
  cfgSaveBtn.textContent = isEdit ? '保存' : '加入计划';
  cfgSaveBtn.appendChild(enSpan(isEdit ? 'Save' : 'Add to Plan'));

  updateSteppers();
  updateOrderButtons();
}

function openConfigNew(ex) {
  cfg.mode = 'new';
  cfg.ex = ex;
  cfg.index = -1;
  cfg.values = {
    phase: ex.phase,
    sets: 3,
    reps: 10,
    sec: 30,
    weight: ex.type === 'weight' ? 20 : null,
    restSec: 60,
  };
  showConfig(ex);
  openOverlay(cfgOverlay);
}

function openConfigEdit(uid) {
  const plan = getActivePlan();
  const index = plan.items.findIndex(it => it.uid === uid);
  if (index < 0) return;
  const item = plan.items[index];
  cfg.mode = 'edit';
  cfg.index = index;
  cfg.values = {
    phase: item.phase,
    sets: item.sets,
    reps: item.reps,
    sec: item.sec,
    weight: item.weight,
    restSec: item.restSec,
  };
  showConfig(item);
  openOverlay(cfgOverlay);
}

/* ---------- 步进器 ---------- */
// 逐行刷新数值显示与 −/+ 边界禁用态
function updateSteppers() {
  cfgCard.querySelectorAll('.stepper').forEach(st => {
    const min = Number(st.dataset.min);
    const max = Number(st.dataset.max);
    const raw = cfg.values[st.dataset.key];
    const shown = raw === null || raw === undefined ? min : raw;
    st.querySelector('.step-val').textContent = fmtNum(shown);
    st.querySelector('.step-btn[data-dir="-1"]').disabled = shown <= min;
    st.querySelector('.step-btn[data-dir="1"]').disabled = shown >= max;
  });
}

function stepValue(stepper, dir) {
  const key = stepper.dataset.key;
  const min = Number(stepper.dataset.min);
  const max = Number(stepper.dataset.max);
  const step = Number(stepper.dataset.step);
  const raw = cfg.values[key];
  const cur = raw === null || raw === undefined ? min : raw;
  let next = cur + dir * step;
  if (next < min) next = min;
  if (next > max) next = max;
  cfg.values[key] = next;
  updateSteppers();
}

/* ---------- 保存 / 排序 / 删除 ---------- */
// 由库动作 + 当前配置生成计划条目（快照，不引用库对象）
function snapshotToItem(ex, v) {
  return {
    uid: makeUid(),
    exId: ex.id,
    name: ex.name,
    part: ex.part,
    phase: v.phase,
    type: ex.type,
    mode: ex.mode,
    sets: v.sets,
    reps: ex.mode === 'reps' ? v.reps : null,
    sec: ex.mode === 'time' ? v.sec : null,
    weight: ex.type === 'weight' ? v.weight : null,
    restSec: v.restSec,
  };
}

function saveConfig() {
  const plan = getActivePlan();
  const v = cfg.values;
  if (cfg.mode === 'new') {
    plan.items.push(snapshotToItem(cfg.ex, v));
  } else {
    const item = plan.items[cfg.index];
    if (!item) return;
    item.phase = v.phase;
    item.sets = v.sets;
    item.reps = cfg.itemMode === 'reps' ? v.reps : null;
    item.sec = cfg.itemMode === 'time' ? v.sec : null;
    item.weight = cfg.itemType === 'weight' ? v.weight : null;
    item.restSec = v.restSec;
  }
  persistPlans();
  renderPlanPage();
  closeOverlay(cfgOverlay);
}

function updateOrderButtons() {
  const plan = getActivePlan();
  cfgUpBtn.disabled = cfg.index <= 0;
  cfgDownBtn.disabled = cfg.index >= plan.items.length - 1;
}

function moveCfgItem(dir) {
  if (cfg.mode !== 'edit') return;
  const plan = getActivePlan();
  const to = cfg.index + dir;
  if (to < 0 || to >= plan.items.length) return;
  const moved = plan.items.splice(cfg.index, 1)[0];
  plan.items.splice(to, 0, moved);
  cfg.index = to;
  persistPlans();
  renderPlanPage();
  updateOrderButtons();
}

function deleteCfgItem() {
  if (cfg.mode !== 'edit') return;
  const plan = getActivePlan();
  const item = plan.items[cfg.index];
  if (!item) return;
  if (!window.confirm('删除「' + item.name + '」？')) return;
  plan.items.splice(cfg.index, 1);
  persistPlans();
  renderPlanPage();
  closeOverlay(cfgOverlay);
}

/* ---------- 事件绑定（委托优先） ---------- */
// 计划页：点击条目 → 编辑配置
planList.addEventListener('click', (event) => {
  const row = event.target.closest('.plan-item');
  if (!row) return;
  openConfigEdit(row.dataset.uid);
});

// 计划页：添加动作 → 打开动作库
btnAddExercise.addEventListener('click', () => openLibrary());

// 动作库：关闭
document.getElementById('lib-close').addEventListener('click', () => closeOverlay(libOverlay));

// 动作库：搜索框实时过滤
libSearch.addEventListener('input', () => {
  libState.search = libSearch.value;
  renderLibResults();
});

// 动作库：筛选 chips + 结果行（同一委托）
libOverlay.addEventListener('click', (event) => {
  const chip = event.target.closest('.chip');
  if (chip) {
    libState[chip.dataset.filter] = chip.dataset.val;
    chip.parentElement.querySelectorAll('.chip').forEach(c => c.classList.toggle('active', c === chip));
    renderLibResults();
    return;
  }
  const row = event.target.closest('.lib-row');
  if (row) {
    const ex = window.EXERCISES.find(e => e.id === row.dataset.exId);
    if (!ex) return;
    closeOverlay(libOverlay);
    openConfigNew(ex);
  }
});

// 配置面板：类型切换 + 步进（同一委托）
cfgCard.addEventListener('click', (event) => {
  const segBtn = event.target.closest('.seg-btn');
  if (segBtn) {
    cfg.values.phase = segBtn.dataset.phase;
    cfgCard.querySelectorAll('.seg-btn').forEach(b => b.classList.toggle('active', b === segBtn));
    return;
  }
  const stepBtn = event.target.closest('.step-btn');
  if (stepBtn) {
    stepValue(stepBtn.closest('.stepper'), Number(stepBtn.dataset.dir));
  }
});

cfgSaveBtn.addEventListener('click', saveConfig);
cfgUpBtn.addEventListener('click', () => moveCfgItem(-1));
cfgDownBtn.addEventListener('click', () => moveCfgItem(1));
cfgDeleteBtn.addEventListener('click', deleteCfgItem);

/* ============================================================
   M3 计划管理
   ============================================================ */

/* ---------- DOM 引用 ---------- */
const btnManagePlans = document.getElementById('btn-manage-plans');
const mgrOverlay = document.getElementById('mgr-overlay');
const mgrList = document.getElementById('mgr-list');
const btnCreatePlan = document.getElementById('btn-create-plan');
const mgrCreateRow = document.getElementById('mgr-create-row');
const mgrCreateInput = document.getElementById('mgr-create-input');

// 面板内临时状态：新建输入行展开中 / 正在原位重命名的计划 id
let mgrCreating = false;
let mgrRenamingId = '';

/* ---------- 面板渲染 ---------- */
function buildMgrRow(plan) {
  const row = document.createElement('div');
  row.className = 'mgr-row' + (plan.id === activePlanId ? ' active' : '');
  row.dataset.planId = plan.id;

  if (mgrRenamingId === plan.id) {
    // 原位重命名：名字区换成输入行
    const renameRow = document.createElement('div');
    renameRow.className = 'mgr-rename-row';

    const input = document.createElement('input');
    input.type = 'text';
    input.className = 'mgr-input mgr-rename-input';
    input.value = plan.name;
    input.maxLength = 12;
    input.autocomplete = 'off';

    const yes = document.createElement('button');
    yes.type = 'button';
    yes.className = 'mgr-yes';
    yes.dataset.planId = plan.id;
    yes.textContent = '确定';
    yes.appendChild(enSpan('OK'));

    const no = document.createElement('button');
    no.type = 'button';
    no.className = 'mgr-no';
    no.dataset.planId = plan.id;
    no.textContent = '取消';
    no.appendChild(enSpan('Cancel'));

    renameRow.appendChild(input);
    renameRow.appendChild(yes);
    renameRow.appendChild(no);
    row.appendChild(renameRow);
  } else {
    const main = document.createElement('button');
    main.type = 'button';
    main.className = 'mgr-row-main';

    const cursor = document.createElement('span');
    cursor.className = 'mgr-cursor';
    cursor.textContent = '◆';

    const info = document.createElement('span');
    info.className = 'mgr-row-info';

    const name = document.createElement('span');
    name.className = 'mgr-row-name';
    name.textContent = plan.name;

    const count = document.createElement('span');
    count.className = 'mgr-row-count';
    count.textContent = plan.items.length + ' 个动作';
    count.appendChild(enSpan(plan.items.length + (plan.items.length === 1 ? ' exercise' : ' exercises')));

    info.appendChild(name);
    info.appendChild(count);
    main.appendChild(cursor);
    main.appendChild(info);
    row.appendChild(main);
  }

  const actions = document.createElement('span');
  actions.className = 'mgr-row-actions';

  const renameBtn = document.createElement('button');
  renameBtn.type = 'button';
  renameBtn.className = 'mgr-rename';
  renameBtn.dataset.planId = plan.id;
  renameBtn.textContent = '重命名';
  renameBtn.appendChild(enSpan('Rename'));

  const delBtn = document.createElement('button');
  delBtn.type = 'button';
  delBtn.className = 'mgr-del';
  delBtn.dataset.planId = plan.id;
  delBtn.textContent = '删除';
  delBtn.appendChild(enSpan('Delete'));
  // 只剩一套计划时禁止删除
  if (plans.length <= 1) {
    delBtn.disabled = true;
    delBtn.title = '至少保留一套计划';
  }

  actions.appendChild(renameBtn);
  actions.appendChild(delBtn);
  row.appendChild(actions);
  return row;
}

function renderMgrList() {
  mgrList.innerHTML = '';
  const frag = document.createDocumentFragment();
  plans.forEach(plan => frag.appendChild(buildMgrRow(plan)));
  mgrList.appendChild(frag);
  // 重命名行渲染后聚焦输入框
  if (mgrRenamingId) {
    const input = mgrList.querySelector('.mgr-rename-input');
    if (input) input.focus();
  }
}

function openMgr() {
  mgrCreating = false;
  mgrRenamingId = '';
  mgrCreateRow.hidden = true;
  mgrCreateInput.value = '';
  renderMgrList();
  openOverlay(mgrOverlay);
}

/* ---------- 新建 ---------- */
function openCreateRow() {
  mgrCreating = true;
  mgrCreateRow.hidden = false;
  mgrCreateInput.classList.remove('error');
  mgrCreateInput.value = '';
  mgrCreateInput.focus();
}

function confirmCreate() {
  const name = mgrCreateInput.value.trim();
  if (name === '') {
    // 空名：不收起输入行，亮边框 + 聚焦提示
    mgrCreateInput.classList.add('error');
    mgrCreateInput.focus();
    return;
  }
  const plan = { id: 'plan-' + Date.now(), name: name, items: [] };
  plans.push(plan);
  activePlanId = plan.id;
  persistPlans();
  Store.set('activePlanId', activePlanId);
  renderPlanPage();
  mgrCreating = false;
  mgrCreateRow.hidden = true;
  mgrCreateInput.value = '';
  renderMgrList();   // 停留在面板内，方便连续建多套
}

function cancelCreate() {
  mgrCreating = false;
  mgrCreateRow.hidden = true;
  mgrCreateInput.value = '';
}

/* ---------- 重命名 ---------- */
function confirmRename(planId) {
  const plan = plans.find(p => p.id === planId);
  const input = mgrList.querySelector('.mgr-rename-input');
  if (plan && input) {
    const name = input.value.trim();
    if (name !== '') {
      plan.name = name;
      persistPlans();
      renderPlanPage();   // 同步计划页标题
    }
    // 空名：不保存，重新渲染即恢复原名
  }
  mgrRenamingId = '';
  renderMgrList();
}

/* ---------- 切换 / 删除 ---------- */
function switchPlan(planId) {
  if (planId === activePlanId) return;   // 点击当前计划行主体：无操作
  activePlanId = planId;
  Store.set('activePlanId', activePlanId);
  persistPlans();
  renderPlanPage();
  closeOverlay(mgrOverlay);   // 回到计划页
}

function deletePlan(planId) {
  const index = plans.findIndex(p => p.id === planId);
  if (index < 0) return;
  const plan = plans[index];
  if (!window.confirm('删除计划「' + plan.name + '」及其 ' + plan.items.length + ' 个动作？此操作不可恢复')) return;
  plans.splice(index, 1);
  // 删除的是当前计划：指针回落到剩余第一套
  if (activePlanId === planId) {
    activePlanId = plans[0].id;
    Store.set('activePlanId', activePlanId);
  }
  persistPlans();
  mgrRenamingId = '';
  renderPlanPage();
  renderMgrList();
}

/* ---------- 事件绑定（面板内统一委托） ---------- */
btnManagePlans.addEventListener('click', () => openMgr());
document.getElementById('mgr-close').addEventListener('click', () => closeOverlay(mgrOverlay));

// 输入时清除空名错误态
mgrCreateInput.addEventListener('input', () => mgrCreateInput.classList.remove('error'));

mgrOverlay.addEventListener('click', (event) => {
  const target = event.target;

  if (target.closest('#mgr-create-yes')) { confirmCreate(); return; }
  if (target.closest('#mgr-create-no')) { cancelCreate(); return; }
  if (target.closest('#btn-create-plan')) {
    // 已展开时再点新建：只聚焦，避免清空正在输入的内容
    if (mgrCreating) mgrCreateInput.focus();
    else openCreateRow();
    return;
  }

  const renameYes = target.closest('.mgr-yes');
  if (renameYes) { confirmRename(renameYes.dataset.planId); return; }
  const renameNo = target.closest('.mgr-no');
  if (renameNo) { mgrRenamingId = ''; renderMgrList(); return; }

  const renameBtn = target.closest('.mgr-rename');
  if (renameBtn) {
    mgrCreating = false;
    mgrCreateRow.hidden = true;
    mgrRenamingId = renameBtn.dataset.planId;
    renderMgrList();
    return;
  }

  const delBtn = target.closest('.mgr-del');
  if (delBtn) {
    if (!delBtn.disabled) deletePlan(delBtn.dataset.planId);
    return;
  }

  const main = target.closest('.mgr-row-main');
  if (main) {
    switchPlan(main.closest('.mgr-row').dataset.planId);
  }
});

/* ---------- 启动（M2/M3） ---------- */
initPlanData();
renderPlanPage();

/* ============================================================
   M4 训练流程（主页状态机）
   activeSession = { planId, planName, startedAt, state:'ready'|'doing'|'resting',
     stateStartTs, restEndTs, cursor:{itemIdx, setIdx}, items:[ {...PlanItem, setRecords:[]} ] }
   所有计时只用时间戳差值；setInterval 仅刷新显示。
   ============================================================ */

const sessionRoot = document.getElementById('session-root');

let activeSessionData = null;   // 进行中的训练快照（与 lianhua.activeSession 同步）
let sessionEngaged = false;     // 本次页面加载内是否已进入训练画面（刷新后回到「继续训练」）
let lastSummary = null;         // summary 展示数据（history 已落盘，内存级即可）

let tickTimer = null;           // 250ms 显示刷新器
let audioCtx = null;            // 用户手势内创建/恢复
let wakeLock = null;            // 屏幕常亮句柄

/* ---------- 工具 ---------- */
function fmtDuration(ms) {
  const total = Math.max(0, Math.floor(ms / 1000));
  const m = String(Math.floor(total / 60)).padStart(2, '0');
  const s = String(total % 60).padStart(2, '0');
  return m + ':' + s;
}

function isValidSession(s) {
  return !!s && typeof s === 'object' &&
    typeof s.planId === 'string' && typeof s.planName === 'string' &&
    typeof s.startedAt === 'number' && Array.isArray(s.items) &&
    !!s.cursor && typeof s.cursor.itemIdx === 'number' &&
    ['ready', 'doing', 'resting'].indexOf(s.state) !== -1;
}

function persistSession() {
  Store.set('activeSession', activeSessionData);
}

function el(tag, className, text) {
  const node = document.createElement(tag);
  if (className) node.className = className;
  if (text !== undefined) node.textContent = text;
  return node;
}

function actionButton(className, text, act) {
  const btn = el('button', className, text);
  btn.type = 'button';
  btn.dataset.act = act;
  return btn;
}

// 参数行：负重次数「40kg × 10次」；自重次数「10次」；时间「维持 30秒」
function paramLine(it) {
  if (it.mode === 'time') {
    const head = it.type === 'weight' ? fmtNum(it.weight) + 'kg · ' : '';
    return head + '维持 ' + it.sec + '秒';
  }
  return (it.type === 'weight' ? fmtNum(it.weight) + 'kg × ' : '') + it.reps + '次';
}

// 参数行英文版（与 paramLine 一一对应，仅用于显示）
function paramLineEn(it) {
  if (it.mode === 'time') {
    const head = it.type === 'weight' ? fmtNum(it.weight) + 'kg · ' : '';
    return head + 'hold ' + it.sec + 's';
  }
  return (it.type === 'weight' ? fmtNum(it.weight) + 'kg × ' : '') + it.reps + ' reps';
}

/* ---------- 音频（Web Audio 振荡器合成，无音频文件） ---------- */
// 必须在用户手势（开始/继续训练的点击）内调用，iOS 才放行后续发声
function ensureAudio() {
  try {
    if (!audioCtx) {
      const Ctx = window.AudioContext || window.webkitAudioContext;
      if (Ctx) audioCtx = new Ctx();
    }
    if (audioCtx && audioCtx.state === 'suspended') audioCtx.resume();
  } catch (err) {
    audioCtx = null;   // 不支持的环境静默降级
  }
}

function beep(freq, when, dur) {
  const osc = audioCtx.createOscillator();
  const gain = audioCtx.createGain();
  osc.type = 'sine';
  osc.frequency.value = freq;
  gain.gain.setValueAtTime(0.001, when);
  gain.gain.exponentialRampToValueAtTime(0.4, when + 0.02);
  gain.gain.exponentialRampToValueAtTime(0.001, when + dur);
  osc.connect(gain);
  gain.connect(audioCtx.destination);
  osc.start(when);
  osc.stop(when + dur + 0.05);
}

// 休息归零提示音：880Hz 短音×2 + 1320Hz 长音×1
function playRestOverBeep() {
  if (!audioCtx) return;
  try {
    if (audioCtx.state === 'suspended') audioCtx.resume();
    const t0 = audioCtx.currentTime;
    beep(880, t0, 0.12);
    beep(880, t0 + 0.22, 0.12);
    beep(1320, t0 + 0.44, 0.5);
  } catch (err) { /* 发声失败不影响流程 */ }
}

/* ---------- 屏幕常亮 ---------- */
function requestWakeLock() {
  try {
    if (navigator.wakeLock) {
      navigator.wakeLock.request('screen').then(lock => {
        wakeLock = lock;
      }).catch(() => { wakeLock = null; });
    }
  } catch (err) {
    wakeLock = null;   // 不支持的环境静默降级
  }
}

function releaseWakeLock() {
  try {
    if (wakeLock) {
      wakeLock.release();
      wakeLock = null;
    }
  } catch (err) {
    wakeLock = null;
  }
}

/* ---------- 显示刷新器 ---------- */
function ensureTicker() {
  if (tickTimer) return;
  tickTimer = setInterval(tick, 250);
}

function stopTicker() {
  if (tickTimer) {
    clearInterval(tickTimer);
    tickTimer = null;
  }
}

function tick() {
  const s = activeSessionData;
  if (!s) {
    stopTicker();
    return;
  }
  // 倒计时归零（含锁屏/后台回来的追账）：走归零流程
  if (s.state === 'resting' && Date.now() >= s.restEndTs) {
    expireRest();
    return;
  }
  updateDynamicReadouts();
}

// 就地刷新动态读数，不做整体重渲染
function updateDynamicReadouts() {
  const s = activeSessionData;
  if (!s) return;
  const now = Date.now();
  document.querySelectorAll('[data-live]').forEach(node => {
    const kind = node.dataset.live;
    if (kind === 'elapsed') {
      node.textContent = fmtDuration(now - s.startedAt);
    } else if (kind === 'set-elapsed') {
      node.textContent = String(Math.max(0, Math.floor((now - s.stateStartTs) / 1000)));
    } else if (kind === 'rest-left') {
      node.textContent = String(Math.max(0, Math.ceil((s.restEndTs - now) / 1000)));
    }
  });
}

/* ---------- 状态迁移 ---------- */
function startSession() {
  const plan = getActivePlan();
  if (!Array.isArray(plan.items) || plan.items.length === 0) {
    // M5 微调：用户点「取消」时不跳转
    if (!window.confirm('当前计划还没有动作，先去训练计划页编排')) return;
    activatePage('plan');
    return;
  }
  ensureAudio();          // iOS 声音授权必须挂在用户手势上
  requestWakeLock();
  const now = Date.now();
  activeSessionData = {
    planId: plan.id,
    planName: plan.name,
    startedAt: now,
    state: 'ready',
    stateStartTs: now,
    restEndTs: null,
    cursor: { itemIdx: 0, setIdx: 0 },
    // 深拷贝快照：训练中改计划不影响本次训练
    items: plan.items.map(it => Object.assign({}, it, { setRecords: [] })),
  };
  sessionEngaged = true;
  persistSession();
  renderSession();
}

// 「继续训练」：恢复既有快照
function resumeSession() {
  ensureAudio();
  requestWakeLock();
  const s = activeSessionData;
  if (s && s.state === 'resting' && Date.now() >= s.restEndTs) {
    expireRest();   // 休息在离开页面期间已归零
    return;
  }
  sessionEngaged = true;
  renderSession();
}

// ready → doing
function beginSet() {
  const s = activeSessionData;
  if (!s || s.state !== 'ready') return;
  s.state = 'doing';
  s.stateStartTs = Date.now();
  s.restEndTs = null;
  persistSession();
  renderSession();
}

// doing → 完成 1 组：记账 → 推进 cursor → resting / summary
function completeSet() {
  const s = activeSessionData;
  if (!s || s.state !== 'doing') return;
  const it = s.items[s.cursor.itemIdx];
  // 记录计划值（严格按计划，M4 不做临场改动）
  it.setRecords.push({ done: true, weight: it.weight, reps: it.reps, sec: it.sec });
  const restSec = it.restSec;

  // 推进到「下一次要做的组」
  if (s.cursor.setIdx + 1 < it.sets) {
    s.cursor.setIdx += 1;
  } else if (s.cursor.itemIdx + 1 < s.items.length) {
    s.cursor.itemIdx += 1;
    s.cursor.setIdx = 0;
  } else {
    finalizeSession();   // 最后一组之后不休息
    return;
  }

  s.state = 'resting';
  s.stateStartTs = Date.now();
  s.restEndTs = s.stateStartTs + restSec * 1000;
  persistSession();
  renderSession();
}

// resting → 下一组 ready（跳过/归零共用；归零先响铃）
function goNextReady() {
  const s = activeSessionData;
  if (!s || s.state !== 'resting') return;
  s.state = 'ready';
  s.stateStartTs = Date.now();
  s.restEndTs = null;
  persistSession();
  renderSession();
}

function expireRest() {
  playRestOverBeep();
  goNextReady();
}

function skipRest() {
  goNextReady();
}

function extendRest() {
  const s = activeSessionData;
  if (!s || s.state !== 'resting') return;
  s.restEndTs += 15000;
  persistSession();
  updateDynamicReadouts();
}

// 会话条「提前结束」：0 组 → 不存历史；有完成组 → summary
function finishEarly() {
  const s = activeSessionData;
  if (!s) return;
  const doneSets = s.items.reduce((n, it) => n + it.setRecords.length, 0);
  if (doneSets === 0) {
    if (!window.confirm('结束当前训练？尚未完成任何一组，进度将不会保存')) return;
    discardSession();
    return;
  }
  if (!window.confirm('结束当前训练？已完成的组会存入历史')) return;
  finalizeSession();
}

// 「放弃本次训练」（idle 继续画面）
function giveUpSession() {
  if (!window.confirm('放弃本次训练？进度将不会保存')) return;
  discardSession();
}

// 清除进行中训练，回到 idle
function discardSession() {
  Store.set('activeSession', null);
  activeSessionData = null;
  sessionEngaged = false;
  releaseWakeLock();
  stopTicker();
  renderSession();
}

// 提交历史 + 清 activeSession + 出 summary 画面（进入瞬间即落盘）
function finalizeSession() {
  const s = activeSessionData;
  if (!s) return;
  const endedAt = Date.now();
  const record = {
    id: 'w-' + s.startedAt,
    startedAt: s.startedAt,
    endedAt: endedAt,
    durationSec: Math.floor((endedAt - s.startedAt) / 1000),
    planId: s.planId,
    planName: s.planName,
    items: s.items.map(it => ({
      uid: it.uid,
      exId: it.exId,
      name: it.name,
      part: it.part,
      phase: it.phase,
      type: it.type,
      mode: it.mode,
      sets: it.sets,
      reps: it.reps,
      sec: it.sec,
      weight: it.weight,
      restSec: it.restSec,
      setRecords: it.setRecords.slice(),
    })),
  };
  const history = Store.get('history', []);
  history.unshift(record);
  if (history.length > 200) history.length = 200;   // 上限 200 条
  Store.set('history', history);
  Store.set('activeSession', null);
  activeSessionData = null;
  sessionEngaged = false;
  lastSummary = record;
  releaseWakeLock();
  stopTicker();
  renderSession();
}

/* ---------- 渲染 ---------- */
// 会话条：计划名 · 已用时 · 提前结束
function buildSessionBar(s) {
  const bar = el('div', 'session-bar');
  bar.appendChild(el('span', 'session-bar-plan', s.planName));
  const time = el('span', 'session-bar-time', '已用时 ');
  time.appendChild(enSpan('Elapsed'));
  const live = el('span', null, fmtDuration(Date.now() - s.startedAt));
  live.dataset.live = 'elapsed';
  time.appendChild(live);
  bar.appendChild(time);
  const endBtn = actionButton('session-end', '提前结束', 'finish-early');
  endBtn.appendChild(enSpan('End'));
  bar.appendChild(endBtn);
  return bar;
}

function buildTagsRow(it) {
  const tags = el('div', 'item-tags');
  const tagPairs = [
    [PHASE_LABEL[it.phase], PHASE_EN[it.phase]],
    [TYPE_LABEL[it.type], TYPE_EN[it.type]],
    [MODE_LABEL[it.mode], MODE_EN[it.mode]],
  ];
  tagPairs.forEach(pair => {
    const tag = el('span', 'tag', pair[0]);
    if (pair[1]) tag.appendChild(enSpan(pair[1]));
    tags.appendChild(tag);
  });
  return tags;
}

// idle：无训练 → 训练开始；有未走完的训练 → 继续训练
function buildIdleScreen() {
  const s = activeSessionData;
  const main = el('div', 'session-main');
  if (isValidSession(s)) {
    const resumeBtn = actionButton('btn-start panel', '继续训练', 'resume');
    resumeBtn.appendChild(enSpan('Resume'));
    main.appendChild(resumeBtn);
    const sub = el('div', 'session-sub');
    sub.appendChild(document.createTextNode(s.planName + ' · 已进行 '));
    const live = el('span', null, fmtDuration(Date.now() - s.startedAt));
    live.dataset.live = 'elapsed';
    sub.appendChild(live);
    sub.appendChild(enSpan('elapsed'));
    main.appendChild(sub);
    const giveupBtn = actionButton('session-secondary', '放弃本次训练', 'giveup');
    giveupBtn.appendChild(enSpan('Discard'));
    main.appendChild(giveupBtn);
  } else {
    const btn = actionButton('btn-start panel', '训练开始', 'start');
    btn.appendChild(enSpan('Start Training'));
    btn.id = 'btn-start';
    main.appendChild(btn);
  }
  return main;
}

// ready：一组待开始
function buildReadyScreen(s) {
  const it = s.items[s.cursor.itemIdx];
  const wrap = el('div', 'session-screen');
  wrap.appendChild(buildSessionBar(s));
  const main = el('div', 'session-main');
  const stateLabel = el('div', 'session-state-label', '准备');
  stateLabel.appendChild(enSpan('Ready'));
  main.appendChild(stateLabel);
  const exName = el('div', 'session-ex-name', it.name);
  const exNameEn = exEn(it.exId);
  if (exNameEn) exName.appendChild(enSpan(exNameEn));
  main.appendChild(exName);
  main.appendChild(buildTagsRow(it));
  const setInfo = el('div', 'session-setinfo', '第 ' + (s.cursor.setIdx + 1) + ' 组 / 共 ' + it.sets + ' 组');
  setInfo.appendChild(enSpan('Set ' + (s.cursor.setIdx + 1) + ' of ' + it.sets));
  main.appendChild(setInfo);
  const params = el('div', 'session-params', paramLine(it));
  params.appendChild(enSpan(paramLineEn(it)));
  main.appendChild(params);
  const beginBtn = actionButton('btn-start panel', '开始', 'begin');
  beginBtn.appendChild(enSpan('Start'));
  main.appendChild(beginBtn);
  wrap.appendChild(main);
  return wrap;
}

// doing：次数=进行中+已进行秒数；时间=大号走秒读数
function buildDoingScreen(s) {
  const it = s.items[s.cursor.itemIdx];
  const wrap = el('div', 'session-screen');
  wrap.appendChild(buildSessionBar(s));
  const main = el('div', 'session-main');
  const stateLabel = el('div', 'session-state-label', '进行中');
  stateLabel.appendChild(enSpan('In Progress'));
  main.appendChild(stateLabel);
  const exName = el('div', 'session-ex-name', it.name);
  const exNameEn = exEn(it.exId);
  if (exNameEn) exName.appendChild(enSpan(exNameEn));
  main.appendChild(exName);
  if (it.mode === 'time') {
    const row = el('div', 'session-count-row');
    const count = el('span', 'session-count', '0');
    count.dataset.live = 'set-elapsed';
    row.appendChild(count);
    const unit = el('span', 'session-count-unit', '秒');
    unit.appendChild(enSpan('sec'));
    row.appendChild(unit);
    main.appendChild(row);
  } else {
    const sub = el('div', 'session-sub');
    sub.appendChild(document.createTextNode('本组已进行 '));
    const live = el('span', null, '0');
    live.dataset.live = 'set-elapsed';
    sub.appendChild(live);
    sub.appendChild(document.createTextNode(' 秒'));
    sub.appendChild(enSpan('elapsed'));
    main.appendChild(sub);
  }
  const doneBtn = actionButton('btn-start panel', '完成', 'complete');
  doneBtn.appendChild(enSpan('Done'));
  main.appendChild(doneBtn);
  wrap.appendChild(main);
  return wrap;
}

// resting：大号倒计时 + 接下来 + +15秒 / 跳过休息
function buildRestingScreen(s) {
  const nextIt = s.items[s.cursor.itemIdx];
  const wrap = el('div', 'session-screen');
  wrap.appendChild(buildSessionBar(s));
  const main = el('div', 'session-main');
  const stateLabel = el('div', 'session-state-label', '组间休息');
  stateLabel.appendChild(enSpan('Rest'));
  main.appendChild(stateLabel);
  const row = el('div', 'session-count-row');
  const count = el('span', 'session-count', '0');
  count.dataset.live = 'rest-left';
  row.appendChild(count);
  const unit = el('span', 'session-count-unit', '秒');
  unit.appendChild(enSpan('sec'));
  row.appendChild(unit);
  main.appendChild(row);
  const nextEn = exEn(nextIt.exId);
  const next = el('div', 'session-next', '接下来：' + nextIt.name);
  next.appendChild(enSpan(nextEn ? 'Next: ' + nextEn : 'Next'));
  main.appendChild(next);
  const actions = el('div', 'session-actions');
  const plusBtn = actionButton('session-secondary', '+15秒', 'rest-plus');
  plusBtn.appendChild(enSpan('+15s'));
  actions.appendChild(plusBtn);
  const skipBtn = actionButton('session-secondary', '跳过休息', 'rest-skip');
  skipBtn.appendChild(enSpan('Skip'));
  actions.appendChild(skipBtn);
  main.appendChild(actions);
  wrap.appendChild(main);
  return wrap;
}

// summary：训练完成（进入本画面时历史已落盘）
function buildSummaryScreen(rec) {
  const main = el('div', 'session-main');
  const title = el('div', 'summary-title', '训练完成');
  title.appendChild(enSpan('Session Complete'));
  main.appendChild(title);

  const doneItems = rec.items.filter(it => it.setRecords.length >= it.sets).length;
  const totalSets = rec.items.reduce((n, it) => n + it.sets, 0);
  const doneSets = rec.items.reduce((n, it) => n + it.setRecords.length, 0);

  const stats = el('div', 'panel summary-stats');
  const rowTime = el('div', null, '总时长 ');
  rowTime.appendChild(enSpan('Total'));
  rowTime.appendChild(document.createTextNode('：'));
  rowTime.appendChild(el('b', null, fmtDuration(rec.durationSec * 1000)));
  stats.appendChild(rowTime);
  const rowItems = el('div', null, '完成动作 ');
  rowItems.appendChild(enSpan('Exercises'));
  rowItems.appendChild(document.createTextNode('：'));
  rowItems.appendChild(el('b', null, doneItems + ' / ' + rec.items.length));
  stats.appendChild(rowItems);
  const rowSets = el('div', null, '完成组数 ');
  rowSets.appendChild(enSpan('Sets'));
  rowSets.appendChild(document.createTextNode('：'));
  rowSets.appendChild(el('b', null, doneSets + ' / ' + totalSets));
  stats.appendChild(rowSets);
  main.appendChild(stats);

  const rows = el('div', 'summary-rows');
  rec.items.forEach(it => {
    const row = el('div', 'summary-row');
    const name = el('span', 'summary-row-name', it.name);
    const nameEn = exEn(it.exId);
    if (nameEn) name.appendChild(enSpan(nameEn));
    row.appendChild(name);
    const cnt = el('span', 'summary-row-count', it.setRecords.length + ' / ' + it.sets + ' 组');
    cnt.appendChild(enSpan('sets'));
    row.appendChild(cnt);
    rows.appendChild(row);
  });
  main.appendChild(rows);

  const backBtn = actionButton('session-secondary', '返回', 'summary-back');
  backBtn.appendChild(enSpan('Back'));
  main.appendChild(backBtn);
  return main;
}

// 状态机总渲染：唯一入口
function renderSession() {
  const s = activeSessionData;
  sessionRoot.innerHTML = '';

  if (!s) {
    stopTicker();
    if (lastSummary) {
      sessionRoot.appendChild(buildSummaryScreen(lastSummary));
    } else {
      sessionRoot.appendChild(buildIdleScreen());
    }
    return;
  }

  ensureTicker();
  if (!sessionEngaged) {
    // 刷新/首次进入：先展示「继续训练」画面
    sessionRoot.appendChild(buildIdleScreen());
  } else if (s.state === 'ready') {
    sessionRoot.appendChild(buildReadyScreen(s));
  } else if (s.state === 'doing') {
    sessionRoot.appendChild(buildDoingScreen(s));
  } else if (s.state === 'resting') {
    sessionRoot.appendChild(buildRestingScreen(s));
  }
  updateDynamicReadouts();
}

/* ---------- 事件绑定（容器统一委托） ---------- */
sessionRoot.addEventListener('click', (event) => {
  const btn = event.target.closest('[data-act]');
  if (!btn) return;
  const act = btn.dataset.act;
  if (act === 'start') startSession();
  else if (act === 'resume') resumeSession();
  else if (act === 'giveup') giveUpSession();
  else if (act === 'begin') beginSet();
  else if (act === 'complete') completeSet();
  else if (act === 'rest-plus') extendRest();
  else if (act === 'rest-skip') skipRest();
  else if (act === 'finish-early') finishEarly();
  else if (act === 'summary-back') {
    lastSummary = null;   // 记录已在 history，返回只负责回 idle
    renderSession();
  }
});

/* ---------- 启动（M4） ---------- */
activeSessionData = Store.get('activeSession', null);
if (activeSessionData && !isValidSession(activeSessionData)) {
  // 损坏的快照视为不存在
  activeSessionData = null;
  Store.set('activeSession', null);
}
renderSession();

// 回到前台：重取常亮 + 按时间戳立即重算（resting 可能已归零）
document.addEventListener('visibilitychange', () => {
  if (document.visibilityState !== 'visible' || !activeSessionData) return;
  requestWakeLock();
  if (activeSessionData.state === 'resting' && Date.now() >= activeSessionData.restEndTs) {
    expireRest();
    return;
  }
  renderSession();
});

/* ============================================================
   M5 训练历史
   ============================================================ */

/* ---------- DOM 引用 ---------- */
const historyList = document.getElementById('history-list');
const detailOverlay = document.getElementById('detail-overlay');
const historyDetail = document.getElementById('history-detail');

/* ---------- 日期格式化 ----------
   本地时区手动补零，不用 toISOString（它按 UTC 输出会有时区偏移）。 */
function pad2(n) {
  return String(n).padStart(2, '0');
}

function fmtDateTime(ts, withYear) {
  const d = new Date(ts);
  const base = pad2(d.getMonth() + 1) + '-' + pad2(d.getDate()) +
    ' ' + pad2(d.getHours()) + ':' + pad2(d.getMinutes());
  return withYear ? d.getFullYear() + '-' + base : base;
}

// 读取历史；损坏数据一律按空数组处理
function getHistory() {
  const history = Store.get('history', []);
  return Array.isArray(history) ? history : [];
}

// 组数汇总：ΣsetRecords / Σsets
function historySetTotals(record) {
  let done = 0;
  let total = 0;
  record.items.forEach(it => {
    done += it.setRecords.length;
    total += it.sets;
  });
  return { done, total };
}

/* ---------- 列表 ---------- */
function buildHistoryRow(record) {
  const row = document.createElement('button');
  row.type = 'button';
  row.className = 'history-item';
  row.dataset.id = record.id;

  const totals = historySetTotals(record);
  row.appendChild(el('span', 'history-date', fmtDateTime(record.startedAt, false)));
  row.appendChild(el('span', 'history-plan', record.planName));

  const meta = el('span', 'history-meta');
  meta.appendChild(el('span', 'history-duration', fmtDuration(record.durationSec * 1000)));
  const sets = el('span', 'history-sets', totals.done + '/' + totals.total + ' 组');
  sets.appendChild(enSpan('sets'));
  meta.appendChild(sets);
  row.appendChild(meta);
  return row;
}

function renderHistoryPage() {
  historyList.innerHTML = '';
  const history = getHistory();
  if (history.length === 0) {
    historyList.appendChild(noteBox('还没有训练记录，去主页开始第一次训练', false, 'No sessions yet — start your first workout on the home page'));
    return;
  }
  const frag = document.createDocumentFragment();
  history.forEach(record => frag.appendChild(buildHistoryRow(record)));
  historyList.appendChild(frag);   // 存储顺序天然最新在前
}

/* ---------- 训练详情 ---------- */
// 逐组行：完成组在前（带 ✓），未完成组灰显在后
function buildDetailSetRow(it, index) {
  if (index < it.setRecords.length) {
    const row = el('div', 'set-row');
    const label = el('span', 'set-label', '第 ' + (index + 1) + ' 组 · ' + paramLine(it));
    label.appendChild(enSpan('Set ' + (index + 1) + ' · ' + paramLineEn(it)));
    row.appendChild(label);
    row.appendChild(el('span', 'set-check', '✓'));
    return row;
  }
  const row = el('div', 'set-row set-missed');
  const label = el('span', 'set-label', '第 ' + (index + 1) + ' 组 · 未完成');
  label.appendChild(enSpan('Set ' + (index + 1) + ' · missed'));
  row.appendChild(label);
  return row;
}

function buildDetailItem(it) {
  const block = el('div', 'panel detail-item');
  const head = el('div', 'detail-item-head');
  const name = el('div', 'detail-item-name', it.name);
  const nameEn = exEn(it.exId);
  if (nameEn) name.appendChild(enSpan(nameEn));
  head.appendChild(name);
  head.appendChild(buildTagsRow(it));   // 复用 M4 标签行
  block.appendChild(head);
  const params = el('div', 'detail-params', paramLine(it));
  params.appendChild(enSpan(paramLineEn(it)));
  block.appendChild(params);

  const setsBox = el('div', 'detail-sets');
  for (let i = 0; i < it.sets; i++) {
    setsBox.appendChild(buildDetailSetRow(it, i));
  }
  block.appendChild(setsBox);
  return block;
}

function openHistoryDetail(record) {
  historyDetail.innerHTML = '';

  const head = el('div', 'detail-head');
  head.appendChild(el('div', 'detail-date', fmtDateTime(record.startedAt, true)));
  head.appendChild(el('div', 'detail-plan', record.planName));
  const duration = el('div', 'detail-duration', '总时长 ' + fmtDuration(record.durationSec * 1000));
  duration.appendChild(enSpan('Total'));
  head.appendChild(duration);
  historyDetail.appendChild(head);

  const doneItems = record.items.filter(it => it.setRecords.length >= it.sets).length;
  const totals = historySetTotals(record);
  const stats = el('div', 'detail-stats');
  const statItems = el('span', null, '完成动作 ' + doneItems + ' / ' + record.items.length);
  statItems.appendChild(enSpan('exercises'));
  stats.appendChild(statItems);
  const statSets = el('span', null, '完成组数 ' + totals.done + ' / ' + totals.total);
  statSets.appendChild(enSpan('sets'));
  stats.appendChild(statSets);
  historyDetail.appendChild(stats);

  const frag = document.createDocumentFragment();
  record.items.forEach(it => frag.appendChild(buildDetailItem(it)));
  historyDetail.appendChild(frag);

  openOverlay(detailOverlay);
}

/* ---------- 事件绑定 ---------- */
// 列表：点击行 → 打开详情
historyList.addEventListener('click', (event) => {
  const row = event.target.closest('.history-item');
  if (!row) return;
  const record = getHistory().find(r => r.id === row.dataset.id);
  if (record) openHistoryDetail(record);
});

// 详情：关闭
document.getElementById('detail-close').addEventListener('click', () => closeOverlay(detailOverlay));

// 切到历史页时刷新列表（训练完成后数据可能已变化）
navBar.addEventListener('click', (event) => {
  if (event.target.closest('[data-page="history"]')) renderHistoryPage();
});

/* ---------- 启动（M5） ---------- */
renderHistoryPage();

/* ============================================================
   M6 数据管理
   ============================================================ */

/* ---------- DOM 引用 ---------- */
const btnDataMgmt = document.getElementById('btn-data-mgmt');
const dataOverlay = document.getElementById('data-overlay');
const btnExport = document.getElementById('btn-export');
const btnImport = document.getElementById('btn-import');
const btnClearAll = document.getElementById('btn-clear-all');
const importFile = document.getElementById('import-file');

/* ---------- 导出 ----------
   打包 plans/activePlanId/history；activeSession 是临时态不导出。 */
function exportData() {
  const payload = {
    app: 'lianhua',
    version: 1,
    exportedAt: new Date().toISOString(),
    data: {
      plans: Store.get('plans', []),
      activePlanId: Store.get('activePlanId', null),
      history: getHistory(),
    },
  };
  const blob = new Blob([JSON.stringify(payload, null, 2)], { type: 'application/json' });
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  const d = new Date();
  a.download = '最终练化备份-' + d.getFullYear() + '-' + pad2(d.getMonth() + 1) + '-' + pad2(d.getDate()) + '.json';
  a.href = url;
  document.body.appendChild(a);
  a.click();
  a.remove();
  // 下载触发后再释放对象 URL
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}

/* ---------- 导入 ---------- */
// 读取用户选择的备份文件
function handleImportFile(file) {
  const reader = new FileReader();
  reader.onload = () => {
    let obj = null;
    try {
      obj = JSON.parse(reader.result);
    } catch (err) {
      obj = null;   // JSON 截断/损坏统一走格式错误分支
    }
    tryImport(obj);
  };
  reader.onerror = () => window.confirm('文件格式不正确，未做任何修改');
  reader.readAsText(file, 'utf-8');
}

// 校验 + 二次确认 + 覆盖写入
function tryImport(obj) {
  const valid = !!obj && typeof obj === 'object' && obj.app === 'lianhua' &&
    !!obj.data && typeof obj.data === 'object' &&
    Array.isArray(obj.data.plans) && Array.isArray(obj.data.history) && obj.data.plans.length > 0;
  if (!valid) {
    window.confirm('文件格式不正确，未做任何修改');
    return;
  }
  const data = obj.data;
  // activePlanId 缺失或失效时回退 plans[0].id
  const importedActiveId = data.plans.some(p => p.id === data.activePlanId)
    ? data.activePlanId
    : data.plans[0].id;

  const exported = obj.exportedAt ? new Date(obj.exportedAt) : null;
  const exportedText = exported && !isNaN(exported.getTime())
    ? fmtDateTime(exported.getTime(), true)
    : '未知时间';

  const msg = '将覆盖当前全部数据：计划 ' + data.plans.length + ' 套 · 历史 ' +
    data.history.length + ' 条（导出于 ' + exportedText + '）。确定导入？';
  if (!window.confirm(msg)) return;

  Store.set('plans', data.plans);
  Store.set('activePlanId', importedActiveId);
  Store.set('history', data.history);

  // 同步内存态并刷新两个页面
  plans = data.plans;
  activePlanId = importedActiveId;
  renderPlanPage();
  renderHistoryPage();
  closeOverlay(dataOverlay);

  window.confirm('导入完成：计划 ' + data.plans.length + ' 套 · 历史 ' + data.history.length + ' 条');
}

/* ---------- 清空 ---------- */
function clearAllData() {
  const planCount = plans.length;
  const historyCount = getHistory().length;
  const msg1 = '将清空全部数据：' + planCount + ' 套计划 · ' + historyCount +
    ' 条训练记录，进行中的训练也会结束。此操作不可恢复！';
  if (!window.confirm(msg1)) return;
  if (!window.confirm('再次确认：真的要清空吗？')) return;

  plans = [{ id: 'plan-1', name: '默认计划', items: [] }];
  activePlanId = 'plan-1';
  Store.set('plans', plans);
  Store.set('activePlanId', activePlanId);
  Store.set('history', []);
  Store.set('activeSession', null);

  // 进行中的训练与遗留的 summary 画面一并结束
  activeSessionData = null;
  sessionEngaged = false;
  lastSummary = null;
  releaseWakeLock();
  stopTicker();

  renderPlanPage();
  renderSession();
  renderHistoryPage();
  closeOverlay(dataOverlay);
}

/* ---------- 事件绑定 ---------- */
btnDataMgmt.addEventListener('click', () => openOverlay(dataOverlay));
document.getElementById('data-close').addEventListener('click', () => closeOverlay(dataOverlay));

btnExport.addEventListener('click', exportData);
btnImport.addEventListener('click', () => importFile.click());   // 用户手势内唤起文件选择器
btnClearAll.addEventListener('click', clearAllData);

// 选完文件即读；清空 value 允许重复选择同一文件
importFile.addEventListener('change', () => {
  const file = importFile.files && importFile.files[0];
  importFile.value = '';
  if (file) handleImportFile(file);
});
