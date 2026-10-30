// 画面表示と操作

(function () {
  'use strict';

  const { SENTE, GOTE, HAND_TYPES } = Shogi;
  const KAN_NUM = ['一', '二', '三', '四', '五', '六', '七', '八', '九'];

  const el = {
    board: document.getElementById('board'),
    files: document.querySelector('.files'),
    ranks: document.querySelector('.ranks'),
    hands: [document.getElementById('hand-sente'), document.getElementById('hand-gote')],
    status: document.getElementById('status'),
    log: document.getElementById('log'),
    mode: document.getElementById('mode'),
    level: document.getElementById('level'),
    newGame: document.getElementById('new-game'),
    undo: document.getElementById('undo'),
    resign: document.getElementById('resign'),
    dialog: document.getElementById('promote-dialog'),
  };

  let state;        // 現在の局面
  let history;      // [{ undo, text, key }]
  let keyCounts;    // 局面キー → 出現回数（千日手判定）
  let legal;        // 現在の手番の合法手
  let selected;     // { from: idx } または { type: 'P' など }
  let result;       // 終局メッセージ（対局中は null）
  let thinking;     // CPU 思考中
  let gameId = 0;   // 新規対局・待ったで古い CPU の結果を捨てるため

  function cpuSide() {
    if (el.mode.value === 'cpu') return GOTE;
    if (el.mode.value === 'cpu-gote') return SENTE;
    return -1;
  }

  function sideName(side) {
    return side === SENTE ? '☗先手' : '☖後手';
  }

  // ---- 盤面の組み立て ----
  function buildBoard() {
    el.files.innerHTML = '';
    for (let f = 9; f >= 1; f--) {
      const d = document.createElement('div');
      d.textContent = f;
      el.files.appendChild(d);
    }
    el.ranks.innerHTML = '';
    for (const k of KAN_NUM) {
      const d = document.createElement('div');
      d.textContent = k;
      el.ranks.appendChild(d);
    }
    el.board.innerHTML = '';
    for (let i = 0; i < 81; i++) {
      const cell = document.createElement('button');
      cell.type = 'button';
      cell.className = 'cell';
      cell.dataset.idx = i;
      cell.addEventListener('click', () => onCellClick(i));
      el.board.appendChild(cell);
    }
  }

  function pieceElement(pc) {
    const d = document.createElement('div');
    d.className = 'piece' + (pc.o === GOTE ? ' gote' : '') + (pc.p ? ' promoted' : '');
    d.textContent = Shogi.pieceChar(pc);
    return d;
  }

  function render() {
    const targets = new Set();
    if (selected) for (const m of movesForSelection()) targets.add(m.to);
    const lastTo = history.length ? history[history.length - 1].undo.m.to : -1;
    const lastFrom = history.length ? history[history.length - 1].undo.m.from : -1;
    let checkSq = -1;
    if (Shogi.isInCheck(state, state.turn)) {
      checkSq = state.board.findIndex((pc) => pc && pc.t === 'K' && pc.o === state.turn);
    }

    el.board.querySelectorAll('.cell').forEach((cell, i) => {
      const pc = state.board[i];
      cell.innerHTML = '';
      if (pc) cell.appendChild(pieceElement(pc));
      cell.classList.toggle('has-piece', !!pc);
      cell.classList.toggle('last', i === lastTo || i === lastFrom);
      cell.classList.toggle('selected', !!selected && selected.from === i);
      cell.classList.toggle('target', targets.has(i));
      cell.classList.toggle('check', i === checkSq);
      const name = pc ? (pc.o === SENTE ? '先手の' : '後手の') + Shogi.pieceChar(pc) : '空き';
      cell.setAttribute('aria-label', `${Shogi.fileOf(i)}${KAN_NUM[Shogi.rankOf(i) - 1]} ${name}`);
    });

    for (const side of [SENTE, GOTE]) {
      const box = el.hands[side];
      box.innerHTML = '';
      box.classList.toggle('active', !result && state.turn === side);
      for (const t of HAND_TYPES) {
        const n = state.hands[side][t];
        if (!n) continue;
        const b = document.createElement('button');
        b.type = 'button';
        b.className = 'hand-piece' + (selected && selected.type === t && state.turn === side ? ' selected' : '');
        b.setAttribute('aria-label', `${Shogi.PIECE_CHAR[t]} ${n}枚`);
        b.appendChild(pieceElement({ t, o: side, p: false }));
        if (n > 1) {
          const c = document.createElement('span');
          c.className = 'count';
          c.textContent = n;
          b.appendChild(c);
        }
        b.addEventListener('click', () => onHandClick(side, t));
        box.appendChild(b);
      }
    }

    if (result) {
      el.status.textContent = result;
      el.status.classList.add('over');
    } else {
      el.status.classList.remove('over');
      let s = `${sideName(state.turn)}の番です`;
      if (thinking) s = `${sideName(state.turn)}（CPU）が考えています…`;
      if (checkSq >= 0) s = '王手！ ' + s;
      el.status.textContent = s;
    }

    el.log.innerHTML = '';
    history.forEach((h) => {
      const li = document.createElement('li');
      li.textContent = h.text;
      el.log.appendChild(li);
    });
    el.log.scrollTop = el.log.scrollHeight;

    el.undo.disabled = thinking || history.length === 0;
    el.resign.disabled = !!result || thinking;
  }

  // ---- 操作 ----
  function movesForSelection() {
    if (!selected) return [];
    if (selected.from !== undefined) return legal.filter((m) => m.from === selected.from);
    return legal.filter((m) => m.from < 0 && m.type === selected.type);
  }

  function humanCanMove() {
    return !result && !thinking && state.turn !== cpuSide();
  }

  function onCellClick(i) {
    if (!humanCanMove()) return;
    const pc = state.board[i];
    if (selected) {
      const ms = movesForSelection().filter((m) => m.to === i);
      if (ms.length) {
        choosePromotion(ms).then((m) => { if (m) playMove(m); });
        return;
      }
    }
    if (pc && pc.o === state.turn) {
      selected = selected && selected.from === i ? null : { from: i };
    } else {
      selected = null;
    }
    render();
  }

  function onHandClick(side, t) {
    if (!humanCanMove() || side !== state.turn) return;
    selected = selected && selected.type === t ? null : { type: t };
    render();
  }

  // 成り・不成が両方指せるときはダイアログで選ばせる
  function choosePromotion(ms) {
    if (ms.length === 1) return Promise.resolve(ms[0]);
    return new Promise((resolve) => {
      const done = (promote) => {
        el.dialog.close();
        resolve(ms.find((m) => m.promote === promote));
      };
      document.getElementById('promote-yes').onclick = () => done(true);
      document.getElementById('promote-no').onclick = () => done(false);
      el.dialog.oncancel = (e) => { e.preventDefault(); };
      el.dialog.showModal();
    });
  }

  function playMove(m) {
    const prevTo = history.length ? history[history.length - 1].undo.m.to : -1;
    const text = Shogi.moveToText(state, m, prevTo);
    const undo = Shogi.makeMove(state, m);
    const key = Shogi.positionKey(state);
    keyCounts.set(key, (keyCounts.get(key) || 0) + 1);
    history.push({ undo, text, key });
    selected = null;
    afterMove();
  }

  function afterMove() {
    legal = Shogi.generateLegalMoves(state);
    result = null;
    const last = history[history.length - 1];
    if (legal.length === 0) {
      result = `まで${history.length}手で${sideName(state.turn ^ 1)}の勝ち（詰み）`;
    } else if (last && keyCounts.get(last.key) >= 4) {
      result = `まで${history.length}手で千日手（引き分け）`;
    }
    render();
    maybeCpuMove();
  }

  function maybeCpuMove() {
    if (result || state.turn !== cpuSide()) return;
    thinking = true;
    render();
    const id = gameId;
    // 描画を先に反映させてから考え始める
    setTimeout(() => {
      if (id !== gameId) return;
      const m = ShogiAI.chooseMove(state, Number(el.level.value));
      thinking = false;
      if (id !== gameId) return;
      if (m) playMove(m);
      else render();
    }, 60);
  }

  function undoOne() {
    const h = history.pop();
    if (!h) return;
    keyCounts.set(h.key, keyCounts.get(h.key) - 1);
    Shogi.unmakeMove(state, h.undo);
  }

  function onUndo() {
    if (thinking || history.length === 0) return;
    gameId++;
    undoOne();
    // CPU 対局ではあなたの手番まで戻す
    if (state.turn === cpuSide() && history.length) undoOne();
    selected = null;
    result = null;
    legal = Shogi.generateLegalMoves(state);
    render();
    maybeCpuMove();
  }

  function onResign() {
    if (result || thinking) return;
    result = `${sideName(state.turn)}の投了。${sideName(state.turn ^ 1)}の勝ち`;
    selected = null;
    render();
  }

  function newGame() {
    gameId++;
    state = Shogi.createInitialState();
    history = [];
    keyCounts = new Map([[Shogi.positionKey(state), 1]]);
    selected = null;
    result = null;
    thinking = false;
    legal = Shogi.generateLegalMoves(state);
    render();
    maybeCpuMove();
  }

  el.newGame.addEventListener('click', newGame);
  el.undo.addEventListener('click', onUndo);
  el.resign.addEventListener('click', onResign);
  el.mode.addEventListener('change', newGame);

  buildBoard();
  newGame();
})();
