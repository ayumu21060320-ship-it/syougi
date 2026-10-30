// 立体将棋の画面と操作。シーンを再生すると自動で起動する（シーンに何も置かなくてよい）
//   左クリック: 駒を選ぶ・指す / 右ドラッグ: 盤を回す / ホイール: 拡大縮小 / ↑↓キー: 見る層を変える
// 駒は各層に寝かせて置く（表が上。先手の駒は奥向き、後手の駒は手前向き）

using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;
using Shogi3D;

public class ShogiGame : MonoBehaviour
{
    const float Cell = 1.0f;       // マスの間隔（横）
    const float LayerGap = 1.0f;   // 層の間隔（高さ）

    static readonly Color Wood = new Color(0.95f, 0.84f, 0.58f);
    static readonly Color WoodDim = new Color(0.45f, 0.40f, 0.30f);
    static readonly Color SelectedColor = new Color(0.55f, 0.78f, 1f);
    static readonly Color LastColor = new Color(1f, 0.72f, 0.35f);
    static readonly Color CheckColor = new Color(1f, 0.35f, 0.35f);
    static readonly Color TargetColor = new Color(0.35f, 0.55f, 1f, 0.28f);   // 動ける・打てるマス（半透明の青）
    static readonly Color CaptureColor = new Color(1f, 0.35f, 0.3f, 0.35f);   // 相手の駒を取れるマス（半透明の赤）
    static readonly Color LineColor = new Color(0.35f, 0.25f, 0.12f);
    static readonly Color FrameColor = new Color(0.62f, 0.62f, 0.68f);
    static readonly Color Background = new Color(0.95f, 0.95f, 0.97f);
    const float PieceScale = 1.25f;   // マスに対する駒の大きさ
    const float PieceHalf = 0.06f;    // 駒の厚みの半分

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (FindAnyObjectByType<ShogiGame>() == null) new GameObject("ShogiGame").AddComponent<ShogiGame>();
    }

    // ---- 対局の状態 ----
    ShogiState state;
    List<Move> legal;
    readonly List<(Undo undo, string text)> history = new List<(Undo, string)>();
    readonly Dictionary<ulong, int> positionCount = new Dictionary<ulong, int>();
    int selectedFrom = -1;
    PieceType selectedDrop = PieceType.None;
    string result;
    Task<Move?> cpuTask;
    int gameId;
    int mode;        // 0 = CPU（あなたが先手）, 1 = CPU（あなたが後手）, 2 = 2人
    int level = 2;   // 1..3
    int focusLayer;  // 0..8
    bool focusOnly;  // 見ている層だけ強調する
    List<Move> pendingPromotion;

    static readonly string[] ModeNames = { "CPU と対局（あなたが先手）", "CPU と対局（あなたが後手）", "2人で対局" };
    static readonly string[] LevelNames = { "", "弱い", "普通", "強い" };

    // ---- 表示 ----
    Camera cam;
    Font font;
    Material baseMat;
    Material glassMat; // 半透明
    Material pieceTextMat; // 駒の文字（手前の駒に隠れる）
    MaterialPropertyBlock mpb;
    Transform pieceRoot, gridRoot;
    TextMesh layerLabel;
    readonly List<Transform> billboards = new List<Transform>(); // いつもカメラを向く文字
    readonly Dictionary<Collider, int> colliderToCell = new Dictionary<Collider, int>();
    readonly List<Transform> pieceObjects = new List<Transform>();
    readonly Dictionary<int, Mesh> pieceMeshes = new Dictionary<int, Mesh>();
    GameObject[] markers;
    float yaw = -35f, pitch = 38f, distance = 20f;

    // ---- UI ----
    Text statusText, kifuText, modeText, levelText, layerText, focusText;
    Transform[] handRows = new Transform[2];
    GameObject promoteDialog;

    void Start()
    {
        cam = Camera.main;
        if (cam == null)
        {
            cam = new GameObject("Main Camera").AddComponent<Camera>();
            cam.tag = "MainCamera";
        }
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = Background;
        if (FindAnyObjectByType<Light>() == null)
        {
            var light = new GameObject("Light").AddComponent<Light>();
            light.type = LightType.Directional;
            light.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
        }

        font = Font.CreateDynamicFontFromOSFont(new[] { "Yu Gothic UI", "Yu Gothic", "Meiryo", "MS Gothic", "Segoe UI Symbol" }, 64);
        font.RequestCharactersInTexture("玉王飛角金銀桂香歩龍馬全圭杏と0123456789層", 64);
        // 駒の文字は専用シェーダー（Assets/Resources/ShogiPieceText.shader）で描き、手前の駒から透けないようにする
        var textShader = Shader.Find("Shogi3D/PieceText");
        pieceTextMat = textShader != null ? new Material(textShader) : font.material;
        pieceTextMat.mainTexture = font.material.mainTexture;
        Font.textureRebuilt += OnFontTextureRebuilt;

        var tmp = GameObject.CreatePrimitive(PrimitiveType.Cube);
        baseMat = tmp.GetComponent<MeshRenderer>().sharedMaterial; // プロジェクト既定の（URP の）マテリアル
        Destroy(tmp);
        // 半透明は専用シェーダー（Assets/Resources/ShogiGlass.shader）で描く。見つからなければ URP の Lit を半透明に切り替える
        var glassShader = Shader.Find("Shogi3D/Glass");
        glassMat = glassShader != null ? new Material(glassShader) : MakeGlass(baseMat);
        mpb = new MaterialPropertyBlock();

        BuildBoardFrame();
        BuildMarkers();
        pieceRoot = new GameObject("Pieces").transform;
        BuildUI();
        NewGame();
    }

    // 新しい文字が必要になるとフォントの画像が作り直されるので、駒の文字のマテリアルにも渡す
    void OnFontTextureRebuilt(Font f)
    {
        if (f == font && pieceTextMat != null) pieceTextMat.mainTexture = font.material.mainTexture;
    }

    void OnDestroy()
    {
        Font.textureRebuilt -= OnFontTextureRebuilt;
    }

    // ================= 盤の組み立て =================

    static Vector3 CellPos(int i)
    {
        return new Vector3((ShogiState.X(i) - 4) * Cell, (ShogiState.Z(i) - 4) * LayerGap, (4 - ShogiState.Y(i)) * Cell);
    }

    GameObject MakeBar(Transform parent, Vector3 center, Vector3 size, Color color)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        Destroy(go.GetComponent<Collider>());
        go.transform.SetParent(parent, false);
        go.transform.localPosition = center;
        go.transform.localScale = size;
        SetColor(go.GetComponent<Renderer>(), color);
        return go;
    }

    // URP の Lit マテリアルを半透明にした複製を作る
    static Material MakeGlass(Material src)
    {
        var m = new Material(src);
        m.SetFloat("_Surface", 1f); // Transparent
        m.SetFloat("_Blend", 0f);   // Alpha
        m.SetOverrideTag("RenderType", "Transparent");
        m.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
        m.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        m.SetFloat("_SrcBlendAlpha", (float)UnityEngine.Rendering.BlendMode.One);
        m.SetFloat("_DstBlendAlpha", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        m.SetFloat("_ZWrite", 0f);
        m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        return m;
    }

    void SetColor(Renderer r, Color c, Material mat = null)
    {
        r.sharedMaterial = mat != null ? mat : baseMat;
        r.GetPropertyBlock(mpb);
        mpb.SetColor("_BaseColor", c);
        mpb.SetColor("_Color", c);
        r.SetPropertyBlock(mpb);
    }

    void BuildBoardFrame()
    {
        // 立方体の外枠（12 本の辺）
        var frame = new GameObject("Frame").transform;
        float h = 4.5f * Cell, v = 4.5f * LayerGap, t = 0.04f;
        foreach (var sy in new[] { -1, 1 })
            foreach (var sz in new[] { -1, 1 })
                MakeBar(frame, new Vector3(0, sy * v, sz * h), new Vector3(2 * h, t, t), FrameColor);
        foreach (var sx in new[] { -1, 1 })
            foreach (var sz in new[] { -1, 1 })
                MakeBar(frame, new Vector3(sx * h, 0, sz * h), new Vector3(t, 2 * v, t), FrameColor);
        foreach (var sx in new[] { -1, 1 })
            foreach (var sy in new[] { -1, 1 })
                MakeBar(frame, new Vector3(sx * h, sy * v, 0), new Vector3(t, t, 2 * h), FrameColor);

        // 見ている層のマス目（層を変えるときは高さだけ動かす）。駒の下面に合わせる
        gridRoot = new GameObject("LayerGrid").transform;
        float gy = -PieceHalf - 0.01f;
        for (int k = 0; k <= 9; k++)
        {
            float p = (k - 4.5f) * Cell;
            MakeBar(gridRoot, new Vector3(p, gy, 0), new Vector3(0.015f, 0.015f, 9 * Cell), LineColor);
            MakeBar(gridRoot, new Vector3(0, gy, p), new Vector3(9 * Cell, 0.015f, 0.015f), LineColor);
        }
        layerLabel = MakeLabel(gridRoot, "", new Vector3(-5.4f * Cell, gy, -4.5f * Cell), 0.07f);

        // 立方体の辺に番号を並べる: 筋（手前の下の辺）、段（右の下の辺）、層（右奥の縦の辺）
        string[] kan = { "一", "二", "三", "四", "五", "六", "七", "八", "九" };
        float bottom = -4.5f * LayerGap;
        for (int k = 0; k < 9; k++)
        {
            MakeLabel(frame, (9 - k).ToString(), new Vector3((k - 4) * Cell, bottom, -5.0f * Cell), 0.06f);
            MakeLabel(frame, kan[k], new Vector3(5.0f * Cell, bottom, (4 - k) * Cell), 0.06f);
            MakeLabel(frame, (k + 1) + "層", new Vector3(5.3f * Cell, (k - 4) * LayerGap, 4.5f * Cell), 0.05f);
        }
    }

    TextMesh MakeLabel(Transform parent, string text, Vector3 pos, float size)
    {
        var go = new GameObject("Label");
        go.transform.SetParent(parent, false);
        go.transform.localPosition = pos;
        billboards.Add(go.transform);
        var tm = go.AddComponent<TextMesh>();
        tm.font = font;
        tm.fontSize = 64;
        tm.characterSize = size;
        tm.anchor = TextAnchor.MiddleCenter;
        tm.alignment = TextAlignment.Center;
        tm.color = new Color(0.3f, 0.3f, 0.35f);
        tm.text = text;
        go.GetComponent<MeshRenderer>().sharedMaterial = font.material;
        return tm;
    }

    void BuildMarkers()
    {
        var root = new GameObject("Markers").transform;
        markers = new GameObject[ShogiState.Cells];
        for (int i = 0; i < ShogiState.Cells; i++)
        {
            // マスいっぱいの半透明の立方体（少し小さくして、となりのマスと区別できるようにする）
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.transform.SetParent(root, false);
            go.transform.localPosition = CellPos(i);
            go.transform.localScale = new Vector3(Cell, LayerGap, Cell) * 0.94f;
            SetColor(go.GetComponent<Renderer>(), TargetColor, glassMat);
            colliderToCell[go.GetComponent<Collider>()] = i;
            go.SetActive(false);
            markers[i] = go;
        }
    }

    // 将棋の駒の形（五角形の板）。玉は大きく、歩は小さく
    Mesh PieceMesh(PieceType t)
    {
        if (pieceMeshes.TryGetValue((int)t, out var cached)) return cached;
        float s = t == PieceType.King ? 1.0f : (t == PieceType.Rook || t == PieceType.Bishop) ? 0.95f :
                  (t == PieceType.Gold || t == PieceType.Silver) ? 0.9f : t == PieceType.Pawn ? 0.8f : 0.85f;
        var outline = new[]
        {
            new Vector2(-0.31f, -0.36f), new Vector2(0.31f, -0.36f), new Vector2(0.26f, 0.2f),
            new Vector2(0f, 0.36f), new Vector2(-0.26f, 0.2f),
        };
        float half = PieceHalf;
        s *= PieceScale;
        var verts = new List<Vector3>();
        var tris = new List<int>();
        void Tri(Vector3 a, Vector3 b, Vector3 c, Vector3 outward)
        {
            if (Vector3.Dot(Vector3.Cross(b - a, c - a), outward) < 0) { var tmpV = b; b = c; c = tmpV; }
            int n = verts.Count;
            verts.Add(a); verts.Add(b); verts.Add(c);
            tris.Add(n); tris.Add(n + 1); tris.Add(n + 2);
        }
        Vector3 P(Vector2 p, float z) { return new Vector3(p.x * s, p.y * s, z); }
        var center = Vector2.zero;
        for (int k = 0; k < outline.Length; k++)
        {
            var a = outline[k];
            var b = outline[(k + 1) % outline.Length];
            Tri(P(center, -half), P(a, -half), P(b, -half), Vector3.back);    // 表（文字の面）
            Tri(P(center, half), P(a, half), P(b, half), Vector3.forward);    // 裏
            var side = new Vector3(b.y - a.y, -(b.x - a.x), 0);              // 外向き
            Tri(P(a, -half), P(b, -half), P(b, half), side);
            Tri(P(a, -half), P(b, half), P(a, half), side);
        }
        var mesh = new Mesh();
        mesh.SetVertices(verts);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        pieceMeshes[(int)t] = mesh;
        return mesh;
    }

    // ================= 対局の進行 =================

    int CpuSide()
    {
        return mode == 0 ? 1 : mode == 1 ? 0 : -1;
    }

    static string SideName(int side)
    {
        return side == 0 ? "☗先手" : "☖後手";
    }

    void NewGame()
    {
        gameId++;
        cpuTask = null;
        state = ShogiState.Initial();
        history.Clear();
        positionCount.Clear();
        positionCount[state.Hash] = 1;
        result = null;
        ClearSelection();
        pendingPromotion = null;
        promoteDialog.SetActive(false);
        legal = state.GenerateLegalMoves();
        focusLayer = 4; // 玉のいる 5層
        yaw = mode == 1 ? 180f - 35f : -35f;
        Refresh();
        MaybeStartCpu();
    }

    void ClearSelection()
    {
        selectedFrom = -1;
        selectedDrop = PieceType.None;
    }

    bool HumanCanMove()
    {
        return result == null && cpuTask == null && pendingPromotion == null && state.Turn != CpuSide();
    }

    List<Move> MovesForSelection()
    {
        var list = new List<Move>();
        if (selectedFrom < 0 && selectedDrop == PieceType.None) return list;
        foreach (var m in legal)
        {
            if (selectedFrom >= 0 ? m.From == selectedFrom : (m.IsDrop && m.Drop == selectedDrop)) list.Add(m);
        }
        return list;
    }

    void OnCellClicked(int i)
    {
        if (!HumanCanMove()) return;
        var to = MovesForSelection().FindAll(m => m.To == i);
        if (to.Count == 1) { Play(to[0]); return; }
        if (to.Count == 2) { AskPromotion(to); return; }
        int p = state.Board[i];
        if (p != 0 && Piece.Owner(p) == state.Turn && selectedFrom != i)
        {
            selectedFrom = i;
            selectedDrop = PieceType.None;
        }
        else ClearSelection();
        Refresh();
    }

    void OnHandClicked(int side, PieceType t)
    {
        if (!HumanCanMove() || side != state.Turn) return;
        selectedFrom = -1;
        selectedDrop = selectedDrop == t ? PieceType.None : t;
        Refresh();
    }

    void AskPromotion(List<Move> moves)
    {
        pendingPromotion = moves;
        promoteDialog.SetActive(true);
    }

    void AnswerPromotion(bool promote)
    {
        var moves = pendingPromotion;
        pendingPromotion = null;
        promoteDialog.SetActive(false);
        if (moves == null) return;
        Play(moves.Find(m => m.Promote == promote));
    }

    void Play(Move m)
    {
        int prevTo = history.Count > 0 ? history[history.Count - 1].undo.Move.To : -1;
        string text = state.MoveText(m, prevTo);
        var u = state.MakeMove(m);
        history.Add((u, text));
        positionCount.TryGetValue(state.Hash, out int c);
        positionCount[state.Hash] = c + 1;
        ClearSelection();
        AfterMove();
    }

    void AfterMove()
    {
        legal = state.GenerateLegalMoves();
        result = null;
        if (legal.Count == 0)
            result = "まで" + history.Count + "手で" + SideName(state.Turn ^ 1) + "の勝ち（詰み）";
        else if (positionCount[state.Hash] >= 4)
            result = "まで" + history.Count + "手で千日手（引き分け）";
        Refresh();
        MaybeStartCpu();
    }

    void MaybeStartCpu()
    {
        if (result != null || state.Turn != CpuSide()) return;
        var snapshot = state.Clone();
        int lv = level;
        int seed = Random.Range(0, int.MaxValue);
        cpuTask = Task.Run(() => ShogiAI.ChooseMove(snapshot, lv, seed));
        Refresh();
    }

    void UndoOne()
    {
        if (history.Count == 0) return;
        var h = history[history.Count - 1];
        history.RemoveAt(history.Count - 1);
        positionCount[state.Hash]--;
        state.UnmakeMove(h.undo);
    }

    void OnUndo()
    {
        if (cpuTask != null || history.Count == 0) return;
        gameId++;
        UndoOne();
        if (state.Turn == CpuSide() && history.Count > 0) UndoOne(); // CPU 対局では自分の手番まで戻す
        ClearSelection();
        result = null;
        legal = state.GenerateLegalMoves();
        Refresh();
        MaybeStartCpu();
    }

    void OnResign()
    {
        if (result != null || cpuTask != null) return;
        result = SideName(state.Turn) + "の投了。" + SideName(state.Turn ^ 1) + "の勝ち";
        ClearSelection();
        Refresh();
    }

    // ================= 毎フレーム =================

    void Update()
    {
        if (cpuTask != null && cpuTask.IsCompleted)
        {
            var task = cpuTask;
            cpuTask = null;
            if (task.Status == TaskStatus.RanToCompletion && task.Result.HasValue) Play(task.Result.Value);
            else Refresh();
        }

        var mouse = Mouse.current;
        if (mouse != null)
        {
            if (mouse.rightButton.isPressed || mouse.middleButton.isPressed)
            {
                var d = mouse.delta.ReadValue();
                yaw += d.x * 0.3f;
                pitch = Mathf.Clamp(pitch - d.y * 0.3f, -80f, 85f);
            }
            float scroll = mouse.scroll.ReadValue().y;
            if (Mathf.Abs(scroll) > 0.01f) distance = Mathf.Clamp(distance - Mathf.Sign(scroll) * 1.2f, 6f, 40f);

            bool overUI = EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();
            if (mouse.leftButton.wasPressedThisFrame && !overUI) ClickBoard(mouse.position.ReadValue());
        }

        var kb = Keyboard.current;
        if (kb != null)
        {
            if (kb.upArrowKey.wasPressedThisFrame) ChangeLayer(1);
            if (kb.downArrowKey.wasPressedThisFrame) ChangeLayer(-1);
        }
    }

    void LateUpdate()
    {
        if (cam == null) return;
        var rot = Quaternion.Euler(pitch, yaw, 0f);
        cam.transform.rotation = rot;
        cam.transform.position = rot * new Vector3(0f, 0f, -distance);
        foreach (var t in billboards) t.rotation = cam.transform.rotation;
    }

    void ClickBoard(Vector2 screenPos)
    {
        if (pendingPromotion != null) return;
        var ray = cam.ScreenPointToRay(screenPos);
        if (Physics.Raycast(ray, out var hit, 200f) && colliderToCell.TryGetValue(hit.collider, out int i))
        {
            OnCellClicked(i);
        }
        else if (selectedFrom >= 0 || selectedDrop != PieceType.None)
        {
            ClearSelection();
            Refresh();
        }
    }

    void ChangeLayer(int delta)
    {
        focusLayer = Mathf.Clamp(focusLayer + delta, 0, 8);
        Refresh();
    }

    // ================= 表示の更新 =================

    void Refresh()
    {
        if (state == null) return;
        gridRoot.position = new Vector3(0f, (focusLayer - 4) * LayerGap, 0f);
        layerLabel.text = (focusLayer + 1) + "層";

        int lastTo = history.Count > 0 ? history[history.Count - 1].undo.Move.To : -1;
        int checkSq = state.IsInCheck(state.Turn) ? state.FindKing(state.Turn) : -1;
        RebuildPieces(lastTo, checkSq);

        // 指せるマスの印。持ち駒を打つ手は数が多いので、「この層だけ」のときは見ている層だけ出す
        foreach (var go in markers) if (go.activeSelf) go.SetActive(false);
        foreach (var m in MovesForSelection())
        {
            if (m.IsDrop && focusOnly && ShogiState.Z(m.To) != focusLayer) continue;
            var marker = markers[m.To];
            if (marker.activeSelf) continue; // 成る・成らないの 2 手がある
            SetColor(marker.GetComponent<Renderer>(), state.Board[m.To] == 0 ? TargetColor : CaptureColor, glassMat);
            marker.SetActive(true);
        }

        RefreshUI(checkSq);
    }

    void RebuildPieces(int lastTo, int checkSq)
    {
        foreach (var t in pieceObjects)
        {
            colliderToCell.Remove(t.GetComponent<Collider>());
            Destroy(t.gameObject);
        }
        pieceObjects.Clear();
        var targets = new HashSet<int>();
        foreach (var m in MovesForSelection()) targets.Add(m.To);

        for (int i = 0; i < ShogiState.Cells; i++)
        {
            int p = state.Board[i];
            if (p == 0) continue;
            var type = Piece.Type(p);
            int owner = Piece.Owner(p);
            bool dim = focusOnly && ShogiState.Z(i) != focusLayer && i != selectedFrom && !targets.Contains(i);

            var go = new GameObject("Piece");
            go.transform.SetParent(pieceRoot, false);
            go.transform.position = CellPos(i);
            // 寝かせて表を上に。先手は奥（+Z）向き、後手は手前向き
            go.transform.rotation = Quaternion.Euler(90f, owner == 0 ? 0f : 180f, 0f);
            go.transform.localScale = Vector3.one * (dim ? 0.45f : 1f);
            go.AddComponent<MeshFilter>().sharedMesh = PieceMesh(type);
            var r = go.AddComponent<MeshRenderer>();
            Color c = dim ? WoodDim : Wood;
            if (i == lastTo) c = LastColor;
            if (i == selectedFrom) c = SelectedColor;
            if (i == checkSq) c = CheckColor;
            SetColor(r, c);
            var col = go.AddComponent<BoxCollider>();
            col.size = new Vector3(0.62f * PieceScale, 0.72f * PieceScale, PieceHalf * 2f + 0.02f);
            colliderToCell[col] = i;

            var label = new GameObject("Char");
            label.transform.SetParent(go.transform, false);
            label.transform.localPosition = new Vector3(0f, -0.04f * PieceScale, -PieceHalf - 0.005f);
            var tm = label.AddComponent<TextMesh>();
            tm.font = font;
            tm.fontSize = 64;
            tm.characterSize = (type == PieceType.King ? 0.065f : 0.055f) * PieceScale;
            tm.anchor = TextAnchor.MiddleCenter;
            tm.alignment = TextAlignment.Center;
            tm.text = ShogiState.PieceChar(p);
            tm.color = Piece.Promoted(p) ? new Color(0.75f, 0.1f, 0.05f) : Color.black;
            label.GetComponent<MeshRenderer>().sharedMaterial = pieceTextMat;
            pieceObjects.Add(go.transform);
        }
    }

    void RefreshUI(int checkSq)
    {
        if (result != null) statusText.text = result;
        else
        {
            string s = SideName(state.Turn) + "の番です";
            if (cpuTask != null) s = SideName(state.Turn) + "（CPU）が考えています…";
            if (checkSq >= 0) s = "王手！ " + s;
            if (selectedFrom >= 0)
                s += "\n選択中: " + ShogiState.SquareName(selectedFrom) + " " + ShogiState.PieceChar(state.Board[selectedFrom]);
            else if (selectedDrop != PieceType.None)
                s += "\n選択中: 持ち駒の" + ShogiState.PieceName(selectedDrop, false) + (focusOnly ? "（見ている層に打てる場所を表示）" : "");
            statusText.text = s;
        }

        modeText.text = "モード: " + ModeNames[mode];
        levelText.text = "CPU の強さ: " + LevelNames[level];
        layerText.text = "見ている層: " + (focusLayer + 1) + "層";
        focusText.text = focusOnly ? "表示: この層だけ" : "表示: 全部の層";

        for (int side = 0; side < 2; side++)
        {
            var row = handRows[side];
            for (int k = row.childCount - 1; k >= 0; k--) Destroy(row.GetChild(k).gameObject);
            bool any = false;
            foreach (var t in ShogiState.HandTypes)
            {
                int n = state.Hands[side, (int)t];
                if (n == 0) continue;
                any = true;
                int sd = side;
                var tt = t;
                bool sel = selectedDrop == t && state.Turn == side;
                MakeButton(row, ShogiState.PieceName(t, false) + (n > 1 ? n.ToString() : ""), 56,
                    () => OnHandClicked(sd, tt), sel ? SelectedColor : Wood);
            }
            if (!any) MakeText(row, "なし", 18, TextAnchor.MiddleLeft, 40, 56);
        }

        var sb = new System.Text.StringBuilder();
        int start = Mathf.Max(0, history.Count - 28);
        for (int k = start; k < history.Count; k++) sb.Append(k + 1).Append(". ").Append(history[k].text).Append('\n');
        kifuText.text = sb.ToString();
    }

    // ================= UI の組み立て =================

    void BuildUI()
    {
        if (FindAnyObjectByType<EventSystem>() == null)
        {
            var es = new GameObject("EventSystem");
            es.AddComponent<EventSystem>();
            es.AddComponent<InputSystemUIInputModule>();
        }

        var canvasGo = new GameObject("UI");
        var canvas = canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        var scaler = canvasGo.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight = 0.5f;
        canvasGo.AddComponent<GraphicRaycaster>();

        // 左のパネル
        var left = MakePanel(canvasGo.transform, new Vector2(0, 1), new Vector2(16, -16), new Vector2(420, 0));
        MakeText(left, "立体将棋", 34, TextAnchor.MiddleLeft, 44);
        statusText = MakeText(left, "", 22, TextAnchor.UpperLeft, 64);
        var row1 = MakeRow(left);
        MakeButton(row1, "新しい対局", 130, NewGame, Wood);
        MakeButton(row1, "待った", 100, OnUndo, Wood);
        MakeButton(row1, "投了", 90, OnResign, Wood);
        modeText = MakeButton(MakeRow(left), "", 388, () => { mode = (mode + 1) % 3; NewGame(); }, Wood);
        levelText = MakeButton(MakeRow(left), "", 388, () => { level = level % 3 + 1; Refresh(); }, Wood);
        var row4 = MakeRow(left);
        MakeButton(row4, "▼", 50, () => ChangeLayer(-1), Wood);
        layerText = MakeText(row4, "", 20, TextAnchor.MiddleCenter, 40, 170);
        MakeButton(row4, "▲", 50, () => ChangeLayer(1), Wood);
        focusText = MakeButton(MakeRow(left), "", 388, () => { focusOnly = !focusOnly; Refresh(); }, Wood);
        MakeText(left, "☗先手の持ち駒", 20, TextAnchor.MiddleLeft, 28);
        handRows[0] = MakeRow(left);
        MakeText(left, "☖後手の持ち駒", 20, TextAnchor.MiddleLeft, 28);
        handRows[1] = MakeRow(left);
        MakeText(left, "左クリック: 選ぶ・指す\n右ドラッグ: 回す　ホイール: 拡大縮小\n↑↓キー: 見る層を変える", 17, TextAnchor.UpperLeft, 72);

        // 右のパネル（棋譜）
        var right = MakePanel(canvasGo.transform, new Vector2(1, 1), new Vector2(-16, -16), new Vector2(380, 0));
        MakeText(right, "棋譜", 24, TextAnchor.MiddleLeft, 32);
        kifuText = MakeText(right, "", 17, TextAnchor.UpperLeft, 760);

        // 成りの確認
        promoteDialog = new GameObject("PromoteDialog");
        promoteDialog.transform.SetParent(canvasGo.transform, false);
        var drt = promoteDialog.AddComponent<RectTransform>();
        drt.anchorMin = drt.anchorMax = drt.pivot = new Vector2(0.5f, 0.5f);
        drt.sizeDelta = new Vector2(320, 150);
        promoteDialog.AddComponent<Image>().color = new Color(1f, 0.97f, 0.9f, 0.97f);
        var dv = promoteDialog.AddComponent<VerticalLayoutGroup>();
        dv.padding = new RectOffset(16, 16, 16, 16);
        dv.spacing = 12;
        dv.childAlignment = TextAnchor.MiddleCenter;
        dv.childControlWidth = dv.childControlHeight = true;
        dv.childForceExpandWidth = dv.childForceExpandHeight = false;
        MakeText(promoteDialog.transform, "成りますか？", 26, TextAnchor.MiddleCenter, 40);
        var drow = MakeRow(promoteDialog.transform);
        MakeButton(drow, "成る", 130, () => AnswerPromotion(true), Wood);
        MakeButton(drow, "成らない", 130, () => AnswerPromotion(false), Wood);
        promoteDialog.SetActive(false);
    }

    Transform MakePanel(Transform parent, Vector2 corner, Vector2 offset, Vector2 size)
    {
        var go = new GameObject("Panel");
        go.transform.SetParent(parent, false);
        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = rt.pivot = corner;
        rt.anchoredPosition = offset;
        rt.sizeDelta = size;
        go.AddComponent<Image>().color = new Color(1f, 0.98f, 0.94f, 0.88f);
        var v = go.AddComponent<VerticalLayoutGroup>();
        v.padding = new RectOffset(14, 14, 12, 12);
        v.spacing = 8;
        v.childControlWidth = v.childControlHeight = true;
        v.childForceExpandWidth = v.childForceExpandHeight = false;
        var fit = go.AddComponent<ContentSizeFitter>();
        fit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        return go.transform;
    }

    Transform MakeRow(Transform parent)
    {
        var go = new GameObject("Row");
        go.transform.SetParent(parent, false);
        go.AddComponent<RectTransform>();
        var h = go.AddComponent<HorizontalLayoutGroup>();
        h.spacing = 8;
        h.childAlignment = TextAnchor.MiddleLeft;
        h.childControlWidth = h.childControlHeight = true;
        h.childForceExpandWidth = h.childForceExpandHeight = false;
        go.AddComponent<LayoutElement>().minHeight = 44;
        return go.transform;
    }

    Text MakeText(Transform parent, string text, int size, TextAnchor anchor, float minHeight, float width = -1)
    {
        var go = new GameObject("Text");
        go.transform.SetParent(parent, false);
        go.AddComponent<RectTransform>();
        var t = go.AddComponent<Text>();
        t.font = font;
        t.fontSize = size;
        t.alignment = anchor;
        t.color = new Color(0.17f, 0.13f, 0.09f);
        t.horizontalOverflow = HorizontalWrapMode.Wrap;
        t.verticalOverflow = VerticalWrapMode.Overflow;
        t.text = text;
        var le = go.AddComponent<LayoutElement>();
        le.minHeight = minHeight;
        le.preferredWidth = width;
        return t;
    }

    // ボタンを作り、ボタンの文字（Text）を返す
    Text MakeButton(Transform parent, string label, float width, UnityEngine.Events.UnityAction onClick, Color color)
    {
        var go = new GameObject("Button");
        go.transform.SetParent(parent, false);
        go.AddComponent<RectTransform>();
        var img = go.AddComponent<Image>();
        img.color = color;
        var b = go.AddComponent<Button>();
        b.targetGraphic = img;
        b.onClick.AddListener(onClick);
        var le = go.AddComponent<LayoutElement>();
        le.preferredWidth = width;
        le.minHeight = 40;

        var tg = new GameObject("Text");
        tg.transform.SetParent(go.transform, false);
        var rt = tg.AddComponent<RectTransform>();
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = rt.offsetMax = Vector2.zero;
        var t = tg.AddComponent<Text>();
        t.font = font;
        t.fontSize = 20;
        t.alignment = TextAnchor.MiddleCenter;
        t.color = new Color(0.17f, 0.13f, 0.09f);
        t.text = label;
        return t;
    }
}
