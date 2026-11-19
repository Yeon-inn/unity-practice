using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;


/// <summary>
/// 백그라운드 스레드에서 UI 를 바꾸는 3가지 방법 실험 (에러 없이 안전하게).
/// Unity API 와 UI 는 메인 스레드 전용이라서, 워커 스레드는 UI 를 직접 만지지 않고
/// "결과를 메인 스레드로 넘겨서" 메인 스레드가 반영하게 한다.
/// - Queue       : 워커가 ConcurrentQueue 에 Action 을 넣고, 메인 스레드 Update() 가 꺼내 실행 (디스패처 패턴)
/// - SyncContext : Awake 에서 UnitySynchronizationContext 를 캡처해두고, 워커가 ctx.Post 로 메인 스레드에 예약
/// - AsyncAwait  : 메인에서 await Task.Run 으로 시작하면 await 이후 코드가 자동으로 메인 스레드로 복귀.
///                 진행률은 Progress&lt;T&gt; (생성 시점의 SynchronizationContext 를 캡처) 로 전달
/// 워커에서는 Thread.CurrentThread.ManagedThreadId 와 순수 C# 값만 다루고, UnityEngine API 는 호출하지 않는다.
/// 각 행에 "워커 스레드 ID → UI 갱신 스레드 ID" 를 표시해서 UI 갱신이 항상 메인 스레드에서 일어남을 확인한다.
/// 키: 1=Queue, 2=SyncContext, 3=AsyncAwait, A=전부 동시에, F1=오버레이 on/off
/// 로그: [THREADLAB] 실행 결과
/// </summary>
public class ThreadLab : MonoBehaviour
{
    public enum Method { Queue, SyncContext, AsyncAwait }

    /// <summary>한 방식의 진행 상태. 메인 스레드에서만 읽고 쓴다 (워커는 직접 만지지 않음).</summary>
    class Row
    {
        public string name;
        public int progress;
        public int workerThread = -1;
        public int uiThread = -1;
        public string status = "대기";
        public long ms;
        public bool running;
        public bool done;
        public string uiName;   // 화면 Text 에 표시할 짧은 이름
        public Slider slider;   // 실제 UI 컴포넌트 (메인 스레드에서만 접근)
        public Text text;       // 실제 UI 컴포넌트 (메인 스레드에서만 접근)

    }

    [Header("Experiment")]
    [Tooltip("워커가 진행률을 보고하는 횟수")]
    [Range(5, 100)] public int steps = 20;
    [Tooltip("한 단계당 워커가 Sleep 하는 시간(ms). 무거운 작업을 흉내냄")]
    [Range(10, 300)] public int stepMs = 50;

    [Header("Run")]
    [Tooltip("Play 시작하면 3가지 방식을 동시에 실행")]
    public bool runOnStart = true;

    [Header("Overlay")]
    public bool showOverlay = true;

    [Header("UI (Inspector 에서 방식 순서대로 연결: Queue, SyncContext, AsyncAwait)")]
    [Tooltip("진행률을 표시할 Slider 3개")]
    public Slider[] progressSliders = new Slider[3];
    [Tooltip("상태/스레드 ID 를 표시할 Text 3개")]
    public Text[] progressTexts = new Text[3];


    // 메인 스레드 ID (Awake 에서 기록)
    int mainThreadId;
    // 방식 2: 메인 스레드에서 캡처한 동기화 컨텍스트
    SynchronizationContext syncCtx;
    // 방식 1: 워커 -> 메인 스레드 작업 큐 (스레드 세이프)
    readonly ConcurrentQueue<Action> mainQueue = new ConcurrentQueue<Action>();
    // 오브젝트 파괴 시 워커를 멈추기 위한 취소 토큰
    CancellationTokenSource cts;

    readonly Row[] rows =
    {
        new Row { name = "1. Queue (ConcurrentQueue + Update)" },
        new Row { name = "2. SynchronizationContext.Post" },
        new Row { name = "3. async/await + Progress<T>" },
    };

    GUIStyle label;

void Awake()
    {
        mainThreadId = Thread.CurrentThread.ManagedThreadId;
        syncCtx = SynchronizationContext.Current; // 메인 스레드에서 캡처해야 한다
        cts = new CancellationTokenSource();

        // Inspector 에서 연결한 UI 를 각 Row 에 묶는다 (연결이 없으면 OnGUI 오버레이만 동작)
        string[] uiNames = { "1. Queue", "2. SyncContext", "3. async/await" };
        for (int i = 0; i < rows.Length; i++)
        {
            rows[i].uiName = uiNames[i];
            if (progressSliders != null && i < progressSliders.Length) rows[i].slider = progressSliders[i];
            if (progressTexts != null && i < progressTexts.Length) rows[i].text = progressTexts[i];
            RefreshUi(rows[i]);
        }
    }

