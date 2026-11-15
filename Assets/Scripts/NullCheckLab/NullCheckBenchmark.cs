using System.Diagnostics;
using UnityEngine;

/// <summary>
/// GameObject null 판정 방법별 속도 측정.
/// 델리게이트 호출 비용이 섞이지 않도록 방법마다 루프를 따로 두고,
/// 워밍업(JIT) 1회 후 rounds회 측정해서 중앙값/최소값을 낸다.
/// </summary>
public static class NullCheckBenchmark
{
    public static readonly string[] Names =
    {
        "g == null",
        "!g (implicit bool)",
        "ReferenceEquals",
        "(object)g == null",
        "g is null",
    };

    public struct Result
    {
        public string name;
        public double medianMs, minMs, nsPerCall;
    }

    // 결과를 소비해서 컴파일러가 루프를 지우지 못하게 한다
    public static int sink;

    public static Result[] Run(GameObject target, int iterations, int rounds)
    {
        var results = new Result[Names.Length];
        var samples = new double[rounds];
        for (int m = 0; m < Names.Length; m++)
        {
            Measure(m, target, iterations / 10);
            for (int r = 0; r < rounds; r++) samples[r] = Measure(m, target, iterations);
            System.Array.Sort(samples);
            double med = samples[rounds / 2];
            results[m] = new Result
            {
                name = Names[m],
                medianMs = med,
                minMs = samples[0],
                nsPerCall = med * 1_000_000.0 / iterations,
            };
        }
        return results;
    }

    static double Measure(int method, GameObject g, int n)
    {
        int hit = 0;
        var sw = Stopwatch.StartNew();
        switch (method)
        {
            case 0: for (int i = 0; i < n; i++) if (g == null) hit++; break;
            case 1: for (int i = 0; i < n; i++) if (!g) hit++; break;
            case 2: for (int i = 0; i < n; i++) if (ReferenceEquals(g, null)) hit++; break;
            case 3: for (int i = 0; i < n; i++) if ((object)g == null) hit++; break;
            case 4: for (int i = 0; i < n; i++) if (g is null) hit++; break;
        }
        sw.Stop();
        sink += hit;
        return sw.Elapsed.TotalMilliseconds;
    }
}
