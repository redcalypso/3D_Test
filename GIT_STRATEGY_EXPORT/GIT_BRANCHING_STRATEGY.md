# 🎮 Trolls Brigade — Git 브랜칭 전략 가이드

> **문서 버전**: v1.0  
> **작성일**: 2026-04-01  
> **작성자**: Miro  
> **대상**: 전체 개발팀 (클라이언트, 아트, 기획)  
> **목적**: 팀 회의 논의 자료 및 Git 워크플로우 표준화

---

## 📌 목차

1. [왜 브랜칭 전략이 필요한가?](#1-왜-브랜칭-전략이-필요한가)
2. [5-Tier 브랜치 구조](#2-5-tier-브랜치-구조)
3. [각 브랜치 상세 규칙](#3-각-브랜치-상세-규칙)
4. [주간 릴리즈 사이클 (금요일)](#4-주간-릴리즈-사이클-금요일)
5. [Hotfix 긴급 대응 플로우](#5-hotfix-긴급-대응-플로우)
6. [브랜치 네이밍 규칙](#6-브랜치-네이밍-규칙)
7. [커밋 메시지 규칙](#7-커밋-메시지-규칙)
8. [Unity 프로젝트 특수 규칙](#8-unity-프로젝트-특수-규칙)
9. [Jenkins CI/CD 연동](#9-jenkins-cicd-연동)
10. [작업자별 일일 워크플로우](#10-작업자별-일일-워크플로우)
11. [FAQ & 트러블슈팅](#11-faq--트러블슈팅)
12. [회의 논의 안건](#12-회의-논의-안건)

---

## 1. 왜 브랜칭 전략이 필요한가?

### 현재 문제점
- 브랜치 네이밍 규칙 없음 → 누구의 어떤 작업인지 파악 불가
- `main`에 검증되지 않은 코드가 직접 올라갈 수 있음
- 씬/프리팹 충돌 시 대응 기준 없음
- 긴급 버그 수정 프로세스 미정의

### 도입 후 기대 효과
| 항목 | Before | After |
|------|--------|-------|
| 빌드 안정성 | main이 깨질 수 있음 | main은 항상 빌드 가능 |
| 작업 추적 | 커밋 로그로 추적 불가 | 브랜치명 + 커밋 규칙으로 즉시 파악 |
| 충돌 대응 | 머지 후 발견 → 패닉 | 격리된 브랜치에서 사전 감지 |
| 긴급 버그 | 임시방편 처리 | 정의된 Hotfix 플로우 |
| 릴리즈 | 불규칙 | 매주 금요일 정기 배포 |

---

## 2. 5-Tier 브랜치 구조

### 전체 흐름도

```
                        ┌─────────────────────────────────────┐
                        │            main (성역)              │
                        │   항상 빌드 가능 · 직접 커밋 금지    │
                        └──────┬──────────────────┬───────────┘
                               │                  ↑
                          (최초 1회)          (금요일 머지)
                               ↓                  │
                        ┌──────┴──────────────────┴───────────┐
                        │          develop (통합 냄비)          │
                        │   다음 버전 통합 · 충돌 테스트        │
                        └──────┬──────────────────┬───────────┘
                               │                  ↑
                          (목표별 분기)       (Epic 완료 시)
                               ↓                  │
                        ┌──────┴──────────────────┴───────────┐
                        │      epic/기능명 (목표 브랜치)        │
                        │   아트+기획+클라 공동 작업 공간       │
                        └──────┬──────────────────┬───────────┘
                               │                  ↑
                          (개인 분기)          (PR 머지)
                               ↓                  │
                        ┌──────┴──────────────────┴───────────┐
                        │  feature/작업자/세부기능 (놀이터)      │
                        │   개인 자유 작업 · PR로 올린다        │
                        └─────────────────────────────────────┘

        ※ 긴급 상황 시:
                        ┌─────────────────────────────────────┐
                        │        hotfix/* (응급실)             │
                        │  main에서 분기 → main+develop 머지   │
                        └─────────────────────────────────────┘
```

### 한눈에 보기

| Tier | 브랜치 | 역할 | 직접 Push | 머지 방법 | 보호 수준 |
|:----:|--------|------|:---------:|-----------|:---------:|
| 1 | `main` | 릴리즈 전용 | ❌ 절대 금지 | PR (2인 승인) | 🔒🔒🔒 |
| 2 | `develop` | 통합 테스트 | ❌ 금지 | PR (1인 승인) | 🔒🔒 |
| 3 | `epic/*` | 대형 기능 | ⚠️ 리드만 | PR (리뷰 권장) | 🔒 |
| 4 | `feature/*` 등 | 개인 작업 | ✅ 자유 | PR → epic | 없음 |
| 5 | `hotfix/*` | 긴급 수정 | ⚠️ 담당자만 | PR (1인 긴급) | 🔒🔒 |

---

## 3. 각 브랜치 상세 규칙

### Tier 1: `main` — 무결점 성역

> **한 줄 요약**: 여기서 직접 코딩하는 사람은 없습니다. 릴리즈 빌드만 이 브랜치에서 뽑습니다.

**규칙:**
- 직접 commit/push **절대 금지**
- `develop` → `main` PR만 허용
- PR 승인: **최소 2명** (클라이언트 프로그래머 필수)
- 머지 후 반드시 **버전 태그** 부여 (예: `v0.3.0`)
- Jenkins가 태그를 감지하여 릴리즈 빌드 자동 실행

**이 브랜치에서 빌드를 뽑으면, 그게 곧 배포 가능한 결과물입니다.**

---

### Tier 2: `develop` — 통합 브랜치

> **한 줄 요약**: 완성된 Epic들이 모여서 서로 안 싸우는지 확인하는 곳입니다.

**규칙:**
- 직접 commit/push **금지** (소규모 설정 변경 예외 — 리드 판단)
- `epic/*` → `develop` PR로만 코드 유입
- PR 승인: **최소 1명**
- 목요일 18:00 이후 새 Epic 머지 금지 (**코드 프리즈**)
- Jenkins에서 push마다 개발 빌드 자동 실행

**여기서 빌드가 깨지면, 금요일 릴리즈도 못 합니다.**

---

### Tier 3: `epic/*` — 대형 기능 브랜치

> **한 줄 요약**: "던전 시스템", "보스 레이드" 같은 큰 목표를 위해 여러 사람이 함께 작업하는 방입니다.

**규칙:**
- `develop`에서 분기: `git checkout -b epic/dungeon-generator develop`
- 해당 Epic에 참여하는 모든 작업자의 feature 브랜치가 여기로 머지됨
- Epic 리드가 주기적으로 `develop`의 최신 변경을 **merge**로 동기화
- 목표 완성 시 `develop`으로 PR

**주의: Epic 브랜치에서는 `rebase` 대신 반드시 `merge`를 사용하세요!**
(이유는 [8. Unity 특수 규칙](#8-unity-프로젝트-특수-규칙) 참조)

**예시:**
```
epic/dungeon-generator     ← 던전 생성 시스템 전체
epic/boss-raid             ← 보스 레이드 모드
epic/gacha-system          ← 가챠 시스템
epic/loading-screen        ← 로딩 화면 리뉴얼
```

---

### Tier 4: `feature/*` — 개인 작업 브랜치

> **한 줄 요약**: 각자 맡은 기능을 마음껏 지지고 볶는 개인 놀이터입니다.

**규칙:**
- 자신이 속한 `epic/*`에서 분기
- 자유롭게 push 가능 (보호 규칙 없음)
- 작업 완료 시 **PR을 올려서** epic에 머지 요청
- PR에 간단한 설명과 스크린샷 첨부 권장

**생성 예시:**
```bash
# epic/dungeon-generator에서 분기
git checkout epic/dungeon-generator
git pull origin epic/dungeon-generator
git checkout -b feature/miro/dungeon-ui
```

---

### Tier 5: `hotfix/*` — 긴급 수정 브랜치

> **한 줄 요약**: main에서 치명적 버그가 발견됐을 때, 가장 빠른 경로로 고치는 응급실입니다.

**규칙:**
- `main`에서 직접 분기: `git checkout -b hotfix/crash-fix main`
- 최소한의 수정만 포함 (신규 기능 추가 금지)
- PR → `main` (1인 긴급 승인)
- 머지 후 패치 태그 부여 (예: `v0.3.1`)
- **반드시** `develop`에도 머지하여 동기화

**Hotfix 플로우는 [5장](#5-hotfix-긴급-대응-플로우)에서 자세히 설명합니다.**

---

## 4. 주간 릴리즈 사이클 (금요일)

### 주간 타임라인

```
  월        화        수        목        금
  ┃         ┃         ┃         ┃         ┃
  ┃ feature → epic    ┃         ┃  ┌──────┃──── 릴리즈!
  ┃ PR 생성 & 리뷰    ┃         ┃  │      ┃
  ┃         ┃         ┃         ┃  │      ┃
  ┃         ┃ epic → develop   ┃  │      ┃
  ┃         ┃ 통합 머지        ┃  │      ┃
  ┃         ┃         ┃         ┃  │      ┃
  ┃         ┃         ┃  18:00  ┃  │      ┃
  ┃         ┃         ┃  코드   ┃  │      ┃
  ┃         ┃         ┃  프리즈 ┃  │      ┃
  ┃         ┃         ┃    ↓    ┃  │      ┃
  ┃         ┃         ┃ Jenkins ┃  │      ┃
  ┃         ┃         ┃ 테스트  ┃  │      ┃
  ┃         ┃         ┃  빌드   ┃  │      ┃
  ┃         ┃         ┃         ┃  │      ┃
  ┃         ┃         ┃    QA   ┃──┘      ┃
  ┃         ┃         ┃   확인  ┃         ┃
```

### 상세 절차

| 시점 | 담당 | 작업 |
|------|------|------|
| **월~수** | 전원 | feature 작업 → epic PR 생성 & 코드 리뷰 |
| **수~목** | Epic 리드 | 완성된 epic → develop PR 생성 & 머지 |
| **목 18:00** | 리드 | 🔒 **코드 프리즈** — develop에 새 머지 금지 |
| **목 18:00~** | Jenkins | develop 브랜치 테스트 빌드 자동 실행 |
| **목~금 오전** | QA | 테스트 빌드 플레이 & 버그 리포트 |
| **금 오전** | 리드 | 크리티컬 버그 있으면 develop에서 직접 수정 |
| **금 오후** | 리드 | develop → main PR 생성 (2인 승인) |
| **금 오후** | 리드 | main에 버전 태그 부여 (예: `v0.4.0`) |
| **금 오후** | Jenkins | 태그 감지 → 릴리즈 빌드 자동 실행 |

### 버전 넘버링 규칙

```
v[마일스톤].[주차].[패치]

예시:
v0.1.0  ← 첫 번째 주 릴리즈
v0.2.0  ← 두 번째 주 릴리즈
v0.2.1  ← 두 번째 주 핫픽스
v0.2.2  ← 두 번째 주 핫픽스 2차
v0.3.0  ← 세 번째 주 릴리즈
v1.0.0  ← 마일스톤 1 완성 (알파 등)
```

---

## 5. Hotfix 긴급 대응 플로우

### 언제 Hotfix를 쓰나?

- main 빌드에서 **크래시** 또는 **게임 진행 불가 버그** 발견
- 다음 금요일까지 기다릴 수 없는 **긴급 상황**
- 일반 버그는 hotfix 아님 → 다음 릴리즈에서 수정

### Hotfix 절차

```
Step 1. 버그 발견 → Hotfix 판정
        ↓
Step 2. main에서 분기
        $ git checkout -b hotfix/이슈명 main
        ↓
Step 3. 최소한의 수정만 작성
        (새 기능 추가 절대 금지!)
        ↓
Step 4. PR → main (1인 긴급 승인)
        ↓
Step 5. main에 머지 + 패치 태그
        $ git tag v0.X.1
        ↓
Step 6. Jenkins 릴리즈 빌드
        ↓
Step 7. develop에도 머지 (동기화)
        $ git checkout develop
        $ git merge hotfix/이슈명
        ↓
Step 8. 진행 중인 epic에도 알림
        (필요시 epic에도 머지)
        ↓
Step 9. hotfix 브랜치 삭제
```

### Hotfix 체크리스트

- [ ] main에서 분기했는가?
- [ ] 수정 범위가 최소한인가? (신규 기능 포함 X)
- [ ] PR 리뷰 받았는가? (1인 이상)
- [ ] main에 패치 태그를 찍었는가?
- [ ] develop에도 머지했는가?
- [ ] 관련 epic에 알림했는가?

---

## 6. 브랜치 네이밍 규칙

### 형식

```
<type>/<작업자>/<설명>
```

- 모두 **영문 소문자**, 단어 구분은 **하이픈(`-`)**
- 이니셜이나 약어 사용 금지 (예: `KM` ❌ → `km/camera-fix` ✅)

### 타입별 분류

| 타입 | 용도 | 예시 |
|------|------|------|
| `epic/` | 대형 기능 (2주+) | `epic/dungeon-generator` |
| `feature/` | 기능 추가 | `feature/miro/party-ui` |
| `fix/` | 버그 수정 | `fix/km/camera-null-ref` |
| `art/` | 아트 리소스 | `art/tk/boss-model-lod0` |
| `data/` | 기획 데이터 | `data/sayzi/balance-table` |
| `hotfix/` | 긴급 수정 | `hotfix/crash-on-load` |

### ❌ 이렇게 하지 마세요

```
KM                      ← 이름만, 무슨 작업인지 모름
refactor                ← 뭘 리팩토링? 범위 불명
EIS-Branch              ← 대문자 혼재, 표준 아님
test123                 ← ...
my-branch               ← ...
```

### ✅ 이렇게 해주세요

```
feature/km/eis-interaction-system
fix/miro/loading-screen-flicker
art/tk/dungeon-tileset-v2
epic/boss-raid
```

---

## 7. 커밋 메시지 규칙

### 형식

```
<타입> | <요약> (한국어 OK, 50자 이내)
```

### 타입 목록

| 타입 | 의미 | 예시 |
|------|------|------|
| `Feat` | 새 기능 추가 | `Feat \| 파티 편성 UI 초기 레이아웃` |
| `Fix` | 버그 수정 | `Fix \| 카메라 빌보드 NullRef 수정` |
| `Art` | 아트 리소스 추가/수정 | `Art \| 보스 모델 1차 임포트 (LOD0)` |
| `Data` | 기획 데이터 추가/수정 | `Data \| 던전 밸런스 테이블 v2` |
| `Refactor` | 코드 구조 개선 | `Refactor \| EIS 매니저 싱글톤 → DI 전환` |
| `Chore` | 설정, 빌드, 도구 | `Chore \| .gitignore에 ProfilerCaptures 추가` |
| `Doc` | 문서 작성/수정 | `Doc \| README 브랜칭 전략 링크 추가` |

### ❌ 이렇게 하지 마세요

```
dd
이거
수정함
ㅋㅋ
asdf
WIP
```

### ✅ 이렇게 해주세요

```
Feat | 던전 타일 자동 배치 알고리즘 구현
Fix  | iOS에서 터치 입력 2회 인식되는 버그 수정
Art  | Laran 캐릭터 풀바디 스프라이트 v3
Data | 챕터2 적 스폰 테이블 최종본
```

---

## 8. Unity 프로젝트 특수 규칙

### 🚨 Rule 1: Scene 파일은 1인 1씬 원칙

Unity Scene 파일(`.unity`)은 바이너리에 가까운 YAML이라 **자동 머지가 거의 불가능**합니다.

**규칙:**
- 각 씬에 **담당자 1명**을 지정
- 해당 씬을 수정할 수 있는 사람은 담당자만
- 다른 사람의 씬을 수정해야 하면, **담당자에게 요청**

**씬 소유권 매트릭스 (회의에서 작성 필요):**

| 씬 이름 | 담당자 | 비고 |
|---------|--------|------|
| `MainMenu.unity` | (미정) | |
| `Lobby.unity` | (미정) | |
| `Dungeon_01.unity` | (미정) | |
| `Boss_Arena.unity` | (미정) | |
| `Loading.unity` | (미정) | |

### 🚨 Rule 2: Prefab은 Variant로 작업

- **Base Prefab**은 변경하지 않음
- 개인 작업은 **Prefab Variant**를 만들어서 진행
- 완성 후 Base Prefab에 반영 (담당자가)

### 🚨 Rule 3: Rebase 금지, Merge만 사용

```bash
# ❌ 절대 하지 마세요
git rebase develop

# ✅ 이렇게 하세요
git merge develop
```

**이유:** Rebase는 커밋 해시를 변경합니다. Unity의 `.meta` 파일이 GUID를 기반으로 참조하는데, rebase로 커밋이 재생성되면 **GUID 충돌**이 발생하여 에셋 참조가 깨질 수 있습니다.

### 🚨 Rule 4: `.meta` 파일은 반드시 커밋

- `.meta` 파일은 Unity 에셋의 **설정 + 참조 ID**
- `.meta` 없이 파일만 올리면 다른 사람의 Unity에서 설정이 초기화됨
- `.gitignore`에서 `.meta`를 전체 무시하면 안 됨

### 🛠️ Rule 5: UnityYAMLMerge 설정

각 팀원의 `.gitconfig`에 다음을 추가:

```ini
[mergetool "unityyamlmerge"]
    cmd = 'C:/Program Files/Unity/Hub/Editor/<VERSION>/Editor/Data/Tools/UnityYAMLMerge.exe' merge -p "$BASE" "$REMOTE" "$LOCAL" "$MERGED"
    trustExitCode = false

[merge]
    tool = unityyamlmerge
```

> `<VERSION>`을 프로젝트의 Unity 버전으로 교체 (예: `6000.0.38f1`)

---

## 9. Jenkins CI/CD 연동

### 브랜치별 빌드 트리거

| 이벤트 | 브랜치 패턴 | 빌드 타입 | 용도 |
|--------|------------|-----------|------|
| Push/PR 머지 | `develop` | Development Build | 통합 테스트 |
| Push/PR 머지 | `epic/*` | Development Build | Epic 단위 QA |
| Tag 생성 (`v*`) | `main` | **Release Build** | 금요일 릴리즈 |
| Push | `hotfix/*` | Development Build | 긴급 패치 확인 |

### Jenkins Pipeline 분기 로직 (참고용)

```groovy
pipeline {
    agent any
    
    stages {
        stage('Determine Build Type') {
            steps {
                script {
                    if (env.TAG_NAME?.startsWith('v')) {
                        env.BUILD_TYPE = 'Release'
                        env.SCRIPTING_DEFINE = ''
                    } else if (env.BRANCH_NAME == 'develop') {
                        env.BUILD_TYPE = 'Development'
                        env.SCRIPTING_DEFINE = 'DEVELOPMENT_BUILD'
                    } else if (env.BRANCH_NAME?.startsWith('epic/')) {
                        env.BUILD_TYPE = 'Development'
                        env.SCRIPTING_DEFINE = 'DEVELOPMENT_BUILD;EPIC_BUILD'
                    } else if (env.BRANCH_NAME?.startsWith('hotfix/')) {
                        env.BUILD_TYPE = 'Development'
                        env.SCRIPTING_DEFINE = 'DEVELOPMENT_BUILD;HOTFIX_BUILD'
                    }
                }
            }
        }
        
        stage('Unity Build') {
            steps {
                // Unity 빌드 커맨드
                // 프로젝트에 맞게 수정 필요
                sh """
                    unity-builder \
                        --build-type ${env.BUILD_TYPE} \
                        --scripting-define '${env.SCRIPTING_DEFINE}'
                """
            }
        }
    }
    
    post {
        success {
            // 빌드 성공 시 슬랙/디스코드 알림
            echo "Build succeeded: ${env.BUILD_TYPE}"
        }
        failure {
            // 빌드 실패 시 알림
            echo "Build FAILED: ${env.BUILD_TYPE}"
        }
    }
}
```

### 빌드 결과 전달

| 빌드 타입 | 결과물 위치 | 알림 대상 |
|-----------|------------|-----------|
| Development | 내부 공유 드라이브 | 팀 전체 |
| Release | 스토어 업로드 / 외부 공유 | PM + QA |
| Hotfix | 내부 공유 드라이브 | 팀 전체 (긴급) |

---

## 10. 작업자별 일일 워크플로우

### 아침에 시작할 때

```bash
# 1. 내 epic의 최신 상태를 가져온다
git checkout epic/dungeon-generator
git pull origin epic/dungeon-generator

# 2. 내 feature 브랜치로 돌아간다
git checkout feature/miro/dungeon-ui

# 3. epic의 최신 변경을 내 브랜치에 merge
git merge epic/dungeon-generator

# 4. 충돌이 있으면 해결 후 커밋
# (충돌 해결 방법은 FAQ 참조)
```

### 작업 중

```bash
# 수시로 커밋 (규칙에 맞게!)
git add .
git commit -m "Feat | 던전 진입 UI 애니메이션 추가"

# 원격에 push (백업 + 공유)
git push origin feature/miro/dungeon-ui
```

### 작업 완료 시

```bash
# 1. 마지막으로 epic 최신 상태 merge
git merge epic/dungeon-generator

# 2. 충돌 해결 & 테스트

# 3. push
git push origin feature/miro/dungeon-ui

# 4. GitHub에서 PR 생성
#    - Base: epic/dungeon-generator
#    - Compare: feature/miro/dungeon-ui
#    - 설명 + 스크린샷 첨부
```

### PR 체크리스트 (PR 생성 시 복사해서 사용)

```markdown
## 작업 내용
- [ ] 기능 설명을 작성했는가?
- [ ] 스크린샷/영상을 첨부했는가? (UI/비주얼 변경 시)

## 품질
- [ ] 에디터에서 에러/경고 없이 실행되는가?
- [ ] 새로 추가한 에셋의 .meta 파일이 포함되어 있는가?
- [ ] 다른 사람의 씬/프리팹을 수정하지 않았는가?

## 커밋
- [ ] 커밋 메시지가 규칙을 따르는가? (타입 | 요약)
- [ ] 불필요한 파일 (Temp, Library 등)이 포함되지 않았는가?
```

---

## 11. FAQ & 트러블슈팅

### Q: 머지할 때 씬 파일이 충돌나면?

**A:** 씬 파일은 수동 머지가 거의 불가능합니다.

1. 먼저 UnityYAMLMerge를 시도합니다
2. 그래도 안 되면, **한 쪽을 선택**합니다:
   ```bash
   # 상대방 것을 선택 (내 변경 포기)
   git checkout --theirs Assets/Scenes/문제씬.unity
   
   # 내 것을 선택 (상대방 변경 포기)
   git checkout --ours Assets/Scenes/문제씬.unity
   ```
3. 포기한 변경사항은 해당 씬 담당자에게 연락하여 재작업

**근본 해결:** 씬 소유권 매트릭스를 지키면 이 상황 자체가 발생하지 않습니다.

---

### Q: 실수로 main에 직접 push했어요!

**A:** Branch Protection이 설정되어 있으면 거부됩니다. 만약 Protection이 없는 상태라면:

```bash
# main의 마지막 정상 커밋으로 되돌리기
git checkout main
git reset --hard HEAD~1    # 커밋 1개 되돌리기
git push --force origin main

# 내 작업은 feature 브랜치에서 다시 push
```

> ⚠️ `--force`는 위험합니다. 반드시 팀에 알리고 실행하세요.

---

### Q: `.meta` 파일이 충돌나면?

**A:** `.meta` 충돌은 대부분 **같은 폴더에 같은 이름의 파일을 동시에 추가**했을 때 발생합니다.

1. 두 `.meta`의 GUID를 비교합니다
2. 먼저 커밋된 쪽의 GUID를 유지합니다
3. 나중에 커밋된 쪽은 Unity에서 에셋을 다시 임포트합니다

---

### Q: 내 feature 브랜치가 너무 오래됐어요 (epic과 많이 벌어졌어요)

**A:** 주기적으로 epic의 최신 상태를 merge해야 합니다. 최소 **하루 1회** 권장.

```bash
git checkout feature/miro/my-feature
git merge epic/dungeon-generator
# 충돌 해결
git push
```

---

### Q: Git LFS가 뭐고 왜 필요한가요?

**A:** Git은 원래 텍스트 파일용입니다. 텍스처(`.png`), 모델(`.fbx`), 사운드(`.wav`) 같은 대용량 바이너리를 Git에 직접 넣으면:

- 리포 용량 폭증 (clone에 수십 분)
- 히스토리에 바이너리가 쌓여서 삭제 불가

**Git LFS**는 대용량 파일을 별도 스토리지에 보관하고, Git에는 포인터만 저장합니다.

**최초 1회 설정:**
```bash
git lfs install
```

이후에는 `.gitattributes` 규칙에 따라 자동 동작합니다.

---

## 12. 회의 논의 안건

### 🗳️ 결정 필요 사항

| # | 안건 | 선택지 | 비고 |
|---|------|--------|------|
| 1 | **씬 소유권 매트릭스** | 4명 × 씬 목록 배정 | 충돌 방지의 핵심 |
| 2 | **LFS 마이그레이션 범위** | A) 기존 히스토리 포함 전환 / B) 앞으로만 적용 | A는 클린하지만 전원 re-clone 필요 |
| 3 | **코드 프리즈 시점** | 목요일 18:00? 다른 시간? | 릴리즈 안정성 vs 작업 시간 |
| 4 | **PR 리뷰어 지정 방식** | A) 자유 지정 / B) 라운드 로빈 / C) 리드 고정 | 4명이면 리드 고정이 현실적 |
| 5 | **Epic 리드 선정 기준** | A) 매번 지정 / B) 역할별 고정 | 책임 범위 결정 |
| 6 | **알림 채널** | 슬랙? 디스코드? 카톡? | Jenkins 빌드 결과 알림 대상 |

### 📝 회의 후 Action Items (예상)

- [ ] 씬 소유권 매트릭스 확정
- [ ] 팀 전원 Git LFS 설치 (`git lfs install`)
- [ ] 팀 전원 UnityYAMLMerge 설정
- [ ] GitHub Branch Protection 규칙 적용 (admin)
- [ ] Jenkins 트리거 규칙 추가
- [ ] 첫 번째 Epic 브랜치 생성 & 테스트 리허설
- [ ] 이 문서를 Notion/Confluence에 팀 위키로 게시

---

> 📌 **이 문서는 팀 회의에서 논의 후 확정됩니다. 결정 사항에 따라 업데이트될 예정입니다.**
