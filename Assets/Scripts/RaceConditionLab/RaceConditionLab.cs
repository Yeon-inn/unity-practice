using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

/// <summary>
/// 멀티스레드 Race Condition 실험: "사라지는 골드".
/// 워커 여러 개가 공유 변수 gold 를 각자 N번씩 +1 한다. 기대값 = 워커 수 x N.
/// - None        : 보호 없음. gold++ 는 읽기 → 더하기 → 쓰기 3단계라서 중간에 다른 스레드가 끼어들면 갱신이 사라진다 (lost update)
/// - Lock        : lock 으로 한 번에 한 스레드만 임계 구역에 진입. 정확하지만 느림
/// - Interlocked : CPU 원자 명령(lock-free). 정확하고 lock 보다 빠름
/// 실행 방식: Task.Run (.NET ThreadPool 워커 재사용) / Thread (OS 스레드 직접 생성).
/// 둘 다 실제 OS 스레드에서 돌기 때문에 경쟁은 똑같이 재현된다.
/// 키: R=선택한 설정으로 1회, A=3모드 비교, M=동기화 방식 전환, T=Task/Thread 전환, F1=오버레이 on/off
/// 로그: [RACECOND] 실행 결과
/// </summary>
public class RaceConditionLab : MonoBehaviour
{
    public enum Runner { TaskRun, Thread }
    public enum SyncMode { None, Lock, Interlocked }

    struct Result
    {
        public Runner runner;
        public SyncMode mode;
        public int workers, expected, actual;
        public long ms;
        public int distinctThreads, mainThreadId;
        public bool ranOnMain;
        public int Lost => expected - actual;
    }

    [Header("Experiment")]
    [Tooltip("Task.Run = ThreadPool 재사용 / Thread = OS 스레드 직접 생성")]
    public Runner runner = Runner.TaskRun;
    [Tooltip("공유 변수 보호 방식. None 이면 race condition 발생")]
    public SyncMode syncMode = SyncMode.None;
    [Range(1, 16)] public int workerCount = 4;
    [Tooltip("워커 하나가 gold++ 를 반복하는 횟수. 너무 작으면 스레드가 겹치지 못해 경쟁이 안 보일 수 있음")]
    public int incrementsPerWorker = 1_000_000;

    [Header("Run")]
    [Tooltip("Play 시작하면 3가지 모드를 순서대로 자동 비교")]
    public bool compareOnStart = true;

    [Header("Overlay")]
    public bool showOverlay = true;

    const int HistoryMax = 9;

    // 공유 자원. 일부러 volatile / 프로퍼티 없이 평범한 필드로 둔다
    int gold;
    readonly object goldLock = new object();

    // 워커가 어느 스레드에서 돌았는지 기록 (이쪽은 lock 으로 보호)
    readonly object idLock = new object();
    readonly HashSet<int> threadIds = new HashSet<int>();

    // 메인 스레드에서만 접근 (await 이후 연속 코드는 UnitySynchronizationContext 덕분에 메인 스레드)
    bool running;
    readonly List<Result> history = new List<Result>();
    GUIStyle label;

    void Start()
    {
        if (compareOnStart) RunCompare();
    }

    void Update()
    {
        if (Input.GetKeyDown(KeyCode.F1)) showOverlay = !showOverlay;
        if (running) return;
        if (Input.GetKeyDown(KeyCode.R)) Run();
        if (Input.GetKeyDown(KeyCode.A)) RunCompare();
        if (Input.GetKeyDown(KeyCode.M)) syncMode = (SyncMode)(((int)syncMode + 1) % 3);
        if (Input.GetKeyDown(KeyCode.T)) runner = runner == Runner.TaskRun ? Runner.Thread : Runner.TaskRun;
    }

    // ---------------- 실행 ----------------

    [ContextMenu("Run")]
    public async void Run()
    {
        if (running) return; // 메인 스레드 전용 플래그라서 여기서는 경쟁이 없다
        running = true;
        try
        {
            Result r = await RunOnce(syncMode);
            if (this == null) return; // await 중에 오브젝트가 파괴됐을 수 있음
            Record(r);
        }
        catch (Exception e) { Debug.LogException(e); }
        finally { running = false; }
    }

    [ContextMenu("Run Compare All Modes")]
    public async void RunCompare()
    {
        if (running) return;
        running = true;
        try
        {
            foreach (SyncMode m in (SyncMode[])Enum.GetValues(typeof(SyncMode)))
            {
                Result r = await RunOnce(m);
                if (this == null) return;
                Record(r);
            }
        }
        catch (Exception e) { Debug.LogException(e); }
        finally { running = false; }
    }

