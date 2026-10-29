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

  let ws = null, seq = SD.after || 0, retries = 0, ended = false, selecting = false;

  function send(obj) { if (ws && ws.readyState === WebSocket.OPEN) ws.send(JSON.stringify(obj)); }

  // The page is as wide as the terminal; the viewport meta makes the web view fit that width on
  // first paint, and pinch-zoom/pan come from the scroll view for free.
  function applySize(cols, rows) {
    if (term.cols !== cols || term.rows !== rows) term.resize(cols, rows);
    const cell = term._core && term._core._renderService ? term._core._renderService.dimensions.css.cell : null;
    const width = Math.ceil((cell ? cell.width : 8.4) * cols) + 12;
    document.getElementById('viewport').setAttribute('content', `width=${width}, initial-scale=1, minimum-scale=0.2, maximum-scale=4`);
    post({ type: 'size', cols, rows, width });
  }

  function showBanner(text) { banner.textContent = text; banner.classList.toggle('shown', text.length > 0); }

  function connect() {
    if (ended || !SD.hostId) return;
    const sock = new WebSocket(`ws://${location.host}/sessions/${encodeURIComponent(SD.hostId)}/attach?token=${encodeURIComponent(SD.token)}&after=${seq}`);
    sock.binaryType = 'arraybuffer';
    ws = sock;
    let expectSnapshot = false, snapStart = 0, openedAt = 0;
    sock.onopen = () => { openedAt = Date.now(); showBanner(''); post({ type: 'connected' }); };
    sock.onmessage = ev => {
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
      setTimeout(connect, delay);
    };
    sock.onerror = () => {};
  }

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
  window.sdReconnect = () => { if (!ws && !ended) { retries = 0; connect(); } };

  term.onSelectionChange(() => { if (selecting) post({ type: 'selection', has: term.hasSelection() }); });
  connect();
})();
