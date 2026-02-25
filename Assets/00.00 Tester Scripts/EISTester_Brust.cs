using UnityEngine;
using UnityEngine.InputSystem; // ✨ 미로의 마법: New Input System 네임스페이스 추가!

public class EISTester_Brust : MonoBehaviour 
{
    public InteractionMapBakerV2 bakerV2;
    public EISStampPreset testPreset;

    void Update() 
    {
        // ✨ 미로의 마법: 마우스 왼쪽 버튼 클릭 감지 (New Input System 방식)
        if (Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame) 
        {
            // 마우스 클릭 시 캐릭터(또는 임의) 위치에 스탬프 요청!
            bakerV2.RequestStamp(transform.position, Vector3.forward, 1f, 1f, testPreset);
            Debug.Log("쾅! 미로 스탬프 발사 완료!");
        }
    }
}