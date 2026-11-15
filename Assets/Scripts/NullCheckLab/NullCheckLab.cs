using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

/// <summary>
/// GameObject null 판정 방법 비교 실험.
/// - 정확성: 살아있음 / 진짜 null / DestroyImmediate 후 / Destroy 후(같은 프레임, 다음 프레임)
///           + ?. / ?? / 캐싱된 컴포넌트의 fake null 동작
/// - 속도: 방법별 반복 측정 (NullCheckBenchmark)
/// 키: C=정확성 체크 다시, B=벤치마크, F1=오버레이 on/off
/// 로그: [NULLCHK] 체크 결과, [NULLBENCH] 벤치마크 결과
/// </summary>
public class NullCheckLab : MonoBehaviour
{
    [Header("Benchmark")]
    public int iterations = 10_000_000;
    public int rounds = 5;
    public bool benchmarkOnStart = true;

    [Header("Overlay")]
    public bool showOverlay = true;

    struct Check
    {
        public string label;
        public bool expected, actual;
        public bool Pass => expected == actual;
    }

    readonly List<Check> checks = new List<Check>();
    NullCheckBenchmark.Result[] bench;
    string benchHeader = "";
    bool running;
    GUIStyle label;

    IEnumerator Start()
    {
        yield return RunChecks();
        if (benchmarkOnStart) RunBenchmark();
    }

    void Update()
    {
        if (running) return;
        if (Input.GetKeyDown(KeyCode.C)) StartCoroutine(RunChecks());
        if (Input.GetKeyDown(KeyCode.B)) RunBenchmark();
        if (Input.GetKeyDown(KeyCode.F1)) showOverlay = !showOverlay;
    }

    // ---------------- 정확성 ----------------

    void Add(string state, string method, bool expected, bool actual)
    {
        checks.Add(new Check { label = $"{state,-18} {method,-20}", expected = expected, actual = actual });
    }

    // unityNull: Unity 수명까지 보는 방법의 기대값, refNull: 순수 C# 참조 비교의 기대값
    void AddAll(string state, GameObject g, bool unityNull, bool refNull)
    {
        Add(state, "g == null", unityNull, g == null);
        Add(state, "!g", unityNull, !g);
        Add(state, "ReferenceEquals", refNull, ReferenceEquals(g, null));
        Add(state, "(object)g == null", refNull, (object)g == null);
        Add(state, "g is null", refNull, g is null);
    }

    IEnumerator RunChecks()
    {
        running = true;
        checks.Clear();

        // 1) 살아있음
        var alive = new GameObject("NullCheck_Alive");
        AddAll("Alive", alive, false, false);
        DestroyImmediate(alive);

        // 2) 진짜 null
        AddAll("Real null", null, true, true);

        // 3) DestroyImmediate 직후 → Unity는 null, C# 참조는 남아있음 (fake null)
        var dead = new GameObject("NullCheck_Dead");
        var cachedTransform = dead.transform;
        DestroyImmediate(dead);
        AddAll("DestroyImmediate", dead, true, false);
        Add("DestroyImmediate", "cached transform==", true, cachedTransform == null);

        // ?. 는 C# 참조만 보므로 통과 → 멤버 접근에서 MissingReferenceException
        bool threw = false;
        try { var _ = dead?.name; } catch (MissingReferenceException) { threw = true; }
        Add("DestroyImmediate", "dead?.name throws", true, threw);

        // ?? 도 fake null을 몰라서 죽은 객체를 그대로 고른다
        var fallback = new GameObject("NullCheck_Fallback");
        Add("DestroyImmediate", "dead ?? fb == dead", true, ReferenceEquals(dead ?? fallback, dead));
        DestroyImmediate(fallback);

        // 4) Destroy() 는 프레임 끝에 처리된다
        var deferred = new GameObject("NullCheck_Deferred");
        Destroy(deferred);
        Add("Destroy same frame", "g == null", false, deferred == null);
        yield return null;
        Add("Destroy next frame", "g == null", true, deferred == null);
        Add("Destroy next frame", "ReferenceEquals", false, ReferenceEquals(deferred, null));

        int pass = 0;
        var sb = new StringBuilder();
        foreach (var c in checks)
        {
            if (c.Pass) pass++;
            sb.AppendLine($"  {(c.Pass ? "PASS" : "FAIL")} {c.label} expected={c.expected} actual={c.actual}");
        }
        Debug.Log($"[NULLCHK] {pass}/{checks.Count} passed\n{sb}");
        running = false;
    }

