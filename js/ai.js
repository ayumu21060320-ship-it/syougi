// CPU の思考（駒得を評価する αβ 探索）

(function (global) {
  'use strict';

  const Shogi = global.Shogi || (typeof require !== 'undefined' ? require('./shogi.js') : null);

  const VALUE = { K: 0, R: 1000, B: 850, G: 550, S: 500, N: 350, L: 300, P: 100 };
  const PROMOTED_VALUE = { R: 1300, B: 1100, S: 550, N: 550, L: 550, P: 600 };
  const HAND_BONUS = 1.1; // 持ち駒は使い道が多いので少し高く見る
  const MATE = 100000;

  function pieceValue(pc) {
    return pc.p ? PROMOTED_VALUE[pc.t] : VALUE[pc.t];
  }

  // 手番側から見た評価値
  function evaluate(s) {
    let score = 0;
    for (let i = 0; i < 81; i++) {
      const pc = s.board[i];
      if (!pc) continue;
      let v = pieceValue(pc);
      // 歩・銀・桂は前に出ているほど少し加点
      if (!pc.p && (pc.t === 'P' || pc.t === 'S' || pc.t === 'N')) {
        const row = Math.floor(i / 9);
        v += (pc.o === Shogi.SENTE ? 8 - row : row) * 3;
      }
      score += pc.o === Shogi.SENTE ? v : -v;
    }
    for (const t of Shogi.HAND_TYPES) {
      score += (s.hands[0][t] - s.hands[1][t]) * VALUE[t] * HAND_BONUS;
    }
    return s.turn === Shogi.SENTE ? score : -score;
  }

  // 駒を取る手・成る手を先に読むと枝刈りが効きやすい
  function orderMoves(s, moves) {
    const key = (m) => {
      let k = 0;
      if (m.from >= 0) {
        const cap = s.board[m.to];
        if (cap) k += pieceValue(cap) * 10 - pieceValue(s.board[m.from]);
        if (m.promote) k += 500;
      }
      return k;
    };
    return moves.map((m) => [key(m), m]).sort((a, b) => b[0] - a[0]).map((x) => x[1]);
  }

  function negamax(s, depth, alpha, beta, ply) {
    if (depth === 0) return evaluate(s);
    const moves = Shogi.generateLegalMoves(s);
    if (moves.length === 0) return -MATE + ply; // 詰み
    let best = -Infinity;
    for (const m of orderMoves(s, moves)) {
      const u = Shogi.makeMove(s, m);
      const v = -negamax(s, depth - 1, -beta, -alpha, ply + 1);
      Shogi.unmakeMove(s, u);
      if (v > best) best = v;
      if (v > alpha) alpha = v;
      if (alpha >= beta) break;
    }
    return best;
  }

  // level: 1=弱い, 2=普通, 3=強い
  function chooseMove(state, level) {
    const s = Shogi.cloneState(state);
    const moves = Shogi.generateLegalMoves(s);
    if (moves.length === 0) return null;
    const depth = level;
    const noise = level === 1 ? 120 : 15; // 同じ局面でも毎回同じ手にならないように
    let best = null;
    let bestScore = -Infinity;
    for (const m of orderMoves(s, moves)) {
      const u = Shogi.makeMove(s, m);
      const v = -negamax(s, depth - 1, -Infinity, Infinity, 1) + Math.random() * noise;
      Shogi.unmakeMove(s, u);
      if (v > bestScore) {
        bestScore = v;
        best = m;
      }
    }
    return best;
  }

  const AI = { chooseMove, evaluate };
  if (typeof module !== 'undefined' && module.exports) module.exports = AI;
  else global.ShogiAI = AI;
})(typeof window !== 'undefined' ? window : globalThis);
