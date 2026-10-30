// 立体将棋の CPU。持ち駒を打てる場所が多すぎて深く読めないので、
// 「指した後の駒の取り合い」だけを読んで駒得を比べる
// Unity に依存しない。画面側は Search を使って毎フレーム少しずつ考えさせる

using System;
using System.Collections.Generic;

namespace Shogi3D
{
    public static class ShogiAI
    {
        const int Mate = 1000000;
        const double HandBonus = 1.1; // 持ち駒は使い道が多いので少し高く見る

        static int Value(PieceType t)
        {
            switch (t)
            {
                case PieceType.Rook: return 1000;
                case PieceType.Bishop: return 950;
                case PieceType.Lance: return 600;
                case PieceType.Gold: return 550;
                case PieceType.Silver: return 500;
                case PieceType.Knight: return 400;
                case PieceType.Pawn: return 100;
            }
            return 0;
        }

        static int PieceValue(int p)
        {
            var t = Piece.Type(p);
            if (t == PieceType.King) return 0;
            if (!Piece.Promoted(p)) return Value(t);
            if (t == PieceType.Rook) return 1300;
            if (t == PieceType.Bishop) return 1250;
            if (t == PieceType.Pawn) return 600;
            return 560;
        }

        // 手番側から見た評価値
        public static int Evaluate(ShogiState s)
        {
            double score = 0;
            for (int i = 0; i < ShogiState.Cells; i++)
            {
                int p = s.Board[i];
                if (p == 0) continue;
                double v = PieceValue(p);
                // 歩・銀・桂は前に出ているほど少し加点
                var t = Piece.Type(p);
                if (!Piece.Promoted(p) && (t == PieceType.Pawn || t == PieceType.Silver || t == PieceType.Knight))
                    v += ShogiState.RelRank(Piece.Owner(p), ShogiState.Y(i)) * 4;
                score += Piece.Owner(p) == 0 ? v : -v;
            }
            foreach (var t in ShogiState.HandTypes)
                score += (s.Hands[0, (int)t] - s.Hands[1, (int)t]) * Value(t) * HandBonus;
            return (int)(s.Turn == 0 ? score : -score);
        }

        // 駒を取る手だけを読む（取り合いの結末を見る）
        static int Quiesce(ShogiState s, int depth, int alpha, int beta)
        {
            int standPat = Evaluate(s);
            if (depth == 0) return standPat;
            if (standPat >= beta) return standPat;
            if (standPat > alpha) alpha = standPat;

            var captures = new List<Move>();
            foreach (var m in s.GeneratePseudoMoves(false))
            {
                int cap = s.Board[m.To];
                if (cap == 0) continue;
                if (Piece.Type(cap) == PieceType.King) return Mate; // 玉を取れる＝相手の手は反則だった
                captures.Add(m);
            }
            captures.Sort((a, b) => PieceValue(s.Board[b.To]).CompareTo(PieceValue(s.Board[a.To])));

            int best = standPat;
            foreach (var m in captures)
            {
                var u = s.MakeMove(m);
                int v = -Quiesce(s, depth - 1, -beta, -alpha);
                s.UnmakeMove(u);
                if (v > best) best = v;
                if (v > alpha) alpha = v;
                if (alpha >= beta) break;
            }
            return best;
        }

        static int KingDistance(int a, int b)
        {
            return Math.Max(Math.Abs(ShogiState.X(a) - ShogiState.X(b)),
                   Math.Max(Math.Abs(ShogiState.Y(a) - ShogiState.Y(b)), Math.Abs(ShogiState.Z(a) - ShogiState.Z(b))));
        }

        // level: 1=弱い, 2=普通, 3=強い。最後まで一気に考える（テスト用）
        public static Move? ChooseMove(ShogiState state, int level, int seed)
        {
            var search = new Search(state, level, seed);
            while (!search.Step(long.MaxValue)) { }
            return search.Result;
        }

        // 少しずつ考える CPU。ブラウザ（WebGL）では別スレッドが使えないので、
        // 毎フレーム Step を呼んで決めた時間だけ考え、画面を止めないようにする
        public class Search
        {
            readonly ShogiState s;
            readonly Random rng;
            readonly int level;
            List<Move> candidates;
            int next;
            int bestScore = int.MinValue;
            bool done;

            public Move? Result { get; private set; }

            public Search(ShogiState state, int level, int seed)
            {
                s = state.Clone();
                rng = new Random(seed);
                this.level = level;
            }

            // budgetMs ミリ秒だけ考える。考え終わったら true
            public bool Step(long budgetMs)
            {
                if (done) return true;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                if (candidates == null)
                {
                    Prepare();
                    if (done) return true;
                }
                int qDepth = level == 1 ? 1 : level == 2 ? 2 : 3;
                int noise = level == 1 ? 150 : 20; // 同じ局面でも毎回同じ手にならないように
                while (next < candidates.Count)
                {
                    var m = candidates[next++];
                    var u = s.MakeMove(m);
                    int v;
                    if (!s.HasAnyLegalMove()) v = Mate; // 詰み
                    else v = -Quiesce(s, qDepth, -Mate, Mate) + rng.Next(noise);
                    s.UnmakeMove(u);
                    if (v > bestScore)
                    {
                        bestScore = v;
                        Result = m;
                    }
                    if (v >= Mate) break;
                    if (sw.ElapsedMilliseconds >= budgetMs) return false;
                }
                done = true;
                return true;
            }

            void Prepare()
            {
                var legal = s.GenerateLegalMoves();
                candidates = new List<Move>();
                if (legal.Count == 0)
                {
                    done = true; // 指せる手が無い（詰み）
                    return;
                }
                // 打つ手は数が多すぎるので、相手玉の近くの手と、ランダムに選んだ少しだけを読む
                int enemyKing = s.FindKing(s.Turn ^ 1);
                var otherDrops = new List<Move>();
                foreach (var m in legal)
                {
                    if (!m.IsDrop || (enemyKing >= 0 && KingDistance(m.To, enemyKing) <= 2)) candidates.Add(m);
                    else otherDrops.Add(m);
                }
                for (int k = 0; k < 20 && otherDrops.Count > 0; k++)
                {
                    int j = rng.Next(otherDrops.Count);
                    candidates.Add(otherDrops[j]);
                    otherDrops.RemoveAt(j);
                }
                Result = candidates[0];
            }
        }
    }
}
