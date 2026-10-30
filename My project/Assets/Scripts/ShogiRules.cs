// 9×9×9 の立体将棋のルール（盤・合法手・局面の更新）。Unity に依存しない純粋な C#
// マス番号 idx = x + 9 * y + 81 * z
//   x: 0..8（筋 = 9 - x。x = 0 が 9筋）
//   y: 0..8（段 = y + 1。y = 0 が 一段）
//   z: 0..8（層 = z + 1。z = 0 が 1層 = 先手側）
// 先手（0）の「前」は +z、後手（1）の「前」は -z

using System;
using System.Collections.Generic;

namespace Shogi3D
{
    public enum PieceType { None = 0, King = 1, Rook = 2, Bishop = 3, Gold = 4, Silver = 5, Knight = 6, Lance = 7, Pawn = 8 }

    public struct Move
    {
        public int From;          // 打つ手は -1
        public int To;
        public PieceType Drop;    // 打つ駒（盤上の手は None）
        public bool Promote;

        public bool IsDrop => From < 0;

        public Move(int from, int to, bool promote)
        {
            From = from; To = to; Drop = PieceType.None; Promote = promote;
        }

        public static Move MakeDrop(PieceType t, int to)
        {
            return new Move { From = -1, To = to, Drop = t, Promote = false };
        }
    }

    // 駒は int で表す: 下位 4 ビット = 種類、16 = 成り、32 = 後手
    public static class Piece
    {
        public const int PromotedBit = 16;
        public const int GoteBit = 32;

        public static int Make(PieceType t, int owner, bool promoted)
        {
            return (int)t | (promoted ? PromotedBit : 0) | (owner == 1 ? GoteBit : 0);
        }
        public static PieceType Type(int p) { return (PieceType)(p & 15); }
        public static int Owner(int p) { return (p & GoteBit) != 0 ? 1 : 0; }
        public static bool Promoted(int p) { return (p & PromotedBit) != 0; }
        public static bool CanPromote(PieceType t)
        {
            return t == PieceType.Rook || t == PieceType.Bishop || t == PieceType.Silver ||
                   t == PieceType.Knight || t == PieceType.Lance || t == PieceType.Pawn;
        }
    }

    public struct Undo
    {
        public Move Move;
        public int Moved;
        public int Captured;
    }

    public class ShogiState
    {
        public const int N = 9;
        public const int Cells = N * N * N;
        public static readonly PieceType[] HandTypes =
            { PieceType.Rook, PieceType.Bishop, PieceType.Gold, PieceType.Silver, PieceType.Knight, PieceType.Lance, PieceType.Pawn };

        public int[] Board = new int[Cells];
        public int[,] Hands = new int[2, 9]; // [手番, 駒の種類]
        public int Turn;
        public ulong Hash;                   // 千日手判定用

        public static int X(int i) { return i % 9; }
        public static int Y(int i) { return (i / 9) % 9; }
        public static int Z(int i) { return i / 81; }
        public static int Index(int x, int y, int z) { return x + 9 * y + 81 * z; }
        public static bool Inside(int x, int y, int z)
        {
            return x >= 0 && x < N && y >= 0 && y < N && z >= 0 && z < N;
        }

        // ---- 駒の動き（先手から見た向き。後手は dz を反転） ----
        struct Dir { public int Dx, Dy, Dz; public Dir(int x, int y, int z) { Dx = x; Dy = y; Dz = z; } }

        class Movement
        {
            public Dir[] Steps;   // 1マスだけ（桂の跳びも含む。跳びは間の駒を無視）
            public Dir[] Slides;  // どこまでも走る
        }

        static readonly Movement[] Moves = new Movement[64]; // [駒コードの先手版 (種類 | 成り)]

        static ShogiState()
        {
            var all26 = new List<Dir>();
            var axis6 = new List<Dir>();
            var diag20 = new List<Dir>();   // 2 軸以上が動く方向（斜め 12 + 立体の対角線 8）
            for (int dz = -1; dz <= 1; dz++)
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nz = (dx != 0 ? 1 : 0) + (dy != 0 ? 1 : 0) + (dz != 0 ? 1 : 0);
                        if (nz == 0) continue;
                        var d = new Dir(dx, dy, dz);
                        all26.Add(d);
                        if (nz == 1) axis6.Add(d); else diag20.Add(d);
                    }

