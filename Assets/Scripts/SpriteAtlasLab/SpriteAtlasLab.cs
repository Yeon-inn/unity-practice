using System.Collections;
using System.Collections.Generic;
using Unity.Profiling;
using UnityEngine;

/// <summary>
/// Sprite Atlas 사용 여부에 따른 Batches 비교 실험.
/// - 그룹 A: 아틀라스에 넣지 않은 스프라이트(텍스처 16장이 각각 따로 존재)
/// - 그룹 B: 같은 모양을 Sprite Atlas로 묶은 스프라이트(텍스처 1장)
/// 키: 0=없음 1=A 2=B 3=A+B, T=그리기 순서(섞임/텍스처별 정렬) 전환, R=자동 측정
/// </summary>
public class SpriteAtlasLab : MonoBehaviour
{
    public Sprite[] noAtlasSprites;
    public Sprite[] atlasSprites;
    public int columns = 20;
    public int rows = 20;
    public float spriteScale = 0.4f;
    public float groupGap = 2f;

    // 0=none 1=NoAtlas 2=Atlas 3=both
    public int mode = 3;
    // true: 스프라이트마다 텍스처가 번갈아 나오는 순서 (배치가 최대로 끊김)
    // false: 같은 텍스처끼리 연속해서 그리는 순서
    public bool interleaved = true;

    readonly List<SpriteRenderer> groupA = new List<SpriteRenderer>();
    readonly List<SpriteRenderer> groupB = new List<SpriteRenderer>();

    ProfilerRecorder batches, setPass, draws, tris;
    string lastResult = "";
    bool sweeping;

    void OnEnable()
    {
        batches = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Batches Count");
        setPass = ProfilerRecorder.StartNew(ProfilerCategory.Render, "SetPass Calls Count");
        draws = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Draw Calls Count");
        tris = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Triangles Count");
    }

    void OnDisable()
    {
        batches.Dispose(); setPass.Dispose(); draws.Dispose(); tris.Dispose();
    }

    void Start()
    {
        Build(noAtlasSprites, groupA, -1);
        Build(atlasSprites, groupB, +1);
        Apply();
    }

    void Build(Sprite[] src, List<SpriteRenderer> list, int side)
    {
        if (src == null || src.Length == 0) return;
        var root = new GameObject(side < 0 ? "Group_NoAtlas" : "Group_Atlas").transform;
        root.SetParent(transform, false);
        float w = columns * spriteScale;
        float h = rows * spriteScale;
        float cx = side * (w * 0.5f + groupGap * 0.5f);
        for (int i = 0; i < columns * rows; i++)
        {
            int cxi = i % columns, cyi = i / columns;
            var go = new GameObject("S" + i);
            go.transform.SetParent(root, false);
            go.transform.position = new Vector3(
                cx - w * 0.5f + (cxi + 0.5f) * spriteScale,
                h * 0.5f - (cyi + 0.5f) * spriteScale, 0f);
            go.transform.localScale = Vector3.one * spriteScale;
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = src[i % src.Length];
            list.Add(sr);
        }
    }

    void Apply()
    {
        int n = noAtlasSprites != null ? Mathf.Max(1, noAtlasSprites.Length) : 1;
        ApplyGroup(groupA, 0, n);
        ApplyGroup(groupB, 10000, n);
        SetActive(groupA, mode == 1 || mode == 3);
        SetActive(groupB, mode == 2 || mode == 3);
    }

    void ApplyGroup(List<SpriteRenderer> list, int baseOrder, int textureCount)
    {
        for (int i = 0; i < list.Count; i++)
        {
            // 섞임: 그리는 순서가 곧 인덱스 -> 텍스처가 번갈아 나옴
            // 정렬: 텍스처 번호가 먼저 -> 같은 텍스처끼리 연속
            int tex = i % textureCount;
            list[i].sortingOrder = baseOrder + (interleaved ? i : tex * 400 + i);
        }
    }

    static void SetActive(List<SpriteRenderer> list, bool on)
    {
        for (int i = 0; i < list.Count; i++) list[i].enabled = on;
    }

    public void SetMode(int m) { mode = m; Apply(); }
    public void SetInterleaved(bool v) { interleaved = v; Apply(); }

    void OnGUI()
    {
        var e = Event.current;
        if (e.type == EventType.KeyDown)
        {
            switch (e.keyCode)
            {
                case KeyCode.Alpha0: SetMode(0); break;
                case KeyCode.Alpha1: SetMode(1); break;
                case KeyCode.Alpha2: SetMode(2); break;
                case KeyCode.Alpha3: SetMode(3); break;
                case KeyCode.T: SetInterleaved(!interleaved); break;
                case KeyCode.R: if (!sweeping) StartCoroutine(Sweep()); break;
            }
        }

        GUILayout.BeginArea(new Rect(10, 10, 430, 200), GUI.skin.box);
        GUILayout.Label("Sprite Atlas Lab");
        GUILayout.Label("mode: " + ModeName(mode) + "   order: " + (interleaved ? "섞임(텍스처 번갈아)" : "텍스처별 정렬"));
        GUILayout.Label("Batches=" + batches.LastValue + "  SetPass=" + setPass.LastValue
            + "  Draw=" + draws.LastValue + "  Tris=" + tris.LastValue);
        GUILayout.Label("※ 이 오버레이(IMGUI) 자체도 Batches에 몇 개 포함됨 → 0번(없음) 값을 기준선으로 비교");
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("0 없음")) SetMode(0);
        if (GUILayout.Button("1 NoAtlas")) SetMode(1);
        if (GUILayout.Button("2 Atlas")) SetMode(2);
        if (GUILayout.Button("3 둘 다")) SetMode(3);
        GUILayout.EndHorizontal();
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("T 순서 전환")) SetInterleaved(!interleaved);
        if (GUILayout.Button("R 자동 측정")) { if (!sweeping) StartCoroutine(Sweep()); }
        GUILayout.EndHorizontal();
        if (lastResult.Length > 0) GUILayout.Label(lastResult);
        GUILayout.EndArea();
    }

    static string ModeName(int m)
    {
        switch (m)
        {
            case 0: return "0 없음(기준선)";
            case 1: return "1 NoAtlas";
            case 2: return "2 Atlas";
            default: return "3 NoAtlas+Atlas";
        }
    }

    // 모든 조합을 돌며 Batches 평균을 로그로 남긴다
    IEnumerator Sweep()
    {
        sweeping = true;
        bool[] orders = new bool[] { true, false };
        foreach (bool inter in orders)
        {
            foreach (int m in new int[] { 0, 1, 2, 3 })
            {
                interleaved = inter;
                SetMode(m);
                for (int i = 0; i < 15; i++) yield return null;
                long sb = 0, ss = 0, sd = 0; int cnt = 0;
                for (int i = 0; i < 20; i++)
                {
                    yield return null;
                    sb += batches.LastValue; ss += setPass.LastValue; sd += draws.LastValue; cnt++;
                }
                string line = "[ATLAS] order=" + (inter ? "interleaved" : "grouped")
                    + " mode=" + ModeName(m)
                    + " batches=" + (sb / (double)cnt).ToString("F1")
                    + " setPass=" + (ss / (double)cnt).ToString("F1")
                    + " draws=" + (sd / (double)cnt).ToString("F1");
                Debug.Log(line);
                lastResult = line;
            }
        }
        sweeping = false;
        Debug.Log("[ATLASRUN] done");
    }
}