    void Start()
    {
        if (runOnStart) RunAll();
    }

    void OnDestroy()
    {
        if (cts != null) { cts.Cancel(); cts.Dispose(); cts = null; }
    }

    void Update()
    {
        // 방식 1: 워커가 넣어둔 작업을 메인 스레드에서 실행
        while (mainQueue.TryDequeue(out var action))
            action?.Invoke();

        if (Input.GetKeyDown(KeyCode.F1)) showOverlay = !showOverlay;
        if (Input.GetKeyDown(KeyCode.Alpha1)) RunQueue();
        if (Input.GetKeyDown(KeyCode.Alpha2)) RunSyncContext();
        if (Input.GetKeyDown(KeyCode.Alpha3)) RunAsync();
        if (Input.GetKeyDown(KeyCode.A)) RunAll();
    }

    // ---------------- 실행 ----------------

    [ContextMenu("Run All")]
    public void RunAll()
    {
        RunQueue();
        RunSyncContext();
        RunAsync();
    }

    /// <summary>방식 1: 큐에 Action 을 넣고 메인 스레드 Update 가 소비한다.</summary>
    [ContextMenu("Run Queue")]
    public void RunQueue()
    {
        Row row = rows[(int)Method.Queue];
        if (row.running) return;
        Begin(row);

        int n = steps, delay = stepMs;
        CancellationToken token = cts.Token;
        var thread = new Thread(() =>
        {
            // ★ 여기는 워커 스레드. UI/Unity API 호출 금지, 값만 큐로 넘긴다
            int wid = Thread.CurrentThread.ManagedThreadId;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 1; i <= n; i++)
            {
                if (token.IsCancellationRequested) return;
                Thread.Sleep(delay);
                int p = 100 * i / n;
                mainQueue.Enqueue(() => ApplyProgress(row, p, wid));
            }
            long ms = sw.ElapsedMilliseconds;
            mainQueue.Enqueue(() => Finish(row, wid, ms));
        })
        { IsBackground = true, Name = "ThreadLab-Queue" };
        thread.Start();
    }

    /// <summary>방식 2: Awake 에서 캡처한 UnitySynchronizationContext 로 Post.</summary>
    [ContextMenu("Run SyncContext")]
    public void RunSyncContext()
    {
        Row row = rows[(int)Method.SyncContext];
        if (row.running) return;
        if (syncCtx == null)
        {
            Debug.LogWarning("[THREADLAB] SynchronizationContext 가 없어서 방식 2를 건너뜁니다.");
            return;
        }
        Begin(row);

        int n = steps, delay = stepMs;
        SynchronizationContext ctx = syncCtx;
        CancellationToken token = cts.Token;
        Task.Run(() =>
        {
            // ★ 여기는 ThreadPool 워커 스레드
            int wid = Thread.CurrentThread.ManagedThreadId;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 1; i <= n; i++)
            {
                if (token.IsCancellationRequested) return;
                Thread.Sleep(delay);
                int p = 100 * i / n;
                ctx.Post(_ => ApplyProgress(row, p, wid), null); // 메인 스레드에서 실행되도록 예약
            }
            long ms = sw.ElapsedMilliseconds;
            ctx.Post(_ => Finish(row, wid, ms), null);
        });
    }

    /// <summary>방식 3: await Task.Run 후 연속 코드는 자동으로 메인 스레드. 진행률은 Progress&lt;T&gt;.</summary>
    [ContextMenu("Run Async")]
    public async void RunAsync()
    {
        Row row = rows[(int)Method.AsyncAwait];
        if (row.running) return;
        Begin(row);

        int n = steps, delay = stepMs;
        CancellationToken token = cts.Token;
        try
        {
            // Progress<T> 는 "생성된 스레드(메인)"의 SynchronizationContext 를 캡처 -> 콜백이 메인에서 실행됨
            var progress = new Progress<(int p, int wid)>(v => ApplyProgress(row, v.p, v.wid));

            // ConfigureAwait(false) 를 붙이지 않았기 때문에 await 이후는 메인 스레드로 복귀한다
            (int wid, long ms) result = await Task.Run(() => HeavyWork(n, delay, progress, token));

            if (this == null) return; // await 중에 오브젝트가 파괴됐을 수 있음
            Finish(row, result.wid, result.ms);
        }
        catch (Exception e)
        {
            Debug.LogException(e);
            row.status = "오류";
            row.running = false;
        }
    }

    // 워커 스레드에서 실행된다. Unity API 는 호출하지 않는다
    static (int wid, long ms) HeavyWork(int n, int delay, IProgress<(int p, int wid)> progress, CancellationToken token)
    {
        int wid = Thread.CurrentThread.ManagedThreadId;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 1; i <= n; i++)
        {
            if (token.IsCancellationRequested) break;
            Thread.Sleep(delay);
            progress.Report((100 * i / n, wid));
        }
        return (wid, sw.ElapsedMilliseconds);
    }

    // ---------------- UI 상태 갱신 (반드시 메인 스레드에서 호출됨) ----------------