            var gold = new List<Dir>();
            var silver = new List<Dir>();
            foreach (var d in all26)
            {
                if (d.Dz == 1 || d.Dz == 0 || (d.Dx == 0 && d.Dy == 0)) gold.Add(d);
                if (d.Dz == 1 || (d.Dz == -1 && d.Dx != 0 && d.Dy != 0)) silver.Add(d);
            }
            var knight = new List<Dir>();
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                    if (dx != 0 || dy != 0) knight.Add(new Dir(dx, dy, 2));
            var layerDiag4 = new[] { new Dir(1, 1, 0), new Dir(1, -1, 0), new Dir(-1, 1, 0), new Dir(-1, -1, 0) };
            var lance = new[] { new Dir(0, 0, 1), new Dir(1, 0, 0), new Dir(-1, 0, 0), new Dir(0, 1, 0), new Dir(0, -1, 0) };

            Movement M(IEnumerable<Dir> steps, IEnumerable<Dir> slides)
            {
                return new Movement { Steps = new List<Dir>(steps).ToArray(), Slides = new List<Dir>(slides).ToArray() };
            }
            var none = new Dir[0];
            var goldM = M(gold, none);
            Moves[(int)PieceType.King] = M(all26, none);
            Moves[(int)PieceType.Rook] = M(layerDiag4, axis6);
            Moves[(int)PieceType.Bishop] = M(none, diag20);
            Moves[(int)PieceType.Gold] = goldM;
            Moves[(int)PieceType.Silver] = M(silver, none);
            Moves[(int)PieceType.Knight] = M(knight, none);
            Moves[(int)PieceType.Lance] = M(none, lance);
            Moves[(int)PieceType.Pawn] = M(new[] { new Dir(0, 0, 1) }, none);
            // 成り駒。走る方向と重なる 1 マスの動きは除く（同じ手が二重にならないように）
            Moves[(int)PieceType.Rook | Piece.PromotedBit] = M(diag20, axis6);
            Moves[(int)PieceType.Bishop | Piece.PromotedBit] = M(axis6, diag20);
            foreach (var t in new[] { PieceType.Silver, PieceType.Knight, PieceType.Lance, PieceType.Pawn })
                Moves[(int)t | Piece.PromotedBit] = goldM;

            InitZobrist();
        }

        static Movement MovementOf(int p) { return Moves[p & (15 | Piece.PromotedBit)]; }

        // ---- 千日手用のハッシュ ----
        static ulong[,] zBoard;
        static ulong[,,] zHand;
        static ulong zTurn;

        static void InitZobrist()
        {
            var rng = new Random(20261030);
            ulong R()
            {
                var b = new byte[8];
                rng.NextBytes(b);
                return BitConverter.ToUInt64(b, 0);
            }
            zBoard = new ulong[Cells, 64];
            for (int i = 0; i < Cells; i++) for (int p = 0; p < 64; p++) zBoard[i, p] = R();
            zHand = new ulong[2, 9, 64];
            for (int o = 0; o < 2; o++) for (int t = 0; t < 9; t++) for (int n = 0; n < 64; n++) zHand[o, t, n] = R();
            zTurn = R();
        }

        void ToggleHand(int owner, PieceType t)
        {
            Hash ^= zHand[owner, (int)t, Hands[owner, (int)t]];
        }

