(() => {
  let el = null, post = () => {}, board = null;
  const rowsByKey = new Map();
  const STATE_WORDS = { working: 'working', awaiting: 'awaiting input', idle: 'idle', error: 'error', scheduled: 'scheduled' };
  const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

  function ago(iso) {
    if (!iso) return '';
    const s = Math.max(0, Math.round((Date.now() - Date.parse(iso)) / 1000));
    return s < 60 ? `${s}s ago` : s < 3600 ? `${Math.floor(s / 60)}m ago` : `${Math.floor(s / 3600)}h ago`;
  }

  function span(iso) {
    const m = Math.max(1, Math.floor((Date.now() - Date.parse(iso)) / 60000));
    return m < 60 ? `${m}m` : m < 48 * 60 ? `${Math.floor(m / 60)}h` : `${Math.floor(m / 1440)}d`;
  }

  function ageText(created, commit) {
    return `opened ${span(created)} ago` + (commit ? `, last commit ${span(commit)} ago` : '');
  }

  function ciTip(r) {
    if (r.seriesCount > 0)
      return r.members.filter(m => m.ci !== 'success').map(m => `#${m.number}: ${ciTip(m).replace(/\n/g, '; ')}`).join('\n');
    const lines = r.checks.filter(c => c.state === 'failure').map(c => `Failed: ${c.name}`)
      .concat(r.checks.filter(c => c.state === 'pending').map(c => c.startedAt ? `Running ${span(c.startedAt)}: ${c.name}` : `Queued: ${c.name}`));
    if (lines.length) return lines.join('\n');
    return r.ci === 'success' ? 'All checks passed' : r.ci === 'none' ? 'No checks' : '';
  }

  function isStale() {
    return !!board && !!board.fetchedAt && (Date.now() - Date.parse(board.fetchedAt)) / 1000 > board.staleAfterSeconds;
  }

  function header() {
    const tip = board.error ? ` title="${esc(board.error)}"` : '';
    return `<div class="prs-head"><span class="prs-total">${board.total} open</span>` +
      `<span class="prs-updated${isStale() ? ' stale' : ''}"${tip}>Updated <span data-ago="${esc(board.fetchedAt)}">${ago(board.fetchedAt)}</span></span></div>`;
  }

  function agent(a) {
    const name = a.clickable
      ? `<a class="prs-agent" href="#" data-session="${esc(a.sessionId)}" title="${esc(a.tip)}">${esc(a.name)}</a>`
      : `<span class="prs-agent-text" title="${esc(a.tip)}">${esc(a.name)}</span>`;
    const via = a.viaCodex ? '<span class="prs-muted">via Codex</span>' : '';
    return `<span class="prs-who">${name}<span class="prs-state ${esc(a.state)}">${esc(STATE_WORDS[a.state] || a.state)}</span>${via}</span>`;
  }

  function row(r) {
    rowsByKey.set(r.key, r);
    const series = r.seriesCount > 0;
    const title = series
      ? `<a class="prs-title" href="#" data-series="${esc(r.key.slice('series:'.length))}" title="${r.expanded ? 'Hide' : 'Show'} the ${r.seriesCount} pull requests">${esc(r.title)}</a><span class="prs-muted">${r.seriesCount} PRs</span>`
      : `<span class="prs-num">#${r.number}</span><a class="prs-title" href="#" data-url="${esc(r.url)}" title="${esc(r.title)}">${esc(r.title)}</a>`;
    const marker = r.marker ? `<span class="prs-muted">${esc(r.marker)}</span>` : '';
    const local = r.local ? `<span class="prs-local" title="${esc(r.localTip)}">${esc(r.local)}</span>` : '';
    const html =
      `<div class="prs-row" style="--depth:${r.depth}">` +
        `<div class="prs-line1"><span class="prs-ci ${esc(r.ci)}" data-key="${esc(r.key)}" title="${esc(ciTip(r))}"></span>${title}${marker}<span class="prs-repo">${esc(r.repo.split('/').pop())}</span></div>` +
        `<div class="prs-line2"><span class="prs-action band${r.band}" title="${esc(r.actionTip)}">${esc(r.action)}</span>${r.agents.map(agent).join('')}${local}<span class="prs-age" data-created="${esc(r.createdAt)}" data-commit="${esc(r.lastCommitAt || '')}">${esc(ageText(r.createdAt, r.lastCommitAt))}</span></div>` +
      `</div>`;
    return html + (series && r.expanded ? r.members.map(row).join('') : '');
  }

  function note(html, retry) {
    return `<div class="prs-note">${html}${retry ? '<button class="prs-retry" data-refresh>Refresh</button>' : ''}</div>`;
  }

  function body(b) {
    switch (b.status) {
      case 'loading': return note('Loading pull requests', false);
      case 'gh-missing': return note('The GitHub CLI (gh) was not found. Install it from <a href="#" data-url="https://cli.github.com">cli.github.com</a>.', true);
      case 'gh-auth': return note('gh is not signed in. Run <code>gh auth login</code> in a terminal.', true);
      case 'failed': return note(`Pull requests could not be loaded: ${esc(b.error)}`, true);
    }
    if (b.total === 0) return note('No open pull requests', false);
    return b.sections.map(s =>
      `<div class="prs-section">${esc(s.name)}<span class="prs-section-count">${s.count}</span></div>` + s.rows.map(row).join('')).join('');
  }

  function render(b) {
    board = b || { status: 'loading' };
    if (!el) return;
    rowsByKey.clear();
    const top = el.scrollTop;
    const warnings = (board.warnings || []).map(w => `<div class="prs-warning">${esc(w)}</div>`).join('');
    el.innerHTML = (board.status === 'ok' ? header() : '') + warnings + body(board);
    el.scrollTop = top;
  }

  function setText(node, text) {
    if (node.textContent !== text) node.textContent = text;
  }

  function tick() {
    if (!el || !board) return;
    for (const s of el.querySelectorAll('[data-ago]')) setText(s, ago(s.dataset.ago));
    for (const s of el.querySelectorAll('.prs-age')) setText(s, ageText(s.dataset.created, s.dataset.commit));
    for (const d of el.querySelectorAll('.prs-ci[data-key]')) {
      const r = rowsByKey.get(d.dataset.key);
      if (r && d.title !== ciTip(r)) d.title = ciTip(r);
    }
    const u = el.querySelector('.prs-updated');
    if (u) u.classList.toggle('stale', isStale());
  }

  function fetched(fetchedAt, error) {
    if (!board) return;
    board.fetchedAt = fetchedAt;
    board.error = error;
    const u = el && el.querySelector('.prs-updated');
    if (!u) return;
    const a = u.querySelector('[data-ago]');
    if (a) { a.dataset.ago = fetchedAt; setText(a, ago(fetchedAt)); }
    if (error) u.title = error; else u.removeAttribute('title');
    u.classList.toggle('stale', isStale());
  }

  function mount(target, poster) {
    el = target;
    post = poster;
    el.addEventListener('click', ev => {
      const t = ev.target.closest('[data-url],[data-session],[data-series],[data-refresh]');
      if (!t) return;
      ev.preventDefault();
      if (t.hasAttribute('data-refresh')) post({ type: 'prRefresh' });
      else if (t.dataset.session) post({ type: 'focusSession', sessionId: t.dataset.session });
      else if (t.dataset.series) post({ type: 'prToggleSeries', key: t.dataset.series });
      else if (t.dataset.url) post({ type: 'open', uri: t.dataset.url });
    });
    setInterval(tick, 10000);
    render(board);
  }

  window.PrPane = { mount, render, fetched };
})();
