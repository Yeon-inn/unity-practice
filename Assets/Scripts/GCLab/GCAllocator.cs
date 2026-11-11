using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/// <summary>
/// GC 실험용 할당 발생기..
/// Space: 할당 on/off, G: GC.Collect() 강제 호출
/// </summary>
public class GCAllocator : MonoBehaviour
{
    public bool allocate = true;
    [Range(0, 500)] public int listsPerFrame = 40;

    public static object Sink; // 최적화로 제거되지 않도록 결과를 보관
    string text = "";

    void Update()
    {
        if (Input.GetKeyDown(KeyCode.Space)) allocate = !allocate;
        if (Input.GetKeyDown(KeyCode.G)) System.GC.Collect();

        if (!allocate) return;

        for (int i = 0; i < listsPerFrame; i++)
        {
            var list = new List<int>(100);               // 힙 할당
            Sink = i;                                    // 박싱
            text = "frame " + Time.frameCount + " #" + i; // 문자열 결합
            Sink = list.Select(x => x + 1).ToList();     // LINQ(델리게이트 + 이터레이터 할당)
        }
        Sink = text;
    }
}
