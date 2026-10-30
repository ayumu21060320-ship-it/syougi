// 立体将棋の画面と操作。シーンを再生すると自動で起動する（シーンに何も置かなくてよい）
//   PC:     左クリック: 駒を選ぶ・指す / 右ドラッグ: 盤を回す / ホイール: 拡大縮小 / ↑↓キー: 見る層を変える
//   スマホ: タップ: 駒を選ぶ・指す / 1 本指ドラッグ: 盤を回す / 2 本指: 拡大縮小
// 画面が縦長（スマホの縦画面）なら、上に 3D の盤、下にパネルを並べる。ブラウザ（WebGL）でも動く
// 駒は各層に寝かせて置く（表が上。先手の駒は奥向き、後手の駒は手前向き）

using System.Collections;
using System.Collections.Generic;
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
    ShogiAI.Search cpuSearch; // CPU が考えている途中（毎フレーム少しずつ考える）
    const long CpuBudgetMs = 12; // 1 フレームに CPU が考える時間
    int gameId;
    int mode;        // 0 = CPU（あなたが先手）, 1 = CPU（あなたが後手）, 2 = 2人
    int level = 2;   // 1..3
    int focusLayer;  // 0..8
    bool focusOnly;  // 見ている層だけ強調する
    List<Move> pendingPromotion;
    bool animating;  // 駒が動いている間はクリックと CPU の着手を待たせる

    static readonly string[] ModeNames = { "CPU と対局（あなたが先手）", "CPU と対局（あなたが後手）", "2人で対局" };

    // ---- オンライン対戦（ShogiOnline.cs）----
    ShogiOnline online;      // 部屋を作った・入ったときだけ
    int onlineSide = -1;     // オンラインでの自分の手番（0 = 先手、1 = 後手、-1 = まだ始まっていない）
    string onlineStatus = "";
    bool onlineBusy;         // 部屋を作っている・入っている途中
    readonly Queue<Move> remoteMoves = new Queue<Move>(); // 相手の手（駒が動いている間は待たせる）
    InputField codeInput;
    Text onlineText;
    static readonly string[] LevelNames = { "", "弱い", "普通", "強い" };

    // ---- 表示 ----
    Camera cam;
    Font font;       // UI と番号の文字（ゴシック）
    Font pieceFont;  // 駒の文字。Assets/Resources/Fonts/ に置いたフォント。無ければ太字の明朝
    bool pieceFontIsCustom; // フォントを置いたか
    bool pieceFontIsGlyph;  // 英字に駒の絵が入っているフォント（K'sShogiPieces など）か
    const int PieceFontSize = 128; // 駒の文字を描く解像度。大きいほどくっきりする
    Material baseMat;
    Material glassMat; // 半透明
    Material pieceTextMat; // 駒の文字（手前の駒に隠れる）
    AudioSource audioSource;
    AudioClip komaSound;   // 駒を置く音
    AudioSource bgmSource; // BGM（くり返し流す）
    bool bgmOn = true;
    MaterialPropertyBlock mpb;
    Transform pieceRoot, gridRoot;
    TextMesh layerLabel;
    readonly List<Transform> billboards = new List<Transform>(); // いつもカメラを向く文字
    readonly Dictionary<Collider, int> colliderToCell = new Dictionary<Collider, int>();
    readonly List<Transform> pieceObjects = new List<Transform>();
    readonly Dictionary<int, Transform> pieceAt = new Dictionary<int, Transform>(); // マス → 駒のオブジェクト
    readonly Dictionary<int, Mesh> pieceMeshes = new Dictionary<int, Mesh>();
    GameObject[] markers;
    float yaw = -35f, pitch = 38f, distance = 20f;

    // ---- UI ----
    Text statusText, kifuText, modeText, levelText, layerText, focusText, bgmText;
    CanvasScaler scaler;
    RectTransform leftPanel, rightPanel;
    GameObject helpText, kifuTitle;
    int layoutPortrait = -1; // 今の並べ方（1 = 縦画面、0 = 横画面、-1 = まだ）
    // 右の 2D の盤（見ている層を普通の将棋盤の形で表示する。クリックでも指せる）
    const float BoardCell = 44f;
    Text layerBoardTitle;
    readonly Image[] layerCells = new Image[81];
    readonly Text[] layerCellTexts = new Text[81];
    readonly Image[] layerCellPieces = new Image[81]; // 駒の絵の画像を使うときの 2D の盤の駒
    static readonly Color BoardWood = new Color(0.93f, 0.78f, 0.52f);
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

        // UI の文字のフォント。Assets/Resources/UIFont/ に日本語フォント（Noto Sans JP など）を置けばそれを使う。
        // ブラウザ（WebGL）版では PC のフォントが使えないので、置かないと文字が表示されない
        var uiFonts = Resources.LoadAll<Font>("UIFont");
        font = uiFonts.Length > 0 ? uiFonts[0]
             : Font.CreateDynamicFontFromOSFont(new[] { "Yu Gothic UI", "Yu Gothic", "Meiryo", "MS Gothic", "Segoe UI Symbol" }, 64);
        font.RequestCharactersInTexture("0123456789層", 64);

        // 駒の文字のフォント。Assets/Resources/Fonts/ に .ttf / .otf を置くと、それを使う（名前は何でもよい。
        // 複数あるときは KomaFont という名前のもの、無ければ最初の 1 つ）。漢字の入ったフォントなら漢字で、
        // 英字に駒の絵が入ったフォント（K'sShogiPieces など）なら駒の絵で描く
        pieceFont = LoadPieceFont();
        pieceFontIsCustom = pieceFont != null;
        // 「歩」の字が入っていなければ、英字に駒の絵が入ったフォントとみなす。
        // Font.HasCharacter は Web（WebGL）版で正しく答えないことがあるので、実際に文字を作れるかで確かめる
        pieceFontIsGlyph = pieceFontIsCustom && !CanDraw(pieceFont, '歩');
        if (!pieceFontIsCustom)
            pieceFont = Font.CreateDynamicFontFromOSFont(new[] { "Yu Mincho", "YuMincho", "MS Mincho", "Yu Gothic" }, PieceFontSize);
        Debug.Log("駒のフォント: " + pieceFont.name + (pieceFontIsGlyph ? "（駒の絵）" : "（漢字）"));
        pieceFont.RequestCharactersInTexture(PieceChars, PieceFontSize, PieceFontStyle);
        // 駒の文字は専用シェーダー（Assets/Resources/ShogiPieceText.shader）で描き、手前の駒から透けないようにする
        var textShader = Shader.Find("Shogi3D/PieceText");
        pieceTextMat = textShader != null ? new Material(textShader) : pieceFont.material;
        pieceTextMat.mainTexture = pieceFont.material.mainTexture;
        Font.textureRebuilt += OnFontTextureRebuilt;

        // 駒の絵の画像（Assets/Resources/KomaPieces.png）があれば、フォントの代わりにそれを駒に貼る。
        // Web（WebGL）版では K'sShogiPieces のフォントが表示されなかったので、駒の絵をあらかじめ画像にしてある
        komaAtlas = Resources.Load<Texture2D>("KomaPieces");
        if (komaAtlas != null)
        {
            komaAtlasMat = new Material(textShader != null ? textShader : Shader.Find("Sprites/Default"));
            komaAtlasMat.mainTexture = komaAtlas;
            komaSprites = new Sprite[AtlasChars.Length];
            for (int k = 0; k < AtlasChars.Length; k++)
                komaSprites[k] = Sprite.Create(komaAtlas, AtlasRect(k), new Vector2(0.5f, 0.5f));
        }

        // 不透明な物は専用シェーダー（Assets/Resources/ShogiSolid.shader）で描く。見つからなければプロジェクト既定のマテリアル
        var solidShader = Shader.Find("Shogi3D/Solid");
        if (solidShader != null) baseMat = new Material(solidShader);
        else
        {
            var tmp = GameObject.CreatePrimitive(PrimitiveType.Cube);
            baseMat = tmp.GetComponent<MeshRenderer>().sharedMaterial;
            Destroy(tmp);
        }
        // 半透明は専用シェーダー（Assets/Resources/ShogiGlass.shader）で描く。見つからなければ URP の Lit を半透明に切り替える
        var glassShader = Shader.Find("Shogi3D/Glass");
        glassMat = glassShader != null ? new Material(glassShader) : MakeGlass(baseMat);
        mpb = new MaterialPropertyBlock();

        BuildBoardFrame();
        BuildMarkers();
        pieceRoot = new GameObject("Pieces").transform;
        UnityEngine.InputSystem.EnhancedTouch.EnhancedTouchSupport.Enable();
        BuildUI();
        SetupSound();
        NewGame();
    }

    // 新しい文字が必要になるとフォントの画像が作り直されるので、駒の文字のマテリアルにも渡す
    // ---- 駒の絵の画像（KomaPieces.png）----
    // 1 枚の画像に 4×4 マス（1 マス 256 ピクセル）で、AtlasChars の順に駒の絵が並んでいる。
    // 絵は白で描いてあり、色（黒・赤）は表示するときに付ける。絵の縦横比は AtlasAspects
    const string AtlasChars = "ABCDEFGHISTVWXY"; // K'sShogiPieces での文字（PieceText と同じ割り当て）
    static readonly float[] AtlasAspects = { 0.906f, 0.906f, 0.858f, 0.858f, 0.817f, 0.817f, 0.771f, 0.714f, 0.651f, 0.858f, 0.858f, 0.817f, 0.771f, 0.714f, 0.651f };
    const int AtlasCell = 256, AtlasCols = 4, AtlasPad = 6;
    Texture2D komaAtlas;
    Material komaAtlasMat;
    Sprite[] komaSprites;
    readonly Dictionary<int, Mesh> atlasQuads = new Dictionary<int, Mesh>();

    int AtlasIndex(int p) { return AtlasChars.IndexOf(GlyphChar(p)); }

    // 画像の中の k 番目の絵の範囲（ピクセル。Unity の画像は左下が原点）
    Rect AtlasRect(int k)
    {
        float h = AtlasCell - 2 * AtlasPad;
        float w = h * AtlasAspects[k];
        float x = (k % AtlasCols) * AtlasCell + (AtlasCell - w) * 0.5f;
        float yTop = (k / AtlasCols) * AtlasCell + AtlasPad;
        float y = komaAtlas.height - yTop - h;
        return new Rect(x, y, w, h);
    }

    // 駒の面に貼る四角形（絵の高さを駒の形の高さに合わせる）
    // 色は成り駒かどうかだけで決まり、成り駒は絵も別なので、絵と駒の種類ごとに 1 つ作って使い回す
    Mesh AtlasQuad(int k, PieceType type, Color ink)
    {
        int key = k * 16 + (int)type;
        if (atlasQuads.TryGetValue(key, out var m)) return m;
        var r = AtlasRect(k);
        float h = MeshHeight(type) * 0.98f, w = h * AtlasAspects[k];
        float u0 = r.x / komaAtlas.width, u1 = (r.x + r.width) / komaAtlas.width;
        float v0 = r.y / komaAtlas.height, v1 = (r.y + r.height) / komaAtlas.height;
        m = new Mesh();
        m.SetVertices(new List<Vector3> { new Vector3(-w / 2, -h / 2, 0), new Vector3(w / 2, -h / 2, 0), new Vector3(w / 2, h / 2, 0), new Vector3(-w / 2, h / 2, 0) });
        m.SetUVs(0, new List<Vector2> { new Vector2(u0, v0), new Vector2(u1, v0), new Vector2(u1, v1), new Vector2(u0, v1) });
        m.SetTriangles(new List<int> { 0, 2, 1, 0, 3, 2 }, 0);
        m.SetColors(new List<Color> { ink, ink, ink, ink }); // 絵は白なので、頂点の色がそのまま絵の色になる
        m.RecalculateBounds();
        atlasQuads[key] = m;
        return m;
    }

    static Color PieceInk(int p) { return Piece.Promoted(p) ? new Color(0.75f, 0.1f, 0.05f) : Color.black; }

    static bool CanDraw(Font f, char c)
    {
        f.RequestCharactersInTexture(c.ToString(), PieceFontSize);
        return f.GetCharacterInfo(c, out _, PieceFontSize);
    }

    static Font LoadPieceFont()
    {
        var fonts = Resources.LoadAll<Font>("Fonts");
        if (fonts.Length == 0) return null;
        foreach (var f in fonts) if (f.name == "KomaFont") return f;
        return fonts[0];
    }

    const string PieceCharsKanji = "玉王飛角金銀桂香歩龍馬全圭杏と";
    // 駒の絵のフォント（K'sShogiPieces）は、英大文字 1 文字に駒の絵（枠と文字）が 1 枚入っている
    const string PieceCharsGlyph = "ABCDEFGHISTVWXY";
    string PieceChars => pieceFontIsGlyph ? PieceCharsGlyph : PieceCharsKanji;

    // 駒に表示する文字。駒の絵のフォントでは、その駒の絵が入っている英字にする
    string PieceText(int p)
    {
        if (!pieceFontIsGlyph) return ShogiState.PieceChar(p);
        return GlyphChar(p).ToString();
    }

    // K'sShogiPieces で、その駒の絵が入っている英字
    static char GlyphChar(int p)
    {
        bool pr = Piece.Promoted(p);
        switch (Piece.Type(p))
        {
            case PieceType.King: return Piece.Owner(p) == 0 ? 'B' : 'A'; // 玉将・王将
            case PieceType.Rook: return pr ? 'S' : 'C';
            case PieceType.Bishop: return pr ? 'T' : 'D';
            case PieceType.Gold: return 'E';
            case PieceType.Silver: return pr ? 'V' : 'F';
            case PieceType.Knight: return pr ? 'W' : 'G';
            case PieceType.Lance: return pr ? 'X' : 'H';
            case PieceType.Pawn: return pr ? 'Y' : 'I';
        }
        return ' ';
    }

    // 駒用の書体はそれだけで太さがあるので、太字にするのは代わりの明朝のときだけ
    FontStyle PieceFontStyle => pieceFontIsCustom ? FontStyle.Normal : FontStyle.Bold;

    void OnFontTextureRebuilt(Font f)
    {
        if (f == pieceFont && pieceTextMat != null) pieceTextMat.mainTexture = pieceFont.material.mainTexture;
    }

    void OnDestroy()
    {
        Font.textureRebuilt -= OnFontTextureRebuilt;
        online?.Dispose();
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
        // 層が重なっているので、影があると下の層の駒が暗くなって見づらい。影は落とさず受けもしない
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        r.receiveShadows = false;
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
    static float MeshScale(PieceType t)
    {
        return t == PieceType.King ? 1.0f : (t == PieceType.Rook || t == PieceType.Bishop) ? 0.95f :
               (t == PieceType.Gold || t == PieceType.Silver) ? 0.9f : t == PieceType.Pawn ? 0.8f : 0.85f;
    }

    // 駒の形の高さ（駒のローカル座標）
    static float MeshHeight(PieceType t) { return 0.72f * MeshScale(t) * PieceScale; }

    Mesh PieceMesh(PieceType t)
    {
        if (pieceMeshes.TryGetValue((int)t, out var cached)) return cached;
        float s = MeshScale(t);
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
        if (online != null) return -1;
        return mode == 0 ? 1 : mode == 1 ? 0 : -1;
    }

    static string SideName(int side)
    {
        return side == 0 ? "▲先手" : "△後手"; // ☗☖ は入っていないフォントが多いので ▲△ にする
    }

    void NewGame()
    {
        gameId++;
        cpuSearch = null;
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
        bool viewFromGote = online != null ? onlineSide == 1 : mode == 1;
        yaw = viewFromGote ? 180f - 35f : -35f;
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
        if (online != null && (!online.Connected || state.Turn != onlineSide)) return false; // オンラインでは自分の番だけ
        return result == null && cpuSearch == null && pendingPromotion == null && !animating && state.Turn != CpuSide();
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
        if (to.Count == 1) { PlayByHuman(to[0]); return; }
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
        PlayByHuman(moves.Find(m => m.Promote == promote));
    }

    void Play(Move m)
    {
        int prevTo = history.Count > 0 ? history[history.Count - 1].undo.Move.To : -1;
        string text = state.MoveText(m, prevTo);
        int captured = m.IsDrop ? 0 : state.Board[m.To];
        var u = state.MakeMove(m);
        history.Add((u, text));
        positionCount.TryGetValue(state.Hash, out int c);
        positionCount[state.Hash] = c + 1;
        ClearSelection();
        AfterMove();
        StartCoroutine(AnimateMove(m, captured));
    }

    // ================= 効果音 =================

    // 駒を置く音。Assets/Resources/Sounds/ に音のファイル（.wav / .mp3 / .ogg）を置けばそれを使う（名前は何でもよい。
    // 複数あれば KomaOto という名前のもの、無ければ最初の 1 つ）。無ければ木の駒を打つような音をその場で作る
    void SetupSound()
    {
        if (FindAnyObjectByType<AudioListener>() == null) cam.gameObject.AddComponent<AudioListener>();
        audioSource = gameObject.AddComponent<AudioSource>();
        audioSource.playOnAwake = false;

        var clips = Resources.LoadAll<AudioClip>("Sounds");
        foreach (var c in clips) if (c.name == "KomaOto") komaSound = c;
        if (komaSound == null && clips.Length > 0) komaSound = clips[0];
        if (komaSound == null) komaSound = MakeKomaSound();

        // BGM: Assets/Resources/BGM/ に曲のファイル（.wav / .mp3 / .ogg）を置けばくり返し流す（名前は何でもよい。最初の 1 つ）
        bgmSource = gameObject.AddComponent<AudioSource>();
        bgmSource.playOnAwake = false;
        bgmSource.loop = true;
        bgmSource.volume = 0.35f; // 効果音が聞こえるように控えめ
        var bgms = Resources.LoadAll<AudioClip>("BGM");
        if (bgms.Length > 0) bgmSource.clip = bgms[0];
        bgmOn = PlayerPrefs.GetInt("bgmOn", 1) == 1; // オン・オフは次に起動したときも覚えておく
        UpdateBgm();
    }

    void UpdateBgm()
    {
        if (bgmSource.clip != null)
        {
            if (bgmOn && !bgmSource.isPlaying) bgmSource.Play();
            if (!bgmOn && bgmSource.isPlaying) bgmSource.Pause();
        }
        if (bgmText != null)
            bgmText.text = bgmSource.clip == null ? "BGM: 曲なし（Resources/BGM に置く）" : bgmOn ? "BGM: オン" : "BGM: オフ";
    }

    void ToggleBgm()
    {
        bgmOn = !bgmOn;
        PlayerPrefs.SetInt("bgmOn", bgmOn ? 1 : 0);
        UpdateBgm();
    }

    void PlayKomaSound(bool capture)
    {
        if (audioSource == null || komaSound == null) return;
        audioSource.pitch = Random.Range(0.94f, 1.06f); // 毎回少しだけ音の高さを変えて、単調にしない
        audioSource.PlayOneShot(komaSound, capture ? 1f : 0.75f); // 駒を取ったときは強めに
    }

    // 「パチッ」という短い音を作る（雑音の立ち上がり ＋ すぐ消える木の響き）
    static AudioClip MakeKomaSound()
    {
        const int rate = 44100;
        int n = (int)(rate * 0.18f);
        var data = new float[n];
        var rng = new System.Random(7);
        for (int i = 0; i < n; i++)
        {
            float t = (float)i / rate;
            float noise = (float)(rng.NextDouble() * 2.0 - 1.0) * Mathf.Exp(-t * 150f) * 0.6f;
            float wood = Mathf.Sin(2f * Mathf.PI * 1850f * t) * Mathf.Exp(-t * 55f) * 0.5f
                       + Mathf.Sin(2f * Mathf.PI * 3150f * t) * Mathf.Exp(-t * 90f) * 0.3f
                       + Mathf.Sin(2f * Mathf.PI * 820f * t) * Mathf.Exp(-t * 35f) * 0.35f;
            data[i] = Mathf.Clamp((noise + wood) * 0.8f, -1f, 1f);
        }
        var clip = AudioClip.Create("KomaOto", n, 1, rate, false);
        clip.SetData(data, 0);
        return clip;
    }

    // ================= アニメーション =================

    const float MoveSeconds = 0.4f;

    // 駒は盤の更新後の位置に作り直されているので、元の位置から行き先まで動かして見せる
    IEnumerator AnimateMove(Move m, int captured)
    {
        if (!pieceAt.TryGetValue(m.To, out var piece)) yield break;
        animating = true;
        Vector3 to = CellPos(m.To);
        Vector3 from = m.IsDrop ? to + Vector3.up * 2.5f : CellPos(m.From); // 打つ駒は上から降りてくる
        float lift = m.IsDrop ? 0f : 0.7f;                                  // 盤上の駒は山なりに飛ぶ
        Quaternion baseRot = piece.rotation;
        Transform ghost = captured != 0 ? CreatePiece(captured, to, Wood, false, -1) : null; // 取られた駒は縮んで消える

        for (float t = 0f; t < MoveSeconds; t += Time.deltaTime)
        {
            float k = t / MoveSeconds;
            float e = k * k * (3f - 2f * k); // ゆっくり出てゆっくり止まる
            if (piece != null)
            {
                piece.position = Vector3.Lerp(from, to, e) + Vector3.up * (Mathf.Sin(k * Mathf.PI) * lift);
                if (m.Promote) piece.rotation = baseRot * Quaternion.AngleAxis(360f * e, Vector3.up); // 成るときは一回転
            }
            if (ghost != null) ghost.localScale = Vector3.one * (1f - e);
            yield return null;
        }
        if (piece != null)
        {
            piece.position = to;
            piece.rotation = baseRot;
        }
        if (ghost != null) Destroy(ghost.gameObject);
        PlayKomaSound(captured != 0);
        animating = false;
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
        cpuSearch = new ShogiAI.Search(snapshot, lv, seed);
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

    // 自分で指した手。オンラインなら相手にも送る
    void PlayByHuman(Move m)
    {
        if (online != null) online.SendMove(m.From, m.To, (int)m.Drop, m.Promote, history.Count);
        Play(m);
    }

    void OnUndo()
    {
        if (online != null)
        {
            onlineStatus = "オンライン対局では「待った」はできません";
            Refresh();
            return;
        }
        if (cpuSearch != null || animating || history.Count == 0) return;
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
        if (result != null || cpuSearch != null) return;
        if (online != null)
        {
            if (onlineSide < 0 || !online.Connected) return;
            online.SendResign();
            result = SideName(onlineSide) + "（あなた）の投了。" + SideName(onlineSide ^ 1) + "の勝ち";
            ClearSelection();
            Refresh();
            return;
        }
        result = SideName(state.Turn) + "の投了。" + SideName(state.Turn ^ 1) + "の勝ち";
        ClearSelection();
        Refresh();
    }

    // ================= オンライン対戦 =================

    void OnNewGameButton()
    {
        if (online == null) { NewGame(); return; }
        // オンラインでは部屋を作った人が始める（先手・後手は毎回ランダム）
        if (online.IsHost && online.Connected) StartOnlineAsHost();
        else onlineStatus = online.IsHost ? "相手がまだ入っていません" : "新しい対局は部屋を作った人が始めます";
        Refresh();
    }

    async void CreateRoom()
    {
        if (onlineBusy) return;
        onlineBusy = true;
        LeaveOnline();
        onlineStatus = "部屋を作っています…";
        Refresh();
        try
        {
            Attach(await ShogiOnline.HostAsync());
            onlineStatus = "参加コード: " + online.JoinCode + "\n友達にこのコードを伝えて、待ってください";
        }
        catch (System.Exception e)
        {
            Debug.LogException(e);
            onlineStatus = "部屋を作れませんでした: " + e.Message;
        }
        onlineBusy = false;
        NewGame();
    }

    async void JoinRoom()
    {
        if (onlineBusy) return;
        string code = codeInput.text.Trim();
        if (code.Length == 0)
        {
            onlineStatus = "友達から聞いた参加コードを入れてください";
            Refresh();
            return;
        }
        onlineBusy = true;
        LeaveOnline();
        onlineStatus = "部屋に入っています…";
        Refresh();
        try
        {
            Attach(await ShogiOnline.JoinAsync(code));
            onlineStatus = "相手とつないでいます…";
        }
        catch (System.Exception e)
        {
            Debug.LogException(e);
            onlineStatus = "部屋に入れませんでした（コードを確かめてください）: " + e.Message;
        }
        onlineBusy = false;
        NewGame();
    }

    void Attach(ShogiOnline o)
    {
        online = o;
        onlineSide = -1;
        o.PeerConnected += () =>
        {
            onlineStatus = "相手とつながりました";
            if (o.IsHost) StartOnlineAsHost();
            else Refresh();
        };
        o.StartReceived += hostIsSente => StartOnlineGame(hostIsSente ? 1 : 0);
        o.MoveReceived += OnRemoteMove;
        o.ResignReceived += () =>
        {
            result = SideName(onlineSide ^ 1) + "（相手）の投了。" + SideName(onlineSide) + "（あなた）の勝ち";
            Refresh();
        };
        o.Disconnected += () =>
        {
            onlineStatus = "相手との接続が切れました";
            Refresh();
        };
    }

    void StartOnlineAsHost()
    {
        bool hostIsSente = Random.value < 0.5f; // 先手はランダム
        online.SendStart(hostIsSente);
        StartOnlineGame(hostIsSente ? 0 : 1);
    }

    void StartOnlineGame(int mySide)
    {
        onlineSide = mySide;
        remoteMoves.Clear();
        onlineStatus = "対局開始！ あなたは" + SideName(mySide) + "です";
        NewGame();
    }

    void OnRemoteMove(int from, int to, int drop, bool promote, int ply)
    {
        // 相手の手が、こちらの盤面で指せる手かを確かめてから指す
        int expectedPly = history.Count + remoteMoves.Count;
        var m = legal.Find(x => x.From == from && x.To == to && (int)x.Drop == drop && x.Promote == promote);
        bool ok = remoteMoves.Count == 0 && ply == expectedPly && state.Turn != onlineSide
                  && (m.From == from && m.To == to && (int)m.Drop == drop && m.Promote == promote);
        if (!ok)
        {
            onlineStatus = "相手の手を受け取れませんでした（盤面がずれています）。部屋を作り直してください";
            Refresh();
            return;
        }
        remoteMoves.Enqueue(m);
    }

    void LeaveOnline()
    {
        if (online == null) return;
        online.Dispose();
        online = null;
        onlineSide = -1;
        remoteMoves.Clear();
        onlineStatus = "";
    }

    // ================= 毎フレーム =================

    void Update()
    {
        online?.Update();
        if (remoteMoves.Count > 0 && !animating) Play(remoteMoves.Dequeue());

        if (cpuSearch != null && !animating && cpuSearch.Step(CpuBudgetMs))
        {
            var search = cpuSearch;
            cpuSearch = null;
            if (search.Result.HasValue) Play(search.Result.Value);
            else Refresh();
        }

        bool portrait = Screen.height > Screen.width;
        if ((portrait ? 1 : 0) != layoutPortrait) ApplyLayout(portrait);

        HandleTouch();
        var mouse = Mouse.current;
        // スマホのブラウザはタッチをマウスの操作としても送ってくるので、タッチの直後はマウスを無視する
        if (mouse != null && Time.unscaledTime - lastTouchTime > 0.5f)
        {
            if (mouse.rightButton.isPressed || mouse.middleButton.isPressed)
            {
                var d = mouse.delta.ReadValue();
                yaw += d.x * 0.3f;
                pitch = Mathf.Clamp(pitch - d.y * 0.3f, -80f, 85f);
            }
            float scroll = mouse.scroll.ReadValue().y;
            if (Mathf.Abs(scroll) > 0.01f) distance = Mathf.Clamp(distance - Mathf.Sign(scroll) * 1.2f, 6f, 45f);

            var pos = mouse.position.ReadValue();
            if (mouse.leftButton.wasPressedThisFrame && !IsOverUI(pos)) ClickBoard(pos);
        }

        var kb = Keyboard.current;
        if (kb != null)
        {
            if (kb.upArrowKey.wasPressedThisFrame) ChangeLayer(1);
            if (kb.downArrowKey.wasPressedThisFrame) ChangeLayer(-1);
        }
    }

    // ---- タッチ操作 ----
    const float TapMovePixels = 15f; // これより動いたらタップではなくドラッグ
    Vector2 touchStart;
    bool touchDragged, touchStartedOnUI;
    float lastTouchTime = -10f;
    float pinchDistance;
    readonly List<RaycastResult> uiHits = new List<RaycastResult>();

    void HandleTouch()
    {
        var touches = UnityEngine.InputSystem.EnhancedTouch.Touch.activeTouches;
        if (touches.Count == 0)
        {
            pinchDistance = 0f;
            return;
        }
        lastTouchTime = Time.unscaledTime;
        float h = Mathf.Max(1, Screen.height);

        if (touches.Count >= 2)
        {
            // 2 本指: 指の間が広がったら近づく、狭まったら離れる
            float dist = Vector2.Distance(touches[0].screenPosition, touches[1].screenPosition);
            if (pinchDistance > 0f) distance = Mathf.Clamp(distance - (dist - pinchDistance) / h * 30f, 6f, 45f);
            pinchDistance = dist;
            touchDragged = true; // 2 本指の操作の後に指を離しても、タップにはしない
            return;
        }
        pinchDistance = 0f;

        var t = touches[0];
        switch (t.phase)
        {
            case UnityEngine.InputSystem.TouchPhase.Began:
                touchStart = t.screenPosition;
                touchDragged = false;
                touchStartedOnUI = IsOverUI(t.screenPosition);
                break;
            case UnityEngine.InputSystem.TouchPhase.Moved:
                if (touchStartedOnUI) break; // パネルの上で始まった操作は盤を回さない
                if (!touchDragged && (t.screenPosition - touchStart).magnitude > TapMovePixels) touchDragged = true;
                if (touchDragged)
                {
                    var d = t.delta;
                    yaw += d.x / h * 300f;
                    pitch = Mathf.Clamp(pitch - d.y / h * 300f, -80f, 85f);
                }
                break;
            case UnityEngine.InputSystem.TouchPhase.Ended:
                if (!touchDragged && !touchStartedOnUI) ClickBoard(t.screenPosition);
                break;
        }
    }

    // その位置にパネルやボタンがあるか
    bool IsOverUI(Vector2 pos)
    {
        if (EventSystem.current == null) return false;
        var ev = new PointerEventData(EventSystem.current) { position = pos };
        uiHits.Clear();
        EventSystem.current.RaycastAll(ev, uiHits);
        return uiHits.Count > 0;
    }

    // ---- 縦画面・横画面の並べ方 ----
    void ApplyLayout(bool portrait)
    {
        layoutPortrait = portrait ? 1 : 0;
        if (portrait)
        {
            // 縦画面: 上に 3D の盤、下に左右 2 つのパネル。棋譜と操作の説明は場所が無いので隠す
            scaler.referenceResolution = new Vector2(1080, 1920);
            scaler.matchWidthOrHeight = 0f;
            SetPanel(leftPanel, new Vector2(0, 0), new Vector2(12, 12), 520f, 1f);
            SetPanel(rightPanel, new Vector2(1, 0), new Vector2(-12, 12), BoardCell * 9 + 64, 1.1f);
            cam.rect = new Rect(0f, 0.36f, 1f, 0.64f);
            distance = 24f;
        }
        else
        {
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;
            SetPanel(leftPanel, new Vector2(0, 1), new Vector2(16, -16), 420f, 1f);
            SetPanel(rightPanel, new Vector2(1, 1), new Vector2(-16, -16), BoardCell * 9 + 64, 1f);
            cam.rect = new Rect(0f, 0f, 1f, 1f);
            distance = 20f;
        }
        helpText.SetActive(!portrait);
        kifuTitle.SetActive(!portrait);
        kifuText.gameObject.SetActive(!portrait);
    }

    static void SetPanel(RectTransform rt, Vector2 corner, Vector2 offset, float width, float scale)
    {
        rt.anchorMin = rt.anchorMax = rt.pivot = corner;
        rt.anchoredPosition = offset;
        rt.sizeDelta = new Vector2(width, rt.sizeDelta.y);
        rt.localScale = Vector3.one * scale;
    }

    void LateUpdate()
    {
        if (cam == null) return;
        var rot = Quaternion.Euler(pitch, yaw, 0f);
        cam.transform.rotation = rot;
        cam.transform.position = rot * new Vector3(0f, 0f, -distance);
        foreach (var t in billboards) t.rotation = cam.transform.rotation;
        FitGlyphs();
    }

    // ---- 駒の絵のフォントを駒の形にぴったり合わせる ----
    // 駒の絵（枠と文字）は 1 文字なので、作った文字の実際の大きさを測って、駒の形と同じ高さ・同じ中心にする。
    // 測った結果は駒の種類ごとに覚えておき、2 枚目からはすぐ当てはめる
    struct GlyphFit { public float Scale; public Vector2 Pos; }
    readonly Dictionary<string, GlyphFit> glyphFits = new Dictionary<string, GlyphFit>();
    readonly List<(TextMesh tm, Transform piece, PieceType type)> pendingGlyphs = new List<(TextMesh, Transform, PieceType)>();

    void QueueGlyphFit(TextMesh tm, Transform piece, PieceType type)
    {
        if (glyphFits.TryGetValue(tm.text, out var fit)) ApplyGlyphFit(tm.transform, fit);
        else pendingGlyphs.Add((tm, piece, type));
    }

    static void ApplyGlyphFit(Transform label, GlyphFit fit)
    {
        label.localScale = Vector3.one * fit.Scale;
        label.localPosition = new Vector3(fit.Pos.x, fit.Pos.y, label.localPosition.z);
    }

    void FitGlyphs()
    {
        if (animating) return; // 動いている途中（成りの回転など）は正しく測れないので、止まってから測る
        for (int k = pendingGlyphs.Count - 1; k >= 0; k--)
        {
            var (tm, piece, type) = pendingGlyphs[k];
            if (tm == null || piece == null) { pendingGlyphs.RemoveAt(k); continue; }
            if (glyphFits.TryGetValue(tm.text, out var known)) { ApplyGlyphFit(tm.transform, known); pendingGlyphs.RemoveAt(k); continue; }

            var b = tm.GetComponent<MeshRenderer>().bounds;
            if (b.size.sqrMagnitude < 1e-8f) continue; // まだ文字が作られていない。次のフレームで測る
            var label = tm.transform;
            // 駒のローカル座標での、文字の上下の端と中心
            var lo = piece.InverseTransformPoint(b.min);
            var hi = piece.InverseTransformPoint(b.max);
            float height = Mathf.Abs(hi.y - lo.y);
            var center = new Vector2((lo.x + hi.x) * 0.5f, (lo.y + hi.y) * 0.5f);
            if (height < 1e-5f) continue;
            float scale = label.localScale.x * MeshHeight(type) * 0.98f / height;
            // 文字の原点（label の位置）を中心に拡大するので、拡大後に中心が駒の中心 (0, 0) に来るよう位置を決める
            var origin = new Vector2(label.localPosition.x, label.localPosition.y);
            float k2 = scale / label.localScale.x;
            var fit = new GlyphFit { Scale = scale, Pos = -(center - origin) * k2 };
            glyphFits[tm.text] = fit;
            ApplyGlyphFit(label, fit);
            pendingGlyphs.RemoveAt(k);
        }
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
        RefreshLayerBoard(lastTo, checkSq);
    }

    void RebuildPieces(int lastTo, int checkSq)
    {
        foreach (var t in pieceObjects)
        {
            colliderToCell.Remove(t.GetComponent<Collider>());
            Destroy(t.gameObject);
        }
        pieceObjects.Clear();
        pieceAt.Clear();
        var targets = new HashSet<int>();
        foreach (var m in MovesForSelection()) targets.Add(m.To);

        for (int i = 0; i < ShogiState.Cells; i++)
        {
            int p = state.Board[i];
            if (p == 0) continue;
            bool dim = focusOnly && ShogiState.Z(i) != focusLayer && i != selectedFrom && !targets.Contains(i);

            Color c = dim ? WoodDim : Wood;
            if (i == lastTo) c = LastColor;
            if (i == selectedFrom) c = SelectedColor;
            if (i == checkSq) c = CheckColor;
            var t = CreatePiece(p, CellPos(i), c, dim, i);
            pieceObjects.Add(t);
            pieceAt[i] = t;
        }
    }

    // 駒を 1 枚作る。cell >= 0 ならクリックできるようにする
    Transform CreatePiece(int p, Vector3 pos, Color c, bool dim, int cell)
    {
        var type = Piece.Type(p);
        int owner = Piece.Owner(p);
        var go = new GameObject("Piece");
        go.transform.SetParent(pieceRoot, false);
        go.transform.position = pos;
        // 寝かせて表を上に。先手は奥（+Z）向き、後手は手前向き
        go.transform.rotation = Quaternion.Euler(90f, owner == 0 ? 0f : 180f, 0f);
        go.transform.localScale = Vector3.one * (dim ? 0.45f : 1f);
        go.AddComponent<MeshFilter>().sharedMesh = PieceMesh(type);
        SetColor(go.AddComponent<MeshRenderer>(), c);
        if (cell >= 0)
        {
            var col = go.AddComponent<BoxCollider>();
            col.size = new Vector3(0.62f * PieceScale, 0.72f * PieceScale, PieceHalf * 2f + 0.02f);
            colliderToCell[col] = cell;
        }

        var label = new GameObject("Char");
        label.transform.SetParent(go.transform, false);
        if (komaAtlas != null)
        {
            // 駒の絵の画像を、駒の表の面に貼る
            int ai = AtlasIndex(p);
            label.transform.localPosition = new Vector3(0f, 0f, -PieceHalf - 0.005f);
            label.AddComponent<MeshFilter>().sharedMesh = AtlasQuad(ai, type, PieceInk(p));
            var qr = label.AddComponent<MeshRenderer>();
            qr.sharedMaterial = komaAtlasMat;
            qr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            qr.receiveShadows = false;
            return go.transform;
        }
        // 駒の絵のフォントは絵の中心が文字の中心に来るのでずらさない。漢字は少し下げると駒の中央に見える
        label.transform.localPosition = new Vector3(0f, pieceFontIsGlyph ? 0f : -0.04f * PieceScale, -PieceHalf - 0.005f);
        var tm = label.AddComponent<TextMesh>();
        tm.font = pieceFont;
        tm.fontStyle = PieceFontStyle;
        tm.fontSize = PieceFontSize;
        // 駒の絵は駒の種類ごとに大きさが描き分けてあるので、全部同じ大きさで描く（駒の面いっぱい）
        // 大きさは fontSize 64 のときの値で決めて、解像度を上げた分だけ縮める
        tm.characterSize = (pieceFontIsGlyph ? 0.115f : type == PieceType.King ? 0.065f : 0.055f) * PieceScale * 64f / PieceFontSize;
        tm.anchor = TextAnchor.MiddleCenter;
        tm.alignment = TextAlignment.Center;
        tm.text = PieceText(p);
        tm.color = Piece.Promoted(p) ? new Color(0.75f, 0.1f, 0.05f) : Color.black;
        label.GetComponent<MeshRenderer>().sharedMaterial = pieceTextMat;
        if (pieceFontIsGlyph) QueueGlyphFit(tm, go.transform, type);
        return go.transform;
    }

    void RefreshUI(int checkSq)
    {
        if (result != null) statusText.text = result;
        else
        {
            string s = SideName(state.Turn) + "の番です";
            if (online != null)
                s = onlineSide < 0 ? "オンライン: 対局の開始を待っています"
                  : state.Turn == onlineSide ? "あなたの番です（" + SideName(onlineSide) + "）" : "相手の番です（" + SideName(state.Turn) + "）";
            if (cpuSearch != null) s = SideName(state.Turn) + "（CPU）が考えています…";
            if (checkSq >= 0) s = "王手！ " + s;
            if (selectedFrom >= 0)
                s += "\n選択中: " + ShogiState.SquareName(selectedFrom) + " " + ShogiState.PieceChar(state.Board[selectedFrom]);
            else if (selectedDrop != PieceType.None)
                s += "\n選択中: 持ち駒の" + ShogiState.PieceName(selectedDrop, false) + (focusOnly ? "（見ている層に打てる場所を表示）" : "");
            statusText.text = s;
        }

        modeText.text = "モード: " + (online != null ? "オンライン対局" : ModeNames[mode]);
        onlineText.text = onlineStatus;
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
        int start = Mathf.Max(0, history.Count - 14);
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
        scaler = canvasGo.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight = 0.5f;
        canvasGo.AddComponent<GraphicRaycaster>();

        // 左のパネル
        var left = MakePanel(canvasGo.transform, new Vector2(0, 1), new Vector2(16, -16), new Vector2(420, 0));
        leftPanel = (RectTransform)left;
        MakeText(left, "立体将棋", 34, TextAnchor.MiddleLeft, 44);
        statusText = MakeText(left, "", 22, TextAnchor.UpperLeft, 64);
        var row1 = MakeRow(left);
        MakeButton(row1, "新しい対局", 130, OnNewGameButton, Wood);
        MakeButton(row1, "待った", 100, OnUndo, Wood);
        MakeButton(row1, "投了", 90, OnResign, Wood);
        modeText = MakeButton(MakeRow(left), "", 388, () =>
        {
            if (online != null) LeaveOnline(); // オンラインをやめて、CPU・2人の対局に戻る
            else mode = (mode + 1) % 3;
            NewGame();
        }, Wood);
        // オンライン対局: 部屋を作る／参加コードで入る
        var onlineRow = MakeRow(left);
        MakeButton(onlineRow, "部屋を作る", 120, CreateRoom, Wood);
        codeInput = MakeInput(onlineRow, "参加コード", 150);
        MakeButton(onlineRow, "参加", 70, JoinRoom, Wood);
        onlineText = MakeText(left, "", 18, TextAnchor.UpperLeft, 24);
        levelText = MakeButton(MakeRow(left), "", 388, () => { level = level % 3 + 1; Refresh(); }, Wood);
        var row4 = MakeRow(left);
        MakeButton(row4, "▼", 50, () => ChangeLayer(-1), Wood);
        layerText = MakeText(row4, "", 20, TextAnchor.MiddleCenter, 40, 170);
        MakeButton(row4, "▲", 50, () => ChangeLayer(1), Wood);
        focusText = MakeButton(MakeRow(left), "", 388, () => { focusOnly = !focusOnly; Refresh(); }, Wood);
        bgmText = MakeButton(MakeRow(left), "", 388, ToggleBgm, Wood);
        MakeText(left, "▲先手の持ち駒", 20, TextAnchor.MiddleLeft, 28);
        handRows[0] = MakeRow(left);
        MakeText(left, "△後手の持ち駒", 20, TextAnchor.MiddleLeft, 28);
        handRows[1] = MakeRow(left);
        helpText = MakeText(left, "左クリック: 選ぶ・指す\n右ドラッグ: 回す　ホイール: 拡大縮小\n↑↓キー: 見る層を変える", 17, TextAnchor.UpperLeft, 72).gameObject;

        // 右のパネル（見ている層の 2D の盤と棋譜）
        var right = MakePanel(canvasGo.transform, new Vector2(1, 1), new Vector2(-16, -16), new Vector2(BoardCell * 9 + 64, 0));
        rightPanel = (RectTransform)right;
        var titleRow = MakeRow(right);
        MakeButton(titleRow, "▼", 50, () => ChangeLayer(-1), Wood);
        layerBoardTitle = MakeText(titleRow, "", 24, TextAnchor.MiddleCenter, 40, 260);
        MakeButton(titleRow, "▲", 50, () => ChangeLayer(1), Wood);
        BuildLayerBoard(right);
        kifuTitle = MakeText(right, "棋譜", 22, TextAnchor.MiddleLeft, 30).gameObject;
        kifuText = MakeText(right, "", 17, TextAnchor.UpperLeft, 300);

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

    // 9×9 のマス目（上に筋の番号、右に段の漢数字）。普通の将棋盤と同じ向き
    void BuildLayerBoard(Transform parent)
    {
        string[] kan = { "一", "二", "三", "四", "五", "六", "七", "八", "九" };
        const float labelH = 22f;
        var box = new GameObject("LayerBoard");
        box.transform.SetParent(parent, false);
        box.AddComponent<RectTransform>();
        var le = box.AddComponent<LayoutElement>();
        le.minHeight = le.preferredHeight = BoardCell * 9 + labelH;

        RectTransform Place(GameObject go, float x, float y, float w, float h)
        {
            go.transform.SetParent(box.transform, false);
            var rt = go.GetComponent<RectTransform>();
            if (rt == null) rt = go.AddComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0, 1);
            rt.anchoredPosition = new Vector2(x, -y);
            rt.sizeDelta = new Vector2(w, h);
            return rt;
        }
        Text Label(string text, int size, float x, float y, float w, float h)
        {
            var go = new GameObject("Label");
            Place(go, x, y, w, h);
            var t = go.AddComponent<Text>();
            t.font = font;
            t.fontSize = size;
            t.alignment = TextAnchor.MiddleCenter;
            t.color = new Color(0.3f, 0.25f, 0.2f);
            t.text = text;
            t.raycastTarget = false;
            return t;
        }

        var bg = new GameObject("Board");
        Place(bg, 0, labelH, BoardCell * 9, BoardCell * 9);
        bg.AddComponent<Image>().color = new Color(0.35f, 0.25f, 0.12f); // マスのすき間が線に見える
        for (int x = 0; x < 9; x++) Label((9 - x).ToString(), 15, x * BoardCell, 0, BoardCell, labelH);
        for (int y = 0; y < 9; y++) Label(kan[y], 15, BoardCell * 9 + 2, labelH + y * BoardCell, 24, BoardCell);

        for (int y = 0; y < 9; y++)
            for (int x = 0; x < 9; x++)
            {
                int k = x + 9 * y;
                var cell = new GameObject("Cell");
                Place(cell, x * BoardCell + 1, labelH + y * BoardCell + 1, BoardCell - 2, BoardCell - 2);
                var img = cell.AddComponent<Image>();
                img.color = BoardWood;
                var b = cell.AddComponent<Button>();
                b.targetGraphic = img;
                int cx = x, cy = y;
                b.onClick.AddListener(() => OnCellClicked(ShogiState.Index(cx, cy, focusLayer)));
                layerCells[k] = img;

                var tg = new GameObject("Text");
                tg.transform.SetParent(cell.transform, false);
                var rt = tg.AddComponent<RectTransform>();
                rt.anchorMin = Vector2.zero;
                rt.anchorMax = Vector2.one;
                rt.offsetMin = rt.offsetMax = Vector2.zero;
                var t = tg.AddComponent<Text>();
                t.font = pieceFont;
                t.fontStyle = PieceFontStyle;
                t.fontSize = pieceFontIsGlyph ? 38 : 26;
                t.alignment = TextAnchor.MiddleCenter;
                t.raycastTarget = false;
                layerCellTexts[k] = t;

                if (komaAtlas != null)
                {
                    var pg = new GameObject("Piece");
                    pg.transform.SetParent(cell.transform, false);
                    var prt = pg.AddComponent<RectTransform>();
                    prt.anchorMin = Vector2.zero;
                    prt.anchorMax = Vector2.one;
                    prt.offsetMin = prt.offsetMax = new Vector2(0f, 0f);
                    var pi = pg.AddComponent<Image>();
                    pi.preserveAspect = true;
                    pi.raycastTarget = false;
                    pi.enabled = false;
                    layerCellPieces[k] = pi;
                }
            }
    }

    void RefreshLayerBoard(int lastTo, int checkSq)
    {
        layerBoardTitle.text = (focusLayer + 1) + "層の盤面";
        var targetColor = new Dictionary<int, Color>();
        foreach (var m in MovesForSelection())
            if (ShogiState.Z(m.To) == focusLayer)
                targetColor[m.To] = state.Board[m.To] == 0 ? new Color(0.6f, 0.75f, 1f) : new Color(1f, 0.6f, 0.55f);

        for (int y = 0; y < 9; y++)
            for (int x = 0; x < 9; x++)
            {
                int i = ShogiState.Index(x, y, focusLayer);
                int k = x + 9 * y;
                int p = state.Board[i];
                Color c = BoardWood;
                if (i == lastTo) c = LastColor;
                if (targetColor.TryGetValue(i, out var tc)) c = tc;
                if (i == selectedFrom) c = SelectedColor;
                if (i == checkSq) c = CheckColor;
                layerCells[k].color = c;

                var pi = layerCellPieces[k];
                if (pi != null)
                {
                    pi.enabled = p != 0;
                    if (p != 0)
                    {
                        pi.sprite = komaSprites[AtlasIndex(p)];
                        pi.color = PieceInk(p);
                        pi.rectTransform.localRotation = Piece.Owner(p) == 1 ? Quaternion.Euler(0f, 0f, 180f) : Quaternion.identity;
                    }
                    layerCellTexts[k].text = "";
                    continue;
                }

                var t = layerCellTexts[k];
                t.text = p == 0 ? "" : PieceText(p);
                t.color = p != 0 && Piece.Promoted(p) ? new Color(0.75f, 0.1f, 0.05f) : Color.black;
                // 後手の駒は逆さまに
                t.rectTransform.localRotation = p != 0 && Piece.Owner(p) == 1 ? Quaternion.Euler(0f, 0f, 180f) : Quaternion.identity;
            }
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

    InputField MakeInput(Transform parent, string placeholder, float width)
    {
        var go = new GameObject("Input");
        go.transform.SetParent(parent, false);
        go.AddComponent<RectTransform>();
        var img = go.AddComponent<Image>();
        img.color = Color.white;
        var le = go.AddComponent<LayoutElement>();
        le.preferredWidth = width;
        le.minHeight = 40;
        var input = go.AddComponent<InputField>();

        Text Child(string name, Color c)
        {
            var cg = new GameObject(name);
            cg.transform.SetParent(go.transform, false);
            var rt = cg.AddComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(8, 2);
            rt.offsetMax = new Vector2(-8, -2);
            var t = cg.AddComponent<Text>();
            t.font = font;
            t.fontSize = 20;
            t.color = c;
            t.alignment = TextAnchor.MiddleLeft;
            t.supportRichText = false;
            return t;
        }
        var text = Child("Text", new Color(0.17f, 0.13f, 0.09f));
        var ph = Child("Placeholder", new Color(0.6f, 0.6f, 0.6f));
        ph.text = placeholder;
        input.textComponent = text;
        input.placeholder = ph;
        input.targetGraphic = img;
        input.characterLimit = 12;
        return input;
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
