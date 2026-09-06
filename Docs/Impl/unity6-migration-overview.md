# dennokoworks ツール群 — Unity 6 移行調査（共通ドキュメント）

- 調査日: 2026-09-06
- 現行環境: Unity **2022.3.22f1** / Built-in Render Pipeline (BiRP)
- 移行目標: Unity **6 (6000.0 LTS)** / **BiRP を維持**
- 前提:
  - VRChat は URP 対応を未発表。VRC 向け拡張として BiRP のまま Unity 6 へ段階移行する方針に合わせる。
  - **VRChat SDK は Unity 6 対応済みとして扱う**（本調査では SDK 側の対応時期を制約条件に含めない）。

> 本書は各ツールリポジトリの `Docs/Impl/unity6-migration-overview.md` に複製されている。
> 原本は `dennokoworks/Docs/Impl/unity6-migration-overview.md`。内容を更新したら全複製に反映すること。

各ツール個別の判定・修正箇所は `<ツール>/Docs/Impl/unity6-migration.md` を参照。
本書はその共通の根拠（Unity 6 での API 差分と、ワークスペース全体の横断調査結果）をまとめる。

---

## 1. 結論

**BiRP 維持のまま Unity 6 へ移行することは技術的に可能。** Unity 6 の公式アップグレードガイドは
Built-in Render Pipeline 向けに書かれており、BiRP は Unity 6 でも継続サポートされている。

ワークスペース全体で **コンパイルエラーになる API は 0 件**。
Obsolete 警告になる API が **5 箇所**（4 ツール）のみで、いずれも 1 行の機械的置換で解消する。

VRChat SDK は Unity 6 対応済みを前提とするため、移行の残る制約は **NDMF / lilToon /
`jp.lilxyzw.shadercore` / VRC Light Volumes / UdonSharp といったサードパーティ製パッケージの
Unity 6 対応時期** に絞られる。これらに依存しないツールは、いま時点で Unity 6 検証を開始できる。

### 判定分布

| 判定 | ツール |
|---|---|
| ✅ 対応済（修正不要・検証のみ） | FolderCleaner, HairFlowNormalGenerator, HowManyPolygons, MeshSplitter, Normalmap_generator, ScaleLink, UniTexEditor, Wire_skybox, 缶バッチ |
| ⚠️ 要修正（Obsolete API あり） | CableGenerator, DirectionalLightController, HairChimeraTool, SocksTuku-ru |
| 🔍 要検証（内部 API リフレクション依存） | AtlasCraft, BoxWeightTransfer, DenEmo, DenLattice, DennokoMeshEditor, DennokoPSDEditor, Uni-Shooting, Uni-Vader |
| ⛔ 外部依存待ち（自コード側は準備完了） | Avatar_scale_specify, DennokoEx, MatcapMaker_forUnity, NdmfObjectActivater, ShadowEx |

複数に該当するツールもある（例: CableGenerator は ⚠️ と 🔍 の両方）。詳細は各ツールのドキュメントを参照。

---

## 2. Unity 2022.3 → Unity 6 の API 差分（調査結果）

