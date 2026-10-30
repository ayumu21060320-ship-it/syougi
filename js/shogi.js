// 将棋のルールエンジン（盤面・合法手生成・局面の更新）
// 盤は 81 マスの配列。idx = row * 9 + col
//   row 0 が上段（後手側）、col 0 が画面左端（９筋）
// 手番 0 = 先手（下側・上へ進む）、1 = 後手

(function (global) {
  'use strict';

  const SENTE = 0;
  const GOTE = 1;

  const HAND_TYPES = ['R', 'B', 'G', 'S', 'N', 'L', 'P'];
  const PROMOTABLE = { R: true, B: true, S: true, N: true, L: true, P: true };

  // 先手から見た方向（dr が負なら前進）
  const KING_STEPS = [[-1, -1], [-1, 0], [-1, 1], [0, -1], [0, 1], [1, -1], [1, 0], [1, 1]];
  const GOLD_STEPS = [[-1, -1], [-1, 0], [-1, 1], [0, -1], [0, 1], [1, 0]];
  const SILVER_STEPS = [[-1, -1], [-1, 0], [-1, 1], [1, -1], [1, 1]];
  const KNIGHT_STEPS = [[-2, -1], [-2, 1]];
  const PAWN_STEPS = [[-1, 0]];
  const ORTHO = [[-1, 0], [1, 0], [0, -1], [0, 1]];
  const DIAG = [[-1, -1], [-1, 1], [1, -1], [1, 1]];
  const LANCE_DIRS = [[-1, 0]];

  // 駒の動き { steps: 1マスだけ動く方向, slides: 走る方向 }
  function movement(pc) {
    if (pc.p) {
      if (pc.t === 'R') return { steps: DIAG, slides: ORTHO };
      if (pc.t === 'B') return { steps: ORTHO, slides: DIAG };
      return { steps: GOLD_STEPS, slides: [] };
    }
    switch (pc.t) {
      case 'K': return { steps: KING_STEPS, slides: [] };
      case 'G': return { steps: GOLD_STEPS, slides: [] };
      case 'S': return { steps: SILVER_STEPS, slides: [] };
      case 'N': return { steps: KNIGHT_STEPS, slides: [] };
      case 'P': return { steps: PAWN_STEPS, slides: [] };
      case 'L': return { steps: [], slides: LANCE_DIRS };
      case 'R': return { steps: [], slides: ORTHO };
      case 'B': return { steps: [], slides: DIAG };
    }
    return { steps: [], slides: [] };
  }

  function emptyHand() {
    return { R: 0, B: 0, G: 0, S: 0, N: 0, L: 0, P: 0 };
  }

  function createInitialState() {
    const board = new Array(81).fill(null);
    const back = ['L', 'N', 'S', 'G', 'K', 'G', 'S', 'N', 'L'];
    const put = (r, c, t, o) => { board[r * 9 + c] = { t, o, p: false }; };
    for (let c = 0; c < 9; c++) {
      put(0, c, back[c], GOTE);
      put(8, c, back[c], SENTE);
      put(2, c, 'P', GOTE);
      put(6, c, 'P', SENTE);
    }
    put(1, 1, 'R', GOTE);
    put(1, 7, 'B', GOTE);
    put(7, 1, 'B', SENTE);
    put(7, 7, 'R', SENTE);
    return { board, hands: [emptyHand(), emptyHand()], turn: SENTE };
  }

  function cloneState(s) {
    return {
      board: s.board.map((pc) => (pc ? { t: pc.t, o: pc.o, p: pc.p } : null)),
      hands: [Object.assign({}, s.hands[0]), Object.assign({}, s.hands[1])],
      turn: s.turn,
    };
  }

  // 敵陣（成れる段）か
  function inPromotionZone(owner, row) {
    return owner === SENTE ? row <= 2 : row >= 6;
  }

  // その段に置くと二度と動けない（行き所のない駒）か
  function isDeadSquare(type, owner, row) {
    const rel = owner === SENTE ? row : 8 - row; // 先手から見た段（0 が最奥）
    if (type === 'P' || type === 'L') return rel === 0;
    if (type === 'N') return rel <= 1;
    return false;
  }

  // 駒 idx が利いているマスを列挙
  function forEachTarget(board, idx, cb) {
    const pc = board[idx];
    const r = Math.floor(idx / 9);
    const c = idx % 9;
    const f = pc.o === SENTE ? 1 : -1;
    const mv = movement(pc);
    for (const [dr, dc] of mv.steps) {
      const nr = r + dr * f;
      const nc = c + dc;
      if (nr < 0 || nr > 8 || nc < 0 || nc > 8) continue;
      cb(nr * 9 + nc);
    }
    for (const [dr, dc] of mv.slides) {
      let nr = r + dr * f;
      let nc = c + dc;
      while (nr >= 0 && nr <= 8 && nc >= 0 && nc <= 8) {
        const to = nr * 9 + nc;
        cb(to);
        if (board[to]) break;
        nr += dr * f;
        nc += dc;
      }
    }
  }

  function findKing(board, owner) {
    for (let i = 0; i < 81; i++) {
      const pc = board[i];
      if (pc && pc.t === 'K' && pc.o === owner) return i;
    }
    return -1;
  }

  function isAttacked(board, target, byOwner) {
    for (let i = 0; i < 81; i++) {
      const pc = board[i];
      if (!pc || pc.o !== byOwner) continue;
      let hit = false;
      forEachTarget(board, i, (to) => { if (to === target) hit = true; });
      if (hit) return true;
    }
    return false;
  }

  function isInCheck(s, owner) {
    const k = findKing(s.board, owner);
    return k >= 0 && isAttacked(s.board, k, owner ^ 1);
  }

  // 自玉の安全を考えない「指せそうな手」
  function generatePseudoMoves(s) {
    const moves = [];
    const me = s.turn;
    const board = s.board;
    for (let from = 0; from < 81; from++) {
      const pc = board[from];
      if (!pc || pc.o !== me) continue;
      const fromRow = Math.floor(from / 9);
      forEachTarget(board, from, (to) => {
        const dst = board[to];
        if (dst && dst.o === me) return;
        const toRow = Math.floor(to / 9);
        const canPromote = !pc.p && PROMOTABLE[pc.t] &&
          (inPromotionZone(me, fromRow) || inPromotionZone(me, toRow));
        if (canPromote) moves.push({ from, to, promote: true });
        if (!canPromote || !isDeadSquare(pc.t, me, toRow)) {
          moves.push({ from, to, promote: false });
        }
      });
    }
    // 打つ手
    const hand = s.hands[me];
    let pawnCols = null;
    for (const t of HAND_TYPES) {
      if (!hand[t]) continue;
      if (t === 'P' && !pawnCols) {
        pawnCols = new Array(9).fill(false);
        for (let i = 0; i < 81; i++) {
          const pc = board[i];
          if (pc && pc.o === me && pc.t === 'P' && !pc.p) pawnCols[i % 9] = true;
        }
      }
      for (let to = 0; to < 81; to++) {
        if (board[to]) continue;
        if (isDeadSquare(t, me, Math.floor(to / 9))) continue;
        if (t === 'P' && pawnCols[to % 9]) continue; // 二歩
        moves.push({ from: -1, to, type: t, promote: false });
      }
    }
    return moves;
  }

  function makeMove(s, m) {
    const undo = { m, captured: null, moved: null };
    if (m.from < 0) {
      s.board[m.to] = { t: m.type, o: s.turn, p: false };
      s.hands[s.turn][m.type]--;
    } else {
      const pc = s.board[m.from];
      const cap = s.board[m.to];
      undo.moved = pc;
      undo.captured = cap;
      if (cap) s.hands[s.turn][cap.t]++;
      s.board[m.to] = m.promote ? { t: pc.t, o: pc.o, p: true } : pc;
      s.board[m.from] = null;
    }
    s.turn ^= 1;
    return undo;
  }

  function unmakeMove(s, undo) {
    const m = undo.m;
    s.turn ^= 1;
    if (m.from < 0) {
      s.board[m.to] = null;
      s.hands[s.turn][m.type]++;
    } else {
      s.board[m.from] = undo.moved;
      s.board[m.to] = undo.captured;
      if (undo.captured) s.hands[s.turn][undo.captured.t]--;
    }
  }

  // 王手を外す手が一つでもあるか（打ち歩詰め判定用。歩の王手は合駒では防げないので盤上の手だけ見る）
  function hasLegalBoardMove(s) {
    const me = s.turn;
    for (const m of generatePseudoMoves(s)) {
      if (m.from < 0) continue;
      const u = makeMove(s, m);
      const safe = !isInCheck(s, me);
      unmakeMove(s, u);
      if (safe) return true;
    }
    return false;
  }

  function generateLegalMoves(s) {
    const me = s.turn;
    const legal = [];
    for (const m of generatePseudoMoves(s)) {
      const u = makeMove(s, m);
      let ok = !isInCheck(s, me);
      if (ok && m.from < 0 && m.type === 'P' && isInCheck(s, me ^ 1)) {
        // 打ち歩詰めの禁止
        if (!hasLegalBoardMove(s)) ok = false;
      }
      unmakeMove(s, u);
      if (ok) legal.push(m);
    }
    return legal;
  }

  // 千日手判定用の局面キー
  function positionKey(s) {
    let k = '';
    for (let i = 0; i < 81; i++) {
      const pc = s.board[i];
      k += pc ? (pc.o ? pc.t.toLowerCase() : pc.t) + (pc.p ? '+' : '') : '.';
    }
    for (const h of s.hands) for (const t of HAND_TYPES) k += h[t];
    return k + s.turn;
  }

  // ---- 表記 ----
  const PIECE_CHAR = { K: '玉', R: '飛', B: '角', G: '金', S: '銀', N: '桂', L: '香', P: '歩' };
  const PROMOTED_CHAR = { R: '龍', B: '馬', S: '全', N: '圭', L: '杏', P: 'と' };
  const PROMOTED_NAME = { R: '龍', B: '馬', S: '成銀', N: '成桂', L: '成香', P: 'と' };
  const ZEN_NUM = ['', '１', '２', '３', '４', '５', '６', '７', '８', '９'];
  const KAN_NUM = ['', '一', '二', '三', '四', '五', '六', '七', '八', '九'];

  function pieceChar(pc) {
    if (pc.t === 'K') return pc.o === SENTE ? '玉' : '王';
    return pc.p ? PROMOTED_CHAR[pc.t] : PIECE_CHAR[pc.t];
  }

  function fileOf(idx) { return 9 - (idx % 9); }
  function rankOf(idx) { return Math.floor(idx / 9) + 1; }

  // KIF 風の表記（例：▲７六歩(77)）。盤の更新前に呼ぶ
  function moveToText(s, m, prevTo) {
    const mark = s.turn === SENTE ? '▲' : '△';
    const dest = m.to === prevTo ? '同　' : ZEN_NUM[fileOf(m.to)] + KAN_NUM[rankOf(m.to)];
    if (m.from < 0) return mark + dest + PIECE_CHAR[m.type] + '打';
    const pc = s.board[m.from];
    let name = pc.p ? PROMOTED_NAME[pc.t] : PIECE_CHAR[pc.t];
    if (m.promote) name += '成';
    return mark + dest + name + '(' + fileOf(m.from) + rankOf(m.from) + ')';
  }

  const Shogi = {
    SENTE, GOTE, HAND_TYPES, PIECE_CHAR,
    createInitialState, cloneState,
    generateLegalMoves, generatePseudoMoves,
    makeMove, unmakeMove, isInCheck, positionKey,
    pieceChar, moveToText, fileOf, rankOf, inPromotionZone, isDeadSquare,
  };

  if (typeof module !== 'undefined' && module.exports) module.exports = Shogi;
  else global.Shogi = Shogi;
})(typeof window !== 'undefined' ? window : globalThis);
