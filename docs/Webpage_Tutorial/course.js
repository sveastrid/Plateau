/* Inside MRBoardGame2 — course behaviour.
   No dependencies and no network: works opened straight from disk.
   Progress is kept in this browser only (localStorage), and everything works without it. */
(function () {
  'use strict';

  var KEY = 'mrbg-course:v1';
  var state = { mcq: {}, open: {}, drafts: {}, exam: {}, examGraded: false, examResult: null, theme: 'system' };

  try {
    var raw = window.localStorage.getItem(KEY);
    if (raw) {
      var saved = JSON.parse(raw) || {};
      Object.keys(saved).forEach(function (k) { state[k] = saved[k]; });
    }
  } catch (e) { /* storage blocked or corrupt: run without it */ }

  function save() {
    try { window.localStorage.setItem(KEY, JSON.stringify(state)); } catch (e) { /* ignore */ }
  }

  function esc(s) {
    return String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
  }

  // ---------------------------------------------------------------- C# highlighting

  var KW = {};
  ('abstract as base bool break byte case catch char checked class const continue decimal default delegate do ' +
   'double else enum event explicit extern false finally fixed float for foreach goto if implicit in int interface ' +
   'internal is lock long namespace new null object operator out override params private protected public readonly ' +
   'ref return sbyte sealed short sizeof stackalloc static string struct switch this throw true try typeof uint ulong ' +
   'unchecked unsafe ushort using virtual void volatile while var async await get set value when where yield nameof')
    .split(' ').forEach(function (w) { KW[w] = true; });

  // comments | preprocessor | strings and chars | numbers | identifiers | whitespace | anything else
  var TOKEN = /(\/\/[^\n]*|\/\*[\s\S]*?\*\/)|(#(?:if|else|elif|endif|region|endregion|define|undef|pragma|nullable)\b[^\n]*)|(\$?@"(?:[^"]|"")*"|@\$"(?:[^"]|"")*"|\$"(?:[^"\\\n]|\\.)*"|"(?:[^"\\\n]|\\.)*"|'(?:[^'\\\n]|\\.)+')|(\b0[xX][0-9a-fA-F_]+[uUlL]*|\b\d[\d_]*(?:\.\d[\d_]*)?(?:[eE][+-]?\d+)?[fFdDmMuUlL]*)|([A-Za-z_][A-Za-z0-9_]*)|(\s+)|([^\sA-Za-z0-9_])/g;
  var CALL = /\s*(?:<[A-Za-z0-9_,\s<>\[\]]*>)?\s*\(/y;

  function highlight(src, plain) {
    var lines = [[]];
    var prev = '';
    var m;
    TOKEN.lastIndex = 0;
    while ((m = TOKEN.exec(src)) !== null) {
      var text = m[0];
      var cls = '';
      if (!plain) {
        if (m[1]) { cls = 'tk-com'; }
        else if (m[2]) { cls = 'tk-attr'; }
        else if (m[3]) { cls = 'tk-str'; }
        else if (m[4]) { cls = 'tk-num'; }
        else if (m[5]) {
          CALL.lastIndex = TOKEN.lastIndex;
          var isCall = CALL.test(src);
          if (KW[text]) { cls = 'tk-kw'; }
          else if (prev === '[' && /^[A-Z]/.test(text)) { cls = 'tk-attr'; }
          else if (isCall) { cls = 'tk-fn'; }
          else if (prev !== '.' && /^[A-Z]/.test(text)) { cls = 'tk-type'; }
        }
      } else if (m[1] && /^\/\//.test(text)) {
        cls = 'tk-com';
      }
      var parts = text.split('\n');
      for (var i = 0; i < parts.length; i++) {
        if (i > 0) { lines.push([]); }
        if (parts[i]) {
          lines[lines.length - 1].push(cls ? '<span class="' + cls + '">' + esc(parts[i]) + '</span>' : esc(parts[i]));
        }
      }
      if (!m[6]) { prev = text; }
    }
    return lines;
  }

  function dedent(text) {
    var lines = text.split('\n');
    var min = Infinity;
    lines.forEach(function (l) {
      if (l.trim()) { min = Math.min(min, l.match(/^[ \t]*/)[0].length); }
    });
    if (!isFinite(min) || min === 0) { return text; }
    return lines.map(function (l) { return l.slice(Math.min(min, l.match(/^[ \t]*/)[0].length)); }).join('\n');
  }

  function buildCode(s) {
    var raw = s.textContent.replace(/\r\n/g, '\n');
    if (raw.charAt(0) === '\n') { raw = raw.slice(1); }
    raw = raw.replace(/\n[ \t]*$/, '');
    var start = parseInt(s.getAttribute('data-start') || '1', 10);
    var file = s.getAttribute('data-file') || '';
    var plain = s.getAttribute('type') !== 'text/x-csharp';
    var lines = highlight(dedent(raw), plain);
    var end = start + lines.length - 1;

    var fig = document.createElement('figure');
    fig.className = 'code';
    fig.setAttribute('aria-label', (file ? file + ', ' : '') + (lines.length > 1 ? 'lines ' + start + ' to ' + end : 'line ' + start));
    var head = '<div class="code-head"><span class="path">' + esc(file || s.getAttribute('data-label') || '') + '</span>' +
      (file ? '<span>' + (lines.length > 1 ? 'lines ' + start + '–' + end : 'line ' + start) + '</span>' : '') + '</div>';
    var body = lines.map(function (parts, i) {
      var n = s.hasAttribute('data-nonum') ? '' : '<span class="ln">' + (start + i) + '</span>';
      return '<span class="line">' + n + parts.join('') + '</span>';
    }).join('');
    fig.innerHTML = head + '<pre><code>' + body + '</code></pre>';
    s.parentNode.replaceChild(fig, s);
  }

  // ---------------------------------------------------------------- questions

  var examGraded = !!state.examGraded;

  function initMcq(q) {
    var exam = q.classList.contains('exam');
    var buttons = [];
    Array.prototype.forEach.call(q.querySelectorAll('.opts > li'), function (li) {
      var b = document.createElement('button');
      b.type = 'button';
      b.setAttribute('data-key', li.getAttribute('data-key'));
      b.setAttribute('aria-pressed', 'false');
      while (li.firstChild) { b.appendChild(li.firstChild); }
      li.appendChild(b);
      buttons.push(b);
      b.addEventListener('click', function () {
        if (exam) { pickExam(q, b.getAttribute('data-key')); }
        else if (!q.classList.contains('answered')) { state.mcq[q.id] = b.getAttribute('data-key'); save(); revealMcq(q); refreshProgress(); }
      });
    });
    var foot = document.createElement('div');
    foot.className = 'q-foot';
    var why = q.querySelector('.why');
    q.insertBefore(foot, why || null);
    q._buttons = buttons;
    q._foot = foot;
    if (!exam && state.mcq[q.id]) { revealMcq(q); }
    if (exam && state.exam[q.id]) { markChoice(q, state.exam[q.id]); }
  }

  function paint(q, chosen) {
    var correct = q.getAttribute('data-correct');
    q._buttons.forEach(function (b) {
      var k = b.getAttribute('data-key');
      b.disabled = true;
      b.classList.toggle('is-right', k === correct);
      b.classList.toggle('is-wrong', k === chosen && k !== correct);
      b.setAttribute('aria-pressed', k === chosen ? 'true' : 'false');
    });
    return chosen === correct;
  }

  function verdict(ok, correct, unanswered) {
    var span = document.createElement('span');
    span.className = 'verdict ' + (ok ? 'ok' : 'bad');
    span.textContent = ok ? '✓ Correct' : (unanswered ? '✗ Not answered' : '✗ Not quite') + ' — the answer is ' + correct.toUpperCase();
    return span;
  }

  function revealMcq(q) {
    var ok = paint(q, state.mcq[q.id]);
    q.classList.add('answered');
    q._foot.innerHTML = '';
    q._foot.appendChild(verdict(ok, q.getAttribute('data-correct'), false));
    var again = document.createElement('button');
    again.type = 'button';
    again.className = 'btn small';
    again.textContent = 'Try again';
    again.addEventListener('click', function () {
      delete state.mcq[q.id];
      save();
      clearMcq(q);
      refreshProgress();
    });
    q._foot.appendChild(again);
  }

  function clearMcq(q) {
    q.classList.remove('answered', 'graded');
    q._foot.innerHTML = '';
    q._buttons.forEach(function (b) {
      b.disabled = false;
      b.classList.remove('is-right', 'is-wrong');
      b.setAttribute('aria-pressed', 'false');
    });
  }

  function markChoice(q, key) {
    q._buttons.forEach(function (b) { b.setAttribute('aria-pressed', b.getAttribute('data-key') === key ? 'true' : 'false'); });
  }

  function initOpen(q) {
    var ta = q.querySelector('textarea');
    if (ta) {
      ta.setAttribute('placeholder', 'Your answer — kept in this browser only');
      if (!ta.getAttribute('aria-label')) { ta.setAttribute('aria-label', 'Your answer'); }
      if (state.drafts[q.id]) { ta.value = state.drafts[q.id]; }
      var timer = null;
      ta.addEventListener('input', function () {
        clearTimeout(timer);
        timer = setTimeout(function () { state.drafts[q.id] = ta.value; save(); }, 400);
      });
    }
    var foot = document.createElement('div');
    foot.className = 'q-foot selfmark';
    foot.innerHTML = '<span class="lbl">After checking the model answer:</span>';
    var btns = [];
    [['got', 'I had it'], ['revisit', 'Revisit this']].forEach(function (pair) {
      var b = document.createElement('button');
      b.type = 'button';
      b.className = 'btn small';
      b.textContent = pair[1];
      b.addEventListener('click', function () {
        if (state.open[q.id] === pair[0]) { delete state.open[q.id]; } else { state.open[q.id] = pair[0]; }
        save();
        sync();
        refreshProgress();
      });
      btns.push([pair[0], b]);
      foot.appendChild(b);
    });
    function sync() {
      btns.forEach(function (p) { p[1].setAttribute('aria-pressed', state.open[q.id] === p[0] ? 'true' : 'false'); });
    }
    sync();
    q.appendChild(foot);
  }

  // ---------------------------------------------------------------- exam

  function examItems() { return Array.prototype.slice.call(document.querySelectorAll('.q.exam')); }

  function pickExam(q, key) {
    if (examGraded) { return; }
    state.exam[q.id] = key;
    save();
    markChoice(q, key);
    updateExamCount();
  }

  function updateExamCount() {
    var items = examItems();
    var n = items.filter(function (q) { return !!state.exam[q.id]; }).length;
    var out = document.getElementById('exam-count');
    if (out) { out.textContent = n + ' of ' + items.length + ' answered'; }
  }

  function moduleLabel(id) {
    var m = document.getElementById(id);
    if (!m) { return id; }
    var num = parseInt(id.replace(/\D/g, ''), 10);
    return 'Module ' + num + ' · ' + (m.getAttribute('data-title') || '');
  }

  function gradeExam(silent) {
    var items = examItems();
    if (!items.length) { return; }
    var score = 0;
    items.forEach(function (q) {
      var chosen = state.exam[q.id];
      var ok = paint(q, chosen);
      if (ok) { score++; }
      q.classList.add('graded');
      q._foot.innerHTML = '';
      q._foot.appendChild(verdict(ok, q.getAttribute('data-correct'), !chosen));
      var ref = q.getAttribute('data-ref');
      if (ref) {
        var a = document.createElement('a');
        a.className = 'review-link';
        a.href = '#' + ref;
        a.textContent = 'Review: ' + moduleLabel(ref);
        q._foot.appendChild(a);
      }
    });
    examGraded = true;
    state.examGraded = true;
    if (!silent) { state.examResult = { score: score, total: items.length, when: new Date().toISOString() }; }
    save();
    showResult(score, items.length);
  }

  function showResult(score, total) {
    var box = document.getElementById('exam-result');
    if (!box) { return; }
    var pct = Math.round(100 * score / total);
    var band = pct >= 90 ? 'You can work on this codebase. The misses below are worth one more read.'
      : pct >= 70 ? 'Solid. Re-read the modules linked from your misses before changing shared code.'
      : pct >= 50 ? 'Getting there. Work back through the linked modules, then take it again.'
      : 'Start again from Part II. The questions you missed each link to the module that answers them.';
    box.innerHTML = '<div class="score">' + score + ' / ' + total + '</div><p>' + pct + '% — ' + esc(band) + '</p>';
    box.hidden = false;
  }

  function resetExam() {
    state.exam = {};
    state.examGraded = false;
    examGraded = false;
    save();
    examItems().forEach(clearMcq);
    var box = document.getElementById('exam-result');
    if (box) { box.hidden = true; }
    updateExamCount();
  }

  // ---------------------------------------------------------------- course map

  var PARTS = { I: 'Orientation', II: 'The shared platform', III: 'The games', IV: 'Working on it' };
  var modules = [];

  function buildRail() {
    var host = document.getElementById('rail-list');
    if (!host) { return; }
    var html = '';
    var part = null;
    modules.forEach(function (m) {
      var p = m.getAttribute('data-part');
      if (p !== part) {
        if (part !== null) { html += '</ol>'; }
        html += '<p class="rail-part">Part ' + esc(p) + ' · ' + esc(PARTS[p] || '') + '</p><ol>';
        part = p;
      }
      var num = parseInt(m.id.replace(/\D/g, ''), 10);
      html += '<li><a class="mod" href="#' + m.id + '" data-mod="' + m.id + '"><span class="num">' + (num < 10 ? '0' + num : num) +
        '</span><span>' + esc(m.getAttribute('data-title') || '') + '</span><span class="pip" aria-hidden="true"></span></a></li>';
    });
    if (part !== null) { html += '</ol>'; }
    html += '<p class="rail-part">Back matter</p><ol>' +
      '<li><a class="mod" href="#exam" data-mod="exam"><span class="num">Ex</span><span>Final exam</span><span></span></a></li>' +
      '<li><a class="mod" href="#glossary" data-mod="glossary"><span class="num">A</span><span>Glossary</span><span></span></a></li>' +
      '<li><a class="mod" href="#files" data-mod="files"><span class="num">B</span><span>Every file, and where it is taught</span><span></span></a></li></ol>';
    host.innerHTML = html;
  }

  function refreshProgress() {
    var complete = 0;
    modules.forEach(function (m) {
      var total = 0;
      var done = 0;
      Array.prototype.forEach.call(m.querySelectorAll('.check .q'), function (q) {
        total++;
        if (q.classList.contains('mcq') ? state.mcq[q.id] : state.open[q.id]) { done++; }
      });
      var link = document.querySelector('.rail a[data-mod="' + m.id + '"]');
      if (!link) { return; }
      var pip = link.querySelector('.pip');
      var cls = total && done === total ? 'done' : done ? 'partial' : '';
      if (cls === 'done') { complete++; }
      pip.className = 'pip' + (cls ? ' ' + cls : '');
      link.title = done + ' of ' + total + ' questions answered';
    });
    var out = document.getElementById('rail-progress');
    if (out) { out.textContent = complete + ' of ' + modules.length + ' modules checked off'; }
  }

  function setCurrent(id) {
    var rail = document.getElementById('rail');
    Array.prototype.forEach.call(document.querySelectorAll('.rail a.mod'), function (a) {
      var on = a.getAttribute('data-mod') === id;
      a.classList.toggle('current', on);
      if (on) {
        a.setAttribute('aria-current', 'true');
        if (rail && getComputedStyle(rail).position === 'sticky') {
          var top = a.offsetTop;
          if (top < rail.scrollTop + 60 || top > rail.scrollTop + rail.clientHeight - 80) {
            rail.scrollTop = Math.max(0, top - rail.clientHeight / 3);
          }
        }
      } else {
        a.removeAttribute('aria-current');
      }
    });
  }

  function spy() {
    if (!('IntersectionObserver' in window)) { return; }
    var io = new IntersectionObserver(function (entries) {
      entries.forEach(function (e) { if (e.isIntersecting) { setCurrent(e.target.id); } });
    }, { rootMargin: '-35% 0px -60% 0px' });
    modules.forEach(function (m) { io.observe(m); });
    ['exam', 'glossary', 'files'].forEach(function (id) {
      var el = document.getElementById(id);
      if (el) { io.observe(el); }
    });
  }

  // ---------------------------------------------------------------- theme + actions

  function applyTheme() {
    var root = document.documentElement;
    if (state.theme === 'light' || state.theme === 'dark') { root.setAttribute('data-theme', state.theme); }
    else { root.removeAttribute('data-theme'); }
    var label = 'Theme: ' + (state.theme === 'light' || state.theme === 'dark' ? state.theme : 'system');
    Array.prototype.forEach.call(document.querySelectorAll('[data-action="theme"]'), function (b) { b.textContent = label; });
  }

  function cycleTheme() {
    state.theme = state.theme === 'system' ? 'light' : state.theme === 'light' ? 'dark' : 'system';
    save();
    applyTheme();
  }

  function resetAll() {
    if (!window.confirm('Clear every answer, draft and exam result saved in this browser?')) { return; }
    try { window.localStorage.removeItem(KEY); } catch (e) { /* ignore */ }
    window.location.reload();
  }

  document.addEventListener('click', function (e) {
    var t = e.target.closest ? e.target.closest('[data-action]') : null;
    if (t) {
      var a = t.getAttribute('data-action');
      if (a === 'theme') { cycleTheme(); }
      else if (a === 'rail') { document.body.classList.toggle('rail-open'); }
      else if (a === 'reset-all') { resetAll(); }
      else if (a === 'grade-exam') { gradeExam(false); }
      else if (a === 'reset-exam') { resetExam(); }
    }
    if (e.target.closest && e.target.closest('.rail a')) { document.body.classList.remove('rail-open'); }
  });
  document.addEventListener('keydown', function (e) {
    if (e.key === 'Escape') { document.body.classList.remove('rail-open'); }
  });

  // ---------------------------------------------------------------- boot

  function boot() {
    Array.prototype.forEach.call(document.querySelectorAll('script[type="text/x-csharp"], script[type="text/x-plain"]'), buildCode);
    Array.prototype.forEach.call(document.querySelectorAll('.q.mcq'), initMcq);
    Array.prototype.forEach.call(document.querySelectorAll('.q.open'), initOpen);
    modules = Array.prototype.slice.call(document.querySelectorAll('section.module'));
    buildRail();
    refreshProgress();
    spy();
    applyTheme();
    updateExamCount();
    if (examGraded) { gradeExam(true); if (state.examResult) { showResult(state.examResult.score, state.examResult.total); } }
  }

  if (document.readyState === 'loading') { document.addEventListener('DOMContentLoaded', boot); } else { boot(); }
})();