公式アップグレードガイド（[Upgrade to Unity 6.0](https://docs.unity3d.com/6000.0/Documentation/Manual/UpgradeGuideUnity6.html)）
に記載された変更のうち、**本ワークスペースに関係し得るもの**を列挙し、実際の使用有無を記載する。

### 2.1 コンパイルエラーになる変更（Breaking）

| 変更 | 内容 | 本ワークスペースでの使用 |
|---|---|---|
| `GraphicsFormat.DepthAuto` / `ShadowAuto` / `VideoAuto` | 2022.1 で deprecated → Unity 6 で **削除**。コンパイルエラー | **なし**（`RenderTextureFormat.ARGB32` のみ使用） |
| `GraphicsFormatUtility.GetGraphicsFormat` | 廃止フォーマットを返さなくなる。`RenderTextureFormat.Depth` → `GraphicsFormat.None` | **なし** |
| Auto Generate Lighting 関連 API | ライティングの自動生成が廃止され関連 API が削除 | **なし** |
| Android `UnityPlayer` → `UnityPlayerForActivityOrService` | Android ネイティブ拡張のみ | **なし**（全て Editor 拡張） |

→ **コンパイルエラーになる箇所は 0 件。**

### 2.2 Obsolete 警告になる変更（Warning）

| 旧 API | Unity 6 での置換 | 本ワークスペースでの使用 |
|---|---|---|
| `Object.FindObjectsOfType<T>()` | `Object.FindObjectsByType<T>(FindObjectsSortMode)` | **4 箇所**（3.1 節） |
| `Object.FindObjectOfType<T>()` | `Object.FindFirstObjectByType<T>()` / `FindAnyObjectByType<T>()` | なし |
| `Object.FindObjectsOfType(Type)`（非ジェネリック） | `Object.FindObjectsByType(Type, FindObjectsSortMode)` | **1 箇所**（3.1 節） |
| `VisualElement.ExecuteDefaultAction` | `HandleEventBubbleUp` | **なし** |
| `VisualElement.ExecuteDefaultActionAtTarget` | `HandleEventTrickleDown` | **なし** |
| `EventBase.PreventDefault` | `StopPropagation` | **なし** |
| `LightingSettings.filteringGaussRadius*`（int） | `filteringGaussianRadius*`（float） | **なし** |
| `Lightmapping.autoGenerate` | `Lightmapping.Bake` / `BakeAsync` | **なし** |
| `CustomEditorForRenderPipelineAttribute` | `CustomEditor` + `SupportedOnRenderPipelineAttribute` | **なし** |
| `VolumeComponentMenuForRenderPipelineAttribute` | `VolumeComponentMenu` + `SupportedOnRenderPipelineAttribute` | **なし** |
| `RenderPipelineEditorUtility.FetchFirstCompatibleTypeUsingScriptableRenderPipelineExtension` | `GetDerivedTypesSupportedOnCurrentPipeline` | **なし** |

> **重要な非該当**: `Resources.FindObjectsOfTypeAll<T>()` は **Obsolete ではない**。
> ワークスペース内の 10 箇所（各ツールの `DennokoUIFont.cs` / `*Version.cs`）はすべてこちらであり、**修正不要**。
> 名前が似ているため一括置換すると壊れる。混同しないこと。

### 2.3 挙動が変わる変更（コンパイルは通るが結果が変わる）

| 変更 | 内容 | 本ワークスペースでの影響 |
|---|---|---|
| **`FindObjectsByType` の並び順** | 旧 `FindObjectsOfType` は InstanceID 順。`FindObjectsSortMode.None` は **未ソート** | 決定性が要る箇所は `FindObjectsSortMode.InstanceID` を指定する（3.1 節） |
| Mipmap Limit のオプトイン化 | ランタイム生成 Texture2D が既定で mipmap limit に従わなくなる | 影響小（生成テクスチャはほぼ `mipChain: false`） |
| ライトプローブのエネルギー保存 | 明るさが 94% → 100% に | Editor 拡張のプレビュー見た目のみ。ベイク結果の再確認推奨 |
| 環境光 / Skybox リフレクションプローブ | 自動ベイクされなくなる | シーン側。ツール本体は非該当 |
| Enlighten Baked GI 廃止 | Progressive Lightmapper に自動置換 | **なし** |
| `Rigidbody.AddForceAtPosition` / `AddExplosionForce` のトルク計算 | `VelocityChange`/`Acceleration` で質量でなく慣性テンソルでスケール | **なし**（物理 API 未使用） |
| Metal の `half` / `min16float` | 32bit float に変換されるように | シェーダは全て `#pragma target 3.0` + `UnityCG.cginc`。影響小 |

### 2.4 変更なし（確認済み）

- **C# 言語バージョン**: 2022.3 / Unity 6.0 ともに **C# 9 / .NET Standard 2.1**。言語移行は不要。
- **BiRP**: Unity 6 で継続サポート。`UnityCG.cginc`、サーフェスシェーダ、`GL` イミディエイトモード、
  `Hidden/Internal-Colored`、`Graphics.Blit`、`RenderTexture.GetTemporary` はいずれも有効。
- **IMGUI**: `EditorWindow.OnGUI` / `EditorGUILayout` / `Handles` / `SceneView.duringSceneGui` は変更なし。
- **`Unity.Collections`**: `NativeArray<BoneWeight1>` + `Mesh.SetBoneWeights` の API 形状は不変。
- **`AnimationUtility`**, **`AssetImporter` / `TextureImporter` / `ModelImporter`**, **`AssetDatabase`**: 変更なし。
- **バージョン定義**: 既存の `#if UNITY_2019_3_OR_NEWER` / `UNITY_2020_2_OR_NEWER` / `UNITY_2022_1_OR_NEWER`
  は Unity 6 でもすべて true。旧側の分岐が死にコードになるだけで、動作は変わらない。

---

## 3. ワークスペース横断の検出事項

### 3.1 Obsolete API: `Object.FindObjectsOfType`（要修正・5 箇所）

| ファイル | 現行 | 置換 |
|---|---|---|
| `CableGenerator/Editor/Inspector/CablePickingColliderManager.cs:90` | `Object.FindObjectsOfType<MeshFilter>(false)` | `Object.FindObjectsByType<MeshFilter>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)` |
| `CableGenerator/Editor/Shared/EditorMeshRaycaster.cs:77` | `Object.FindObjectsOfType<MeshFilter>(false)` | 同上 |
| `DirectionalLightController/Editor/DirectionalLightControllerWindow.cs:24` | `FindObjectsOfType<Light>()` | `FindObjectsByType<Light>(FindObjectsSortMode.InstanceID)` |
| `HairChimeraTool/Editor/Services/AvatarBridge.cs:22` | `Object.FindObjectsOfType(descriptorType)` | `Object.FindObjectsByType(descriptorType, FindObjectsSortMode.None)` |
| `SocksTuku-ru/Editor/SocksGeneratorWindow.cs:414` | `FindObjectsOfType<Animator>(false)` | `FindObjectsByType<Animator>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)` |

**ソートモードの選択基準**

- 単に「1 件だけ存在するか」を判定する / 全件を集合として扱う → `FindObjectsSortMode.None`（高速）
- UI に並べる、先頭要素を採用する等、**順序が結果に影響する** → `FindObjectsSortMode.InstanceID`（従来と同順）

`DirectionalLightController` は取得した Light をウィンドウに列挙するため `InstanceID` を指定する。
他 4 箇所は集合として扱うため `None` で問題ない。

**互換性**: `FindObjectsByType` は Unity **2022.2 以降**に存在するため、
2022.3.22f1 のまま置換して先行コミットできる。`#if` 分岐は不要。

### 3.2 UnityEditor 内部 API へのリフレクション（要検証・最大リスク）

**コンパイルエラーにならず、Unity 6 で静かに機能が無効化される**タイプの問題。
本ワークスペースの実質的な最大リスクはここに集中している。

| 対象 | 使用箇所 | 用途 | 失敗時の挙動 |
|---|---|---|---|
| `UnityEditor.Unwrap` / `UnwrapParam` | `AtlasCraft/Editor/Core/UV2Repacker.cs:117-133` | UV2 自動生成 (`GenerateSecondaryUVSet`) | ガードあり。手動パックへフォールバック |
| `UnityEditor.AnnotationUtility.showSelectionOutline` | `DenLattice/Editor/Session/SelectionOutline.cs:124-145`<br>`DennokoMeshEditor/Editor/Session/SelectionOutline.cs:124-145` | 編集中の選択アウトライン抑制 | ガードあり。アウトラインが出たままになる |
| `UnityEditor.AudioUtil.PlayPreviewClip(AudioClip,int,bool)` / `StopAllPreviewClips()` | `Uni-Shooting/Editor/Audio/SfxPlayer.cs:29-34`<br>`Uni-Vader/Editor/Audio/SfxPlayer.cs:29-34` | 効果音再生 | ガードあり（`Available` プロパティ）。無音になる |
| `UnityEditor.Splines.SplineContainerEditor` + 非 public `OnSceneGUI` | `CableGenerator/Editor/SplineContainerEditor/CableSplineContainerEditor.cs:54,119` | 標準 Spline インスペクタへのフォールバック | ガードあり。`DrawDefaultInspector` へ縮退 |
| `nadena.dev.ndmf.preview.PreviewSession` の非 public プロパティ | `BoxWeightTransfer/Editor/PreviewMeshProvider.cs:78-84`<br>`DenEmo/Editor/DenEmoWindow.VertexFilter.cs:239-250`<br>`HairChimeraTool/Editor/Services/NdmfPreviewBridge.cs:75-76` | NDMF プレビュー結果メッシュの取得 | ガードあり。ベイク済みメッシュへフォールバック |

**評価**: いずれも `null` チェックとフォールバック経路を持ち、**壊れても例外を投げず機能縮退する**設計に
なっている。したがって Unity 6 で「動かなくなる」のではなく「**気付かないまま機能が消える**」のが
リスク。移行時は必ず **各機能を実操作で確認**すること（各ツールの検証チェックリスト参照）。

**推奨**: 解決に失敗した際に一度だけ `Debug.LogWarning` を出す（既にログを出している実装はそのまま）。
サイレント縮退のままだと移行後の不具合発見が遅れる。

### 3.3 IMGUI テーマ: 共有 `EditorStyles` / `GUI.skin` の書き換え（要視覚検証）

`AtlasCraft`, `BoxWeightTransfer`, `CableGenerator`, `DennokoPSDEditor`, `FolderCleaner`,
`ScaleLink`, `SocksTuku-ru` の `*Theme.cs` は、`PushEditorTheme()` で
`EditorStyles.label / objectField / numberField / textField / popup / toggle` と
`GUI.skin.textField / label / settings` を **グローバルに書き換え**、`PopEditorTheme()` で復元している。

例: `AtlasCraft/Editor/AtlasCraftTheme.cs:294-356`

- Unity 6 は Editor スキンが刷新されており、**border / padding / 背景テクスチャの前提が変わる**。
  コンパイルは通るが、フィールドの余白や枠線が崩れる可能性がある。
- API 自体（`EditorStyles`, `GUIStyle`, `GUIStyleState`, `RectOffset`）は Unity 6 でも不変のため、
  **修正ではなく見た目の再調整**の作業になる。
- Push/Pop の対称性は維持されているので、例外時にスタイルが壊れたまま残る恐れは低い。

**移行タスク**: Unity 6 上で各ウィンドウを開き、テキストフィールド / Popup / ObjectField の
枠線・余白・文字色を目視確認し、必要なら `RectOffset` の値だけ調整する。

### 3.4 UI Toolkit（低リスク・視覚検証のみ）

UITK を使うツール: `DenEmo`, `DennokoPSDEditor`, `HairChimeraTool`, `MatcapMaker_forUnity`,
`MeshSplitter`, `Normalmap_generator`, `UniTexEditor`

- Unity 6 で Obsolete 化する `ExecuteDefaultAction` / `ExecuteDefaultActionAtTarget` / `PreventDefault`
  は **未使用**。
- Unity 6 で非推奨になる `UxmlFactory` / `UxmlTraits`（→ `[UxmlElement]` / `[UxmlAttribute]`）も **未使用**。
  UXML はレイアウト定義のみで、カスタム要素は C# で直接構築している。
- したがって **コード修正は不要**。Unity 6 の既定 USS 変更による見た目のずれのみ確認する。

`.uxml` / `.uss` は 20 ファイル（DenEmo 8, HairChimeraTool 3, MeshSplitter 3, Normalmap_generator 3,
MatcapMaker 2, DennokoPSDEditor 1）。スキーマ変更はないためそのまま読み込める。

### 3.5 フォント生成（`UnityEngine.TextCore.Text.FontAsset`）

各ツールの `DennokoUIFont.cs`（DenEmo / DennokoPSDEditor / HairChimeraTool / MeshSplitter /
Normalmap_generator / UniTexEditor）は、OS のメイリオから
`FontAsset.CreateFontAsset(familyName, styleName)` で SDF フォントを実行時生成している。

- `UnityEngine.TextCore.Text` は **ビルトインモジュール**（`com.unity.modules.uielements` 経由）であり、
  `com.unity.textmeshpro` パッケージには依存していない。
  → Unity 6 での TextMeshPro パッケージ統合（`com.unity.ugui` 2.0 への吸収）の **影響を受けない**。
- API 自体は Unity 6 でも存在する。
- ただし SDF アトラス生成は TextCore の内部実装に依存するため、**文字が表示されるかの実機確認は必須**。

### 3.6 シェーダ（BiRP 維持につき影響なし）

| 種別 | 件数 | 状況 |
|---|---|---|
| `.shader`（BiRP / CGPROGRAM） | 4 | `DennokoEx_MaskPacker`, `LayerBlend`, `MatcapEdgeDilation`, `GridSkybox` — すべて `UnityCG.cginc` + `#pragma target 3.0`。Unity 6 BiRP で有効 |
| `.hlsl` / `.cginc`（lilToon 拡張） | 38 | lilToon / `jp.lilxyzw.shadercore` のヘッダを include。**lilToon 側の Unity 6 対応に従属** |
| `.compute` | 12 | UniTexEditor 11, Normalmap_generator 1。RP 非依存。Unity 6 で API 変更なし |

BiRP を維持する限り、`UnityCG.cginc` / `unity_ObjectToWorld` / `UnityObjectToClipPos` などの
組み込みマクロはそのまま使える。URP へ移行する場合のみ全面書き換えが必要になるが、本計画の対象外。

---

## 4. プロジェクト側（manifest.json）の確認事項

`Packages/manifest.json` の現行依存のうち、Unity 6 で確認が必要なもの。
**これらはツール個別ではなくプロジェクト共通の作業。**

| パッケージ | 現行 | 対応方針 |
|---|---|---|
| `com.unity.barracuda` | 3.0.2 | **コード参照 0 件。削除可**。Barracuda は後継（Sentis / Inference Engine）に移行済みで Unity 6 では非推奨 |
| `com.unity.textmeshpro` | 3.0.6 | Unity 6 では TMP が `com.unity.ugui` に統合される。**コードは `TMPro` 名前空間を一切使っていない**ため、エントリを削除して問題ないか要検証 |
| `com.unity.modules.unityanalytics` | 1.0.0 | Unity 6 で解決するか **要検証**。未使用のため解決しなければ削除 |
| `com.unity.splines` | 2.5.2 | Unity 6 は新しい Splines を同梱。**CableGenerator の API 面を再検証**（3.2 節のリフレクション含む） |
| `com.unity.animation.rigging` | 1.2.1 | コード参照 0 件。未使用なら削除 |
| `com.unity.timeline` / `com.unity.collab-proxy` / `com.unity.feature.development` | — | Unity 6 が対応版に自動更新 |
| `com.coplaydev.unity-mcp` | git | 開発補助のみ。Unity 6 対応は別途確認 |

**未使用パッケージの削除は Unity 6 移行前に 2022.3 上で先行実施できる。**
依存が減るほど移行時の不確定要素が減るため、先にやる価値がある。

---

## 5. 外部依存（移行スケジュールの実質的な律速）

自コード側の修正が完了しても、以下が Unity 6 に対応するまで移行は完了できない。

| 依存 | 依存するツール | 備考 |
|---|---|---|
| **VRChat SDK**（`com.vrchat.base` / Avatars） | Avatar_scale_specify, BoxWeightTransfer, DenLattice, DennokoMeshEditor, NdmfObjectActivater（asmdef 直接参照）<br>DenEmo, HairChimeraTool, MeshSplitter, ScaleLink（リフレクション参照） | **Unity 6 対応済みを前提とする**（制約条件から除外）。導入版のみ確認する |
| **NDMF**（`nadena.dev.ndmf`） | Avatar_scale_specify, BoxWeightTransfer, DenLattice, DennokoEx, DennokoMeshEditor, NdmfObjectActivater | **実質的な最大の律速**。内部 API リフレクション 3 箇所（3.2 節）もここに依存 |
| **lilToon**（`lilToon.Editor`） | DennokoEx, ShadowEx | `lilToonInspector` を継承しているため lilToon の Unity 6 対応が必須 |
| **`jp.lilxyzw.shadercore`** | MatcapMaker_forUnity, DennokoEx | シェーダ include の解決に必要 |
| **VRC Light Volumes**（`red.sim.lightvolumes`） | DennokoEx | `LightVolumes.cginc` を include |
| **AudioLink** | DennokoEx（コメントアウト状態の include） | 現状は無効化されているため影響なし |
| **UdonSharp / VRCSDK3-Worlds** | ColorChangeShader/slider（別プロジェクト） | ワールド側。アバター SDK とは別系統 |

**リフレクション参照は asmdef 依存より有利**: `DenEmo` / `HairChimeraTool` / `MeshSplitter` / `ScaleLink`
は VRChat SDK の型を文字列＋リフレクションで解決しており、SDK が無くても、あるいは型が移動しても
コンパイルは通る。SDK を導入していない検証用プロジェクトでもツール単体の Unity 6 動作確認ができ、
SDK 側のバージョン差異にも強いという利点がある。

---

## 6. 推奨移行手順

### フェーズ 1: Unity 2022.3.22f1 のまま先行実施できる作業（今すぐ着手可）

1. **`FindObjectsOfType` → `FindObjectsByType` 置換**（3.1 節、5 箇所）
   `FindObjectsByType` は 2022.2 以降に存在するため、現行環境でそのままビルド・動作確認できる。
2. **未使用パッケージの整理**（4 節: barracuda, animation.rigging 等）
3. **リフレクション失敗時の警告ログ追加**（3.2 節）
   Unity 6 で機能がサイレント縮退したときに気付けるようにしておく。

### フェーズ 2: Unity 6 検証用プロジェクトでの動作確認（外部依存の対応を待たずに実施可）

外部依存なしで動くツール（AtlasCraft, DennokoPSDEditor, DirectionalLightController, FolderCleaner,
HairFlowNormalGenerator, HowManyPolygons, Normalmap_generator, SocksTuku-ru, Uni-Shooting, Uni-Vader,
UniTexEditor, MeshSplitter, Wire_skybox）を **Unity 6 の空プロジェクトにコピーして検証**する。

重点確認項目:

- 3.2 節の内部 API リフレクションが機能しているか（**実操作で確認**）
- 3.3 節の IMGUI テーマの見た目
- 3.4 節の UI Toolkit ウィンドウの見た目
- 3.5 節のフォント（文字が表示されるか）
- Compute シェーダのカーネルがコンパイルされるか

### フェーズ 3: 外部依存の Unity 6 対応後

1. NDMF / lilToon / `jp.lilxyzw.shadercore` / VRC Light Volumes の Unity 6 対応版に更新
   （VRChat SDK は対応済み前提のため、導入版の確認のみ）
2. NDMF 依存ツール（Avatar_scale_specify, BoxWeightTransfer, DenLattice,
   DennokoMeshEditor, NdmfObjectActivater）を検証
3. lilToon 依存ツール（DennokoEx, ShadowEx）と `shadercore` 依存ツール（MatcapMaker_forUnity）を検証
4. VCC のプロジェクトを Unity 6 へ移行

### フェーズ 4: リリース

- 各ツールの配布物 / `package.json` の `unity` フィールドを更新
- 2022.3 系と Unity 6 系を **同一ブランチで両対応** する場合は `#if UNITY_6000_0_OR_NEWER` を使う。
  ただし 3.1 節の置換は両バージョンで動くため分岐不要。

---

## 7. リスク評価まとめ

| リスク | 深刻度 | 検知しやすさ | 対策 |
|---|---|---|---|
| 外部依存（VRChat SDK 等）の Unity 6 未対応 | **高** | 高（そもそも入らない） | 待つ。フェーズ 2 を先行して手戻りを減らす |
| 内部 API リフレクションのサイレント縮退 | 中 | **低**（エラーが出ない） | 実操作での機能確認 ＋ 警告ログ追加 |
| IMGUI テーマの見た目崩れ | 低 | 中（目視で分かる） | Unity 6 上で再調整 |
| UI Toolkit の見た目ずれ | 低 | 中 | Unity 6 上で再調整 |
| `FindObjectsOfType` の Obsolete 警告 | 低 | **高**（警告が出る） | 5 箇所を置換（フェーズ 1） |
| BiRP シェーダの破損 | **なし** | — | BiRP は Unity 6 で継続サポート |
| C# 言語バージョン差異 | **なし** | — | 両方 C# 9 |

---

## 8. 参照

- [Unity - Manual: Upgrade to Unity 6.0](https://docs.unity3d.com/6000.0/Documentation/Manual/UpgradeGuideUnity6.html)
- [Unity - Manual: New in Unity 6.0](https://docs.unity3d.com/6000.0/Documentation/Manual/WhatsNewUnity6.html)
- [VRChat Creation: Current Unity Version](https://creators.vrchat.com/sdk/upgrade/current-unity-version/)
- [VRChat Creation: Upgrading Projects to 2022](https://creators.vrchat.com/sdk/upgrade/unity-2022/)
