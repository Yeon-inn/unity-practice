using Unity.Profiling;
using UnityEngine;

/// <summary>
/// GC 발생 프레임 감지 + 인게임 그래프 오버레이.
/// Unity(Boehm GC)는 세대 구분이 없어서 CollectionCount(0/1/2)가 모두 같다 → 하나만 사용.
/// - 위 그래프: 프레임 시간(ms). GC가 일어난 프레임은 빨간색
/// - 아래 그래프: GC Used Memory(MB). 톱니가 떨어지는 지점이 수집 시점
/// F1: 오버레이 on/off.  Play 종료 시 [GCSUM] 요약 로그를 남긴다.
/// 참고: GC 감지는 프레임 시작 시점에 하므로 실제 수집 시점과 최대 1프레임 차이가 날 수 있다.
/// </summary>
public class GCMonitor : MonoBehaviour
{
    const int N = 300;

    [Header("Overlay")]
    public bool showOverlay = true;
    public float frameMsCap = 40f;
    public Vector2 graphSize = new Vector2(680f, 90f);

    readonly float[] frameMs = new float[N];
    readonly float[] usedMB = new float[N];
    readonly bool[] gcFlag = new bool[N];
    int head, count;

    // GC 추적 (Play 시작 기준)
    int lastCount;
    int gcCount;
    int lastGcFrame = -1;
    float lastGcTime;
    float accAllocMB;            // 직전 GC 이후 누적 할당량
    float lastAllocBetweenMB;    // 직전 두 GC 사이 할당량
    int lastIntervalFrames;
    float lastIntervalSec;
    float prevUsed;

    // 요약 통계 (두 번째 GC부터 구간 단위)
    int sN;
    double sFrames, sSec, sAllocMB;
    int sMinF = int.MaxValue, sMaxF;
    float sMinA = float.MaxValue, sMaxA;

    int lastSeenFrame;
    float lastSeenTime;
    float allocKB, usedNow, reservedNow;

    ProfilerRecorder allocRec, usedRec, reservedRec;
    GCAllocator allocator;
    GUIStyle label;

    void OnEnable()
    {
        allocRec = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Allocated In Frame");
        usedRec = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Used Memory");
        reservedRec = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Reserved Memory");
    }

    void OnDisable()
    {
        if (sN > 0)
        {
            Debug.Log($"[GCSUM] listsPerFrame={(allocator != null ? allocator.listsPerFrame : -1)} " +
                      $"frames={lastSeenFrame} elapsed={lastSeenTime:F1}s " +
                      $"gcCount={gcCount} intervals={sN} " +
                      $"intervalFrames avg={sFrames / sN:F1} min={sMinF} max={sMaxF} " +
                      $"intervalSec avg={sSec / sN:F2} " +
                      $"allocBetweenGC(MB) avg={sAllocMB / sN:F2} min={sMinA:F2} max={sMaxA:F2}");
        }
        else
        {
            Debug.Log($"[GCSUM] gcCount={gcCount} intervals=0 (구간 데이터 부족)");
        }
        Debug.Log($"[GCRUN] frames={lastSeenFrame} elapsed={lastSeenTime:F1}s gcCount={gcCount}");
        allocRec.Dispose();
        usedRec.Dispose();
        reservedRec.Dispose();
    }

    void Start()
    {
        allocator = FindFirstObjectByType<GCAllocator>();
        lastCount = System.GC.CollectionCount(0); // 에디터 누적값을 기준점으로 삼아 Play 이후 증가분만 센다
    }

