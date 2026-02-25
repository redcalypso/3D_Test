using UnityEngine;

public class EISTester : MonoBehaviour 
{
    public InteractionMapBakerV2 bakerV2;
    public EISStampPreset testPreset;

    void Update() 
    {
        // ✨ 미로의 마법: 마우스 클릭? 그런 거 필요 없어! 
        // 매 프레임마다 내 위치(transform.position)에 계속 도장을 쾅쾅쾅! 찍어줘얌!
        // 이렇게 하면 플레이어가 서 있는 곳은 절대 풀이 일어나지 못해!
        bakerV2.RequestStamp(transform.position, transform.forward, 1f, 1f, testPreset);
    }
}