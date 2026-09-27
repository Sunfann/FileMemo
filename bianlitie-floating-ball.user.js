// ==UserScript==
// @name         便利贴悬浮球 v2.7.2 复刻
// @namespace    bianlitie
// @version      2.7.2
// @description  完全复刻「便利贴/奇点标签 v2.7.2」的悬浮球系统（主球/关闭球/外环/涟漪/粒子/批量最小化/收纳/退出特效）
// @match        *://*/*
// @grant        none
// @run-at       document-idle
// ==/UserScript==
'use strict';
(function () {
// ================= Bianlitie floating-ball replica (v2.7.2) =================
// Script-owned, page-agnostic widget. Injected under document.body as #bianlitie-root.
const NS = 'bianlitie';
const STORAGE_KEY = 'bianlitie-data-v10';

// ---- §12 configurable constants ----
const FS = 44;          // 主球直径
const FCG = -1;         // close 按钮横向偏移
const RP = -100;        // 外环偏移（负值，照字面计算）
const RB = 0.05;        // 外环边框
const RBC = 66;         // 收纳粒子数
const RBCL = '#e04545'; // 收纳粒子色
const RBS = 8;          // 收纳粒子直径
const BBC = 40;         // 空退出粒子数
const BBCC = '#000000'; // 空退出粒子色
const BBS = 10;         // 空退出粒子直径
const RS = FS + RP * 2; // 外环直径（字面公式，负值即不可见）

const cfg = (typeof args !== 'undefined' && args && typeof args === 'object') ? args : {};

// ---- §14.10 idempotent re-run: clean previous instance ----
try {
  const prev = window['__' + NS];
  if (prev && typeof prev.cleanup === 'function') prev.cleanup();
} catch (e) { /* ignore */ }

const cleanups = [];
const on = (t, ev, fn, opt) => {
  try { t.addEventListener(ev, fn, opt); } catch (e) {}
  cleanups.push(() => { try { t.removeEventListener(ev, fn, opt); } catch (e) {} });
};
const uid = () => 'n' + Date.now().toString(36) + Math.random().toString(36).slice(2, 7);
const clamp = (v, a, b) => Math.max(a, Math.min(b, v));
const pickPt = (e) => {
  const t = (e.touches && e.touches[0]) || (e.changedTouches && e.changedTouches[0]);
  return t ? { x: t.clientX, y: t.clientY } : { x: e.clientX, y: e.clientY };
};

// ===================== 1. persistence =====================
let data = { notes: [], fab: null };
try {
  const raw = localStorage.getItem(STORAGE_KEY);
  if (raw) {
    const p = JSON.parse(raw);
    if (p && typeof p === 'object') {
      data = { notes: Array.isArray(p.notes) ? p.notes : [], fab: p.fab || null };
    }
  }
} catch (e) { /* ignore */ }
if (cfg && cfg.reset === true) data.fab = null;

const save = () => { try { localStorage.setItem(STORAGE_KEY, JSON.stringify(data)); return true; } catch (e) { return false; } };
let _saveT = null;
const saveDeb = () => { if (_saveT) clearTimeout(_saveT); _saveT = setTimeout(() => { _saveT = null; save(); }, 200); };
const silentSave = () => { try { localStorage.setItem(STORAGE_KEY, JSON.stringify(data)); } catch (e) {} };

// ===================== 2. injected style helpers =====================
const STYLE_ID = 'bianlitie-style';
const injectedStyles = [];
const injectStyle = (id, css) => {
  if (document.getElementById(id)) return;
  const s = document.createElement('style');
  s.id = id; s.textContent = css;
  (document.head || document.documentElement).appendChild(s);
  injectedStyles.push(id);
};
const cleanupInjectedStyles = () => {
  injectedStyles.forEach((id) => { const el = document.getElementById(id); if (el) el.remove(); });
  injectedStyles.length = 0;
};

injectStyle(STYLE_ID, [
  '#bianlitie-root{position:fixed;inset:0;pointer-events:none;z-index:2147483647;font-family:"PingFang SC","Microsoft YaHei",sans-serif}',
  '.blt-fab{position:fixed;width:44px;height:44px;border:none;border-radius:50%;color:#fff;font-size:20px;background:rgba(0,0,0,0.65);-webkit-backdrop-filter:blur(6px);backdrop-filter:blur(6px);box-shadow:0 4px 14px rgba(0,0,0,0.25);display:flex;align-items:center;justify-content:center;line-height:1;z-index:100;overflow:hidden;cursor:pointer;pointer-events:auto;transition:transform .18s,background .2s,box-shadow .2s}',
  '.blt-fab:hover{transform:scale(1.08);background:rgba(0,0,0,0.8)}',
  '.blt-fab.tap-flash{background:rgba(255,255,255,0.35)!important;box-shadow:0 0 20px rgba(255,255,255,0.6)!important;transition:background .05s,box-shadow .05s}',
  '.blt-fab.initial-pop{animation:blt-fab-intro 1.2s ease-in-out forwards}',
  '.blt-fab.is-dragging{cursor:grabbing}',
  '@keyframes blt-fab-intro{0%{transform:scale(1)}30%{transform:scale(.6)}50%{transform:scale(1.2)}70%{transform:scale(1);box-shadow:0 0 30px rgba(255,255,255,.8)}100%{transform:scale(1);box-shadow:0 4px 14px rgba(0,0,0,.25)}}',
  '.blt-ripple{position:absolute;top:50%;left:50%;width:36px;height:36px;margin-left:-18px;margin-top:-18px;border:2px solid rgba(255,255,255,0.5);border-radius:50%;transform:scale(1.4);opacity:0;pointer-events:none}',
  '.blt-ripple.active{animation:blt-ripple-in var(--ripple-duration,1.5s) ease-out infinite}',
  '@keyframes blt-ripple-in{0%{transform:scale(1.4);opacity:.6}100%{transform:scale(0);opacity:0}}',
  '.blt-ring{position:fixed;pointer-events:none;z-index:99;border:0.05px solid #000;border-radius:50%;transition:opacity .2s;opacity:1}',
  '.blt-close{position:fixed;width:28px;height:28px;border:none;border-radius:50%;color:#fff;font-size:14px;background:rgba(220,50,50,0.85);-webkit-backdrop-filter:blur(4px);backdrop-filter:blur(4px);box-shadow:0 3px 10px rgba(220,50,50,0.35);display:flex;align-items:center;justify-content:center;transition:all .2s ease;opacity:0;transform:scale(.8);z-index:101;pointer-events:none;cursor:pointer}',
  '.blt-close::before{content:"\\00d7"}',
  '.blt-close.visible{opacity:1;transform:scale(1);pointer-events:auto}',
  '.blt-close:hover{background:rgba(255,60,60,1);transform:scale(1.1)}',
  '.blt-close.bulk-active{background:rgba(34,180,80,0.85)!important;box-shadow:0 3px 10px rgba(34,180,80,0.35)!important}',
  '.blt-close.bulk-active:hover{background:rgba(50,200,90,1)!important}',
  '.blt-close.stash-active{background:rgba(255,200,0,0.85)!important;box-shadow:0 3px 10px rgba(255,200,0,0.4)!important}',
  '.blt-close.stash-active:hover{background:rgba(255,220,0,1)!important}',
  '.blt-close.tap-elastic{animation:blt-tap-elastic .35s ease-in-out forwards}',
  '@keyframes blt-tap-elastic{0%{transform:scale(1)}30%{transform:scale(.6)}60%{transform:scale(1.2)}100%{transform:scale(1)}}',
  '.blt-close.vanish{animation:blt-close-vanish .4s ease-out forwards;pointer-events:none}',
  '@keyframes blt-close-vanish{0%{transform:scale(1);opacity:1}30%{transform:scale(.6);opacity:1;box-shadow:0 0 20px rgba(220,50,50,.8)}100%{transform:scale(0);opacity:0}}',
  '.blt-close-ripple{position:absolute;inset:-4px;border-radius:50%;border:2px solid rgba(255,255,255,0);pointer-events:none}',
  '.blt-close-ripple.long{animation:blt-close-ripple 2s ease-out}',
  '@keyframes blt-close-ripple{0%{border-color:rgba(255,255,255,.6);transform:scale(.8)}100%{border-color:rgba(255,255,255,0);transform:scale(1.6)}}',
  '.blt-particle{position:fixed;width:8px;height:8px;background:#fff;border-radius:50%;pointer-events:none;z-index:2147483647;animation:blt-particle-burst .6s ease-out forwards}',
  '.blt-particle.red{background:#e04545}',
  '.blt-particle.white{background:#fff}',
  '@keyframes blt-particle-burst{0%{opacity:1;transform:translate(0,0) scale(1)}100%{opacity:0;transform:translate(var(--dx),var(--dy)) scale(0)}}',
  '.blt-note{position:fixed;box-sizing:border-box;background:#fffbe6;border:1px solid #eadfb0;border-radius:10px;box-shadow:0 6px 20px rgba(0,0,0,0.18);z-index:50;display:flex;flex-direction:column;overflow:hidden;pointer-events:auto}',
  '.blt-note.is-dragging{opacity:.95}',
  '.blt-note-head{display:flex;align-items:center;gap:6px;padding:6px 8px;background:rgba(0,0,0,0.04);cursor:move;-webkit-user-select:none;user-select:none}',
  '.blt-note-num{min-width:18px;height:18px;border-radius:9px;background:#e04545;color:#fff;font-size:11px;display:flex;align-items:center;justify-content:center;padding:0 5px}',
  '.blt-note-title{flex:1;font-size:12px;color:#7a6a2f;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}',
  '.blt-note-del{width:18px;height:18px;border:none;border-radius:50%;background:transparent;color:#c08899;cursor:pointer;font-size:14px;line-height:1;padding:0}',
  '.blt-note-del:hover{background:rgba(224,69,69,0.15);color:#e04545}',
  '.blt-note-body{flex:1;width:100%;box-sizing:border-box;border:none;outline:none;resize:none;background:transparent;padding:8px;font:13px/1.5 "PingFang SC","Microsoft YaHei",sans-serif;color:#3a3320}',
  '.blt-note.is-minimized .blt-note-body{display:none}',
  '.blt-note.is-editing .blt-note-body{background:rgba(255,255,255,0.6)}'
].join('\n'));

// ===================== 3. DOM build (§1/§4) =====================
const root = document.createElement('div');
root.id = 'bianlitie-root';

const fabRing = document.createElement('div');
fabRing.className = 'blt-ring';
fabRing.style.width = RS + 'px';   // 负值 → 无效 → 不可见（照字面公式）
fabRing.style.height = RS + 'px';

const fab = document.createElement('div');
fab.className = 'blt-fab';
fab.setAttribute('role', 'button');
fab.setAttribute('aria-label', '便利贴');
fab.setAttribute('tabindex', '-1');

const ripple = document.createElement('div');
ripple.className = 'blt-ripple';
fab.appendChild(ripple);

const fabClose = document.createElement('div');
fabClose.className = 'blt-close';
fabClose.setAttribute('title', '长按2秒关闭全部便利贴');
const closeRipple = document.createElement('div');
closeRipple.className = 'blt-close-ripple';
fabClose.appendChild(closeRipple);

root.appendChild(fabRing);
root.appendChild(fab);
root.appendChild(fabClose);
(document.body || document.documentElement).appendChild(root);

// 初始弹出动画
fab.classList.add('initial-pop');
on(fab, 'animationend', () => fab.classList.remove('initial-pop'), { once: true });

// ===================== 4. position (§3/§9) =====================
let fabX, fabY;
if (data.fab && Number.isFinite(data.fab.left) && Number.isFinite(data.fab.top)) {
  fabX = clamp(data.fab.left, 0, Math.max(0, window.innerWidth - FS));
  fabY = clamp(data.fab.top, 0, Math.max(0, window.innerHeight - FS));
} else {
  fabX = window.innerWidth - FS - 28 - 12; // = innerWidth - 84
  fabY = 30;
}
const placeBall = (x, y) => {
  fab.style.left = x + 'px';
  fab.style.top = y + 'px';
  fabClose.style.left = (x + FS + FCG) + 'px';
  fabClose.style.top = (y - 10) + 'px';
  fabRing.style.left = (x - RP) + 'px';
  fabRing.style.top = (y - RP) + 'px';
};
placeBall(fabX, fabY);

// ===================== 5. drag system (§9) =====================
const makeDraggable = (el, opts) => {
  opts = opts || {};
  const onMove = opts.onMove, onEnd = opts.onEnd, isInteractive = opts.isInteractive;
  let drag = false, sx = 0, sy = 0, ol = 0, ot = 0, moved = false;
  const mine = [];
  const add = (t, ev, fn, o) => { try { t.addEventListener(ev, fn, o); } catch (e) {} mine.push([t, ev, fn, o]); };

  const down = (e) => {
    if (isInteractive && isInteractive(e)) return;
    const p = pickPt(e);
    const r = el.getBoundingClientRect();
    sx = p.x; sy = p.y; ol = r.left; ot = r.top;
    drag = true; moved = false;
    el.classList.add('is-dragging');
    if (e.preventDefault) e.preventDefault();
  };
  const move = (e) => {
    if (!drag) return;
    const p = pickPt(e);
    const dx = p.x - sx, dy = p.y - sy;
    if (Math.abs(dx) + Math.abs(dy) > 3) moved = true;
    if (onMove) onMove(dx, dy, ol, ot);
  };
  const up = () => {
    if (!drag) return;
    drag = false;
    el.classList.remove('is-dragging');
    if (onEnd) onEnd(moved);
  };

  add(el, 'mousedown', down);
  add(el, 'touchstart', down, { passive: false });
  add(document, 'mousemove', move);
  add(document, 'touchmove', move, { passive: false });
  add(document, 'mouseup', up);
  add(document, 'touchend', up);
  // 阻止拖拽后误触发 click
  add(el, 'click', (e) => { if (moved) { e.stopPropagation(); e.preventDefault(); moved = false; } }, true);

  const destroy = () => { mine.forEach(([t, ev, fn, o]) => { try { t.removeEventListener(ev, fn, o); } catch (e) {} }); mine.length = 0; };
  el.__bltDragDestroy = destroy;
  cleanups.push(destroy);
  return { hadMove: () => moved, reset: () => { moved = false; }, destroy };
};

// ===================== 6. particles (§5) =====================
const createBurst = (centerX, centerY, count, color, size) => {
  for (let i = 0; i < count; i++) {
    const angle = Math.random() * 2 * Math.PI;
    const distance = 50 + Math.random() * 80; // 50 ~ 130
    const dx = Math.cos(angle) * distance;
    const dy = Math.sin(angle) * distance;
    const p = document.createElement('div');
    p.className = 'blt-particle' + (color === RBCL ? ' red' : (color === '#ffffff' ? ' white' : ''));
    p.style.left = (centerX - size / 2) + 'px';
    p.style.top = (centerY - size / 2) + 'px';
    p.style.width = size + 'px';
    p.style.height = size + 'px';
    p.style.background = color;
    p.style.setProperty('--dx', dx + 'px');
    p.style.setProperty('--dy', dy + 'px');
    (document.body || document.documentElement).appendChild(p);
    p.addEventListener('animationend', () => p.remove());
  }
};

const flashWhite = (el, times) => {
  const r = el.getBoundingClientRect();
  const cx = r.left + r.width / 2, cy = r.top + r.height / 2;
  for (let i = 0; i < times; i++) {
    setTimeout(() => {
      const d = document.createElement('div');
      d.className = 'blt-particle white';
      d.style.width = '30px'; d.style.height = '30px';
      d.style.left = (cx - 15) + 'px'; d.style.top = (cy - 15) + 'px';
      d.style.background = '#fff';
      d.style.animation = 'blt-particle-burst 0.4s ease-out forwards';
      d.style.setProperty('--dx', '0px');
      d.style.setProperty('--dy', '0px');
      (document.body || document.documentElement).appendChild(d);
      d.addEventListener('animationend', () => d.remove());
    }, i * 500);
  }
};

// ===================== 7. state machine (§7) =====================
let bS = false;      // 批量最小化激活
let sS = false;      // 已收纳
let bN = [];         // 批量快照
let exSeq = false;   // 退出序列执行中

const noteRefs = new Map();

// ===================== 8. notes render (§15.13) =====================
const updateNumber = (id) => {
  const el = noteRefs.get(id);
  if (!el) return;
  const idx = data.notes.findIndex((n) => n.id === id);
  const badge = el.querySelector('.blt-note-num');
  if (badge) badge.textContent = idx >= 0 ? String(idx + 1) : '';
};
const updateAllNumbers = () => { data.notes.forEach((n) => updateNumber(n.id)); };
const saveNotePos = (nd, el) => {
  const r = el.getBoundingClientRect();
  nd.left = r.left; nd.top = r.top; nd.w = r.width; nd.h = r.height;
};

const renderNote = (nd) => {
  if (noteRefs.has(nd.id)) return noteRefs.get(nd.id);
  const el = document.createElement('div');
  el.className = 'blt-note';
  el.dataset.id = nd.id;
  el.style.left = (nd.left != null ? nd.left : 200) + 'px';
  el.style.top = (nd.top != null ? nd.top : 200) + 'px';
  el.style.width = (nd.w || 220) + 'px';
  el.style.height = (nd.h || 160) + 'px';
  if (nd.scale && nd.scale !== 1) el.style.transform = 'scale(' + nd.scale + ')';

  const head = document.createElement('div');
  head.className = 'blt-note-head';
  const num = document.createElement('span');
  num.className = 'blt-note-num';
  const title = document.createElement('span');
  title.className = 'blt-note-title';
  title.textContent = nd.title || '便利贴';
  const del = document.createElement('button');
  del.className = 'blt-note-del';
  del.type = 'button';
  del.textContent = '\u00d7';
  del.title = '删除';
  head.appendChild(num); head.appendChild(title); head.appendChild(del);

  const body = document.createElement('textarea');
  body.className = 'blt-note-body';
  body.value = nd.text || '';
  body.placeholder = '写点什么…';

  el.appendChild(head); el.appendChild(body);
  root.appendChild(el);
  noteRefs.set(nd.id, el);
  updateNumber(nd.id);

  // 头部拖拽
  makeDraggable(head, {
    isInteractive: (e) => !!(e.target && e.target.closest && e.target.closest('.blt-note-del')),
    onMove: (dx, dy, ol, ot) => {
      el.style.left = clamp(ol + dx, 0, window.innerWidth - 40) + 'px';
      el.style.top = clamp(ot + dy, 0, window.innerHeight - 30) + 'px';
    },
    onEnd: (moved) => { if (moved) { saveNotePos(nd, el); save(); } }
  });

  body.readOnly = false;
  on(body, 'input', () => { nd.text = body.value; saveDeb(); });
  on(body, 'focus', () => el.classList.add('is-editing'));
  on(body, 'blur', () => { el.classList.remove('is-editing'); saveNotePos(nd, el); save(); });

  on(del, 'click', (e) => {
    e.stopPropagation();
    el.remove(); noteRefs.delete(nd.id);
    data.notes = data.notes.filter((x) => x.id !== nd.id);
    save(); updateAllNumbers();
  });

  return el;
};

const addNote = () => {
  if (exSeq) return;
  const w = 220, h = 160;
  const left = clamp(window.innerWidth / 2 - w / 2 + (Math.random() * 80 - 40), 10, Math.max(10, window.innerWidth - w - 10));
  const top = clamp(window.innerHeight / 2 - h / 2 + (Math.random() * 80 - 40), 10, Math.max(10, window.innerHeight - h - 10));
  const nd = {
    id: uid(), text: '', images: [], tables: [], title: '',
    left, top, w, h, scale: 1, minimized: false, createdAt: Date.now(), timer: null, gridScroll: 0
  };
  data.notes.push(nd);
  renderNote(nd);
  save();
  updateAllNumbers();
};

// 恢复已存便签
data.notes.forEach((nd) => renderNote(nd));
updateAllNumbers();

// ===================== 9. 主球交互 (§6.1/§6.3) =====================
let hideTimer = null;
const showClose = () => {
  if (hideTimer) { clearTimeout(hideTimer); hideTimer = null; }
  fabClose.classList.add('visible');
  fabRing.style.opacity = '0';
};
const scheduleHide = () => {
  if (hideTimer) clearTimeout(hideTimer);
  hideTimer = setTimeout(() => {
    hideTimer = null;
    fabClose.classList.remove('visible');
    fabRing.style.opacity = '1';
  }, 1000);
};
on(fab, 'mouseenter', showClose);
on(fab, 'mouseleave', scheduleHide);
on(fabClose, 'mouseenter', () => { if (hideTimer) { clearTimeout(hideTimer); hideTimer = null; } });
on(fabClose, 'mouseleave', scheduleHide);

// 拖拽
const fabDrag = makeDraggable(fab, {
  onMove: (dx, dy, ol, ot) => {
    fabX = clamp(ol + dx, 0, Math.max(0, window.innerWidth - FS));
    fabY = clamp(ot + dy, 0, Math.max(0, window.innerHeight - FS));
    placeBall(fabX, fabY);
  },
  onEnd: (moved) => { if (moved) { data.fab = { left: fabX, top: fabY }; save(); } }
});

// 长按退出（800ms 判定 + extra）
let lpa = false, lpdt = null, lpt = null;
const resetFab = () => {
  lpa = false;
  ripple.classList.remove('active');
  fabClose.classList.remove('vanish');
  fabClose.style.display = '';
  fab.style.transform = '';
};
const startLP = (e) => {
  if (exSeq) return;
  lpa = false;
  lpdt = setTimeout(() => {
    lpdt = null;
    if (fabDrag.hadMove()) return;
    lpa = true;
    ripple.classList.add('active');
    ripple.style.setProperty('--ripple-duration', '1.5s');
    fabClose.classList.add('vanish');
    setTimeout(() => { fabClose.style.display = 'none'; }, 400);
    fab.style.transform = 'scale(1)';
    const extraDelay = data.notes.length > 0 ? 3000 : 1200;
    lpt = setTimeout(() => { lpt = null; exitSequence(); }, 800 + extraDelay);
  }, 800);
  if (e && e.preventDefault) e.preventDefault();
};
const endLP = () => {
  if (lpdt) { clearTimeout(lpdt); lpdt = null; }
  if (lpa) resetFab();
};
on(fab, 'mousedown', startLP);
on(fab, 'touchstart', startLP, { passive: false });
on(fab, 'mouseup', endLP);
on(fab, 'touchend', endLP);
// 移动即取消长按
on(document, 'mousemove', () => { if (lpdt && fabDrag.hadMove()) { clearTimeout(lpdt); lpdt = null; } });

// 单击
on(fab, 'click', () => {
  if (exSeq) return;
  if (fabDrag.hadMove()) { fabDrag.reset(); return; }
  fab.classList.add('tap-flash');
  setTimeout(() => fab.classList.remove('tap-flash'), 80);
  addNote();
}, true);

// ===================== 10. 关闭按钮交互 (§6.2) =====================
let pressTimer = null, fcpt = null, fcMoved = false, fcLongFired = false, fcSx = 0, fcSy = 0;
const startPress = (e) => {
  if (exSeq) return;
  if (e && e.preventDefault) e.preventDefault();
  const p = pickPt(e); fcSx = p.x; fcSy = p.y;
  fcMoved = false; fcLongFired = false;
  pressTimer = setTimeout(() => {
    pressTimer = null;
    if (fcMoved) return;
    fcLongFired = true;
    // 视觉反馈
    if (bS === true) {
      fabClose.style.transition = 'transform 2s ease';
      fabClose.style.transform = 'scale(1.8)';
    } else {
      fabClose.style.transition = 'transform 2s ease-in';
      fabClose.style.transform = 'scale(0.6)';
    }
    closeRipple.classList.add('long');
    // 再等 2000ms（总计 4s）
    fcpt = setTimeout(() => {
      fcpt = null;
      fabClose.style.transition = '';
      fabClose.style.transform = '';
      closeRipple.classList.remove('long');
      if (bS && !sS) {
        stashAllNotes();
      } else if (!bS && !sS) {
        const r = fabClose.getBoundingClientRect();
        createBurst(r.left + r.width / 2, r.top + r.height / 2, RBC, RBCL, RBS);
        noteRefs.forEach((el) => el.remove());
        noteRefs.clear();
        data.notes = [];
        save();
        bS = false; sS = false; bN = [];
        fabClose.classList.remove('visible');
      }
    }, 2000);
  }, 2000);
};
const movePress = (e) => {
  if (!pressTimer && !fcpt) return;
  const p = pickPt(e);
  if (Math.abs(p.x - fcSx) + Math.abs(p.y - fcSy) > 5) {
    fcMoved = true;
    if (pressTimer) { clearTimeout(pressTimer); pressTimer = null; }
  }
};
const endPress = () => {
  if (pressTimer) { clearTimeout(pressTimer); pressTimer = null; }
  if (!fcMoved && !fcLongFired) {
    fabClose.classList.add('tap-elastic');
    setTimeout(() => fabClose.classList.remove('tap-elastic'), 350);
    if (sS === true) unstashAllNotes();
    else toggleBulkMinimize();
  }
  fcMoved = false; fcLongFired = false;
};
on(fabClose, 'mousedown', startPress);
on(fabClose, 'touchstart', startPress, { passive: false });
on(document, 'mousemove', movePress);
on(document, 'touchmove', movePress, { passive: false });
on(fabClose, 'mouseup', endPress);
on(fabClose, 'touchend', endPress);

// ===================== 11. 批量最小化 / 收纳 / 释放 (§7.1-7.3) =====================
const restoreSnapshot = () => {
  bN.forEach((s) => {
    const nd = data.notes.find((n) => n.id === s.id);
    const el = noteRefs.get(s.id);
    if (nd) { nd.minimized = s.minimized; nd.scale = s.scale; nd.left = s.left; nd.top = s.top; nd.w = s.width; nd.h = s.height; }
    if (el) {
      el.classList.remove('is-minimized');
      el.style.left = s.left + 'px'; el.style.top = s.top + 'px';
      el.style.width = s.width + 'px'; el.style.height = s.height + 'px';
      el.style.transform = (s.scale && s.scale !== 1) ? 'scale(' + s.scale + ')' : '';
      const ta = el.querySelector('.blt-note-body');
      if (ta) ta.readOnly = false;
    }
  });
  bN = []; bS = false;
  fabClose.classList.remove('bulk-active');
};

const toggleBulkMinimize = () => {
  if (exSeq) return;
  if (bS === false) {
    const els = [];
    noteRefs.forEach((el, id) => {
      const nd = data.notes.find((n) => n.id === id);
      if (nd && !nd.minimized) els.push({ el, nd });
    });
    bN = els.map(({ el, nd }) => {
      const r = el.getBoundingClientRect();
      return { id: nd.id, left: nd.left, top: nd.top, width: nd.w, height: nd.h, scale: nd.scale, minimized: nd.minimized };
    });
    els.forEach(({ el, nd }) => {
      nd.minimized = true;
      el.classList.add('is-minimized');
      el.style.width = 'auto';
      el.style.height = '38px';
      const ta = el.querySelector('.blt-note-body');
      if (ta) ta.readOnly = true;
      el.classList.remove('is-editing');
    });
    silentSave();
    requestAnimationFrame(() => {
      const fr = fabClose.getBoundingClientRect();
      const startTop = fr.bottom + 66;
      const gap = 20;
      els.forEach(({ el }, k) => {
        const noteWidth = el.offsetWidth;
        el.style.left = (fr.right - noteWidth - 18) + 'px';
        el.style.top = (startTop + k * (38 + gap)) + 'px';
      });
    });
    bS = true;
    fabClose.classList.add('bulk-active');
    fabClose.title = '单击恢复标签原始位置 · 长按收纳全部';
  } else {
    restoreSnapshot();
    silentSave();
    fabClose.title = '长按2秒关闭全部便利贴';
  }
  updateAllNumbers();
};

const stashAllNotes = () => {
  if (bS && bN.length) restoreSnapshot();
  // 保存当前位置
  data.notes.forEach((nd) => {
    const el = noteRefs.get(nd.id);
    if (el) { const r = el.getBoundingClientRect(); nd.left = r.left; nd.top = r.top; }
  });
  save();

  if (data.notes.length === 0) {
    fabClose.classList.add('stash-active');
    fabClose.classList.remove('bulk-active');
    fabClose.title = '单击释放被收纳的标签';
    sS = true;
    return;
  }

  const fr = fabClose.getBoundingClientRect();
  const targetCX = fr.left + fr.width / 2, targetCY = fr.top + fr.height / 2;
  let pending = 0;
  const afterAll = () => {
    fabClose.classList.add('stash-active');
    fabClose.classList.remove('bulk-active');
    fabClose.title = '单击释放被收纳的标签';
    sS = true;
  };
  data.notes.slice().forEach((nd) => {
    const el = noteRefs.get(nd.id);
    if (!el) return;
    const r = el.getBoundingClientRect();
    const dx = targetCX - (r.left + r.width / 2);
    const dy = targetCY - (r.top + r.height / 2);
    pending++;
    const anim = el.animate([
      { transform: 'translate(0,0) scale(1)', opacity: 1 },
      { transform: 'translate(' + dx + 'px, ' + dy + 'px) scale(0)', opacity: 0 }
    ], { duration: 800, easing: 'ease-in', fill: 'forwards' });
    anim.onfinish = () => {
      el.remove(); noteRefs.delete(nd.id);
      pending--;
      if (pending === 0) afterAll();
    };
  });
  if (pending === 0) afterAll();
};

const unstashAllNotes = () => {
  const sorted = data.notes.slice().sort((a, b) => (a.createdAt || 0) - (b.createdAt || 0));
  sorted.forEach((nd) => renderNote(nd));
  updateAllNumbers();
  fabClose.classList.remove('stash-active');
  bS = false; sS = false; bN = [];
  fabClose.title = '长按2秒关闭全部便利贴';
};

// ===================== 12. 退出序列 (§8) =====================
const exitSequence = () => {
  if (exSeq) return;
  exSeq = true;
  if (data.notes.length > 0) exitFancySequence();
  else exitEmptySequence();
};

const exitEmptySequence = () => {
  const r = fab.getBoundingClientRect();
  const cx = r.left + r.width / 2, cy = r.top + r.height / 2;
  fab.style.transition = 'transform 0.8s ease-in, opacity 0.8s ease-in';
  fab.style.transform = 'scale(0.06)';
  fab.style.opacity = '1';
  setTimeout(() => {
    fab.style.transition = 'none';
    injectStyle('bianlitie-empty-breathe', '@keyframes empty-breathe{0%,100%{box-shadow:0 0 4px 1px rgba(255,255,255,0)}50%{box-shadow:0 0 14px 4px rgba(255,255,255,0.9)}}');
    fab.style.animation = 'empty-breathe 1s infinite ease-in-out';
    setTimeout(() => {
      createBurst(cx, cy, BBC, BBCC, BBS);
      setTimeout(() => cleanupAll(), 550);
    }, 560);
  }, 800);
};

const exitFancySequence = () => {
  const sorted = data.notes.slice().sort((a, b) => (a.createdAt || 0) - (b.createdAt || 0));
  const els = sorted.map((nd) => noteRefs.get(nd.id)).filter(Boolean);
  if (els.length === 0) { cleanupAll(); return; }

  fab.style.pointerEvents = 'none';
  injectStyle('bianlitie-breath', '@keyframes blt-breath{0%,100%{box-shadow:0 0 4px 1px rgba(255,255,255,0.7)}50%{box-shadow:0 0 12px 3px rgba(255,255,255,1)}}.blt-breath{animation:blt-breath 1s infinite ease-in-out}');

  const BALL_SIZE = Math.round(FS * 0.05); // 2
  const SHRINK_DURATION = 1000;
  const STAGGER = 600;
  const ballElements = [];
  const n = els.length;

  els.forEach((el, idx) => {
    el.classList.remove('is-minimized', 'is-editing');
    Array.prototype.forEach.call(el.children, (c) => { c.style.visibility = 'hidden'; });
    const r = el.getBoundingClientRect();
    const startLeft = r.left, startTop = r.top, startW = r.width, startH = r.height;
    const centerX = startLeft + startW / 2, centerY = startTop + startH / 2;
    const endLeft = centerX - BALL_SIZE / 2, endTop = centerY - BALL_SIZE / 2;

    el.style.position = 'fixed';
    el.style.background = '#000';
    el.style.borderRadius = '10px';
    el.style.border = 'none';
    el.style.boxShadow = 'none';
    el.style.filter = 'none';
    el.style.overflow = 'hidden';

    const anim = el.animate([
      { left: startLeft + 'px', top: startTop + 'px', width: startW + 'px', height: startH + 'px', borderRadius: '10px', boxShadow: '0 0 0px rgba(255,255,255,0)', offset: 0 },
      { left: (centerX - startW * 0.35) + 'px', top: (centerY - startH * 0.35) + 'px', width: (startW * 0.7) + 'px', height: (startH * 0.7) + 'px', borderRadius: '18px', boxShadow: '0 0 15px 3px rgba(255,255,255,0.7)', offset: 0.25 },
      { left: (centerX - BALL_SIZE / 2) + 'px', top: (centerY - BALL_SIZE / 2) + 'px', width: BALL_SIZE + 'px', height: BALL_SIZE + 'px', borderRadius: '50%', boxShadow: '0 0 20px 5px rgba(255,255,255,1)', offset: 0.6 },
      { left: endLeft + 'px', top: endTop + 'px', width: BALL_SIZE + 'px', height: BALL_SIZE + 'px', borderRadius: '50%', boxShadow: '0 0 8px 2px rgba(255,255,255,0.6)', offset: 1 }
    ], { duration: SHRINK_DURATION, delay: idx * STAGGER, fill: 'forwards', easing: 'cubic-bezier(0.25, 0.1, 0.25, 1)' });

    anim.onfinish = () => {
      el.classList.add('blt-breath');
      makeDraggable(el, {
        onMove: (dx, dy, ol, ot) => { el.style.left = (ol + dx) + 'px'; el.style.top = (ot + dy) + 'px'; }
      });
      ballElements.push(el);
    };
  });

  const totalDelay = (n - 1) * STAGGER + SHRINK_DURATION;
  setTimeout(() => {
    setTimeout(() => {
      ballElements.forEach((el) => {
        el.classList.remove('blt-breath');
        el.style.animation = 'none';
        el.style.cursor = 'default';
        if (typeof el.__bltDragDestroy === 'function') { el.__bltDragDestroy(); }
      });
      fab.style.transition = 'none';
      fab.style.transform = 'scale(1)';
      fab.style.opacity = '1';

      setTimeout(() => {
        const targets = ballElements.concat([fab]);
        targets.forEach((t) => flashWhite(t, 2)); // 每个闪白 2 次（间隔 500ms）
        setTimeout(() => {
          targets.forEach((t) => {
            t.style.transition = 'transform 0.6s, opacity 0.6s';
            t.style.transform = 'scale(0)';
            t.style.opacity = '0';
          });
          setTimeout(() => cleanupAll(), 800);
        }, 1000);
      }, 50);
    }, 1200);
  }, totalDelay);
};

// ===================== 13. 清理 (§8.3) =====================
const cleanupAll = () => {
  try { document.querySelectorAll('.blt-particle').forEach((p) => p.remove()); } catch (e) {}
  noteRefs.forEach((el) => { try { el.remove(); } catch (e) {} });
  noteRefs.clear();
  try { if (fab && fab.remove) fab.remove(); } catch (e) {}
  try { if (fabClose && fabClose.remove) fabClose.remove(); } catch (e) {}
  try { if (fabRing && fabRing.remove) fabRing.remove(); } catch (e) {}
  try { root.remove(); } catch (e) {}
  const st = document.getElementById(STYLE_ID); if (st) st.remove();
  cleanupInjectedStyles();
  cleanups.forEach((fn) => { try { fn(); } catch (e) {} });
  cleanups.length = 0;
  try { delete window['__' + NS]; } catch (e) { try { window['__' + NS] = undefined; } catch (e2) {} }
  try {
    console.log('%c[便利贴]%c 已退出',
      'color:#fff;background:#000;padding:2px 6px;border-radius:4px;font-weight:bold',
      'color:#e04545;font-weight:bold');
  } catch (e) {}
};

// ===================== 14. controller + result =====================
window['__' + NS] = { cleanup: cleanupAll, version: '2.7.2-replica', state: () => ({ bS, sS, exSeq, notes: data.notes.length }) };

const ok = !!document.getElementById('bianlitie-root') && !!document.querySelector('.blt-fab');
return {
  ok,
  summary: '便利贴悬浮球已注入：#bianlitie-root + 主球(44px 右上角)/关闭球/外环/涟漪/粒子；单击建便签、悬停显示关闭球、长按退出。',
  changed_count: data.notes.length,
  data: {
    ns: NS,
    fs: FS, fcG: FCG, rp: RP, rs: RS,
    storage_key: STORAGE_KEY,
    fab_left: fabX, fab_top: fabY,
    notes: data.notes.length,
    restored_position: !!data.fab,
    root_present: !!document.getElementById('bianlitie-root'),
    fab_present: !!document.querySelector('.blt-fab'),
    close_present: !!document.querySelector('.blt-close'),
    ring_present: !!document.querySelector('.blt-ring')
  },
  warnings: []
};

})();