    void Update()
    {
        if (Input.GetKeyDown(KeyCode.F1)) showOverlay = !showOverlay;

        int cnt = System.GC.CollectionCount(0);
        bool gc = cnt != lastCount;
        lastSeenFrame = Time.frameCount;
        lastSeenTime = Time.realtimeSinceStartup;

        float ms = Time.unscaledDeltaTime * 1000f;
        allocKB = allocRec.Valid ? allocRec.LastValue / 1024f : 0f;
        usedNow = usedRec.Valid ? usedRec.LastValue / (1024f * 1024f) : 0f;
        reservedNow = reservedRec.Valid ? reservedRec.LastValue / (1024f * 1024f) : 0f;

        if (gc)
        {
            gcCount += cnt - lastCount;
            lastCount = cnt;

            bool hasPrev = lastGcFrame >= 0;
            int frames = hasPrev ? Time.frameCount - lastGcFrame : 0;
            float sec = hasPrev ? Time.realtimeSinceStartup - lastGcTime : 0f;
            float between = accAllocMB;

            if (hasPrev)
            {
                lastIntervalFrames = frames;
                lastIntervalSec = sec;
                lastAllocBetweenMB = between;
                sN++;
                sFrames += frames; sSec += sec; sAllocMB += between;
                if (frames < sMinF) sMinF = frames;
                if (frames > sMaxF) sMaxF = frames;
                if (between < sMinA) sMinA = between;
                if (between > sMaxA) sMaxA = between;
            }

            Debug.Log($"[GC] #{gcCount} frame={Time.frameCount} t={Time.realtimeSinceStartup:F2}s " +
                      $"interval={frames}f/{sec:F2}s allocSinceLast={between:F2}MB " +
                      $"usedBefore={prevUsed:F1}MB usedAfter={usedNow:F1}MB reserved={reservedNow:F1}MB dt={ms:F1}ms");

            lastGcFrame = Time.frameCount;
            lastGcTime = Time.realtimeSinceStartup;
            accAllocMB = 0f;
        }

        accAllocMB += allocKB / 1024f;
        prevUsed = usedNow;

        frameMs[head] = ms;
        usedMB[head] = usedNow;
        gcFlag[head] = gc;
        head = (head + 1) % N;
        if (count < N) count++;
    }

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

        float x = 15f, y = 15f, w = graphSize.x, h = graphSize.y;
        Fill(new Rect(x - 8, y - 8, w + 16, h * 2 + 140), new Color(0f, 0f, 0f, 0.65f));

        string allocState = allocator != null ? (allocator.allocate ? "ON" : "OFF") : "-";
        GUI.Label(new Rect(x, y, w, 22),
            $"GC count (since Play): {gcCount}   last GC: frame {lastGcFrame}   interval: {lastIntervalFrames}f / {lastIntervalSec:F2}s", label);
        GUI.Label(new Rect(x, y + 20, w, 22),
            $"alloc/frame: {allocKB:F1} KB   since last GC: {accAllocMB:F2} MB   (prev interval: {lastAllocBetweenMB:F2} MB)", label);
        GUI.Label(new Rect(x, y + 40, w, 22),
            $"heap used/reserved: {usedNow:F1}/{reservedNow:F1} MB   allocator: {allocState}", label);
        GUI.Label(new Rect(x, y + 60, w, 22), "Space: alloc on/off   G: GC.Collect   F1: overlay", label);

        // --- 프레임 시간 그래프 ---
        float gy = y + 90f;
        Fill(new Rect(x, gy, w, h), new Color(1f, 1f, 1f, 0.08f));
        float barW = w / N;
        for (int i = 0; i < count; i++)
        {
            int idx = (head - count + i + N) % N;
            float bh = Mathf.Clamp01(frameMs[idx] / frameMsCap) * h;
            Fill(new Rect(x + i * barW, gy + h - bh, Mathf.Max(barW, 1f), bh),
                 gcFlag[idx] ? new Color(1f, 0.25f, 0.25f) : new Color(0.3f, 0.85f, 0.4f));
        }
        float line60 = gy + h - (16.7f / frameMsCap) * h;
        Fill(new Rect(x, line60, w, 1f), new Color(1f, 0.9f, 0.2f, 0.8f));
        GUI.Label(new Rect(x + w - 170, gy - 2, 170, 20), $"frame ms (cap {frameMsCap:F0})", label);

        // --- 힙 사용량 그래프 ---
        float my = gy + h + 26f;
        Fill(new Rect(x, my, w, h), new Color(1f, 1f, 1f, 0.08f));
        float min = float.MaxValue, max = float.MinValue;
        for (int i = 0; i < count; i++)
        {
            int idx = (head - count + i + N) % N;
            if (usedMB[idx] < min) min = usedMB[idx];
            if (usedMB[idx] > max) max = usedMB[idx];
        }
        if (count == 0) { min = 0f; max = 1f; }
        if (max - min < 1f) max = min + 1f;
        for (int i = 0; i < count; i++)
        {
            int idx = (head - count + i + N) % N;
            float bh = Mathf.Clamp01((usedMB[idx] - min) / (max - min)) * h;
            Fill(new Rect(x + i * barW, my + h - bh, Mathf.Max(barW, 1f), bh), new Color(0.3f, 0.7f, 1f, 0.9f));
            if (gcFlag[idx])
                Fill(new Rect(x + i * barW, my, Mathf.Max(barW, 2f), h), new Color(1f, 0.25f, 0.25f, 0.8f));
        }
        GUI.Label(new Rect(x + w - 250, my - 2, 250, 20), $"GC used MB ({min:F1} ~ {max:F1})", label);
    }
}
