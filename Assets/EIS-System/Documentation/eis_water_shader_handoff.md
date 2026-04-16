# EIS Water Shader — Session Handoff

> **목적**: 이 문서를 새 세션에 전달하면, 현재 세션의 모든 맥락을 이어받아 작업을 계속할 수 있습니다.

---

## 🎯 전체 목표

Trolls Brigade 프로젝트의 **Toon Water Shader를 PC 품질 기반 모바일 최적화 버전으로 만들고**, EIS(Environment Interaction System)의 InteractionRT를 연동하여 **캐릭터/오브젝트가 물 위를 지나갈 때 수면이 반응**하도록 구현합니다.

---

## ✅ 완료된 작업 (건드리지 마세요)

### Phase 1: PC → TB 모바일 다운그레이드

| # | 작업 | 상태 |
|---|------|------|
| 1 | PC `Toon Water Shader.shadergraph` 복사 → `EIS_Toon_Water.shadergraph` | ✅ |
| 2 | **Planar Reflection** 그룹 전체 제거 (RT 카메라 2배 드로우콜 제거) | ✅ |
| 3 | **Generate Normals** 그룹: GradientNoise×2 + NormalFromHeight → **Normal From Texture** 교체 | ✅ |
| 4 | `ToonWaterInspector` 커스텀 인스펙터 종속 제거 (`m_CustomEditorGUI: ""`) | ✅ |
| 5 | Point Specular 토글 (`Additional Light Specular Toggle`) — 이미 존재 확인 | ✅ |

### Phase 2: EIS 연동 (진행 중)

| # | 작업 | 상태 |
|---|------|------|
| 1 | `EISWaterInteraction.hlsl` Custom Function 생성 | ✅ |
| 2 | ShaderGraph Blackboard에 글로벌 프로퍼티 4개 추가 | ✅ |
| 3 | Custom Function 노드 생성 및 입출력 연결 | ✅ |
| 4 | **테스트 결과: RT 데이터 수신 성공** (NormalOffset이 색상으로 확인됨) | ✅ |
| 5 | **문제 발견: RT 256×256 해상도로 인한 격자 무늬** | 🔴 |
| 6 | Normal/Foam 연결 방식 변경 필요 | ❌ 미완료 |

---

## 🔴 현재 문제 및 해결해야 할 것

### 격자 문제

**원인**: InteractionRT는 256×256 해상도로, Pebble 위치 데이터용으로는 충분하지만 물 수면의 Normal 데이터로 직접 사용하면 해상도가 부족하여 격자/계단 무늬가 선명하게 보입니다.

```
InteractionRT 1 텍셀 = 물 위 약 40cm 영역
→ 물 쉐이더에서 Normal로 직접 매핑하면 셀 경계가 보임
```

### 해결책: 방안 D — Intensity 방식 (권장)

**핵심 아이디어**: RT 데이터를 Normal 벡터로 직접 쓰지 않습니다. 대신 RT의 magnitude(세기)만 추출하여, **기존 텍스쳐 기반 Normal의 Strength를 증폭**합니다.

```
❌ 이전 (실패):
  RT direction(Vector2) → Normal에 직접 주입 → 격자 보임

✅ 새 방향:
  RT magnitude(Float, 0~1) → 기존 Normal Strength 곱하기 → 출렁임 증가
  RT magnitude → Foam threshold 낮춤 → 물보라 추가
  
  → 기존 텍스쳐 노멀이 디테일을 담당하므로 격자 안 보임!
```

---

## 📋 다음 작업 체크리스트

### Step 1: HLSL 수정
파일: `Assets/EIS-System/Runtime/InteractableEnvironment/Shaders/Scatters/SG_Pebble/Shaders/EISWaterInteraction.hlsl`

**현재 출력:**
```hlsl
out half3 NormalOffset,  // ← 격자 문제 원인
out half FoamBoost
```

**변경할 출력:**
```hlsl
out half InteractionIntensity,  // 0~1: direction magnitude
out half FoamBoost              // 0~1: foam 증가량
```

**변경 내용:**
- `NormalOffset(half3)` → `InteractionIntensity(half)` 로 교체
- InteractionIntensity = `length((data.rg - 0.5) * 2.0)`  (방향 벡터의 크기)
- FoamBoost 유지 (magnitude + press 기반)

### Step 2: ShaderGraph 재연결

**Custom Function 노드 출력 변경:**
- `NormalOffset(Vector3)` 삭제 → `InteractionIntensity(Float)` 추가

**Normal 연결 (핵심):**
```
Generate Normals 그룹의 Normal From Texture 
  → Strength 입력 (현재 고정값 8)
  
변경:
  기본 Strength(8) + InteractionIntensity × BoostMultiplier(10~20)
  = 평소 8, 인터랙션 시 18~28 → 물결이 세게 출렁

노드: Add(기본Strength, Multiply(InteractionIntensity, BoostMultiplier))
  → Normal From Texture의 Strength 입력에 연결
```

**Foam 연결:**
```
기존 Foam 결과에 FoamBoost를 Add
  → Saturate(0~1 클램프) 
  → 기존 Foam 연결 지점
```

### Step 3: 선택적 개선 — RT FilterMode 변경
파일: `InteractionMapBakerV2.cs` (line ~485 CreateRT 메서드)

```csharp
// 현재: _rtFilter = FilterMode.Point;
// 변경: _rtFilter = FilterMode.Bilinear;
```

Bilinear로 바꾸면 Intensity 값이 부드럽게 보간되어 경계가 더 자연스러워집니다. (Pebble 시스템에 영향 없는지 확인 필요)

