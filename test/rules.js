// 特殊ルール（二歩・打ち歩詰め・行き所のない駒・成りの強制）の確認
const S = require('../js/shogi.js');
const assert = require('assert');

function empty(turn = S.SENTE) {
  const s = S.createInitialState();
  s.board.fill(null);
  s.turn = turn;
  return s;
}
const sq = (file, rank) => (rank - 1) * 9 + (9 - file);
const put = (s, file, rank, t, o, p = false) => { s.board[sq(file, rank)] = { t, o, p }; };
const drops = (s, t) => S.generateLegalMoves(s).filter((m) => m.from < 0 && m.type === t).map((m) => m.to);

// 二歩：歩のある筋には打てない
{
  const s = empty();
  put(s, 5, 9, 'K', 0); put(s, 5, 1, 'K', 1); put(s, 3, 7, 'P', 0);
  s.hands[0].P = 1;
  const d = drops(s, 'P');
  assert(!d.some((to) => to % 9 === sq(3, 5) % 9), '二歩が打ててしまう');
  assert(!d.some((to) => Math.floor(to / 9) === 0), '１段目に歩が打ててしまう');
  console.log('二歩 / 行き所のない歩: OK');
}

// 打ち歩詰め：玉の頭に歩を打って詰む手は禁止
{
  const s = empty();
  put(s, 1, 1, 'K', 1);           // 後手玉 １一
  put(s, 2, 1, 'L', 1); put(s, 2, 2, 'P', 1); // 逃げ道を自駒でふさぐ
  put(s, 1, 3, 'G', 0);           // １二に打つ歩にひもを付ける
  put(s, 5, 9, 'K', 0);
  s.hands[0].P = 1;
  assert(!drops(s, 'P').includes(sq(1, 2)), '打ち歩詰めが指せてしまう');
  console.log('打ち歩詰め: OK');
}

// 突き歩詰めは合法（盤上の歩を進めて詰ますのは OK）
{
  const s = empty();
  put(s, 1, 1, 'K', 1); put(s, 2, 1, 'L', 1); put(s, 2, 2, 'P', 1);
  put(s, 1, 3, 'P', 0); put(s, 2, 3, 'G', 0); put(s, 5, 9, 'K', 0); // 金が１二にひもを付ける
  const m = S.generateLegalMoves(s).find((x) => x.from === sq(1, 3) && x.to === sq(1, 2) && x.promote === false);
  assert(m, '突き歩が指せない');
  S.makeMove(s, m);
  assert.strictEqual(S.generateLegalMoves(s).length, 0, '突き歩詰めになっていない');
  console.log('突き歩詰め: OK');
}

// 桂馬は２段目以上へ不成で進めない
{
  const s = empty();
  put(s, 5, 9, 'K', 0); put(s, 5, 1, 'K', 1); put(s, 3, 4, 'N', 0);
  const ms = S.generateLegalMoves(s).filter((m) => m.from === sq(3, 4));
  assert(ms.length > 0 && ms.every((m) => m.promote), '桂の不成が許されている');
  console.log('行き所のない桂（成り強制）: OK');
}
console.log('all rules tests passed');