        // ---- 初期局面 ----
        public static ShogiState Initial()
        {
            var s = new ShogiState();
            // 普通の将棋の最初の 3 段を、9 つの段それぞれに並べる（片側 180 枚）
            var line = new[] { PieceType.Lance, PieceType.Knight, PieceType.Silver, PieceType.Gold, PieceType.Gold,
                               PieceType.Gold, PieceType.Silver, PieceType.Knight, PieceType.Lance };
            for (int owner = 0; owner < 2; owner++)
            {
                int Layer(int rel) { return owner == 0 ? rel : 8 - rel; }
                for (int y = 0; y < 9; y++)
                {
                    for (int x = 0; x < 9; x++)
                    {
                        // 1層: 香桂銀金○金銀桂香（○は五段だけ玉、ほかは金）
                        var t = (x == 4 && y == 4) ? PieceType.King : line[x];
                        s.Board[Index(x, y, Layer(0))] = Piece.Make(t, owner, false);
                        // 3層: 全部歩
                        s.Board[Index(x, y, Layer(2))] = Piece.Make(PieceType.Pawn, owner, false);
                    }
                    // 2層: 8筋に角、2筋に飛（筋 = 9 - x）
                    s.Board[Index(9 - 8, y, Layer(1))] = Piece.Make(PieceType.Bishop, owner, false);
                    s.Board[Index(9 - 2, y, Layer(1))] = Piece.Make(PieceType.Rook, owner, false);
                }
            }
            s.RecomputeHash();
            return s;
        }

        public void RecomputeHash()
        {
            Hash = 0;
            for (int i = 0; i < Cells; i++) if (Board[i] != 0) Hash ^= zBoard[i, Board[i]];
            for (int o = 0; o < 2; o++)
                foreach (var t in HandTypes) Hash ^= zHand[o, (int)t, Hands[o, (int)t]];
            if (Turn == 1) Hash ^= zTurn;
        }

        public ShogiState Clone()
        {
            var s = new ShogiState();
            Array.Copy(Board, s.Board, Cells);
            s.Hands = (int[,])Hands.Clone();
            s.Turn = Turn;
            s.Hash = Hash;
            return s;
        }

        // ---- 成り・行き所 ----
        public static int RelLayer(int owner, int z) { return owner == 0 ? z : 8 - z; } // 自分から見た層（0 が自陣の奥）

        public static bool InPromotionZone(int owner, int z) { return RelLayer(owner, z) >= 6; }

        public static bool IsDeadSquare(PieceType t, int owner, int z)
        {
            int rel = RelLayer(owner, z);
            if (t == PieceType.Pawn) return rel == 8;
            if (t == PieceType.Knight) return rel >= 7;
            return false;
        }

        // ---- 利き ----
        // 駒 from が利いているマスを列挙する
        public void ForEachTarget(int from, Action<int> cb)
        {
            int p = Board[from];
            var mv = MovementOf(p);
            int f = Piece.Owner(p) == 0 ? 1 : -1;
            int x = X(from), y = Y(from), z = Z(from);
            foreach (var d in mv.Steps)
            {
                int nx = x + d.Dx, ny = y + d.Dy, nz = z + d.Dz * f;
                if (Inside(nx, ny, nz)) cb(Index(nx, ny, nz));
            }
            foreach (var d in mv.Slides)
            {
                int nx = x + d.Dx, ny = y + d.Dy, nz = z + d.Dz * f;
                while (Inside(nx, ny, nz))
                {
                    int to = Index(nx, ny, nz);
                    cb(to);
                    if (Board[to] != 0) break;
                    nx += d.Dx; ny += d.Dy; nz += d.Dz * f;
                }
            }
        }

        public int FindKing(int owner)
        {
            int k = Piece.Make(PieceType.King, owner, false);
            for (int i = 0; i < Cells; i++) if (Board[i] == k) return i;
            return -1;
        }

        public bool IsAttacked(int target, int byOwner)
        {
            for (int i = 0; i < Cells; i++)
            {
                int p = Board[i];
                if (p == 0 || Piece.Owner(p) != byOwner) continue;
                // 遠すぎる駒は走り駒でなければ届かない（高速化）
                var mv = MovementOf(p);
                if (mv.Slides.Length == 0)
                {
                    int dx = Math.Abs(X(i) - X(target)), dy = Math.Abs(Y(i) - Y(target)), dz = Math.Abs(Z(i) - Z(target));
                    if (dx > 1 || dy > 1 || dz > 2) continue;
                }
                bool hit = false;
                ForEachTarget(i, to => { if (to == target) hit = true; });
                if (hit) return true;
            }
            return false;
        }