    async Task<Result> RunOnce(SyncMode mode)
    {
        int workers = workerCount;
        int n = incrementsPerWorker;
        Runner how = runner;
        int mainId = Thread.CurrentThread.ManagedThreadId;

        gold = 0;
        lock (idLock) threadIds.Clear();

        var sw = System.Diagnostics.Stopwatch.StartNew();

        if (how == Runner.TaskRun)
        {
            var tasks = new Task[workers];
            for (int i = 0; i < workers; i++)
                tasks[i] = Task.Run(() => Work(n, mode));
            await Task.WhenAll(tasks);
        }
        else
        {
            var threads = new Thread[workers];
            for (int i = 0; i < workers; i++)
            {
                threads[i] = new Thread(() => Work(n, mode)) { IsBackground = true, Name = "RaceLab-" + i };
                threads[i].Start();
            }
            // 메인 스레드를 막지 않으려고 Join 은 풀 스레드에서 대기
            await Task.Run(() => { foreach (var t in threads) t.Join(); });
        }

        sw.Stop();

        int distinct;
        bool ranOnMain;
        lock (idLock)
        {
            distinct = threadIds.Count;
            ranOnMain = threadIds.Contains(mainId);
        }

        return new Result
        {
            runner = how,
            mode = mode,
            workers = workers,
            expected = workers * n,
            actual = gold,
            ms = sw.ElapsedMilliseconds,
            distinctThreads = distinct,
            mainThreadId = mainId,
            ranOnMain = ranOnMain,
        };
    }

    // 워커 스레드에서 실행된다. Unity API(transform 등)는 여기서 호출하면 안 된다
    void Work(int n, SyncMode mode)
    {
        lock (idLock) threadIds.Add(Thread.CurrentThread.ManagedThreadId);

        switch (mode)
        {
            case SyncMode.None:
                // ★ 버그: gold++ 는 원자적이지 않다 (read → add → write)
                for (int i = 0; i < n; i++) gold++;
                break;

            case SyncMode.Lock:
                for (int i = 0; i < n; i++)
                    lock (goldLock) { gold++; }
                break;

            case SyncMode.Interlocked:
                for (int i = 0; i < n; i++)
                    Interlocked.Increment(ref gold);
                break;
        }
    }

    // ---------------- 기록 ----------------

    static string RunnerLabel(Runner r) => r == Runner.TaskRun ? "Task.Run" : "Thread";

    static string ModeLabel(SyncMode m)
    {
        switch (m)
        {
            case SyncMode.None: return "None";
            case SyncMode.Lock: return "lock";
            default: return "Interlocked";
        }
    }

    void Record(Result r)
    {
        history.Add(r);
        if (history.Count > HistoryMax) history.RemoveAt(0);

        float pct = r.expected > 0 ? 100f * r.Lost / r.expected : 0f;
        var sb = new StringBuilder();
        sb.Append($"[RACECOND] {(r.Lost == 0 ? "OK" : "RACE!")} {RunnerLabel(r.runner)} / {ModeLabel(r.mode)}\n");
        sb.Append($"  expected={r.expected:N0} actual={r.actual:N0} lost={r.Lost:N0} ({pct:F2}%)\n");
        sb.Append($"  {r.ms}ms, workers={r.workers}, distinctThreads={r.distinctThreads} (main={r.mainThreadId}, ranOnMain={r.ranOnMain})");
        Debug.Log(sb.ToString());
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
        int rows = 4 + Mathf.Max(history.Count, 1);
        Fill(new Rect(x - 8, y - 8, w + 16, rows * line + 24), new Color(0f, 0f, 0f, 0.7f));

        GUI.Label(new Rect(x, y, w, 22), "R: 실행   A: 3모드 비교   M: 동기화 전환   T: Runner 전환   F1: overlay", label);
        y += line * 1.5f;

        int expected = workerCount * incrementsPerWorker;
        GUI.Label(new Rect(x, y, w, 22),
            $"[설정] {RunnerLabel(runner)} / {ModeLabel(syncMode)} | 워커 {workerCount} x {incrementsPerWorker:N0} = 기대값 {expected:N0}" +
            (running ? "   (실행 중...)" : ""), label);
        y += line * 1.5f;

        if (history.Count == 0)
        {
            GUI.Label(new Rect(x, y, w, 22), running ? "측정 중..." : "R 또는 A 키로 실행", label);
            return;
        }

        const float barW = 260f;
        var old = label.normal.textColor;
        for (int i = history.Count - 1; i >= 0; i--)
        {
            Result r = history[i];
            bool ok = r.Lost == 0;
            float pct = r.expected > 0 ? 100f * r.Lost / r.expected : 0f;

            label.normal.textColor = ok ? new Color(0.4f, 0.9f, 0.5f) : new Color(1f, 0.35f, 0.35f);
            GUI.Label(new Rect(x, y, 230, 22), $"{(ok ? "OK  " : "RACE")} {RunnerLabel(r.runner)}/{ModeLabel(r.mode)}", label);
            label.normal.textColor = old;

            // 초록 = 실제로 쌓인 골드, 빨강 = 사라진 골드
            float okW = r.expected > 0 ? (float)r.actual / r.expected * barW : 0f;
            Fill(new Rect(x + 235, y + 4, Mathf.Max(okW, 1f), line - 6), new Color(0.35f, 0.8f, 0.45f, 0.95f));
            Fill(new Rect(x + 235 + okW, y + 4, Mathf.Max(barW - okW, 0f), line - 6), new Color(0.9f, 0.3f, 0.3f, 0.9f));

            GUI.Label(new Rect(x + 505, y, 360, 22),
                $"{r.actual:N0} / {r.expected:N0}  lost {pct:F1}%  {r.ms}ms  thr {r.distinctThreads}{(r.ranOnMain ? " (main!)" : "")}", label);
            y += line;
        }
        label.normal.textColor = old;
    }
}