void Begin(Row row)
    {
        row.running = true;
        row.done = false;
        row.progress = 0;
        row.workerThread = -1;
        row.uiThread = -1;
        row.ms = 0;
        row.status = "실행 중";
        RefreshUi(row);
    }

void ApplyProgress(Row row, int percent, int workerId)
    {
        row.progress = percent;
        row.workerThread = workerId;
        row.uiThread = Thread.CurrentThread.ManagedThreadId; // 이 코드가 도는 스레드 = 메인이어야 한다
        RefreshUi(row); // 실제 Slider/Text 갱신. 메인 스레드이므로 안전
    }

void Finish(Row row, int workerId, long ms)
    {
        row.progress = 100;
        row.workerThread = workerId;
        row.uiThread = Thread.CurrentThread.ManagedThreadId;
        row.ms = ms;
        row.running = false;
        row.done = true;
        row.status = row.uiThread == mainThreadId ? "OK" : "MAIN 아님!";
        RefreshUi(row);

        Debug.Log($"[THREADLAB] {row.status} {row.name}\n" +
                  $"  worker thread={workerId}, UI 갱신 thread={row.uiThread} (main={mainThreadId}), {ms}ms");
    }

/// <summary>
    /// Row 의 값을 실제 UI 컴포넌트(Slider, Text)에 반영한다.
    /// Slider/Text 는 UnityEngine.Object 라서 워커 스레드에서 호출하면 안 된다.
    /// 이 메서드는 Begin/ApplyProgress/Finish 에서만 호출되고, 셋 다 메인 스레드에서 실행된다.
    /// (화면 폰트 호환을 위해 UI 문구는 영어로 표시)
    /// </summary>
    void RefreshUi(Row row)
    {
        if (row.slider != null) row.slider.value = row.progress / 100f;
        if (row.text == null) return;

        string state = row.done ? "DONE" : row.running ? "RUNNING" : "IDLE";
        string thr = row.workerThread == -1
            ? "waiting for worker..."
            : $"worker thr {row.workerThread}  ->  UI thr {row.uiThread}" + (row.uiThread == mainThreadId ? " (main)" : " (NOT main!)");
        row.text.text = $"{row.uiName}   [{state}]   {row.progress}%\n{thr}";
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

        float x = 15f, y = 15f, w = 860f, line = 19f;
        Fill(new Rect(x - 8, y - 8, w + 16, line * 4f + rows.Length * (line * 2f + 10f) + 24f), new Color(0f, 0f, 0f, 0.7f));

        GUI.Label(new Rect(x, y, w, 22), "1: Queue   2: SyncContext   3: async/await   A: 전부 실행   F1: overlay", label);
        y += line * 1.5f;
        GUI.Label(new Rect(x, y, w, 22),
            $"[설정] 워커 Sleep {stepMs}ms x {steps}단계   |   메인 스레드 ID = {mainThreadId}   (워커는 항상 다른 ID)", label);
        y += line * 1.5f;

        const float barW = 360f;
        var old = label.normal.textColor;
        for (int i = 0; i < rows.Length; i++)
        {
            Row r = rows[i];
            bool bad = r.uiThread != -1 && r.uiThread != mainThreadId;
            Color c = bad ? new Color(1f, 0.35f, 0.35f)
                    : r.done ? new Color(0.4f, 0.9f, 0.5f)
                    : r.running ? new Color(1f, 0.85f, 0.4f)
                    : old;

            label.normal.textColor = c;
            GUI.Label(new Rect(x, y, 330, 22), $"{r.name}  [{r.status}]", label);
            label.normal.textColor = old;

            Fill(new Rect(x + 340, y + 4, barW, line - 6), new Color(0.25f, 0.25f, 0.28f, 0.95f));
            Fill(new Rect(x + 340, y + 4, Mathf.Max(barW * r.progress / 100f, 1f), line - 6), new Color(0.35f, 0.8f, 0.45f, 0.95f));
            GUI.Label(new Rect(x + 710, y, 150, 22), $"{r.progress}%" + (r.done ? $"  {r.ms}ms" : ""), label);
            y += line;

            string thr = r.workerThread == -1
                ? "   (아직 보고 없음)"
                : $"   워커 thread {r.workerThread}  ->  UI 갱신 thread {r.uiThread}" + (bad ? "   (메인 아님!)" : "   (main)");
            GUI.Label(new Rect(x, y, w, 22), thr, label);
            y += line + 10f;
        }
        label.normal.textColor = old;
    }
}