        public bool IsInCheck(int owner)
        {
            int k = FindKing(owner);
            return k >= 0 && IsAttacked(k, owner ^ 1);
        }

        // ---- 手の生成 ----
        public List<Move> GeneratePseudoMoves(bool includeDrops = true)
        {
            var moves = new List<Move>();
            int me = Turn;
            for (int from = 0; from < Cells; from++)
            {
                int p = Board[from];
                if (p == 0 || Piece.Owner(p) != me) continue;
                var t = Piece.Type(p);
                bool promotable = !Piece.Promoted(p) && Piece.CanPromote(t);
                int fromZ = Z(from);
                ForEachTarget(from, to =>
                {
                    int dst = Board[to];
                    if (dst != 0 && Piece.Owner(dst) == me) return;
                    int toZ = Z(to);
                    bool canPromote = promotable && (InPromotionZone(me, fromZ) || InPromotionZone(me, toZ));
                    if (canPromote) moves.Add(new Move(from, to, true));
                    if (!canPromote || !IsDeadSquare(t, me, toZ)) moves.Add(new Move(from, to, false));
                });
            }
            if (!includeDrops) return moves;

            bool[] pawnColumn = null; // [x + 9y] 自分の歩がある縦の列
            foreach (var t in HandTypes)
            {
                if (Hands[me, (int)t] == 0) continue;
                if (t == PieceType.Pawn && pawnColumn == null)
                {
                    pawnColumn = new bool[81];
                    int myPawn = Piece.Make(PieceType.Pawn, me, false);
                    for (int i = 0; i < Cells; i++) if (Board[i] == myPawn) pawnColumn[i % 81] = true;
                }
                for (int to = 0; to < Cells; to++)
                {
                    if (Board[to] != 0) continue;
                    if (IsDeadSquare(t, me, Z(to))) continue;
                    if (t == PieceType.Pawn && pawnColumn[to % 81]) continue; // 二歩
                    moves.Add(Move.MakeDrop(t, to));
                }
            }
            return moves;
        }

        public Undo MakeMove(Move m)
        {
            var u = new Undo { Move = m };
            int me = Turn;
            if (m.IsDrop)
            {
                int p = Piece.Make(m.Drop, me, false);
                ToggleHand(me, m.Drop);
                Hands[me, (int)m.Drop]--;
                ToggleHand(me, m.Drop);
                Board[m.To] = p;
                Hash ^= zBoard[m.To, p];
            }
            else
            {
                int p = Board[m.From];
                int cap = Board[m.To];
                u.Moved = p;
                u.Captured = cap;
                if (cap != 0)
                {
                    Hash ^= zBoard[m.To, cap];
                    var ct = Piece.Type(cap);
                    ToggleHand(me, ct);
                    Hands[me, (int)ct]++;
                    ToggleHand(me, ct);
                }
                int np = m.Promote ? (p | Piece.PromotedBit) : p;
                Hash ^= zBoard[m.From, p];
                Hash ^= zBoard[m.To, np];
                Board[m.To] = np;
                Board[m.From] = 0;
            }
            Turn ^= 1;
            Hash ^= zTurn;
            return u;
        }

        public void UnmakeMove(Undo u)
        {
            var m = u.Move;
            Turn ^= 1;
            Hash ^= zTurn;
            int me = Turn;
            if (m.IsDrop)
            {
                Hash ^= zBoard[m.To, Board[m.To]];
                Board[m.To] = 0;
                ToggleHand(me, m.Drop);
                Hands[me, (int)m.Drop]++;
                ToggleHand(me, m.Drop);
            }
            else
            {
                Hash ^= zBoard[m.To, Board[m.To]];
                Hash ^= zBoard[m.From, u.Moved];
                Board[m.From] = u.Moved;
                Board[m.To] = u.Captured;
                if (u.Captured != 0)
                {
                    Hash ^= zBoard[m.To, u.Captured];
                    var ct = Piece.Type(u.Captured);
                    ToggleHand(me, ct);
                    Hands[me, (int)ct]--;
                    ToggleHand(me, ct);
                }
            }
        }