### Step 4: 테스트 및 튜닝

| 파라미터 | 추천 시작값 | 역할 |
|---------|-----------|------|
| Normal Base Strength | 8 | 기본 물결 (인터랙션 없을 때) |
| Interaction Boost | 15 | 인터랙션 시 추가 Normal Strength |
| Foam Strength | 0.5 | 물보라 강도 |

### Step 5: 구버전 노드 경고 해결
ShaderGraph에서 ⚠️ 노드 7개 → 우클릭 → Update Node

---

## 📁 관련 파일 전체 목록

### 물 쉐이더
| 파일 | 경로 | 설명 |
|------|------|------|
| **EIS_Toon_Water.shadergraph** | `Assets/EIS-System/Runtime/InteractableEnvironment/Shaders/Scatters/SG_Pebble/Shaders/` | TB 물 쉐이더 (작업 대상) |
| **EISWaterInteraction.hlsl** | 같은 폴더 | Custom Function HLSL (수정 대상) |

### 원본 참조 (읽기 전용)
| 파일 | 경로 | 설명 |
|------|------|------|
| Toon Water Shader.shadergraph | `Assets/Toon Water URP/` | PC 원본 |
| Toon Water Shader Mobile.shadergraph | `Assets/Toon Water URP/Mobile version/` | Mobile 원본 |
| ToonWaterInspector.cs | `Assets/Toon Water URP/Editor/` | 원본 커스텀 인스펙터 (비활성됨) |
| DirLightInfo.cginc | `Assets/Toon Water URP/Functions/` | 디렉셔널 라이트 함수 |
| OtherLightsInfo.cginc | `Assets/Toon Water URP/Functions/` | 포인트 라이트 함수 |

### EIS 시스템
| 파일 | 경로 | 설명 |
|------|------|------|
| InteractionMapBakerV2.cs | `Assets/EIS-System/Runtime/EnvironmentInteractionSystem/Interaction/` | RT 베이커 |
| Stamp.shader | `Assets/EIS-System/Runtime/EnvironmentInteractionSystem/Shaders/Interaction/` | Stamp 쉐이더 |

---

## 🏗️ ShaderGraph 현재 구조

```
EIS_Toon_Water.shadergraph
│
├─ [유지] Calculate UVs
├─ [교체 완료] Generate Normals
│    └─ NormalsTexture → Normal From Texture (Strength:8) → Split
│         → "Use Normal Texture" Keyword → Out(1D), Out(3D)
├─ [유지] Calculate Water Color Based On Depth
├─ [유지] Calculate Foam + Foam UVs
├─ [유지] Refraction (토글: Use Refraction In Depth Based Water Color)
├─ [유지] Fresnel
├─ [유지] Directional Light Specular
├─ [유지] Other Lights Specular (토글: Additional Light Specular Toggle)
├─ [삭제됨] Planar Reflection
└─ [작업 중] EIS Interaction
      └─ Custom Function: EISWaterInteraction
           Blackboard properties:
             _InteractionRT (Texture2D, ref: _InteractionRT)
             _InteractionCamPosXZ (Vector4, ref: _InteractionCamPosXZ)
             _InteractionCamParams (Vector4, ref: _InteractionCamParams)
             _InteractionUVOffset (Vector4, ref: _InteractionUVOffset)
```

---

## 📊 InteractionRT 채널 해석

```hlsl
float4 data = tex2D(InteractionRT, uv);

// 디코딩
float2 dir = (data.rg - 0.5) * 2.0;  // 방향 벡터 (-1 ~ 1)
float magnitude = length(dir);         // 세기 (0 ~ 1.4)
float press = data.b;                  // 압력 (0 ~ 1)
float weight = data.a;                 // 가중치 (0 ~ 1)
```

| 채널 | 의미 | neutral값 | 범위 |
|------|------|----------|------|
| R | 방향 X (packed) | 0.5 | 0~1 |
| G | 방향 Z (packed) | 0.5 | 0~1 |
| B | 압력 (press) | 0.0 | 0~1 |
| A | 가중치 (weight) | 0.0 | 0~1 |

---

## ⚙️ InteractionMapBakerV2 글로벌 Push

```csharp
// PushGlobals() — 매 Tick마다 호출
Shader.SetGlobalTexture("_InteractionRT", _prevRT);
Shader.SetGlobalVector("_InteractionCamPosXZ", new Vector4(snapX, snapZ, 0, 0));
Shader.SetGlobalVector("_InteractionCamParams", new Vector4(orthoSize, worldSize, invWorldSize, pixelSize));
Shader.SetGlobalVector("_InteractionUVOffset", new Vector4(offsetU, offsetV, 0, 0));
```

**ShaderGraph에서 수신하는 방법:** Blackboard 프로퍼티의 **Override Reference Name**을 위 글로벌 이름과 동일하게 설정하면 자동 수신됩니다.

---

## 💡 이전 세션에서의 설계 결정

1. **왜 PC 기반 다운그레이드?** — Mobile 쉐이더에 Fresnel/Point Specular가 없어서 추가하려면 3~4시간. PC에서 제거하는 게 1.5시간으로 빠름.
2. **왜 Vertex Displacement 안 함?** — 탑다운(Ortho) 카메라에서 Y축 높이는 거의 안 보임. Normal + Foam만으로 충분.
3. **왜 방안 D?** — RT 직접 Normal 매핑은 256×256 해상도에서 격자 보임. Intensity 방식은 기존 텍스쳐 노멀이 디테일을 담당하므로 해상도 무관.
