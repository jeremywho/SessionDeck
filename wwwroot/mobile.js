(() => {
  // One host's terminal, as the phone sees it: the host's own size, never resized from here. The
  // native shell injects window.SD = { token, hostId, after }; a browser can pass them as a query.
  const params = new URLSearchParams(location.search);
  const SD = Object.assign({ token: params.get('token') || '', hostId: params.get('host') || '', after: Number(params.get('after') || 0) }, window.SD || {});
  const native = window.webkit && window.webkit.messageHandlers && window.webkit.messageHandlers.sd;
  const post = obj => { if (native) native.postMessage(obj); else console.log('sd', JSON.stringify(obj)); };

  const campbell = {
    background: '#0c0c0c', foreground: '#cccccc', cursor: '#ffffff', selectionBackground: '#ffffff40',
    black: '#0c0c0c', red: '#c50f1f', green: '#13a10e', yellow: '#c19c00', blue: '#0037da',
    magenta: '#881798', cyan: '#3a96dd', white: '#cccccc', brightBlack: '#767676', brightRed: '#e74856',
    brightGreen: '#16c60c', brightYellow: '#f9f1a5', brightBlue: '#3b78ff', brightMagenta: '#b4009e',
    brightCyan: '#61d6d6', brightWhite: '#f2f2f2',
  };

  const el = document.getElementById('term');
  const banner = document.getElementById('banner');
  const term = new Terminal({
    theme: campbell,
    fontFamily: '"Cascadia Code", "Cascadia Mono", Menlo, monospace',
    fontSize: 14,
    lineHeight: 1.0,
    scrollback: 5000,
    cursorBlink: false,
    cursorStyle: 'bar',
    cursorInactiveStyle: 'none',
    allowProposedApi: true,
    disableStdin: true,
    cols: 120,
    rows: 30,
  });
  term.loadAddon(new Unicode11Addon.Unicode11Addon());
  term.unicode.activeVersion = '11';
  term.open(el);

  let ws = null, seq = SD.after || 0, retries = 0, retryTimer = null, ended = false, selecting = false;

  function send(obj) {
    if (!ws || ws.readyState !== WebSocket.OPEN) return false;
    ws.send(JSON.stringify(obj));
    return true;
  }

  // The page is as wide as the terminal. Without an initial scale the web view fits that width to
  // the screen on first paint; pinch-zoom and pan come from the scroll view.
  function applySize(cols, rows) {
    if (term.cols !== cols || term.rows !== rows) term.resize(cols, rows);
    const cell = term._core && term._core._renderService ? term._core._renderService.dimensions.css.cell : null;
    const width = Math.ceil((cell ? cell.width : 8.4) * cols) + 12;
    document.getElementById('viewport').setAttribute('content', `width=${width}, minimum-scale=0.2, maximum-scale=4`);
    post({ type: 'size', cols, rows, width });
  }

  function showBanner(text) { banner.textContent = text; banner.classList.toggle('shown', text.length > 0); }

  function connect() {
    if (retryTimer) { clearTimeout(retryTimer); retryTimer = null; }
    if (ended || !SD.hostId || ws) return;
    const sock = new WebSocket(`ws://${location.host}/sessions/${encodeURIComponent(SD.hostId)}/attach?token=${encodeURIComponent(SD.token)}&after=${seq}`);
    sock.binaryType = 'arraybuffer';
    ws = sock;
    let expectSnapshot = false, snapStart = 0, openedAt = 0;
    sock.onopen = () => { openedAt = Date.now(); showBanner(''); post({ type: 'connected' }); };
    sock.onmessage = ev => {
      if (ws !== sock) return;
      if (typeof ev.data === 'string') {
        let msg;
        try { msg = JSON.parse(ev.data); } catch { return; }
        if (msg.type === 'snapshot') {
          expectSnapshot = msg.end > msg.start;
          snapStart = msg.start;
          if (seq === 0 || msg.start !== seq) term.reset();
          seq = expectSnapshot ? msg.start : msg.end;
          if (msg.cols && msg.rows) applySize(msg.cols, msg.rows);
        } else if (msg.type === 'size') {
          applySize(msg.cols, msg.rows);
        } else if (msg.type === 'exit') {
          ended = true;
          term.write(`\r\n\x1b[90m[process exited with code ${msg.code}]\x1b[0m\r\n`);
          post({ type: 'exit', code: msg.code });
        }
        return;
      }
      const bytes = new Uint8Array(ev.data);
      if (expectSnapshot) { expectSnapshot = false; seq = snapStart; }
      seq += bytes.length;
      term.write(bytes);
    };
    sock.onclose = ev => {
      if (ws !== sock) return;
      ws = null;
      if (ended) return;
      if (ev.code === 4401) { ended = true; showBanner('This phone was revoked from the deck.'); post({ type: 'revoked' }); return; }
      if (openedAt && Date.now() - openedAt > 15000) retries = 0;
      const delay = Math.min(30000, 500 * Math.pow(2, Math.min(retries++, 6)));
      showBanner('Reconnecting…');
      post({ type: 'disconnected', retryMs: delay });
      retryTimer = setTimeout(() => { retryTimer = null; connect(); }, delay);
    };
    sock.onerror = () => {};
  }

  // Selection by touch, by whole lines: xterm selects with a mouse, which a phone has not got. In
  // selection mode a touch picks a line and a drag extends to the lines it crosses.
  let anchorRow = null;
  function rowAt(touch) {
    const rect = term.element.getBoundingClientRect();
    const cell = term._core._renderService.dimensions.css.cell;
    const row = Math.floor((touch.clientY - rect.top) / cell.height);
    return Math.max(0, Math.min(term.rows - 1, row)) + term.buffer.active.viewportY;
  }
  function onTouchStart(ev) {
    if (!selecting || ev.touches.length !== 1) return;
    ev.preventDefault();
    anchorRow = rowAt(ev.touches[0]);
    term.selectLines(anchorRow, anchorRow);
  }
  function onTouchMove(ev) {
    if (!selecting || anchorRow === null || ev.touches.length !== 1) return;
    ev.preventDefault();
    const row = rowAt(ev.touches[0]);
    term.selectLines(Math.min(anchorRow, row), Math.max(anchorRow, row));
  }
  function onTouchEnd() { anchorRow = null; }
  el.addEventListener('touchstart', onTouchStart, { passive: false });
  el.addEventListener('touchmove', onTouchMove, { passive: false });
  el.addEventListener('touchend', onTouchEnd);
  el.addEventListener('touchcancel', onTouchEnd);

  window.sdInput = text => send({ type: 'input', data: text });
  window.sdPaste = text => send({ type: 'input', data: text });
  window.sdSelect = on => {
    selecting = !!on;
    el.classList.toggle('selecting', selecting);
    if (!selecting) term.clearSelection();
    post({ type: 'selecting', on: selecting });
  };
  window.sdCopySelection = () => {
    const text = term.hasSelection() ? term.getSelection() : '';
    post({ type: 'copy', text });
    term.clearSelection();
    selecting = false;
    el.classList.remove('selecting');
    return text;
  };
  window.sdScrollToBottom = () => term.scrollToBottom();
  window.sdReconnect = () => { if (!ended) { retries = 0; connect(); } };

  term.onSelectionChange(() => { if (selecting) post({ type: 'selection', has: term.hasSelection() }); });
  connect();
})();
