using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 살아있는(수집되지 않는) 데이터를 일부러 붙들어 두는 실험용 컴포넌트.
/// mode: 0=없음, 1=포인터 없는 데이터(int[]), 2=포인터 있는 데이터(object[], 내용은 null이지만 스캔 대상)
/// megabytes: 1MB 조각을 몇 개 붙들지
/// </summary>
public class GCLiveData : MonoBehaviour
{
    [Tooltip("0=none, 1=atomic(int[]), 2=composite(object[])")]
    public int mode = 0;
    public int megabytes = 0;

    static readonly List<object> keep = new List<object>();

    void Awake()
    {
        keep.Clear();
        long before = UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong();

        for (int i = 0; i < megabytes; i++)
        {
            if (mode == 1) keep.Add(new int[256 * 1024]);          // 1MB, 포인터 없음
            else if (mode == 2) keep.Add(new object[128 * 1024]);  // 1MB, 포인터 배열
        }

        System.GC.Collect();
        System.GC.Collect();
        long after = UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong();
        Debug.Log($"[LIVE] mode={mode} MB={megabytes} usedBefore={before / 1048576f:F1}MB usedAfterSettle={after / 1048576f:F1}MB");
    }

    void OnDestroy()
    {
        keep.Clear();
    }
}