        // 手を指した後に自玉が取られないか
        bool LeavesKingSafe(Move m)
        {
            int me = Turn;
            var u = MakeMove(m);
            bool ok = !IsInCheck(me);
            UnmakeMove(u);
            return ok;
        }

        // 盤上の駒の動きで王手を外せるか（打ち歩詰めの判定用。歩の王手は隣接なので合駒では防げない）
        bool HasLegalBoardMove()
        {
            foreach (var m in GeneratePseudoMoves(false))
                if (LeavesKingSafe(m)) return true;
            return false;
        }

        bool IsLegal(Move m)
        {
            int me = Turn;
            var u = MakeMove(m);
            bool ok = !IsInCheck(me);
            if (ok && m.IsDrop && m.Drop == PieceType.Pawn && IsInCheck(me ^ 1))
            {
                if (!HasLegalBoardMove()) ok = false; // 打ち歩詰め
            }
            UnmakeMove(u);
            return ok;
        }

        public List<Move> GenerateLegalMoves()
        {
            var legal = new List<Move>();
            bool inCheck = IsInCheck(Turn);
            foreach (var m in GeneratePseudoMoves())
            {
                // 王手されていない時の打つ手は、自玉を危険にしない（打ち歩詰めだけ確かめる）
                if (m.IsDrop && !inCheck && m.Drop != PieceType.Pawn) { legal.Add(m); continue; }
                if (IsLegal(m)) legal.Add(m);
            }
            return legal;
        }

        public bool HasAnyLegalMove()
        {
            foreach (var m in GeneratePseudoMoves(false)) if (LeavesKingSafe(m)) return true;
            foreach (var m in GeneratePseudoMoves())
                if (m.IsDrop && IsLegal(m)) return true;
            return false;
        }

        // ---- 表記 ----
        static readonly string[] KanNum = { "一", "二", "三", "四", "五", "六", "七", "八", "九" };
        static readonly string[] ZenNum = { "１", "２", "３", "４", "５", "６", "７", "８", "９" };

        public static string PieceName(PieceType t, bool promoted)
        {
            if (promoted)
            {
                switch (t)
                {
                    case PieceType.Rook: return "龍";
                    case PieceType.Bishop: return "馬";
                    case PieceType.Silver: return "成銀";
                    case PieceType.Knight: return "成桂";
                    case PieceType.Lance: return "成香";
                    case PieceType.Pawn: return "と";
                }
            }
            switch (t)
            {
                case PieceType.King: return "玉";
                case PieceType.Rook: return "飛";
                case PieceType.Bishop: return "角";
                case PieceType.Gold: return "金";
                case PieceType.Silver: return "銀";
                case PieceType.Knight: return "桂";
                case PieceType.Lance: return "香";
                case PieceType.Pawn: return "歩";
            }
            return "";
        }

        // 盤に表示する 1 文字
        public static string PieceChar(int p)
        {
            var t = Piece.Type(p);
            if (t == PieceType.King) return Piece.Owner(p) == 0 ? "玉" : "王";
            if (!Piece.Promoted(p)) return PieceName(t, false);
            switch (t)
            {
                case PieceType.Silver: return "全";
                case PieceType.Knight: return "圭";
                case PieceType.Lance: return "杏";
            }
            return PieceName(t, true);
        }

        // 例: ５五３層
        public static string SquareName(int i)
        {
            return ZenNum[8 - X(i)] + KanNum[Y(i)] + ZenNum[Z(i)] + "層";
        }

        // 指す前の局面で呼ぶ。例: ▲５五４層歩(５五３層)
        public string MoveText(Move m, int prevTo)
        {
            string mark = Turn == 0 ? "▲" : "△";
            string dest = m.To == prevTo ? "同　" : SquareName(m.To);
            if (m.IsDrop) return mark + dest + PieceName(m.Drop, false) + "打";
            int p = Board[m.From];
            string name = PieceName(Piece.Type(p), Piece.Promoted(p)) + (m.Promote ? "成" : "");
            return mark + dest + name + "(" + SquareName(m.From) + ")";
        }
    }
}
