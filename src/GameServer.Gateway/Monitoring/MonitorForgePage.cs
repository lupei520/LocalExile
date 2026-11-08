/// <summary>
/// The forge's page (Monitoring/MonitorForge.cs): the crafting simulator at <c>/forge</c>. The middle column is the item
/// slot — the item as it is being composed, drawn the way the client draws it (the rarity's colour, the client's own
/// affix sentences) — with the bases to its left and the affixes that base may roll to its right.
/// </summary>
/// <remarks>
/// One page, no build step: it asks the server for a base's affixes once (<c>/forge/base</c>), filters and adds them
/// locally, and only calls the server again to preview the composed item's card or to place it. The affix texts are the
/// client's own, rendered by the server with each stat's roll range, so what a row says is what the item will say.
/// </remarks>
static class MonitorForgePage
{
    public const string Html = """
<!doctype html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>LocalExile 做装台</title>
<style>
  :root { color-scheme: dark; --bg:#14161a; --panel:#171b22; --slot:#0f1115; --line:#232833; --ink:#d8dee9;
          --dim:#7b8494; --gold:#c8b06a; --rare:#ffff77; --magic:#8888ff; --unique:#af6025; --normal:#c8c8c8; }
  * { box-sizing: border-box; }
  body { margin:0; height:100vh; display:flex; flex-direction:column; background:var(--bg); color:var(--ink);
         font:14px/1.5 "Segoe UI", "Microsoft YaHei", sans-serif; }
  header { display:flex; align-items:center; gap:10px; flex-wrap:wrap; padding:10px 16px; border-bottom:1px solid var(--line); }
  header h1 { font-size:16px; margin:0; font-weight:600; }
  header .sp { flex:1; }
  a, .link { color:#6ea8d8; text-decoration:none; font-size:12px; }
  a:hover { text-decoration:underline; }
  input, select, button { background:#1b1f27; color:var(--ink); border:1px solid #2a303c; border-radius:6px;
                          padding:5px 8px; font:13px "Segoe UI", "Microsoft YaHei", sans-serif; }
  input:focus, select:focus { outline:1px solid #3a4d63; }
  button { cursor:pointer; }
  button:hover { background:#232935; }
  button.primary { background:#2c6e4a; border-color:#37865a; color:#eaf3ea; }
  button.primary:hover { background:#37865a; }
  button.ghost { background:#1b1f27; }
  .dim { color:var(--dim); font-size:12px; }
  main { flex:1; min-height:0; display:grid; grid-template-columns:280px minmax(380px,1fr) 400px; gap:12px; padding:12px 16px 16px; }
  .col { min-height:0; display:flex; flex-direction:column; gap:8px; }
  .col h2 { font-size:12px; margin:0; color:#9aa4b2; font-weight:600; letter-spacing:.4px; }
  .box { background:var(--panel); border:1px solid var(--line); border-radius:8px; overflow:auto; min-height:0; flex:1; }
  .fills { display:flex; gap:6px; flex-wrap:wrap; }
  .fills input, .fills select { flex:1; min-width:74px; }
  /* The middle column is the item slot: an inventory cell with the item drawn inside it. */
  .slotwrap { flex:1; min-height:0; display:flex; flex-direction:column; gap:8px; }
  .slot { position:relative; flex:1; min-height:240px; overflow:auto; padding:16px 18px 14px;
          background:linear-gradient(180deg,#101318,#0d1015); border:1px solid #2a303c; border-radius:10px;
          box-shadow:inset 0 0 40px #00000066; }
  .slot::after { content:""; position:absolute; inset:7px; border:1px dashed #262c37; border-radius:8px; pointer-events:none; }
  .slot .cell { position:absolute; right:12px; top:10px; font-size:11px; color:#5d6675; }
  .card { position:relative; z-index:1; max-width:520px; margin:0 auto; }
  .card .name { font-size:16px; font-weight:600; text-align:center; }
  .card .base { text-align:center; font-size:12px; color:var(--dim); margin-top:2px; }
  .card .sep { height:1px; background:linear-gradient(90deg,transparent,#2f3644,transparent); margin:9px 0; }
  .card .line { display:flex; align-items:center; gap:7px; font-size:13px; min-height:24px; }
  .card .line .txt { flex:1; }
  .card .enchant { color:#b4b4ff; } .card .implicit { color:#8888ff; } .card .explicit { color:#8888ff; }
  .card .crafted { color:#b4b4ff; } .card .fractured { color:#a29162; } .card .facts { color:var(--dim); font-size:12px; text-align:center; }
  .card .props { text-align:center; font-size:12px; color:#9aa4b2; }
  .tag { display:inline-block; min-width:20px; padding:0 5px; border-radius:4px; background:#232833; color:var(--gold);
         font-size:11px; text-align:center; }
  .tag.pre { background:#25313f; color:#8fb8dd; } .tag.suf { background:#332b3f; color:#c39be0; }
  .tag.cr { background:#2b3038; color:#b4b4ff; }
  .x { cursor:pointer; color:#6b7385; border:0; background:none; padding:0 4px; font-size:14px; }
  .x:hover { color:#b8564d; background:none; }
  .val { width:62px; padding:1px 4px; font-size:12px; text-align:right; }
  .val.over { border-color:#b8564d; color:#e0a458; }
  .empty { color:#6b7385; padding:14px; font-size:13px; text-align:center; }
  .brow { display:flex; align-items:center; gap:8px; padding:5px 9px; border-bottom:1px solid #1e232c; cursor:pointer;
          font-size:13px; }
  .brow:hover { background:#1d222b; }
  .brow.on { background:#22303d; }
  .brow .nm { flex:1; }
  .brow .cl { color:var(--dim); font-size:11px; }
  .arow { display:grid; grid-template-columns:26px 1fr 52px 30px; gap:6px; align-items:center; padding:5px 8px;
          border-bottom:1px solid #1e232c; font-size:13px; }
  .arow:hover { background:#1d222b; }
  .arow .lv { color:var(--dim); font-size:11px; text-align:right; }
  .arow .ad { justify-self:end; }
  .arow.off { opacity:.38; }
  .arow.off .ad { cursor:not-allowed; }
  .arow .ad { background:#26313d; border:1px solid #33404f; border-radius:5px; width:24px; height:22px; padding:0;
              line-height:20px; text-align:center; }
  .tabs { display:flex; gap:6px; }
  .tabs button { flex:1; font-size:12px; padding:4px 6px; }
  .tabs button.on { background:#2b3644; border-color:#3a4d63; }
  .msg { font-size:12px; min-height:18px; }
  .ok { color:#4d9a63; } .bad { color:#d08a7a; }
  .banner { font-size:11px; color:#8fb8dd; text-align:center; margin-bottom:6px; }
  .scroll { flex:1; min-height:0; overflow:auto; }
</style>
</head>
<body>
<header>
  <h1>LocalExile 做装台</h1>
  <span class="dim">词缀按该底子自己的原版词缀池 · 前缀后缀照稀有度上限 · 成品进「市场」页</span>
  <span class="sp"></span>
  <a href="/">← 返回监测</a>
  <input id="token" type="password" placeholder="密码 = Game_MONITOR_ADMIN_TOKEN" style="width:230px">
  <button id="loadAccounts" class="ghost">载入账号</button>
  <input id="account" list="accounts" placeholder="发给哪个账号" style="width:150px">
  <datalist id="accounts"></datalist>
</header>
<main>
  <section class="col">
    <h2>底子</h2>
    <div class="fills">
      <select id="fclass" title="类别"></select>
      <input id="bsearch" placeholder="搜底子名" spellcheck="false">
    </div>
    <div id="baselist" class="box"><div class="empty">载入中…</div></div>
    <div id="baseinfo" class="dim"></div>
  </section>

  <section class="col">
    <h2>物品槽</h2>
    <div class="slotwrap">
      <div class="slot" id="slot"><div class="empty">← 先在左边挑一个底子</div></div>
      <div class="fills">
        <select id="rarity" title="稀有度">
          <option value="rare">稀有</option>
          <option value="magic">魔法</option>
          <option value="normal">普通</option>
          <option value="unique">传奇</option>
        </select>
        <input id="ilvl" type="number" value="84" min="1" max="100" title="物品等级：决定能出哪些词缀">
        <input id="quality" type="number" value="20" min="0" max="100" title="品质">
        <input id="sockets" placeholder="孔位，如 R-R-G" spellcheck="false" title="和 /sockets 一样；留空按底子默认">
        <input id="states" placeholder="状态，如 corrupted, shaper" spellcheck="false"
               title="corrupted, mirrored, split, synthesised, shaper, elder, crusader, redeemer, hunter, warlord">
      </div>
      <div class="fills">
        <button id="preview" class="ghost">预览（服务器）</button>
        <button id="give" class="primary">发放给账号</button>
        <button id="clear" class="ghost">清空词缀</button>
      </div>
      <div id="res" class="msg"></div>
    </div>
  </section>

  <section class="col">
    <h2>词缀</h2>
    <div class="tabs">
      <button id="tabPrefix" data-tab="prefix" class="on">前缀 <span class="dim" id="cPrefix"></span></button>
      <button id="tabSuffix" data-tab="suffix">后缀 <span class="dim" id="cSuffix"></span></button>
      <button id="tabCrafted" data-tab="crafted">工艺</button>
    </div>
    <input id="asearch" placeholder="搜词缀，如 maximum Life" spellcheck="false">
    <div id="affixes" class="box"><div class="empty">先挑底子</div></div>
  </section>
</main>
<script>
const $ = s => document.querySelector(s);
const esc = t => String(t).replace(/[&<>"]/g, c => ({ '&':'&amp;', '<':'&lt;', '>':'&gt;', '"':'&quot;' }[c]));
const RARITY = { normal:'#c8c8c8', magic:'#8888ff', rare:'#ffff77', unique:'#af6025' };
const S = { classes: [], bases: [], base: null, affixes: { prefix: [], suffix: [], crafted: [] }, caps: null,
            implicits: [], item: { rarity: 'rare', itemLevel: 84, quality: 20, sockets: '', states: [], mods: [] },
            tab: 'prefix', card: null, note: '' };
const token = () => $('#token').value;

function say(text, bad) { $('#res').innerHTML = '<span class="' + (bad ? 'bad' : 'ok') + '">' + esc(text) + '</span>'; }

// ── 底子列表 ───────────────────────────────────────────────────────────────
async function loadList() {
  let d;
  try { d = await (await fetch('/forge/list', { cache: 'no-store' })).json(); }
  catch (e) { $('#baselist').innerHTML = '<div class="empty">载入失败</div>'; return; }
  if (!d.ok) { $('#baselist').innerHTML = '<div class="empty">' + esc(d.error) + '</div>'; return; }
  S.classes = d.classes || []; S.bases = d.bases || [];
  $('#fclass').innerHTML = '<option value="">全部类别</option>'
    + S.classes.map(c => '<option value="' + esc(c.id) + '">' + esc(c.name) + '</option>').join('');
  renderBases();
}
function renderBases() {
  const want = $('#fclass').value, q = $('#bsearch').value.trim().toLowerCase();
  const list = S.bases.filter(b => (!want || b.class === want)
    && (!q || b.name.toLowerCase().includes(q) || b.id.toLowerCase().includes(q)));
  const shown = list.slice(0, 400);
  const current = S.base ? S.base.id : '';
  $('#baselist').innerHTML = shown.length
    ? shown.map(b => '<div class="brow' + (b.id === current ? ' on' : '') + '" data-id="' + esc(b.id) + '">'
        + '<span class="nm">' + esc(b.name) + '</span><span class="cl">' + esc(b.class) + '</span></div>').join('')
    : '<div class="empty">没有这个底子</div>';
  if (list.length > shown.length) $('#baselist').innerHTML += '<div class="empty">还有 ' + (list.length - shown.length) + ' 个，请再打几个字</div>';
  for (const row of document.querySelectorAll('#baselist .brow'))
    row.addEventListener('click', () => pickBase(row.dataset.id));
}
function pickBase(id) {
  if (S.base && S.base.id === id) return;
  S.item.mods = []; S.card = null;
  loadBase(id);
}
async function loadBase(id) {
  const ilvl = clamp(parseInt($('#ilvl').value, 10) || 84, 1, 100);
  $('#baseinfo').textContent = '载入词缀…';
  let d;
  try { d = await (await fetch('/forge/base?base=' + encodeURIComponent(id) + '&ilvl=' + ilvl, { cache: 'no-store' })).json(); }
  catch (e) { $('#baseinfo').textContent = '载入失败'; return; }
  if (!d.ok) { $('#baseinfo').innerHTML = '<span class="bad">' + esc(d.error) + '</span>'; return; }
  S.base = d.base; S.affixes = { prefix: d.prefixes || [], suffix: d.suffixes || [], crafted: d.crafted || [] };
  S.caps = d.caps; S.implicits = d.implicits || [];
  const req = (S.base.requirements || []).filter(v => v > 0);
  $('#baseinfo').textContent = S.base.name + ' · ' + S.base.className + ' · 掉落等级 ' + S.base.dropLevel
    + ' · 最大孔 ' + S.base.sockets + (req.length ? ' · 需求 ' + req.join('/') : '')
    + ' · 可出词缀 ' + S.affixes.prefix.length + '+' + S.affixes.suffix.length;
  renderBases(); renderAffixes(); renderItem();
}

// ── 词缀列表 ───────────────────────────────────────────────────────────────
function caps() {
  if (!S.caps) return { prefix: 3, suffix: 3 };
  const r = S.item.rarity;
  if (r === 'normal') return { prefix: 0, suffix: 0 };
  if (r === 'magic') return S.caps.magic;
  return S.caps.rare;
}
function counts() {
  let p = 0, s = 0;
  for (const m of S.item.mods) (m.generation === 1 ? p++ : s++);
  return { prefix: p, suffix: s };
}
function full() { const c = caps(), n = counts(); return { prefix: n.prefix >= c.prefix, suffix: n.suffix >= c.suffix }; }
function familyConflict(mod) {
  const have = new Set(), groups = new Set();
  for (const m of S.item.mods) {
    for (const f of m.families || []) have.add(f);
    if (m.group) groups.add(m.group);
  }
  return (mod.families || []).some(f => have.has(f)) || (mod.group && groups.has(mod.group));
}
function onItem(id) { return S.item.mods.some(m => m.id === id); }
function renderAffixes() {
  const c = caps(), n = counts();
  $('#cPrefix').textContent = n.prefix + '/' + c.prefix;
  $('#cSuffix').textContent = n.suffix + '/' + c.suffix;
  for (const id of ['tabPrefix', 'tabSuffix', 'tabCrafted'])
    $('#' + id).classList.toggle('on', $('#' + id).dataset.tab === S.tab);
  const q = $('#asearch').value.trim().toLowerCase();
  const list = (S.affixes[S.tab] || []).filter(a => !q || a.text.toLowerCase().includes(q) || (a.name || '').toLowerCase().includes(q));
  const shown = list.slice(0, 600);
  const busy = full();
  $('#affixes').innerHTML = shown.length
    ? shown.map(a => {
        const taken = onItem(a.id), conflict = !taken && familyConflict(a);
        const sideFull = a.generation === 1 ? busy.prefix : busy.suffix;
        const off = taken || conflict || sideFull || S.item.rarity === 'normal';
        const why = taken ? '已经在物品上' : conflict ? '同族或同一条词缀只能有一条' : sideFull ? '这一侧已满'
          : S.item.rarity === 'normal' ? '普通物品没有词缀' : '加上去';
        return '<div class="arow' + (off ? ' off' : '') + '" data-id="' + esc(a.id) + '" title="' + esc(why) + '">'
          + '<span class="tag' + (a.generation === 1 ? ' pre' : ' suf') + '">' + (a.tier ? 'T' + a.tier : '·') + '</span>'
          + '<span>' + esc(a.text) + '</span><span class="lv">Lv' + a.level + '</span>'
          + '<button class="ad"' + (off ? ' disabled' : '') + '>＋</button></div>';
      }).join('')
    : '<div class="empty">没有符合条件的词缀</div>';
  if (list.length > shown.length) $('#affixes').innerHTML += '<div class="empty">还有 ' + (list.length - shown.length) + ' 条</div>';
  for (const row of document.querySelectorAll('#affixes .arow')) {
    const button = row.querySelector('.ad');
    if (button && !button.disabled) button.addEventListener('click', () => addAffix(row.dataset.id));
  }
}
function findAffix(id) {
  for (const side of ['prefix', 'suffix', 'crafted']) for (const a of S.affixes[side]) if (a.id === id) return a;
  return null;
}
function addAffix(id) {
  const a = findAffix(id);
  if (!a || onItem(id) || familyConflict(a)) return;
  const values = (a.stats || []).map(s => s.max);
  S.item.mods.push({ id: a.id, generation: a.generation, name: a.name, text: a.text, stats: a.stats, families: a.families,
                     group: a.group, tier: a.tier, level: a.level, crafted: S.tab === 'crafted', values: values });
  S.card = null;
  renderItem(); renderAffixes();
}

// ── 物品槽 ─────────────────────────────────────────────────────────────────
function renderItem() {
  const slot = $('#slot');
  if (!S.base) { slot.innerHTML = '<div class="empty">← 先在左边挑一个底子</div>'; return; }
  if (S.card) { renderCard(); return; }
  const colour = RARITY[S.item.rarity] || '#d8dee9';
  const prefix = S.item.mods.filter(m => m.generation === 1);
  const suffix = S.item.mods.filter(m => m.generation === 2);
  const facts = ['物品等级 ' + S.item.itemLevel];
  if (S.item.quality > 0) facts.push('品质 +' + S.item.quality + '%');
  if (S.item.sockets) facts.push(S.item.sockets.split(/[-\s]+/).filter(Boolean).length + ' 孔（' + S.item.sockets + '）');
  for (const s of S.item.states) facts.push(s);
  const line = (m, i) => {
    const kind = m.crafted ? 'crafted' : 'explicit';
    const vals = (m.stats || []).map((s, k) =>
      '<input class="val" type="number" data-mod="' + i + '" data-k="' + k + '" value="' + (m.values[k] ?? s.max)
      + '" min="' + s.min + '" max="' + s.max + '" title="' + s.min + '–' + s.max + '">').join(' ');
    return '<div class="line"><span class="tag' + (m.generation === 1 ? ' pre' : ' suf') + '">'
      + (m.generation === 1 ? '前' : '后') + '</span>'
      + '<span class="tag">' + (m.tier ? 'T' + m.tier : '·') + '</span>'
      + '<span class="txt ' + kind + '">' + esc(m.text) + '</span>' + vals
      + '<button class="x" data-del="' + i + '" title="去掉">×</button></div>';
  };
  slot.innerHTML = '<div class="cell">' + S.base.width + '×' + S.base.height + '</div><div class="card">'
    + '<div class="name" style="color:' + colour + '">' + esc(S.base.name) + '</div>'
    + '<div class="base">' + esc(S.base.name) + ' · ' + esc(S.base.className) + '</div>'
    + '<div class="sep"></div><div class="props">' + esc(facts.join(' · ')) + '</div>'
    + (S.implicits.length ? '<div class="sep"></div>' + S.implicits.map(m =>
        '<div class="line"><span class="tag">隐</span><span class="txt implicit">' + esc(m.text) + '</span></div>').join('') : '')
    + (S.item.mods.length ? '<div class="sep"></div>'
        + prefix.map(m => line(m, S.item.mods.indexOf(m))).join('')
        + suffix.map(m => line(m, S.item.mods.indexOf(m))).join('') : '')
    + '<div class="sep"></div><div class="facts">前缀 ' + counts().prefix + '/' + caps().prefix
    + ' · 后缀 ' + counts().suffix + '/' + caps().suffix + (S.note ? ' · ' + esc(S.note) : '') + '</div></div>';
  for (const button of slot.querySelectorAll('.x'))
    button.addEventListener('click', () => { S.item.mods.splice(parseInt(button.dataset.del, 10), 1); S.card = null; renderItem(); renderAffixes(); });
  for (const input of slot.querySelectorAll('.val'))
    input.addEventListener('input', () => {
      const m = S.item.mods[parseInt(input.dataset.mod, 10)], k = parseInt(input.dataset.k, 10);
      if (!m) return;
      const stat = m.stats[k], v = parseInt(input.value, 10);
      input.classList.toggle('over', v > stat.max || v < stat.min);
      m.values[k] = isNaN(v) ? stat.max : v;
    });
}
function renderCard() {
  const colour = RARITY[S.cardRarity || 'rare'] || '#d8dee9';
  $('#slot').innerHTML = '<div class="banner">服务器预览：名称与数值都是真的</div>'
    + '<div class="card">' + S.card.map(l =>
        '<div class="line"><span class="txt ' + esc(l.kind) + '" style="' + (l.kind === 'name' ? 'text-align:center;font-size:16px;font-weight:600;color:' + colour : '')
        + '">' + (l.tier ? '<span class="tag">T' + l.tier + '</span> ' : '') + esc(l.text) + '</span></div>').join('') + '</div>';
}
async function preview() {
  if (!S.base) { say('先挑底子', true); return; }
  const body = compose(true);
  if (!body) return;
  say('预览中…');
  try {
    const d = await (await fetch('/forge/preview', { method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body) })).json();
    if (!d.ok) { say('预览失败：' + d.error, true); return; }
    S.card = d.card; S.cardRarity = S.item.rarity; S.note = '';
    renderCard();
    say('这是会发出去的样子');
  } catch (e) { say('请求失败', true); }
}
async function give() {
  if (!S.base) { say('先挑底子', true); return; }
  const body = compose(false);
  if (!body) return;
  say('发放中…');
  try {
    const d = await (await fetch('/forge', { method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body) })).json();
    if (!d.ok) { say('失败：' + d.error, true); return; }
    const total = (d.placed || []).reduce((n, p) => n + (p.moved || p.stack || 1), 0);
    say('已放进 ' + d.account + ' 的「' + d.tabName + '」页：' + d.item + ' ×' + total
      + (d.online && d.online.length ? '（' + d.online.join('、') + ' 在线，重开仓库页可见）' : ''));
  } catch (e) { say('请求失败', true); }
}
function compose(forPreview) {
  const body = { token: token(), base: S.base.id, rarity: S.item.rarity, itemLevel: S.item.itemLevel,
                 quality: S.item.quality, sockets: S.item.sockets, states: S.item.states,
                 mods: S.item.mods.map(m => ({ id: m.id, values: m.values })) };
  if (!forPreview) {
    const account = $('#account').value.trim();
    if (!account) { say('填一个要发放的账号', true); return null; }
    body.account = account;
  }
  return body;
}

// ── 界面事件 ───────────────────────────────────────────────────────────────
$('#fclass').addEventListener('change', renderBases);
$('#bsearch').addEventListener('input', renderBases);
$('#asearch').addEventListener('input', renderAffixes);
for (const id of ['tabPrefix', 'tabSuffix', 'tabCrafted'])
  $('#' + id).addEventListener('click', () => { S.tab = $('#' + id).dataset.tab; $('#asearch').value = ''; renderAffixes(); });
$('#rarity').addEventListener('change', () => {
  S.item.rarity = $('#rarity').value;
  const c = caps();
  // A rarity that takes fewer affixes drops what no longer fits, like the game's own orbs do.
  const keep = [], seen = { prefix: 0, suffix: 0 };
  for (const m of S.item.mods) {
    const side = m.generation === 1 ? 'prefix' : 'suffix';
    if (++seen[side] > (side === 'prefix' ? c.prefix : c.suffix)) continue;
    keep.push(m);
  }
  if (keep.length !== S.item.mods.length) S.note = '换了稀有度，多出的词缀已去掉';
  S.item.mods = keep; S.card = null;
  renderItem(); renderAffixes();
});
$('#ilvl').addEventListener('change', () => {
  S.item.itemLevel = clamp(parseInt($('#ilvl').value, 10) || 84, 1, 100);
  $('#ilvl').value = S.item.itemLevel;
  // A lower item level takes affixes away from under the item: drop what it can no longer roll.
  const before = S.item.mods.length;
  if (S.base) {
    const allowed = new Set();
    for (const side of ['prefix', 'suffix', 'crafted']) for (const a of S.affixes[side]) if (a.level <= S.item.itemLevel) allowed.add(a.id);
    if (allowed.size) { S.item.mods = S.item.mods.filter(m => allowed.has(m.id)); S.note = S.item.mods.length !== before ? '降低物品等级，超出的词缀已去掉' : ''; }
    loadBase(S.base.id);
  }
  S.card = null; renderItem();
});
$('#quality').addEventListener('change', () => { S.item.quality = clamp(parseInt($('#quality').value, 10) || 0, 0, 100); $('#quality').value = S.item.quality; S.card = null; renderItem(); });
$('#sockets').addEventListener('input', () => { S.item.sockets = $('#sockets').value.trim(); S.card = null; renderItem(); });
$('#states').addEventListener('input', () => {
  S.item.states = $('#states').value.split(',').map(s => s.trim()).filter(Boolean); S.card = null; renderItem();
});
$('#clear').addEventListener('click', () => { S.item.mods = []; S.card = null; S.note = ''; renderItem(); renderAffixes(); });
$('#preview').addEventListener('click', preview);
$('#give').addEventListener('click', give);
$('#loadAccounts').addEventListener('click', async () => {
  if (!token()) { say('先填密码', true); return; }
  try {
    const d = await (await fetch('/accounts?admin=' + encodeURIComponent(token()), { cache: 'no-store' })).json();
    if (!d.accounts) { say('账号载入失败：密码不对？', true); return; }
    $('#accounts').innerHTML = d.accounts.map(a => '<option value="' + esc(a.name) + '">').join('');
    say('载入 ' + d.accounts.length + ' 个账号');
  } catch (e) { say('账号载入失败', true); }
});
function clamp(v, lo, hi) { return Math.max(lo, Math.min(hi, v)); }
loadList();
</script>
</body>
</html>
""";
}
