// 初期局面からの合法手数（perft）を既知の値と照合する
const S = require('../js/shogi.js');
function perft(s, d) {
  if (d === 0) return 1;
  let n = 0;
  for (const m of S.generateLegalMoves(s)) {
    const u = S.makeMove(s, m);
    n += perft(s, d - 1);
    S.unmakeMove(s, u);
  }
  return n;
}
const expected = [1, 30, 900, 25470, 719731];
const maxDepth = Number(process.argv[2] || 4);
let ok = true;
for (let d = 1; d <= maxDepth; d++) {
  const n = perft(S.createInitialState(), d);
  const pass = n === expected[d];
  ok = ok && pass;
  console.log(`depth ${d}: ${n} ${pass ? 'OK' : 'NG (expected ' + expected[d] + ')'}`);
}
process.exit(ok ? 0 : 1);