    // ---------------- 속도 ----------------

    void RunBenchmark()
    {
        running = true;
        var target = new GameObject("NullCheck_BenchTarget");
        bench = NullCheckBenchmark.Run(target, iterations, rounds);
        DestroyImmediate(target);

#if ENABLE_IL2CPP
        const string backend = "IL2CPP";
#else
        const string backend = "Mono";
#endif
        benchHeader = $"{iterations:N0}회 x {rounds}라운드 | Unity {Application.unityVersion} | " +
                      $"{(Application.isEditor ? "Editor" : "Player")} | {backend}";

        var sb = new StringBuilder();
        foreach (var r in bench)
            sb.AppendLine($"  {r.name,-20} median={r.medianMs:F2}ms min={r.minMs:F2}ms {r.nsPerCall:F2}ns/call");
        Debug.Log($"[NULLBENCH] {benchHeader}\n{sb}");
        running = false;
    }

    // ---------------- 오버레이 ----------------

    static void Fill(Rect r, Color c)
    {
        var old = GUI.color;
        GUI.color = c;
        GUI.DrawTexture(r, Texture2D.whiteTexture);
        GUI.color = old;
    }

    void OnGUI()
    {
        if (!showOverlay || Event.current.type != EventType.Repaint) return;

        if (label == null)
        {
            label = new GUIStyle(GUI.skin.label) { fontSize = 14, wordWrap = false };
            label.normal.textColor = Color.white;
        }

        float x = 15f, y = 15f, w = 760f, line = 19f;
        int rows = 3 + checks.Count + 3 + (bench != null ? bench.Length : 1);
        Fill(new Rect(x - 8, y - 8, w + 16, rows * line + 24), new Color(0f, 0f, 0f, 0.7f));

        GUI.Label(new Rect(x, y, w, 22), "C: 정확성 체크   B: 벤치마크   F1: overlay", label);
        y += line * 1.5f;

        int pass = 0;
        foreach (var c in checks) if (c.Pass) pass++;
        GUI.Label(new Rect(x, y, w, 22), $"[정확성] {pass}/{checks.Count} PASS", label);
        y += line;

        var old = label.normal.textColor;
        foreach (var c in checks)
        {
            label.normal.textColor = c.Pass ? new Color(0.4f, 0.9f, 0.5f) : new Color(1f, 0.35f, 0.35f);
            GUI.Label(new Rect(x, y, w, 22), $"{(c.Pass ? "PASS" : "FAIL")}  {c.label}  → {c.actual}", label);
            y += line;
        }
        label.normal.textColor = old;

        y += line * 0.5f;
        GUI.Label(new Rect(x, y, w, 22), $"[속도] {benchHeader}", label);
        y += line;
        if (bench == null)
        {
            GUI.Label(new Rect(x, y, w, 22), running ? "측정 중..." : "B 키로 측정", label);
            return;
        }

        double max = 0;
        foreach (var r in bench) if (r.nsPerCall > max) max = r.nsPerCall;
        foreach (var r in bench)
        {
            GUI.Label(new Rect(x, y, 180, 22), r.name, label);
            float bw = (float)(r.nsPerCall / max) * 380f;
            Fill(new Rect(x + 180, y + 4, Mathf.Max(bw, 2f), line - 6), new Color(0.3f, 0.7f, 1f, 0.9f));
            GUI.Label(new Rect(x + 570, y, 190, 22), $"{r.nsPerCall:F2} ns/call", label);
            y += line;
        }
    }
}
